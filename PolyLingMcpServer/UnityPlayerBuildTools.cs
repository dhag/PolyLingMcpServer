using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;

namespace PolyLingMcpServer;

/// <summary>
/// Player ビルドの作成と起動（通信テスト用）。
///
/// 【unity_build_player】
///   Editor 側 PolyLingEditorControlServer の build_player（all=true なら build_all）を呼ぶ。
///   ビルド構成は Editor が読む（Assets/PolyLingBuild/ビルド構成.json、無ければ
///   パッケージ既定 Packages/com.haglib.polyling/Editor/Build/既定ビルド構成.json）。
///   ここは構成名（config）と「全部」（all）を渡すだけで、構成ファイルは読まない
///   （構成ファイルを読むのは instance を引く InstanceResolver。段 4）。
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
///   役割は起動引数 --polyling-remote=server|host|collaborator|separate|none で決める
///   （Player 側 PolyLingLaunchArgs。本体ビューア・リストクライアントともシーンの設定より優先する）。
///   MCP の受付ポートは --polyling-control-port=N、リストクライアントのユーザー名は --polyling-user=名前。
///   instance（構成名、段 4）を渡すと、exe・役割・ポート・ユーザー名をビルド構成ファイルから引いて渡す
///   （InstanceResolver。呼び出しごとにファイルを読む）。このとき exe_path と remote_mode は渡せない。
///   instance を省くと、従来どおり exe_path・remote_mode・extra_args だけで起動する。
///
/// 【exe の場所】
///   プロジェクト直下からの相対か絶対。プロジェクトの外の exe は起動も停止もしない。
///   既定は ビルド用/PolyLing.exe（Editor 側 build_player の既定と同じ）。
/// </summary>
[McpServerToolType]
public static class UnityPlayerBuildTools
{
    /// <summary>既定の exe（プロジェクト直下からの相対）。パッケージ既定のビルド構成 player の exe と同じ。</summary>
    private const string DefaultExe = "ビルド用/PolyLing.exe";

    [McpServerTool(Name = "unity_build_player", Destructive = true),
     Description("Builds the PolyLing player for Windows 64-bit from a build configuration, overwriting the previous build. "
               + "Configurations are read by the Editor from Assets/PolyLingBuild/ビルド構成.json, or from the package default "
               + "Packages/com.haglib.polyling/Editor/Build/既定ビルド構成.json when that file does not exist. "
               + "Each configuration has a name, scenes (empty means the scenes enabled in Build Settings), an exe, a remote role and a port. "
               + "config picks one configuration by name (default: the first one); all=true builds every configuration in order and stops at the first failure. "
               + "If the Editor is in Play mode it is stopped first, and the tool waits for Edit mode before building. "
               + "The reply comes back when the build has finished. Fails if an output exe is running; call player_stop first.")]
    public static Task<string> UnityBuildPlayer(
        ServerState state,
        UiDispatcher ui,
        [Description("Build configuration name. Defaults to the first configuration in the file. Not allowed with all=true.")] string? config = null,
        [Description("Build every configuration in the file, in order. Default false.")] bool all = false,
        [Description("Output exe for this build only, relative to the Unity project folder or absolute. Defaults to the configuration's exe. Not allowed with all=true.")] string? output_path = null,
        [Description("Make a development build. Default false.")] bool? development = null,
        [Description("How long to wait for the whole thing, including stopping Play mode, in milliseconds. With all=true it covers every build.")] int timeout_ms = 1_200_000,
        CancellationToken cancellationToken = default)
    {
        if (all && (!string.IsNullOrWhiteSpace(config) || !string.IsNullOrWhiteSpace(output_path)))
            return Task.FromResult("ERROR: all=true builds every configuration as written in the file; config and output_path cannot be given with it.");

        var p = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(config)) p["config"] = config!.Trim();
        if (!string.IsNullOrWhiteSpace(output_path)) p["outputPath"] = output_path!;
        if (development.HasValue) p["development"] = development.Value ? "true" : "false";
        string action = all ? "build_all" : "build_player";

        // Editor 側はビルド本体を EditorApplication.delayCall で実行する（プレイヤーループ内では
        // BuildPlayer が拒否されるため）。Editor が背面にあると delayCall が回らずビルドが始まらないので、
        // 先に Editor を前面に出す。前面化できなければビルドは送らない。
        string focus = UnityWindowTools.UnityFocus(state, ui);
        if (focus.StartsWith("ERROR", StringComparison.Ordinal))
            return Task.FromResult(focus + "\nERROR: unity_build_player brings the Editor to the front before building and could not, so the build was not started.");

        return UnityPrefabTools.RunInEditMode(action, p, ui, timeout_ms, cancellationToken);
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

    /// <summary>instance がビルド構成（builds の名前）であることを確かめて引く。editor は起動・停止の対象にしない。</summary>
    private static bool TryResolveBuildInstance(ServerState state, string? instance, out ResolvedInstance resolved, out string error)
    {
        if (!InstanceResolver.TryResolve(state, instance, out resolved, out error)) return false;
        if (resolved.Build == null)
        {
            error = $"ERROR: {resolved.Describe} is the Unity Editor, not a build configuration. "
                  + "player_launch / player_stop take a builds entry; start the Editor with unity_editor_launch.";
            return false;
        }
        return true;
    }

    /// <summary>空白や " を含む引数を 1 つの引数として渡せるように囲む（Windows のコマンドライン規則）。</summary>
    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
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
               + "remote_mode is the remote role (server, host, collaborator, separate or none, the same values as a build configuration's role), "
               + "passed as --polyling-remote= and overriding the scene setting. The main viewer accepts server, separate (client) and none; "
               + "host and collaborator are for list-client scenes. "
               + "With instance (a build configuration name), the exe, role, port and user come from the build configuration file and are passed as "
               + "--polyling-remote=<role> --polyling-control-port=<port> [--polyling-user=<user>]; exe_path and remote_mode cannot be given then. "
               + "Only exe files inside the Unity project folder can be started. player_stop ends it later.")]
    public static string PlayerLaunch(
        ServerState state,
        UiDispatcher ui,
        [Description("server, host, collaborator, separate or none. Empty keeps the setting saved in the scene. Not allowed with instance.")] string? remote_mode = null,
        [Description("Exe to start, relative to the Unity project folder or absolute. Defaults to ビルド用/PolyLing.exe. Not allowed with instance.")] string? exe_path = null,
        [Description("Extra command-line arguments passed as they are, after the ones above.")] string? extra_args = null,
        [Description("Build configuration name (a builds entry, for example 'player') whose exe is started with its role, port and user. "
                   + "Omit to start exe_path as before.")] string? instance = null)
    {
        string exe;
        string args = "";
        string from = "";

        if (!string.IsNullOrWhiteSpace(instance))
        {
            if (!string.IsNullOrWhiteSpace(exe_path) || !string.IsNullOrWhiteSpace(remote_mode))
                return "ERROR: with instance, the exe and the remote role come from the build configuration; exe_path and remote_mode cannot be given.";
            if (!TryResolveBuildInstance(state, instance, out var resolved, out var instanceError))
                return instanceError;

            exe  = resolved.ExeFullPath!;
            var b = resolved.Build!;
            args = $"--polyling-remote={b.Role} --polyling-control-port={b.Port}";
            if (!string.IsNullOrEmpty(b.User))
                args += " " + QuoteArg("--polyling-user=" + b.User);
            from = $"; instance={resolved.Name}; config={resolved.ConfigFile}";
        }
        else
        {
            if (!TryResolveExe(state, exe_path, out exe, out var error))
                return error;

            if (!string.IsNullOrWhiteSpace(remote_mode))
            {
                string m = remote_mode!.Trim().ToLowerInvariant();
                if (m != "server" && m != "host" && m != "collaborator" && m != "separate" && m != "none")
                    return $"ERROR: remote_mode must be server, host, collaborator, separate or none: {remote_mode}";
                args = "--polyling-remote=" + m;
            }
        }

        if (!File.Exists(exe))
            return $"ERROR: exe not found: {exe}. Build it first with unity_build_player.";

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
            return $"OK: started pid={proc.Id}; exe={exe}; args={args}{from}";
        }
        catch (Exception ex)
        {
            return $"ERROR: could not start {exe}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "player_stop", Destructive = true),
     Description("Ends running PolyLing players whose executable is the given exe inside the Unity project folder "
               + "(default ビルド用/PolyLing.exe, or the exe of the build configuration named by instance). "
               + "Without pid, ends all of them. Other applications are never touched.")]
    public static string PlayerStop(
        ServerState state,
        UiDispatcher ui,
        [Description("Process id to end. 0 ends every running player of that exe.")] int pid = 0,
        [Description("Exe whose players are ended, relative to the Unity project folder or absolute. Defaults to ビルド用/PolyLing.exe. Not allowed with instance.")] string? exe_path = null,
        [Description("Build configuration name (a builds entry) whose exe's players are ended. Omit to use exe_path as before.")] string? instance = null)
    {
        string exe;
        if (!string.IsNullOrWhiteSpace(instance))
        {
            if (!string.IsNullOrWhiteSpace(exe_path))
                return "ERROR: with instance, the exe comes from the build configuration; exe_path cannot be given.";
            if (!TryResolveBuildInstance(state, instance, out var resolved, out var instanceError))
                return instanceError;
            exe = resolved.ExeFullPath!;
        }
        else if (!TryResolveExe(state, exe_path, out exe, out var error))
        {
            return error;
        }

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
