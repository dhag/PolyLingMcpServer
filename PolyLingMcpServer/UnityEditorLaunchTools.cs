using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace PolyLingMcpServer;

/// <summary>
/// Unity Editor の起動（unity_editor_launch）。
///
/// 【起動する版】
///   ProjectSettings/ProjectVersion.txt の m_EditorVersion。
///
/// 【Unity.exe の探し方（前から順に、最初に存在したもの）】
///   1. 起動引数 --unity-editor で与えた Unity.exe（ServerState.UnityEditorPath）
///   2. Unity Hub の追加インストール先 %APPDATA%\UnityHub\secondaryInstallPath.json の下の
///      &lt;版&gt;\Editor\Unity.exe
///   3. C:\Program Files\Unity\Hub\Editor\&lt;版&gt;\Editor\Unity.exe（Hub の既定）
///   どれも無ければ、試したパスを全部並べてエラーにする。
///
/// 【二重起動の防止】
///   Temp/UnityLockfile を排他で開けなければ、このプロジェクトは既に開かれているとみなす。
///
/// 【応答】
///   起動を投げたところで返す（Editor の読み込み完了は待たない）。
///   待ち受け（8770）が立ったかどうかは unity_state で確かめる。
/// </summary>
[McpServerToolType]
public static class UnityEditorLaunchTools
{
    private const string VersionFile = "ProjectSettings/ProjectVersion.txt";
    private const string LockFile = "Temp/UnityLockfile";

    [McpServerTool(Name = "unity_editor_launch", Destructive = false),
     Description("Starts the Unity Editor with this project (Unity.exe -projectPath <project>). "
               + "The Editor version comes from ProjectSettings/ProjectVersion.txt; Unity.exe is looked up from --unity-editor, "
               + "the Unity Hub secondary install folder, then C:\\Program Files\\Unity\\Hub\\Editor. "
               + "Fails if the project is already open. The reply comes back as soon as the process starts; "
               + "loading takes a while, so call unity_state until it answers before using other Unity tools.")]
    public static string UnityEditorLaunch(ServerState state, UiDispatcher ui)
    {
        if (string.IsNullOrEmpty(state.ProjectRoot))
            return "ERROR: no Unity project folder is configured.";

        string project = Path.GetFullPath(state.ProjectRoot);

        if (IsProjectOpen(project))
            return $"ERROR: the project is already open in a Unity Editor ({LockFile} is locked): {project}";

        if (!TryReadEditorVersion(project, out var version, out var versionError))
            return versionError;

        var tried = new List<string>();
        string? unityExe = FindUnityExe(state, version, tried);
        if (unityExe == null)
            return $"ERROR: Unity.exe for version {version} was not found. Tried: {string.Join("; ", tried)}. "
                 + "Start the MCP server with --unity-editor <path to Unity.exe>.";

        try
        {
            var psi = new ProcessStartInfo(unityExe)
            {
                UseShellExecute  = false,
                WorkingDirectory = project,
            };
            psi.ArgumentList.Add("-projectPath");
            psi.ArgumentList.Add(project);

            using var proc = Process.Start(psi);
            if (proc == null) return $"ERROR: Unity did not start: {unityExe}";

            ui.Log($"unity_editor_launch pid={proc.Id} {unityExe} -projectPath {project}");
            return $"OK: started pid={proc.Id}; unity={unityExe}; version={version}; project={project}. "
                 + "Call unity_state until it answers before using other Unity tools.";
        }
        catch (Exception ex)
        {
            return $"ERROR: could not start {unityExe}: {ex.Message}";
        }
    }

    /// <summary>Temp/UnityLockfile が他のプロセスに握られているか。</summary>
    private static bool IsProjectOpen(string project)
    {
        string lockPath = Path.Combine(project, LockFile);
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>ProjectVersion.txt の m_EditorVersion を読む。</summary>
    private static bool TryReadEditorVersion(string project, out string version, out string error)
    {
        version = ""; error = "";
        string path = Path.Combine(project, VersionFile);
        if (!File.Exists(path))
        {
            error = $"ERROR: {VersionFile} not found: {path}";
            return false;
        }

        foreach (var line in File.ReadAllLines(path))
        {
            const string key = "m_EditorVersion:";
            if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
            version = line[key.Length..].Trim();
            if (version.Length > 0) return true;
        }

        error = $"ERROR: m_EditorVersion is missing in {path}";
        return false;
    }

    /// <summary>候補を順に試し、最初に存在した Unity.exe を返す。試したパスは tried に積む。</summary>
    private static string? FindUnityExe(ServerState state, string version, List<string> tried)
    {
        foreach (var candidate in Candidates(state, version))
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { tried.Add(candidate + " (invalid path)"); continue; }

            tried.Add(full);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static IEnumerable<string> Candidates(ServerState state, string version)
    {
        if (!string.IsNullOrWhiteSpace(state.UnityEditorPath))
            yield return state.UnityEditorPath;

        string? secondary = ReadHubSecondaryInstallPath();
        if (!string.IsNullOrWhiteSpace(secondary))
            yield return Path.Combine(secondary, version, "Editor", "Unity.exe");

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe");
    }

    /// <summary>%APPDATA%\UnityHub\secondaryInstallPath.json（JSON の文字列 1 つ）を読む。無ければ null。</summary>
    private static string? ReadHubSecondaryInstallPath()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnityHub", "secondaryInstallPath.json");
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<string>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
