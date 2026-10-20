using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PolyLingMcpServer;

/// <summary>
/// Unity Editor のプレファブ操作（書き出し・ヒエラルキーへの登録・取り込み・コンポーネントの付与）。
///
/// 【Play 中なら先に止める】
///   Editor 側（PolyLingEditorControlServer の prefab_*）は Play 中の要求を拒否する。
///   Play 中に作った GameObject は Play 終了で破棄されるため。
///   ここでは state を読み、Play 中なら stop を送り、
///   isPlaying / isPlayingOrWillChangePlaymode / isCompiling / isUpdating が
///   すべて false になるまで待ってから要求を送る。
///   待ち方は UnityProjectTools.SendEditorActionAndWait と同じで、
///   ドメインリロード中の接続失敗・応答なしは失敗とせず上限まで待つ。
///
/// 【パス】
///   プロジェクトファイル側（model_folder / output_folder）は PolyLing の作業フォルダ基準で、
///   Editor 側が PLSandbox で解決する。プレファブ側は Assets/ 以下のアセットパス。
///
/// 【接続先は常に Editor】
///   --target player でも EditorControlClient.SendToEditorAsync で Editor（8770）へ送る。
///   ビルド版で作ったファイルを Editor 拡張でプレファブにする使い方があるため、
///   player のときもこの 4 つは隠さない（ServerTarget.cs）。
/// </summary>
[McpServerToolType]
public static class UnityPrefabTools
{
    /// <summary>state を読み直す間隔。</summary>
    private const int PollIntervalMs = 500;

    /// <summary>state 1 回あたりの応答待ち。リロード中は返らないので長く待たない。</summary>
    private const int PollReplyMs = 3000;

    /// <summary>stop の受付待ち。受付だけなのですぐ返る。</summary>
    private const int StopAcceptTimeoutMs = 10_000;

    private static readonly Regex IsPlayingPattern   = new(@"\bisPlaying=(true|false)");
    private static readonly Regex WillChangePattern  = new(@"\bisPlayingOrWillChangePlaymode=(true|false)");
    private static readonly Regex IsCompilingPattern = new(@"\bisCompiling=(true|false)");
    private static readonly Regex IsUpdatingPattern  = new(@"\bisUpdating=(true|false)");

    [McpServerTool(Name = "unity_prefab_export", Destructive = true),
     Description("Writes a PolyLing project file (folder format) out as a Unity prefab, the same as the Hierarchy Export window with 'save as prefab' on. "
               + "A model folder (one with model.csv) exports that model; its parent folder exports every model under it. "
               + "The prefab, meshes, materials, textures and optional Avatar go to <output_root>/[<project name>/]<model name>/, overwriting the previous output. "
               + "If the Editor is in Play mode it is stopped first, and the tool waits for Edit mode before exporting. "
               + "Options left out use the window's defaults.")]
    public static Task<string> UnityPrefabExport(
        UiDispatcher ui,
        [Description("Model folder or project folder, relative to the PolyLing work folder.")] string model_folder,
        [Description("Destination root under Assets/, for example Assets/PolyLing. Defaults to Assets/PolyLing.")] string? output_root = null,
        [Description("Create the Armature bone hierarchy. Default true.")] bool? create_armature = null,
        [Description("Use each mesh's BindPose for bindposes. Default true.")] bool? use_bindpose = null,
        [Description("Export visible meshes only. Default true.")] bool? export_visible_only = null,
        [Description("When exporting visible meshes only, keep invisible parents as Transform-only nodes. Default true.")] bool? include_invisible_ancestors = null,
        [Description("Leave bones out and export meshes only. Default false.")] bool? export_mesh_only = null,
        [Description("Export rigid bodies and joints as Unity physics components. Default true.")] bool? export_physics = null,
        [Description("Build a Humanoid Avatar asset alongside the prefab. Default true.")] bool? build_avatar = null,
        [Description("Put an Animator on the root and assign the built Avatar. Default true.")] bool? attach_animator = null,
        [Description("Fill missing required Humanoid joints with dummy objects. Default false.")] bool? supplement_humanoid = null,
        [Description("Write IK attachments to attach.csv next to the prefab. Default true.")] bool? write_attach = null,
        [Description("Tolerate objects under a mirror branch that lack their own mirror setting. Default true.")] bool? tolerant_mirror_branch = null,
        [Description("Auto (skinned when the mesh has bone weights) or ForceMeshFilter. Default Auto.")] string? renderer_mode = null,
        [Description("Optional Animator Controller asset path under Assets/ to set on the root Animator.")] string? animator_controller = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds.")] int timeout_ms = 300_000,
        CancellationToken cancellationToken = default)
    {
        var p = new Dictionary<string, string> { ["modelFolder"] = model_folder ?? "" };

        AddString(p, "outputRoot",                output_root);
        AddBool  (p, "createArmature",            create_armature);
        AddBool  (p, "useBindpose",               use_bindpose);
        AddBool  (p, "exportVisibleOnly",         export_visible_only);
        AddBool  (p, "includeInvisibleAncestors", include_invisible_ancestors);
        AddBool  (p, "exportMeshOnly",            export_mesh_only);
        AddBool  (p, "exportPhysics",             export_physics);
        AddBool  (p, "buildAvatar",               build_avatar);
        AddBool  (p, "attachAnimator",            attach_animator);
        AddBool  (p, "supplementHumanoid",        supplement_humanoid);
        AddBool  (p, "writeAttach",               write_attach);
        AddBool  (p, "tolerantMirrorBranch",      tolerant_mirror_branch);
        AddString(p, "rendererMode",              renderer_mode);
        AddString(p, "animatorController",        animator_controller);

        return RunInEditMode("prefab_export", p, ui, timeout_ms, cancellationToken);
    }

    [McpServerTool(Name = "unity_prefab_instantiate", Destructive = true),
     Description("Places a prefab asset into the open scene's hierarchy as a connected prefab instance, with Undo, and marks the scene dirty (the scene is not saved). "
               + "If the Editor is in Play mode it is stopped first, because objects created in Play mode are destroyed when Play mode ends.")]
    public static Task<string> UnityPrefabInstantiate(
        UiDispatcher ui,
        [Description("Prefab asset path, for example Assets/PolyLing/MyProject/MyModel/MyModel.prefab.")] string prefab_path,
        [Description("Optional parent in the hierarchy as 'Root/Child'. Inactive objects are found too. Empty places it at the root of the active scene.")] string? parent_path = null,
        [Description("Optional local position as 'x,y,z'.")] string? position = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds.")] int timeout_ms = 60_000,
        CancellationToken cancellationToken = default)
    {
        var p = new Dictionary<string, string> { ["prefabPath"] = prefab_path ?? "" };

        AddString(p, "parentPath", parent_path);
        AddString(p, "position",   position);

        return RunInEditMode("prefab_instantiate", p, ui, timeout_ms, cancellationToken);
    }

    [McpServerTool(Name = "unity_prefab_import", Destructive = true),
     Description("Reads a prefab asset and saves it as a PolyLing project file (folder format), the same as the Hierarchy Import window. "
               + "The Humanoid mapping is restored from the Avatar, and IK attachments from attach.csv next to the prefab when present. "
               + "If the Editor is in Play mode it is stopped first.")]
    public static Task<string> UnityPrefabImport(
        UiDispatcher ui,
        [Description("Prefab asset path under Assets/.")] string prefab_path,
        [Description("Destination model folder, relative to the PolyLing work folder.")] string output_folder,
        [Description("Treat names ending in '+' as mirror copies. Default true.")] bool? detect_named_mirror = null,
        [Description("Restore the Humanoid mapping and limits from the Avatar. Default true.")] bool? restore_humanoid = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds.")] int timeout_ms = 300_000,
        CancellationToken cancellationToken = default)
    {
        var p = new Dictionary<string, string>
        {
            ["prefabPath"]   = prefab_path ?? "",
            ["outputFolder"] = output_folder ?? "",
        };

        AddBool(p, "detectNamedMirror", detect_named_mirror);
        AddBool(p, "restoreHumanoid",   restore_humanoid);

        return RunInEditMode("prefab_import", p, ui, timeout_ms, cancellationToken);
    }

    [McpServerTool(Name = "unity_prefab_add_component", Destructive = true),
     Description("Adds a component to an object in a prefab asset and saves the prefab (PrefabUtility.LoadPrefabContents → AddComponent → SaveAsPrefabAsset). "
               + "Use this instead of editing .prefab or .meta files, which the file tools cannot write. "
               + "If the same component type is already there, nothing changes unless allow_multiple is true. "
               + "A script written just now is not a type until it is compiled: run unity_validate_changes first. "
               + "If the Editor is in Play mode it is stopped first.")]
    public static Task<string> UnityPrefabAddComponent(
        UiDispatcher ui,
        [Description("Prefab asset path under Assets/, for example Assets/MyModel/MyModel.prefab.")] string prefab_path,
        [Description("Component type: full name (Namespace.Type) or short name (Type). Ambiguous short names fail with the candidates listed.")] string component_type,
        [Description("Optional object inside the prefab as 'Child/Grandchild', relative to the prefab root (do not include the root's name). Empty means the root. A bare name without '/' that is not a direct path is looked up among all descendants; it must be unique.")] string? object_path = null,
        [Description("Add another one even if the type is already on the object. Default false.")] bool? allow_multiple = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds.")] int timeout_ms = 60_000,
        CancellationToken cancellationToken = default)
    {
        var p = new Dictionary<string, string>
        {
            ["prefabPath"]    = prefab_path ?? "",
            ["componentType"] = component_type ?? "",
        };

        AddString(p, "objectPath",    object_path);
        AddBool  (p, "allowMultiple", allow_multiple);

        return RunInEditMode("prefab_add_component", p, ui, timeout_ms, cancellationToken);
    }

    private static void AddString(Dictionary<string, string> p, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) p[key] = value;
    }

    private static void AddBool(Dictionary<string, string> p, string key, bool? value)
    {
        if (value.HasValue) p[key] = value.Value ? "true" : "false";
    }

    /// <summary>
    /// Edit モードで待機状態にしてから action を送る。
    /// Play 中なら stop を送る。
    /// </summary>
    internal static async Task<string> RunInEditMode(
        string action,
        Dictionary<string, string> parameters,
        UiDispatcher ui,
        int timeout_ms,
        CancellationToken cancellationToken)
    {
        timeout_ms = Math.Clamp(timeout_ms, 5000, 1_800_000);
        ui.Log($"unity_{action} timeout={timeout_ms}ms");

        var sw = Stopwatch.StartNew();

        // ---- 現在の状態 ----
        EditorControlResult first;
        try
        {
            first = await EditorControlClient.SendToEditorAsync("state", PollReplyMs, null, cancellationToken);
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return StageMessage(ex, PollReplyMs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"ERROR: the Unity Editor did not answer 'state' within {PollReplyMs} ms.";
        }
        catch (Exception ex)
        {
            return $"ERROR: could not reach the Unity Editor on {EditorControlClient.EditorEndpoint}. Is it open? {ex.Message}";
        }

        if (!first.Success)
            return $"ERROR: the Unity Editor rejected 'state': {first.Error}";

        if (!TryParseState(first.Data, out bool playing, out bool willChange, out bool compiling, out bool updating))
            return $"ERROR: the Unity Editor state has no isPlaying / isPlayingOrWillChangePlaymode / isCompiling / isUpdating. "
                 + $"state: {first.Data}";

        // ---- Play 中なら止める ----
        bool stopped = false;
        if (playing || willChange)
        {
            EditorControlResult stop;
            try
            {
                stop = await EditorControlClient.SendToEditorAsync("stop", StopAcceptTimeoutMs, null, cancellationToken);
            }
            catch (EditorControlClient.EditorControlException ex)
            {
                return StageMessage(ex, StopAcceptTimeoutMs);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return $"ERROR: the Unity Editor did not answer 'stop' within {StopAcceptTimeoutMs} ms.";
            }

            if (!stop.Success)
                return $"ERROR: the Unity Editor rejected 'stop' before '{action}': {stop.Error}";

            stopped = true;
        }

        // ---- Edit モードで待機状態になるまで待つ ----
        string lastState   = first.Data;
        string lastProblem = "(none)";
        bool   ready       = !(playing || willChange || compiling || updating);

        while (!ready)
        {
            if (sw.ElapsedMilliseconds >= timeout_ms)
                return $"ERROR: the Unity Editor did not reach idle Edit mode within {timeout_ms} ms before '{action}'. "
                     + $"last state: {lastState}; last problem: {lastProblem}";

            await Task.Delay(PollIntervalMs, cancellationToken);

            EditorControlResult polled;
            try
            {
                polled = await EditorControlClient.SendToEditorAsync("state", PollReplyMs, null, cancellationToken);
            }
            catch (EditorControlClient.EditorControlException ex)
            {
                // ドメインリロード中（接続不可）かメインスレッドが塞がっている（応答なし）。待ち続ける。
                lastProblem = $"{ex.Stage}: {ex.InnerName} after {ex.ElapsedMs} ms";
                continue;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastProblem = "state timed out";
                continue;
            }

            if (!polled.Success)
            {
                lastProblem = polled.Error;
                continue;
            }

            lastState = polled.Data;

            if (!TryParseState(polled.Data, out playing, out willChange, out compiling, out updating))
                return $"ERROR: the Unity Editor state has no isPlaying / isPlayingOrWillChangePlaymode / isCompiling / isUpdating. "
                     + $"state: {polled.Data}";

            ready = !(playing || willChange || compiling || updating);
        }

        // ---- 本体 ----
        int remaining = (int)Math.Max(1000, timeout_ms - sw.ElapsedMilliseconds);
        string prefix = stopped ? "Play mode was stopped first. " : "";

        try
        {
            var result = await EditorControlClient.SendToEditorAsync(action, remaining, parameters, cancellationToken);

            if (!result.Success)
                return $"ERROR: {prefix}the Unity Editor rejected '{action}': {result.Error}";

            return $"OK: {prefix}{action} finished in {sw.ElapsedMilliseconds} ms.\n{result.Data}";
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return prefix + StageMessage(ex, remaining);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"ERROR: {prefix}the Unity Editor did not answer '{action}' within {remaining} ms.";
        }
        catch (Exception ex)
        {
            return $"ERROR: {prefix}could not reach the Unity Editor on {EditorControlClient.EditorEndpoint}. {ex.Message}";
        }
    }

    private static bool TryParseState(
        string state, out bool playing, out bool willChange, out bool compiling, out bool updating)
    {
        playing = willChange = compiling = updating = false;

        var mp = IsPlayingPattern.Match(state ?? "");
        var mw = WillChangePattern.Match(state ?? "");
        var mc = IsCompilingPattern.Match(state ?? "");
        var mu = IsUpdatingPattern.Match(state ?? "");

        if (!mp.Success || !mw.Success || !mc.Success || !mu.Success) return false;

        playing    = mp.Groups[1].Value == "true";
        willChange = mw.Groups[1].Value == "true";
        compiling  = mc.Groups[1].Value == "true";
        updating   = mu.Groups[1].Value == "true";
        return true;
    }

    private static string StageMessage(EditorControlClient.EditorControlException ex, int timeoutMs)
        => ex.Stage == EditorControlClient.FailStage.Connect
            ? $"ERROR: could not connect to {EditorControlClient.EditorEndpoint} within {ex.ElapsedMs} ms (limit {timeoutMs} ms); {ex.InnerName}. Is the Unity Editor open?"
            : $"ERROR: connected, but no answer within {ex.ElapsedMs} ms (limit {timeoutMs} ms); {ex.InnerName}. Is the Editor compiling or importing?";
}
