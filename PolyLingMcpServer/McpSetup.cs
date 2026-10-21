using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PolyLingMcpServer;

/// <summary>
/// stdio と HTTP で共通の MCP 構成。条件（profile）ごとの出し分けと計測をここに集める。
///
/// 【出し分けの方法】
///   固定の道具はこれまでどおり属性から全部登録し、tools/list のフィルタで条件に合わないものを外す。
///   外した道具を名指しで呼ばれたときは tools/call のフィルタが "Unknown tool" で断る。
///   道具の実装そのものは条件によって変えない。
///   profile=generic の pl_ 道具は WithListToolsHandler / WithCallToolHandler で足す
///   （SDK が固定の道具と合わせて返す）。
///
/// 【計測】
///   同じ 2 つのフィルタで、tools/list と tools/call の 1 回ごとに量と時間を MetricsLog へ書く。
///
/// 【利用シーン】（profile=optimized だけ）
///   要求の _meta "polyling/scene"（無ければ --scene）で利用シーンを明示すると、
///   固定の道具をその利用シーンの tools に絞る（ProfileRules.IsVisibleInScene）。
///   利用シーンの定義は tools/list のたびに PolyLing から取り直す（SceneCatalog）。
///     ・PolyLing にその利用シーンが無い … tools/list を InvalidParams で断る（黙って全部を見せない）
///     ・Unity に届かない            … 前回の写しを使う。写しも無ければ絞らない。理由は計測ログへ
///   tools/call は前回の写しで判定する（Editor がコンパイル中でも待たされないため）。
/// </summary>
public static class McpSetup
{
    public static IMcpServerBuilder AddPolyLingMcp(this IServiceCollection services, ServerState state)
    {
        return services
            .AddMcpServer(options =>
            {
                // 入門（GuideRules）も載せる。instructions が AI まで届くクライアント向け。
                // 届かないクライアントには、最初の道具応答に同じものを添える（CallFilter）。
                var primer = GuideRules.PrimerText();
                options.ServerInstructions = ProfileRules.Instructions(state.Profile)
                                           + TargetRules.InstructionsSuffix(state.Target)
                                           + (primer.Length > 0 ? "\n\n" + primer : string.Empty);
                options.ServerInfo = new Implementation
                {
                    Name    = "PolyLingMcpServer",
                    Version = $"{AssemblyVersion()} profile={ProfileRules.Name(state.Profile)} target={TargetRules.Name(state.Target)}"
                };
            })
            .WithListToolsHandler(GenericCommandTools.ListAsync)
            .WithCallToolHandler(GenericCommandTools.CallAsync)
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(next => (request, ct) => ListFilter(state, next, request, ct));
                filters.AddCallToolFilter(next => (request, ct) => CallFilter(state, next, request, ct));
            });
    }

    private static string AssemblyVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    // ================================================================
    // tools/list
    // ================================================================

    private static async ValueTask<ListToolsResult> ListFilter(
        ServerState state,
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next,
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        string sceneName = SceneFor(state, request.Params?.Meta);
        SceneCatalog.SceneInfo? scene = null;
        string sceneProblem = string.Empty;

        if (sceneName.Length > 0)
        {
            var (status, info, problem) = await SceneCatalog.RefreshAndGetAsync(sceneName, cancellationToken);
            if (status == SceneCatalog.FetchStatus.NotFound)
                throw new McpProtocolException(
                    $"unknown scene '{sceneName}'. Create it with the PolyLing command setScene, or omit the scene.",
                    McpErrorCode.InvalidParams);
            scene = info;
            sceneProblem = problem;
            if (problem.Length > 0) await Console.Error.WriteLineAsync($"tools/list scene: {problem}");
        }

        var result = await next(request, cancellationToken);

        var kept = new List<Tool>();
        foreach (var tool in result.Tools)
        {
            bool isGeneric = tool.Name.StartsWith(ProfileRules.GenericToolPrefix, StringComparison.Ordinal);

            if (isGeneric)
            {
                if (state.Profile == ServerProfile.Generic) kept.Add(tool);
                continue;
            }

            if (!ProfileRules.IsFixedToolVisible(state.Profile, tool.Name)) continue;
            if (!TargetRules.IsFixedToolVisible(state.Target, tool.Name)) continue;
            if (scene is not null && !ProfileRules.IsVisibleInScene(tool.Name, scene.Tools)) continue;

            var description = ProfileRules.DescriptionOverride(state.Profile, tool.Name);
            var schema = PatchSchema(state.Profile, tool.Name, tool.InputSchema);
            kept.Add(description is null && schema is null
                ? tool
                : Copy(tool, description ?? tool.Description, schema ?? tool.InputSchema));
        }

        result.Tools = kept;
        sw.Stop();

        if (state.Metrics is { } metrics)
        {
            var record = new JsonObject
            {
                ["profile"]     = ProfileRules.Name(state.Profile),
                ["kind"]        = "list",
                ["taskId"]      = MetricsLog.TaskIdOf(request.Params?.Meta),
                ["scene"]       = sceneName,
                ["tools"]       = kept.Count,
                ["resultBytes"] = SerializedBytes(result),
                ["elapsedMs"]   = sw.ElapsedMilliseconds
            };

            if (state.Profile == ServerProfile.Generic && state.LastGenericList is { } g)
            {
                record["genericAdded"]    = g.Added;
                record["genericRejected"] = g.Rejected;
                record["schemaRevision"]  = g.SchemaRevision;
                if (g.Problem.Length > 0) record["problem"] = g.Problem;
            }

            if (sceneProblem.Length > 0) record["sceneProblem"] = sceneProblem;

            metrics.Write(record);
        }

        return result;
    }

    /// <summary>
    /// 条件に合わせて入力 Schema を直す。直す所が無ければ null。
    ///   ・この条件で見せない引数（ProfileRules.HiddenParams）を properties と required から外す
    ///   ・Optimized でサーバが入れる既定値（ProfileRules.DefaultsFor）を "default" に書く
    /// </summary>
    private static JsonElement? PatchSchema(ServerProfile profile, string toolName, JsonElement inputSchema)
    {
        var hidden   = ProfileRules.HiddenParams(profile, toolName);
        var defaults = ProfileRules.DefaultsFor(profile, toolName);
        if (hidden.Count == 0 && defaults.Count == 0) return null;

        if (JsonNode.Parse(inputSchema.GetRawText()) is not JsonObject schema) return null;
        var properties = schema["properties"] as JsonObject;

        foreach (var key in hidden) properties?.Remove(key);

        if (schema["required"] is JsonArray required)
            foreach (var node in required.ToList())
                if (node?.GetValue<string>() is { } r && hidden.Contains(r)) required.Remove(node);

        foreach (var (key, value, visible) in defaults)
        {
            if (!visible || properties?[key] is not JsonObject prop) continue;
            prop["default"] = JsonValue.Create(value);
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>説明文・入力 Schema を差し替えた写しを作る。登録済みの道具の定義は書き換えない。</summary>
    private static Tool Copy(Tool tool, string? description, JsonElement inputSchema) => new()
    {
        Name         = tool.Name,
        Title        = tool.Title,
        Description  = description,
        InputSchema  = inputSchema,
        OutputSchema = tool.OutputSchema,
        Annotations  = tool.Annotations,
        Icons        = tool.Icons,
        Meta         = tool.Meta
    };

    // ================================================================
    // tools/call
    // ================================================================

    private static async ValueTask<CallToolResult> CallFilter(
        ServerState state,
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        string name = request.Params?.Name ?? string.Empty;
        bool isGeneric = name.StartsWith(ProfileRules.GenericToolPrefix, StringComparison.Ordinal);
        bool visible = isGeneric
            ? state.Profile == ServerProfile.Generic
            : ProfileRules.IsFixedToolVisible(state.Profile, name)
              && TargetRules.IsFixedToolVisible(state.Target, name);

        string sceneName = SceneFor(state, request.Params?.Meta);
        if (visible && !isGeneric && SceneCatalog.TryGetCached(sceneName) is { } scene)
            visible = ProfileRules.IsVisibleInScene(name, scene.Tools);

        // 引数の量は呼び出し側が送った分で測る（サーバが入れる既定値を含めない）。
        int argBytes = request.Params?.Arguments is { } sent ? SerializedBytes(sent) : 0;

        if (visible && !isGeneric && request.Params is { } p)
        {
            var defaults = ProfileRules.DefaultsFor(state.Profile, name);
            if (defaults.Count > 0)
            {
                var args = p.Arguments is null
                    ? new Dictionary<string, JsonElement>()
                    : new Dictionary<string, JsonElement>(p.Arguments);
                foreach (var (key, value, _) in defaults)
                    if (!args.ContainsKey(key)) args[key] = JsonSerializer.SerializeToElement(value);
                p.Arguments = args;
            }
        }

        var sw = Stopwatch.StartNew();
        CallToolResult result = visible
            ? await next(request, cancellationToken)
            : new CallToolResult { Content = McpContent.Error($"Unknown tool: {name}").ToList() };
        sw.Stop();

        if (state.Metrics is { } metrics)
        {
            metrics.Write(BuildCallRecord(state, request, result, name, sceneName, isGeneric, visible, argBytes, sw.ElapsedMilliseconds));

            // --metrics-search のときだけ、検索の問い合わせと上位の結果を別の行で残す（既定はオフ）。
            if (state.MetricsSearch && visible && name == "polyling_search")
                metrics.Write(BuildSearchRecord(state, request, result, sceneName));
        }

        // AI への案内（入門・きっかけ）を末尾に添える。計測の後に足すので、計測値は道具本体の量のまま。
        if (visible)
            GuideRules.Decorate(request.Server?.SessionId ?? string.Empty, name, request.Params?.Arguments, result);

        return result;
    }

    /// <summary>
    /// 検索の記録。問い合わせ文・利用シーン・返した候補の名前（返した順）。
    /// 集計（MetricsSummary）が、同じ課題で次に実行したコマンドと突き合わせる。
    /// </summary>
    private static JsonObject BuildSearchRecord(
        ServerState state, RequestContext<CallToolRequestParams> request, CallToolResult result, string sceneName)
    {
        var args = request.Params?.Arguments;
        string query = args is not null && args.TryGetValue("query", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString() ?? "" : "";
        string sceneArg = args is not null && args.TryGetValue("scene", out var sc) && sc.ValueKind == JsonValueKind.String
            ? sc.GetString() ?? "" : "";

        var names = new List<string>();
        var text = (result.Content ?? []).OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";
        if (text.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
                    foreach (var m in matches.EnumerateArray())
                    {
                        // 候補にはシナリオも混ざる（kind=scenario）。集計は次に実行したコマンドと
                        // 突き合わせるので、コマンドの候補だけを記録する。
                        if (m.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                            && k.GetString() == "scenario") continue;
                        if (m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            names.Add(n.GetString() ?? "");
                    }
            }
            catch (JsonException) { /* 結果が JSON でなければ候補なしとして残す */ }
        }

        return new JsonObject
        {
            ["profile"] = ProfileRules.Name(state.Profile),
            ["kind"]    = "search",
            ["taskId"]  = MetricsLog.TaskIdOf(request.Params?.Meta),
            // 検索の引数 scene を優先し、無ければ要求の利用シーン（_meta / --scene）。
            ["scene"]   = sceneArg.Length > 0 ? sceneArg : sceneName,
            ["query"]   = query,
            ["results"] = string.Join(",", names)
        };
    }

    private static JsonObject BuildCallRecord(
        ServerState state,
        RequestContext<CallToolRequestParams> request,
        CallToolResult result,
        string name,
        string sceneName,
        bool isGeneric,
        bool visible,
        int argBytes,
        long elapsedMs)
    {
        int textBytes = 0, imageBytes = 0, images = 0;
        bool ok = result.IsError != true;

        foreach (var block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock text:
                    textBytes += MetricsLog.Utf8Bytes(text.Text);
                    if (text.Text.StartsWith("ERROR:", StringComparison.Ordinal)) ok = false;
                    break;
                case ImageContentBlock image:
                    images++;
                    imageBytes += image.DecodedData.Length;
                    break;
            }
        }

        var arguments = request.Params?.Arguments;
        string polylingCommand = isGeneric
            ? name[ProfileRules.GenericToolPrefix.Length..]
            : name == "polyling_call" && arguments is not null && arguments.TryGetValue("command", out var c)
                && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;

        var record = new JsonObject
        {
            ["profile"]     = ProfileRules.Name(state.Profile),
            ["kind"]        = "call",
            ["taskId"]      = MetricsLog.TaskIdOf(request.Params?.Meta),
            ["scene"]       = sceneName,
            ["tool"]        = name,
            ["argBytes"]    = argBytes,
            ["resultBytes"] = SerializedBytes(result),
            ["textBytes"]   = textBytes,
            ["imageBytes"]  = imageBytes,
            ["images"]      = images,
            ["elapsedMs"]   = elapsedMs,
            ["ok"]          = ok
        };

        if (polylingCommand.Length > 0) record["polylingCommand"] = polylingCommand;
        if (!visible) record["hidden"] = true;

        return record;
    }

    /// <summary>
    /// この要求の利用シーン名。profile=optimized 以外は常に空。
    /// _meta の指定が優先で、無ければ --scene。
    /// </summary>
    private static string SceneFor(ServerState state, JsonObject? meta)
    {
        if (state.Profile != ServerProfile.Optimized) return string.Empty;
        var fromMeta = MetricsLog.SceneOf(meta);
        return fromMeta.Length > 0 ? fromMeta : state.DefaultScene;
    }

    /// <summary>JSON-RPC で送られる形に直したときの UTF-8 バイト数。</summary>
    private static int SerializedBytes<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions).Length;
}
