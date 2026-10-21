using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace PolyLingMcpServer;

/// <summary>
/// profile=optimized の段階的なコマンド発見。
///
///   polyling_search   … 目的に合うコマンドとシナリオを名前と要約だけで返す（Editor の tools_search）
///                       両者は 1 本の列で、offset / limit が共通（シナリオが増えても応答は limit 件まで）
///   polyling_describe … 選んだコマンドの完全な入力・出力 Schema と、選んだシナリオの中身を返す（Editor の tools_describe）
///   polyling_scenes   … 利用シーンの定義を返す（Editor の scenes）
///
/// 照合・Schema の組み立ては PolyLing 側（PanelCommandSchemaIndex.cs）が行う。
/// ここはコマンドの台帳を持たず、中継するだけ。
///
/// 他の条件では tools/list に出ない（ProfileRules.IsFixedToolVisible）。
/// </summary>
[McpServerToolType]
public static class ProgressiveTools
{
    [McpServerTool(Name = "polyling_search", ReadOnly = true),
     Description("Finds PolyLing commands and scenarios (saved multi-step procedures) for a purpose and returns only their names and one-line summaries. "
               + "Commands and scenarios come back in one ranked list; each match has kind \"command\" or \"scenario\", "
               + "and offset / limit / total / hasMore apply to the whole list. "
               + "Commands match on name, description, category, tags and argument descriptions; scenarios on name, goal, tags, "
               + "preconditions, success criteria and the commands and purposes of their items. Japanese queries work. "
               + "Pass the chosen command names as commands, and scenario names as scenarios, to polyling_describe. "
               + "An empty query lists everything by name, page by page. Requires the PolyLing panel to be open.")]
    public static Task<string> PolyLingSearch(
        UiDispatcher ui,
        [Description("What you want to do, in words, for example '選択した辺から板を作る' or 'mirror bones'. Empty lists everything.")] string query = "",
        [Description("Optional: \"command\" returns only commands, \"scenario\" only scenarios. Empty returns both.")] string kind = "",
        [Description("Optional command category to restrict the search to. Categories are hierarchical: \"geometry\" also matches \"geometry.topology\". "
                   + "Scenarios are left out when this is set.")] string category = "",
        [Description("Optional scenario folder to restrict the search to. Folders are hierarchical: \"a\" also matches \"a/b\". "
                   + "Commands are left out when this is set.")] string folder = "",
        [Description("Optional usage scene (利用シーン) name from polyling_scenes. Only the commands that scene covers are searched, "
                   + "its tags raise matching commands in the ranking, commands whose hazards it hides are left out, and matches it "
                   + "warns about carry a policy field. Scenarios are limited to those whose items belong to the scene or that the scene lists as related. "
                   + "The result warns when the model state differs from what the scene assumes. Not a Unity scene.")] string scene = "",
        [Description("Number of matches to skip, for paging.")] int offset = 0,
        [Description("Maximum number of matches to return. 1 to 50.")] int limit = 8,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 10000,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["query"]    = query ?? string.Empty,
            ["kind"]     = kind ?? string.Empty,
            ["category"] = category ?? string.Empty,
            ["folder"]   = folder ?? string.Empty,
            ["scene"]    = scene ?? string.Empty,
            ["offset"]   = Math.Max(0, offset).ToString(),
            ["limit"]    = Math.Clamp(limit, 1, 50).ToString()
        };

        return SendForResult("tools_search", parameters, ui, timeout_ms, cancellationToken);
    }

    [McpServerTool(Name = "polyling_scenes", ReadOnly = true),
     Description("A usage scene (利用シーン) is a named preset for one phase of modeling work, such as skinning or morph editing. It sits between a scenario (a whole procedure) and a single command. It is not a Unity scene. "
               + "Lists the usage scenes defined in PolyLing: name, description, the commands and categories it covers, "
               + "the tags it ranks higher, the fixed tools it shows (empty = all), the commands it excludes, "
               + "the model state it assumes, how it treats each hazard (allow / warn / require-confirmation / hide), "
               + "what to verify after running commands, related scenarios, and notes. "
               + "Also lists the valid category, state, hazard and verification names for setScene. "
               + "Pass a usage scene name to polyling_search to search only its commands. "
               + "Scenes are created and changed with the PolyLing commands setScene and deleteScene.")]
    public static Task<string> PolyLingScenes(
        UiDispatcher ui,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 10000,
        CancellationToken cancellationToken = default)
        => SendForResult("scenes", new Dictionary<string, string>(), ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "polyling_describe", ReadOnly = true),
     Description("Returns the full argument (input) and result (output) JSON Schema of the named PolyLing commands, "
               + "with their write scope, and the contents of the named scenarios (goal, preconditions, success criteria, "
               + "declared parameters (paramNames / paramDefaults / paramDescriptions), "
               + "and each item's kind, command, purpose and argument names, the same as the describeScenario command). "
               + "A scenario's parameters are passed to runScenario as paramKeys / paramValues; omitted ones use their defaults. "
               + "Scenario argument values are left out by default (detail=outline) because they can hold long data such as vertex coordinates; "
               + "running a scenario with runScenario does not need them. To edit items, use detail=full with items set to the item IDs you need. "
               + "Use the names returned by polyling_search: kind \"command\" goes in commands, kind \"scenario\" in scenarios. "
               + "At most 10 names per call, commands and scenarios together. Requires the PolyLing panel to be open.")]
    public static Task<string> PolyLingDescribe(
        UiDispatcher ui,
        [Description("Command names, for example [\"lineExtrude\", \"queryDrawableStats\"]. May be empty when scenarios is given.")] string[]? commands = null,
        [Description("Scenario names from polyling_search matches of kind \"scenario\". May be empty when commands is given.")] string[]? scenarios = null,
        [Description("If true, scenario items that call other scenarios are followed and returned as one flat list of items. Cannot be combined with items.")] bool resolve = false,
        [Description("Scenario detail: \"outline\" (default) leaves out argument values; \"full\" includes them.")] string detail = "outline",
        [Description("Scenario item IDs (scenarioItemIds) to return; empty returns every item. Only when exactly one scenario is named, and not with resolve.")] string[]? items = null,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 10000,
        CancellationToken cancellationToken = default)
    {
        var names = CleanNames(commands);
        var scenarioNames = CleanNames(scenarios);
        var itemIds = CleanNames(items);

        if (itemIds.Any(n => n.Contains(',')))
            return Task.FromResult("ERROR: an item ID must not contain ','.");

        if (names.Length == 0 && scenarioNames.Length == 0)
            return Task.FromResult("ERROR: give at least one name in commands or scenarios.");
        if (names.Length + scenarioNames.Length > 10)
            return Task.FromResult($"ERROR: at most 10 names per call, commands and scenarios together ({names.Length + scenarioNames.Length} given).");
        if (names.Any(n => n.Contains(',')))
            return Task.FromResult("ERROR: a command name must not contain ','.");
        if (scenarioNames.Any(n => n.Contains(',')))
            return Task.FromResult("ERROR: a scenario name containing ',' cannot be described here; use polyling_call with describeScenario.");

        var parameters = new Dictionary<string, string>
        {
            ["names"]     = string.Join(",", names),
            ["scenarios"] = string.Join(",", scenarioNames),
            ["resolve"]   = resolve ? "true" : "false",
            ["detail"]    = detail ?? string.Empty,
            ["items"]     = string.Join(",", itemIds)
        };
        return SendForResult("tools_describe", parameters, ui, timeout_ms, cancellationToken);
    }

    /// <summary>空白を除き、空と重複を外す。</summary>
    private static string[] CleanNames(string[]? raw)
        => (raw ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Editor の action を送り、応答の "result" オブジェクトだけを文字列で返す。
    /// 失敗は他の道具と同じく "ERROR: " 始まり。
    /// </summary>
    private static async Task<string> SendForResult(
        string action,
        IReadOnlyDictionary<string, string> parameters,
        UiDispatcher ui,
        int timeout_ms,
        CancellationToken cancellationToken)
    {
        ui.Log($"polyling_{action} timeout={timeout_ms}ms");

        try
        {
            var reply = await EditorControlClient.SendAsync(action, timeout_ms, parameters, cancellationToken);
            if (!reply.Success)
                return $"ERROR: the Unity Editor rejected '{action}': {reply.Error}";

            using var doc = JsonDocument.Parse(reply.Raw);
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return $"ERROR: the Unity Editor answered '{action}' without a result. "
                     + "Is PolyLingEditorControlServer.cs up to date?";

            return result.GetRawText();
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return UnityProjectTools.StageMessage(ex, timeout_ms);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UnityProjectTools.TimeoutMessage(timeout_ms);
        }
        catch (Exception ex)
        {
            return $"ERROR: could not reach the Unity Editor on {EditorControlClient.Endpoint}. "
                   + $"Is it open? {ex.Message}";
        }
    }
}
