using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;

namespace PolyLingMcpServer;

/// <summary>
/// Player ビルドの作成と起動（通信テスト用）。
///
/// 【unity_build_player】
///   Editor 側 PolyLingEditorControlServer の build_player を呼ぶ。
///   Play 中なら先に止め、Edit モードで待機状態になってから送る（UnityPrefabTools.RunInEditMode）。
///   ビルドは Editor のメインスレッドを塞ぐので、応答は終わってから返る。
///   出力先の exe が起動中だと上書きできずに失敗するので、先に player_stop する。
///
/// 【player_launch / player_stop】
///   exe はこの MCP サーバのプロセスから起動する（同じ PC 上）。
///   player_stop は、実行ファイルのパスが指定 exe（既定 ビルド用/PolyLing.exe）と一致する
///   プロセスだけを終わらせる。MCP サーバはツール呼び出しごとに別プロセスで動くことがあるため、
///   起動したプロセスをメモリに控える方式は使わない。
///   同じ exe を手で起動した Player も対象になる（プロジェクトのビルド出力に限る）。
///   クライアント／サーバは起動引数 --polyling-remote=client|server|none で決める
///   （Player 側 PolyLingPlayerViewer.ResolveRemoteMode がシーンの設定より優先する）。
///
/// 【exe の場所】
///   プロジェクト直下からの相対か絶対。プロジェクトの外の exe は起動も停止もしない。
///   既定は ビルド用/PolyLing.exe（Editor 側 build_player の既定と同じ）。
/// </summary>
[McpServerToolType]
public static class UnityPlayerBuildTools
{
    /// <summary>既定の exe（プロジェクト直下からの相対）。Editor 側 DefaultPlayerOutput と同じ。</summary>
    private const string DefaultExe = "ビルド用/PolyLing.exe";

    [McpServerTool(Name = "unity_build_player", Destructive = true),
     Description("Builds the PolyLing player for Windows 64-bit from the scenes enabled in Build Settings, overwriting the previous build. "
               + "If the Editor is in Play mode it is stopped first, and the tool waits for Edit mode before building. "
               + "The reply comes back when the build has finished. Fails if the output exe is running; call player_stop first.")]
    public static Task<string> UnityBuildPlayer(
        UiDispatcher ui,
        [Description("Output exe, relative to the Unity project folder or absolute. Defaults to ビルド用/PolyLing.exe.")] string? output_path = null,
        [Description("Make a development build. Default false.")] bool? development = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds.")] int timeout_ms = 1_200_000,
        CancellationToken cancellationToken = default)
    {
        var p = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(output_path)) p["outputPath"] = output_path;
        if (development.HasValue) p["development"] = development.Value ? "true" : "false";

        return UnityPrefabTools.RunInEditMode("build_player", p, ui, timeout_ms, cancellationToken);
    }

    /// <summary>exe のパスを絶対パスにし、プロジェクト内の .exe であることを確かめる。</summary>
    private static bool TryResolveExe(ServerState state, string? exePath, out string exe, out string error)
    {
        exe = ""; error = "";
        if (string.IsNullOrEmpty(state.ProjectRoot))
        {
            error = "ERROR: no Unity project folder is configured.";
            return false;
        }

        exe = string.IsNullOrWhiteSpace(exePath) ? DefaultExe : exePath!;
        if (!Path.IsPathRooted(exe)) exe = Path.Combine(state.ProjectRoot, exe);
        exe = Path.GetFullPath(exe);

        string root = Path.GetFullPath(state.ProjectRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            error = $"ERROR: the exe must be inside the Unity project folder ({state.ProjectRoot}): {exe}";
            return false;
        }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"ERROR: not an exe: {exe}";
            return false;
        }
        return true;
    }

    /// <summary>実行ファイルのパスが exe と一致するプロセス。</summary>
    private static List<Process> FindPlayers(string exe)
    {
        var found = new List<Process>();
        foreach (var proc in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            string? file = null;
            try { file = proc.MainModule?.FileName; } catch { }
            if (file != null && string.Equals(Path.GetFullPath(file), exe, StringComparison.OrdinalIgnoreCase))
                found.Add(proc);
            else
                proc.Dispose();
        }
        return found;
    }

    [McpServerTool(Name = "player_launch", Destructive = false),
     Description("Starts the built PolyLing player exe on this computer. "
               + "remote_mode chooses client, server or none, overriding the scene setting. "
               + "Only exe files inside the Unity project folder can be started. player_stop ends it later.")]
    public static string PlayerLaunch(
        ServerState state,
        UiDispatcher ui,
        [Description("client, server or none. Empty keeps the setting saved in the scene.")] string? remote_mode = null,
        [Description("Exe to start, relative to the Unity project folder or absolute. Defaults to ビルド用/PolyLing.exe.")] string? exe_path = null,
        [Description("Extra command-line arguments passed as they are.")] string? extra_args = null)
    {
        if (!TryResolveExe(state, exe_path, out var exe, out var error))
            return error;
        if (!File.Exists(exe))
            return $"ERROR: exe not found: {exe}. Build it first with unity_build_player.";

        string args = "";
        if (!string.IsNullOrWhiteSpace(remote_mode))
        {
            string m = remote_mode!.Trim().ToLowerInvariant();
            if (m != "client" && m != "server" && m != "none")
                return $"ERROR: remote_mode must be client, server or none: {remote_mode}";
            args = "--polyling-remote=" + m;
        }
        if (!string.IsNullOrWhiteSpace(extra_args))
            args = (args + " " + extra_args).Trim();

        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute  = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? state.ProjectRoot,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return $"ERROR: the exe did not start: {exe}";

            ui.Log($"player_launch pid={proc.Id} {exe} {args}");
            return $"OK: started pid={proc.Id}; exe={exe}; args={args}";
        }
        catch (Exception ex)
        {
            return $"ERROR: could not start {exe}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "player_stop", Destructive = true),
     Description("Ends running PolyLing players whose executable is the given exe inside the Unity project folder "
               + "(default ビルド用/PolyLing.exe). Without pid, ends all of them. Other applications are never touched.")]
    public static string PlayerStop(
        ServerState state,
        UiDispatcher ui,
        [Description("Process id to end. 0 ends every running player of that exe.")] int pid = 0,
        [Description("Exe whose players are ended, relative to the Unity project folder or absolute. Defaults to ビルド用/PolyLing.exe.")] string? exe_path = null)
    {
        if (!TryResolveExe(state, exe_path, out var exe, out var error))
            return error;

        var done = new List<string>();
        foreach (var proc in FindPlayers(exe))
        {
            using (proc)
            {
                int id = proc.Id;
                if (pid != 0 && id != pid) continue;
                try
                {
                    proc.Kill();
                    proc.WaitForExit(10_000);
                    done.Add($"pid={id} ended");
                }
                catch (Exception ex)
                {
                    done.Add($"pid={id} could not be ended: {ex.Message}");
                }
            }
        }

        if (done.Count == 0)
            return pid == 0
                ? $"OK: no player of {exe} is running."
                : $"ERROR: pid={pid} is not a running player of {exe}.";

        ui.Log("player_stop " + string.Join("; ", done));
        return "OK: " + string.Join("; ", done);
    }
}
