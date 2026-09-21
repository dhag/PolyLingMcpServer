using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace PolyLingMcpServer;

/// <summary>
/// profile=optimized の段階的なコマンド発見。
///
///   polyling_search   … 目的に合うコマンドを名前と要約だけで返す（Editor の tools_search）
///   polyling_describe … 選んだコマンドだけ完全な入力・出力 Schema を返す（Editor の tools_describe）
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
     Description("Finds PolyLing commands for a purpose and returns only their names and one-line summaries. "
               + "Matches the command name, description, category, tags and argument descriptions; Japanese queries work. "
               + "Pass the chosen names to polyling_describe to get their arguments. "
               + "An empty query lists every command by name, page by page. Requires the PolyLing panel to be open.")]
    public static Task<string> PolyLingSearch(
        UiDispatcher ui,
        [Description("What you want to do, in words, for example '選択した辺から板を作る' or 'mirror bones'. Empty lists all commands.")] string query = "",
        [Description("Optional category to restrict the search to. Categories are hierarchical: \"geometry\" also matches \"geometry.topology\".")] string category = "",
        [Description("Optional usage scene (利用シーン) name from polyling_scenes. Only the commands that scene covers are searched, "
                   + "its tags raise matching commands in the ranking, commands whose hazards it hides are left out, and matches it "
                   + "warns about carry a policy field. The result warns when the model state differs from what the scene assumes. Not a Unity scene.")] string scene = "",
        [Description("Number of matches to skip, for paging.")] int offset = 0,
        [Description("Maximum number of matches to return. 1 to 50.")] int limit = 8,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 10000,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["query"]    = query ?? string.Empty,
            ["category"] = category ?? string.Empty,
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
               + "with their write scope. Use the names returned by polyling_search. At most 10 commands per call. "
               + "Requires the PolyLing panel to be open.")]
    public static Task<string> PolyLingDescribe(
        UiDispatcher ui,
        [Description("Command names, for example [\"lineExtrude\", \"queryDrawableStats\"].")] string[] commands,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 10000,
        CancellationToken cancellationToken = default)
    {
        var names = (commands ?? [])
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
            return Task.FromResult("ERROR: commands must contain at least one command name.");
        if (names.Length > 10)
            return Task.FromResult($"ERROR: at most 10 commands per call ({names.Length} given).");
        if (names.Any(n => n.Contains(',')))
            return Task.FromResult("ERROR: a command name must not contain ','.");

        var parameters = new Dictionary<string, string> { ["names"] = string.Join(",", names) };
        return SendForResult("tools_describe", parameters, ui, timeout_ms, cancellationToken);
    }

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
