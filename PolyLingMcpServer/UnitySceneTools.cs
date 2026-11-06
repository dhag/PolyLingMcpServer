using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PolyLingMcpServer;

/// <summary>
/// 開いている Unity シーンのオブジェクト操作（空の GameObject・標準の形を置く、コンポーネントを付ける、値を書く…）。
///
/// 【道具は 1 本だけ】
///   action と平らな JSON の args を受け、Editor へ "scene_" + action として転送する。
///   操作の名前・引数・説明は Editor 側（Packages/com.haglib.polyling/Editor/HierarchyIO/SceneObjectOps.cs）が持ち、
///   action=help で返す。操作を足しても、この道具も tools/list も変わらない（MCP サーバの再ビルド不要）。
///   頭に "scene_" を付けるので、この道具から play・build_all など他の action は呼べない。
///
/// 【Play 中なら先に止める】
///   help 以外は UnityPrefabTools.RunInEditMode を通す（Play 中に作った GameObject は Play 終了で破棄されるため）。
///
/// 【接続先は常に Editor】
///   --target player でも Editor（8770）へ送る。unity_prefab_* と同じ（ServerTarget.cs）。
/// </summary>
[McpServerToolType]
public static class UnitySceneTools
{
    /// <summary>help の応答待ち。</summary>
    private const int HelpTimeoutMs = 10_000;

    /// <summary>action に使える文字。英小文字・数字・_ だけ。</summary>
    private static readonly Regex ActionPattern = new(@"^[a-z0-9_]+$");

    [McpServerTool(Name = "unity_scene_call", Destructive = true),
     Description("Edits the open Unity scene in the Editor: list the hierarchy, create an empty GameObject or a primitive (Cube, Sphere...), "
               + "add a component to a scene object, read and write component properties. "
               + "Call with action='help' first: it returns every action and its args. "
               + "Changes are registered with Undo and mark the scene dirty; the scene is not saved. "
               + "Except for help, Play mode is stopped first, because objects created in Play mode are destroyed when it ends.")]
    public static Task<string> UnitySceneCall(
        UiDispatcher ui,
        [Description("Action name from action='help', for example 'create_primitive'.")] string action,
        [Description("Arguments as a flat JSON object of string values, for example {\"type\":\"Cube\",\"position\":\"0,1,0\"}.")] string? args = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds.")] int timeout_ms = 60_000,
        CancellationToken cancellationToken = default)
    {
        string name = (action ?? "").Trim();
        if (name.StartsWith("scene_", StringComparison.Ordinal)) name = name.Substring("scene_".Length);

        if (!ActionPattern.IsMatch(name))
            return Task.FromResult("ERROR: action must be a name such as 'help' or 'create_primitive' (a-z, 0-9, _).");

        Dictionary<string, string> parameters;
        try
        {
            parameters = UnityProjectTools.FlattenArgs(args);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"ERROR: args is not a flat JSON object of string values: {ex.Message}");
        }

        string editorAction = "scene_" + name;

        if (name == "help")
            return SendHelp(editorAction, ui, cancellationToken);

        return UnityPrefabTools.RunInEditMode(editorAction, parameters, ui, timeout_ms, cancellationToken);
    }

    /// <summary>help は Play を止めずにそのまま送る。</summary>
    private static async Task<string> SendHelp(string editorAction, UiDispatcher ui, CancellationToken cancellationToken)
    {
        ui.Log($"unity_{editorAction}");
        try
        {
            var result = await EditorControlClient.SendToEditorAsync(editorAction, HelpTimeoutMs, null, cancellationToken);
            return result.Success
                ? result.Data
                : $"ERROR: the Unity Editor rejected '{editorAction}': {result.Error}";
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return ex.Stage == EditorControlClient.FailStage.Connect
                ? $"ERROR: could not connect to {EditorControlClient.EditorEndpoint}; {ex.InnerName}. Is the Unity Editor open?"
                : $"ERROR: connected, but no answer within {ex.ElapsedMs} ms; {ex.InnerName}. Is the Editor compiling or importing?";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"ERROR: the Unity Editor did not answer '{editorAction}' within {HelpTimeoutMs} ms.";
        }
        catch (Exception ex)
        {
            return $"ERROR: could not reach the Unity Editor on {EditorControlClient.EditorEndpoint}. {ex.Message}";
        }
    }
}
