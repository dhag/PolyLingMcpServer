using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PolyLingMcpServer;

/// <summary>
/// profile=generic の「1 コマンド 1 道具」。
///
/// 【形】
///   PolyLing の全コマンドを pl_&lt;コマンド名&gt; の道具として tools/list に並べる。
///   一般的な MCP サーバ（機能ごとに道具を 1 本ずつ公開する）と同じ形にするため、
///   各道具の inputSchema は PolyLing が生成したものをそのまま使い、
///   model_index（既定 0）だけを足す。polyling_call の model_index と同じ役割。
///
/// 【outputSchema を付けない理由】
///   MCP 仕様では outputSchema を宣言した道具は structuredContent を返さなければならない。
///   このサーバは polyling_call と同じテキスト応答を返すので、宣言しない。
///
/// 【Unity が応答しないとき】
///   tools/list は固定の道具だけを返す（pl_ は 0 本）。理由は stderr と計測ログに残す。
///   Unity を起動してから接続し直すと pl_ が並ぶ。
///
/// 【実行】
///   polyling_call と同じ経路（Editor の "call"）で実行する。違いは引数の受け方だけで、
///   Schema が配列を宣言しているので JSON 配列をカンマ区切りの文字列へ直して渡す。
/// </summary>
public static class GenericCommandTools
{
    /// <summary>tools_generic の応答待ち。全コマンドの Schema を組むので長めにとる。</summary>
    private const int ListTimeoutMs = 15000;

    /// <summary>1 コマンドの実行の応答待ち。polyling_call の既定と同じ。</summary>
    private const int CallTimeoutMs = 30000;

    /// <summary>Anthropic API が受け付ける道具名・引数名。合わないものは除外して記録する。</summary>
    private static readonly Regex ToolNamePattern     = new(@"^[a-zA-Z0-9_-]{1,64}$");
    private static readonly Regex PropertyNamePattern = new(@"^[a-zA-Z0-9_.-]{1,64}$");

    private const string ModelIndexKey = "model_index";

    /// <summary>最後に組み立てた一覧の要約。計測ログに載せる。</summary>
    public sealed record ListOutcome(int Added, int Rejected, string SchemaRevision, string Problem);

    public static async ValueTask<ListToolsResult> ListAsync(
        RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        var state = request.Services!.GetRequiredService<ServerState>();
        var result = new ListToolsResult { Tools = new List<Tool>() };

        if (state.Profile != ServerProfile.Generic) return result;

        var outcome = await FillAsync(result.Tools, cancellationToken);
        state.LastGenericList = outcome;

        if (outcome.Problem.Length > 0)
            await Console.Error.WriteLineAsync($"generic tools/list: {outcome.Problem}");

        return result;
    }

    private static async Task<ListOutcome> FillAsync(IList<Tool> tools, CancellationToken cancellationToken)
    {
        EditorControlResult reply;
        try
        {
            reply = await EditorControlClient.SendAsync("tools_generic", ListTimeoutMs, null, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ListOutcome(0, 0, string.Empty, $"Unity Editor unreachable ({ex.GetType().Name}); pl_ tools are not listed.");
        }

        if (!reply.Success)
            return new ListOutcome(0, 0, string.Empty, $"tools_generic rejected: {reply.Error}");

        using var doc = JsonDocument.Parse(reply.Raw);
        if (!doc.RootElement.TryGetProperty("result", out var body) ||
            !body.TryGetProperty("tools", out var list) || list.ValueKind != JsonValueKind.Array)
            return new ListOutcome(0, 0, string.Empty, "tools_generic returned no result.tools. Is PolyLingEditorControlServer.cs up to date?");

        string revision = body.TryGetProperty("schemaRevision", out var rev) ? rev.GetString() ?? "" : "";

        int added = 0, rejected = 0;
        var rejectedNames = new List<string>();

        foreach (var item in list.EnumerateArray())
        {
            string command = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string name = ProfileRules.GenericToolPrefix + command;

            if (command.Length == 0 || !ToolNamePattern.IsMatch(name) ||
                !item.TryGetProperty("inputSchema", out var inputSchema) ||
                JsonNode.Parse(inputSchema.GetRawText()) is not JsonObject schema)
            {
                rejected++;
                rejectedNames.Add(command);
                continue;
            }

            var properties = schema["properties"] as JsonObject ?? new JsonObject();
            schema["properties"] = properties;

            if (properties.Any(p => !PropertyNamePattern.IsMatch(p.Key)) || properties.ContainsKey(ModelIndexKey))
            {
                rejected++;
                rejectedNames.Add(command);
                continue;
            }

            properties[ModelIndexKey] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Target model index. Defaults to 0."
            };

            tools.Add(new Tool
            {
                Name        = name,
                Description = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                InputSchema = JsonSerializer.SerializeToElement(schema)
            });
            added++;
        }

        string problem = rejected > 0
            ? $"{rejected} command(s) not listed because the name or an argument name does not fit the Anthropic API pattern: {string.Join(", ", rejectedNames)}"
            : string.Empty;

        return new ListOutcome(added, rejected, revision, problem);
    }

    public static async ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var state = request.Services!.GetRequiredService<ServerState>();
        var ui    = request.Services!.GetRequiredService<UiDispatcher>();
        string name = request.Params?.Name ?? string.Empty;

        if (state.Profile != ServerProfile.Generic || !name.StartsWith(ProfileRules.GenericToolPrefix, StringComparison.Ordinal))
            return ErrorResult($"Unknown tool: {name}");

        string command = name[ProfileRules.GenericToolPrefix.Length..];
        var parameters = new Dictionary<string, string>();
        int modelIndex = 0;

        try
        {
            if (request.Params?.Arguments is { } args)
            {
                foreach (var kv in args)
                {
                    if (kv.Key == ModelIndexKey)
                    {
                        if (!int.TryParse(UnityProjectTools.ScalarText(kv.Key, kv.Value, allowArray: false), out modelIndex))
                            return ErrorResult($"{ModelIndexKey} must be an integer.");
                        continue;
                    }
                    parameters[kv.Key] = UnityProjectTools.ScalarText(kv.Key, kv.Value, allowArray: true);
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            return ErrorResult(ex.Message);
        }

        parameters["command"]    = command;
        parameters["modelIndex"] = modelIndex.ToString();

        // uiCapture で保存先を省いたら、キャプチャフォルダへ撮らせる（UnityCaptureTools）。
        UnityCaptureTools.ApplyDefaultCaptureFolder(state, command, parameters);

        // 応答の形は polyling_call と同じにする（失敗も "ERROR: " 始まりのテキストで返し、
        // isError は立てない）。条件の違いを「道具の見せ方」だけに保つため。
        var blocks = await UnityProjectTools.SendEditorRaw("call", parameters, ui, CallTimeoutMs, cancellationToken);
        return new CallToolResult { Content = blocks.ToList() };
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        Content = McpContent.Error(message).ToList()
    };
}
