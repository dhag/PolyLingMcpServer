using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PolyLingMcpServer;

[McpServerToolType]
public static class UnityProjectTools
{
    [McpServerTool(Name = "ping", ReadOnly = true), Description("Checks whether the MCP server is running.")]
    public static string Ping(UiDispatcher ui)
    {
        ui.Log("ping");
        return "OK";
    }

    [McpServerTool(Name = "list_files", ReadOnly = true),
     Description("Lists files under the configured Unity project. Paths are project-relative. Use this before reading unfamiliar source trees.")]
    public static string ListFiles(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative directory, for example Assets/MyPackage/Runtime. Empty means project root.")] string relative_dir = "",
        [Description("Search pattern such as *.cs. Defaults to *.cs.")] string search_pattern = "*.cs",
        [Description("If true, searches all descendant directories.")] bool recursive = true,
        [Description("Maximum number of files to return. 1 to 5000.")] int max_results = MaxListFiles,
        [Description("Internal: report the total when the list is cut short. Set by the server profile.")] bool note_truncation = false)
    {
        ui.Log($"list_files dir=\"{relative_dir}\" pattern={search_pattern} recursive={recursive} max={max_results}");
        max_results = Math.Clamp(max_results, 1, MaxListFiles);

        var dir = PathGuard.ResolveReadablePath(state, relative_dir);
        if (!Directory.Exists(dir))
            return $"ERROR: Directory not found: {relative_dir}";

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        try
        {
            var all = Directory.EnumerateFiles(dir, search_pattern, option)
                .Select(path => new FileInfo(path))
                .OrderBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var files = all
                .Take(max_results)
                .Select(info => $"{PathGuard.ToDisplayPath(state, info.FullName)}\t{info.Length} bytes")
                .ToArray();

            if (files.Length == 0)
                return "No files found.";

            var text = string.Join(Environment.NewLine, files);

            // 黙って先頭だけ返すと、呼び出し側が全部だと誤認する。
            // profile=optimized でだけ付ける（note_truncation はサーバが入れる。McpSetup の注記）。
            if (note_truncation && all.Length > files.Length)
                text += Environment.NewLine
                      + $"TRUNCATED: shown {files.Length} of {all.Length} files. "
                      + "Narrow relative_dir or search_pattern, or raise max_results.";

            return text;
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    /// <summary>list_files が一度に返す件数の上限。5ee9a518 までの固定値。</summary>
    private const int MaxListFiles = 5000;

    [McpServerTool(Name = "read_file", ReadOnly = true),
     Description("Reads a text file from the configured Unity project. The path must be project-relative. "
               + "The file is decoded as UTF-8 unless encoding says otherwise (MQO files are shift_jis).")]
    public static async Task<string> ReadFile(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative file path, for example Assets/MyPackage/Runtime/Foo.cs.")] string path,
        [Description(SourceSearchTools.EncodingDescription)] string encoding = "utf-8",
        CancellationToken cancellationToken = default)
    {
        ui.Log($"read_file {path} encoding={encoding}");

        if (!SourceSearchTools.TryResolveEncoding(encoding, out var fileEncoding, out var encodingError))
            return encodingError;

        try
        {
            var fullPath = PathGuard.ResolveReadablePath(state, path);
            if (!File.Exists(fullPath))
                return $"ERROR: File not found: {path}";

            var info = new FileInfo(fullPath);
            const long maxBytes = 2 * 1024 * 1024;
            if (info.Length > maxBytes)
                return $"ERROR: File is too large ({info.Length} bytes). Limit is {maxBytes} bytes.";

            return await SharedFileRead.ReadAllTextAsync(fullPath, fileEncoding, cancellationToken);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    [McpServerTool(Name = "write_file", Destructive = true),
     Description("Creates or replaces a text file inside one of the configured editable folders of the Unity project.")]
    public static async Task<string> WriteFile(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative destination path. It must be inside one of the editable folders.")] string path,
        [Description("Complete file contents to write.")] string content,
        [Description("If false, writing fails when the destination already exists.")] bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = PathGuard.ResolveWritablePath(state, path);

            if (!overwrite && File.Exists(fullPath))
                return $"ERROR: File already exists: {path}";

            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            await File.WriteAllTextAsync(fullPath, content, new UTF8Encoding(false), cancellationToken);

            var written = PathGuard.ToDisplayPath(state, fullPath);
            ui.Log($"write_file {written} ({content.Length} chars)");
            return $"OK: wrote {content.Length} characters to {written}";
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    [McpServerTool(Name = "apply_patch", Destructive = true),
     Description("Applies a safe exact-text patch inside one of the editable folders. By default old_text must occur exactly once, avoiding ambiguous edits.")]
    public static async Task<string> ApplyPatch(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative file path inside one of the editable folders.")] string path,
        [Description("Exact existing text to replace.")] string old_text,
        [Description("Replacement text.")] string new_text,
        [Description("If true, replaces every occurrence. If false, old_text must occur exactly once.")] bool replace_all = false,
        [Description("Internal: also report how much changed and the new hash. Set by the server profile.")] bool report_detail = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(old_text))
                return "ERROR: old_text must not be empty.";

            var fullPath = PathGuard.ResolveWritablePath(state, path);
            if (!File.Exists(fullPath))
                return $"ERROR: File not found: {path}";

            var source = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken);
            var count = CountOccurrences(source, old_text);

            if (count == 0)
                return "ERROR: old_text was not found. Read the file again before patching.";

            if (!replace_all && count != 1)
                return $"ERROR: old_text occurs {count} times. Supply a more specific old_text or set replace_all=true.";

            string patched;
            int replaced;

            if (replace_all)
            {
                patched = source.Replace(old_text, new_text, StringComparison.Ordinal);
                replaced = count;
            }
            else
            {
                var index = source.IndexOf(old_text, StringComparison.Ordinal);
                patched = string.Concat(source.AsSpan(0, index), new_text, source.AsSpan(index + old_text.Length));
                replaced = 1;
            }

            await File.WriteAllTextAsync(fullPath, patched, new UTF8Encoding(false), cancellationToken);

            var patchedPath = PathGuard.ToDisplayPath(state, fullPath);
            ui.Log($"apply_patch {patchedPath} replacements={replaced}");

            if (!report_detail)
                return $"OK: patched {patchedPath}; replacements={replaced}";

            // 変更量とハッシュを返すと、呼び出し側が読み直さずに「当たったか」を確かめられる。
            int charsBefore = source.Length, charsAfter = patched.Length;
            int linesBefore = CountLines(source), linesAfter = CountLines(patched);
            bool needsRefresh = UnityAssetExtensions.Contains(Path.GetExtension(fullPath));

            return $"OK: patched {patchedPath}; replacements={replaced}; "
                 + $"chars={charsAfter - charsBefore:+#;-#;0}; lines={linesAfter - linesBefore:+#;-#;0}; "
                 + $"hash={ShortHash(patched)}; unity_refresh_required={(needsRefresh ? "true" : "false")}";
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    [McpServerTool(Name = "compile", Destructive = false),
     Description("Builds a Unity-generated C# project with dotnet build. This is an external compile check; it does not command the running Unity Editor to compile.")]
    public static async Task<string> Compile(
        ServerState state,
        UiDispatcher ui,
        [Description("Optional project-relative .csproj path. If omitted, Assembly-CSharp.csproj is preferred.")] string? project_file = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset startedAt = DateTimeOffset.Now;

        try
        {
            if (string.IsNullOrWhiteSpace(state.ProjectRoot) || !Directory.Exists(state.ProjectRoot))
                return "ERROR: Unity project folder is not configured.";

            var csproj = FindProjectFile(state, project_file);
            if (csproj is null)
                return "ERROR: No .csproj was found. Open the Unity project and regenerate project files first.";

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = state.ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                // dotnet writes UTF-8. Without these, a redirected stream is
                // decoded with the console code page (cp932 on Japanese
                // Windows), which turns every non-ASCII path and message in the
                // build output into mojibake.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),

                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(csproj);
            psi.ArgumentList.Add("--nologo");
            psi.ArgumentList.Add("--verbosity");
            psi.ArgumentList.Add("minimal");

            ui.Log($"compile {PathGuard.ToDisplayPath(state, csproj)} ...");

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            if (!process.Start())
                return "ERROR: Failed to start dotnet build.";

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            var output = stdout.ToString();
            if (stderr.Length > 0)
                output += Environment.NewLine + stderr;

            var errors = ExtractErrors(output);
            var finishedAt = DateTimeOffset.Now;
            var result = new CompileResult(
                process.ExitCode == 0,
                process.ExitCode,
                PathGuard.ToDisplayPath(state, csproj),
                startedAt,
                finishedAt,
                errors,
                output);

            state.SetLastCompileResult(result);
            ui.Log(result.Success
                ? $"compile OK {result.ProjectFile}"
                : $"compile FAILED {result.ProjectFile} errors={result.Errors.Count}");

            if (result.Success)
                return $"OK: build succeeded. Project={result.ProjectFile}; errors=0";

            return $"ERROR: build failed. ExitCode={result.ExitCode}; errors={result.Errors.Count}\n" +
                   string.Join(Environment.NewLine, result.Errors.Take(50));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return "ERROR: Could not launch 'dotnet'. Install the .NET 8 SDK or add dotnet to PATH. " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            return "ERROR: build cancelled.";
        }
        catch (Exception ex)
        {
            var failed = new CompileResult(false, -1, project_file ?? string.Empty,
                startedAt, DateTimeOffset.Now, [ex.Message], ex.ToString());
            state.SetLastCompileResult(failed);
            return "ERROR: " + ex.Message;
        }
    }

    [McpServerTool(Name = "get_compile_errors", ReadOnly = true),
     Description("Returns errors from the most recent compile tool invocation.")]
    public static string GetCompileErrors(
        ServerState state,
        UiDispatcher ui,
        [Description("Maximum number of error lines to return.")] int max_errors = 100)
    {
        ui.Log("get_compile_errors");

        var result = state.LastCompileResult;
        if (result is null)
            return "No compile has been run yet.";

        if (result.Errors.Count == 0)
            return result.Success
                ? $"No compile errors. Last build succeeded: {result.ProjectFile}"
                : "The last build failed, but no standard error lines were recognized. Inspect compile output.";

        max_errors = Math.Clamp(max_errors, 1, 500);
        return string.Join(Environment.NewLine, result.Errors.Take(max_errors));
    }

    // ================================================================
    // Unity Editor 制御（名前付きパイプ）
    // ================================================================
    //
    // These tools need PolyLingEditorControlServer running inside the Unity
    // Editor. It is started by [InitializeOnLoad], so an open Editor is the
    // only requirement; the PolyLing window does not have to be open.
    // ================================================================

    [McpServerTool(Name = "unity_ping", ReadOnly = true),
     Description("Checks whether the Unity Editor is running and its control server answers. Use this before any other unity_ tool.")]
    public static Task<string> UnityPing(
        UiDispatcher ui,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 3000,
        CancellationToken cancellationToken = default)
        => SendEditorAction("ping", ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "unity_state", ReadOnly = true),
     Description("Reports whether the Unity Editor is in Play mode, paused, compiling or importing assets. Call this after unity_play or unity_stop to confirm the result.")]
    public static Task<string> UnityState(
        UiDispatcher ui,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 3000,
        CancellationToken cancellationToken = default)
        => SendEditorAction("state", ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "unity_play", Destructive = false),
     Description("Puts the Unity Editor into Play mode. The reply comes back before the transition happens, because entering Play mode reloads the Editor's script domain and drops this connection. Call unity_state afterwards to confirm. Fails if the Editor is already playing, compiling or importing assets.")]
    public static Task<string> UnityPlay(
        UiDispatcher ui,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 5000,
        CancellationToken cancellationToken = default)
        => SendEditorAction("play", ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "unity_stop", Destructive = false),
     Description("Takes the Unity Editor out of Play mode and back to Edit mode. The reply comes back before the transition happens. Call unity_state afterwards to confirm. Fails if the Editor is not playing.")]
    public static Task<string> UnityStop(
        UiDispatcher ui,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 5000,
        CancellationToken cancellationToken = default)
        => SendEditorAction("stop", ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "unity_refresh", Destructive = false),
     Description("Makes the Unity Editor pick up changed files: it refreshes the Asset Database and requests script compilation, "
               + "then waits until importing and compiling have finished. Use this after editing scripts. "
               + "Compile errors are not returned here; read them with read_unity_log. "
               + "Fails in Play mode, and while the Editor is already compiling or importing; call unity_stop first.")]
    public static Task<string> UnityRefresh(
        UiDispatcher ui,
        [Description("How long to wait for importing and compiling to finish, in milliseconds.")] int timeout_ms = DefaultRefreshTimeoutMs,
        CancellationToken cancellationToken = default)
        => SendEditorActionAndWait("refresh", ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "unity_recompile", Destructive = false),
     Description("Forces the Unity Editor to recompile every script even if nothing changed, by clearing the build cache, "
               + "then waits until compiling has finished. Slower than unity_refresh. "
               + "Compile errors are not returned here; read them with read_unity_log. "
               + "Fails in Play mode, and while the Editor is already compiling or importing; call unity_stop first.")]
    public static Task<string> UnityRecompile(
        UiDispatcher ui,
        [Description("How long to wait for compiling to finish, in milliseconds.")] int timeout_ms = DefaultRefreshTimeoutMs,
        CancellationToken cancellationToken = default)
        => SendEditorActionAndWait("recompile", ui, timeout_ms, cancellationToken);

    /// <summary>refresh / recompile の既定の待ち時間。全スクリプトの再コンパイルを見込む。</summary>
    internal const int DefaultRefreshTimeoutMs = 120_000;

    /// <summary>refresh / recompile を送るときの応答待ち。受付だけなのですぐ返る。</summary>
    private const int RefreshAcceptTimeoutMs = 10_000;

    /// <summary>完了待ちで state を読み直す間隔。</summary>
    private const int RefreshPollIntervalMs = 500;

    /// <summary>
    /// 完了待ちの state 1 回あたりの応答待ち。コンパイル中・インポート中は
    /// Editor のメインスレッドが塞がって応答が返らないので、長く待たずに次の回へ回す。
    /// </summary>
    private const int RefreshPollReplyMs = 3000;

    private static readonly Regex AcceptedIdPattern     = new(@"\bid=(\d+)");
    private static readonly Regex RefreshDonePattern    = new(@"\brefreshDone=(\d+)");
    private static readonly Regex IsCompilingPattern    = new(@"\bisCompiling=(true|false)");
    private static readonly Regex IsUpdatingPattern     = new(@"\bisUpdating=(true|false)");
    private static readonly Regex RefreshResultPattern  = new(@"\brefreshResult=(.*)$");

    /// <summary>
    /// refresh / recompile を送り、Editor 側の実行と、それに続く取り込み・コンパイルの完了を待つ。
    ///
    /// Editor は受付番号 id を返してから実行する（PolyLingEditorControlServer.HandleRefresh）。
    /// 完了は state の「refreshDone ≧ id」「isCompiling=false」「isUpdating=false」の 3 つで判定する。
    /// Editor はコンパイル要求を立ててから refreshDone を書き、isCompiling は要求が立っている間も
    /// true を返すので、3 つがそろった時点で取り込みとコンパイルは終わっている。
    ///
    /// コンパイル中・インポート中は応答が返らず、ドメインリロード中は接続できない。
    /// その間は失敗とせず、上限まで待ち続ける。
    /// </summary>
    internal static async Task<string> SendEditorActionAndWait(
        string action,
        UiDispatcher ui,
        int timeout_ms,
        CancellationToken cancellationToken)
    {
        timeout_ms = Math.Clamp(timeout_ms, 1000, 600_000);
        ui.Log($"unity_{action} timeout={timeout_ms}ms");

        var sw = Stopwatch.StartNew();

        EditorControlResult accepted;
        try
        {
            accepted = await EditorControlClient.SendAsync(action, RefreshAcceptTimeoutMs, null, cancellationToken);
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return StageMessage(ex, RefreshAcceptTimeoutMs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimeoutMessage(RefreshAcceptTimeoutMs);
        }
        catch (Exception ex)
        {
            return $"ERROR: could not reach the Unity Editor on {EditorControlClient.Endpoint}. "
                   + $"Is it open? {ex.Message}";
        }

        if (!accepted.Success)
            return $"ERROR: the Unity Editor rejected '{action}': {accepted.Error}";

        var idMatch = AcceptedIdPattern.Match(accepted.Data);
        if (!idMatch.Success || !long.TryParse(idMatch.Groups[1].Value, out long id))
            return $"ERROR: the Unity Editor accepted '{action}' but returned no id. "
                 + $"Is PolyLingEditorControlServer.cs up to date? reply: {accepted.Data}";

        string lastState   = "(none)";
        string lastProblem = "(none)";

        while (sw.ElapsedMilliseconds < timeout_ms)
        {
            await Task.Delay(RefreshPollIntervalMs, cancellationToken);

            EditorControlResult polled;
            try
            {
                polled = await EditorControlClient.SendAsync("state", RefreshPollReplyMs, null, cancellationToken);
            }
            catch (EditorControlClient.EditorControlException ex)
            {
                // コンパイル中（応答なし）かドメインリロード中（接続不可）。待ち続ける。
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

            var doneMatch      = RefreshDonePattern.Match(polled.Data);
            var compilingMatch = IsCompilingPattern.Match(polled.Data);
            var updatingMatch  = IsUpdatingPattern.Match(polled.Data);

            if (!doneMatch.Success || !compilingMatch.Success || !updatingMatch.Success)
                return $"ERROR: the Unity Editor state has no refreshDone / isCompiling / isUpdating. "
                     + $"Is PolyLingEditorControlServer.cs up to date? state: {polled.Data}";

            long done      = long.Parse(doneMatch.Groups[1].Value);
            bool compiling = compilingMatch.Groups[1].Value == "true";
            bool updating  = updatingMatch.Groups[1].Value == "true";

            if (done < id || compiling || updating)
                continue;

            // refreshDone が id を超えていれば、後から来た要求の結果が載っている。
            // この要求はそれより前にメインスレッドで実行済み。
            var resultMatch = RefreshResultPattern.Match(polled.Data);
            string result   = resultMatch.Success ? resultMatch.Groups[1].Value.Trim() : string.Empty;

            if (done == id && !result.StartsWith("executed", StringComparison.Ordinal))
                return $"ERROR: '{action}' id={id} was not executed: {result}. state: {polled.Data}";

            return $"OK: {action} finished in {sw.ElapsedMilliseconds} ms. id={id}; {polled.Data}";
        }

        return $"ERROR: '{action}' id={id} did not finish within {timeout_ms} ms. "
             + $"last state: {lastState}; last problem: {lastProblem}";
    }

    /// <summary>
    /// Shared body of every unity_ tool. The Editor answers with the same
    /// success/data/error shape for all of them, so only the action name differs.
    /// </summary>
    private static async Task<string> SendEditorAction(
        string action,
        UiDispatcher ui,
        int timeout_ms,
        CancellationToken cancellationToken)
    {
        ui.Log($"unity_{action} timeout={timeout_ms}ms");

        try
        {
            var result = await EditorControlClient.SendAsync(action, timeout_ms, null, cancellationToken);

            if (!result.Success)
                return $"ERROR: the Unity Editor rejected '{action}': {result.Error}";

            return $"OK: {action}; {result.Data}";
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return StageMessage(ex, timeout_ms);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimeoutMessage(timeout_ms);
        }
        catch (Exception ex)
        {
            return $"ERROR: could not reach the Unity Editor on {EditorControlClient.Endpoint}. "
                   + $"Is it open? {ex.Message}";
        }
    }

    // ================================================================
    // PolyLing コマンド（MCP）
    // ================================================================
    //
    // These two need the PolyLing panel open, not just the Editor. The command
    // is executed through PolyLingCommandGateway, which the panel registers when
    // it builds its dispatcher and clears on dispose.
    // ================================================================

    [McpServerTool(Name = "polyling_tools", ReadOnly = true),
     Description("Lists the PolyLing commands that can be issued with polyling_call, as a JSON Schema document. Requires the PolyLing panel to be open.")]
    public static Task<IEnumerable<ContentBlock>> PolyLingTools(
        UiDispatcher ui,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 10000,
        CancellationToken cancellationToken = default)
        => SendEditorRaw("tools", null, ui, timeout_ms, cancellationToken);

    [McpServerTool(Name = "polyling_call", Destructive = true),
     Description("Runs one PolyLing command. Use polyling_tools first to see the command names and their arguments. Requires the PolyLing panel to be open.")]
    // profile=optimized では応答に modelRevision / createdObjectIds / deletedObjectIds が付く（ServerProfile.cs）。
    public static Task<IEnumerable<ContentBlock>> PolyLingCall(
        ServerState state,
        UiDispatcher ui,
        [Description("Command name from polyling_tools, for example 'smoothEdges'.")] string command,
        [Description("Arguments as a flat JSON object of string values, for example {\"strength\":\"0.5\"}. Nested values use dotted keys such as 'params.widthTop'. Arrays are sent as one comma-separated string such as \"1,2,3\".")] string? args = null,
        [Description("Target model index. Defaults to 0.")] int model_index = 0,
        [Description("How long to wait for the Editor to answer, in milliseconds.")] int timeout_ms = 30000,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Task.FromResult(McpContent.Error("command must not be empty."));

        Dictionary<string, string> parameters;
        try
        {
            parameters = FlattenArgs(args);
        }
        catch (Exception ex)
        {
            return Task.FromResult(
                McpContent.Error($"args is not a flat JSON object of string values: {ex.Message}"));
        }

        parameters["command"]    = command;
        parameters["modelIndex"] = model_index.ToString();

        // uiCapture で保存先を省いたら、キャプチャフォルダへ撮らせる（UnityCaptureTools）。
        UnityCaptureTools.ApplyDefaultCaptureFolder(state, command, parameters);

        // 版と差分は profile=optimized のときだけ足す。他の条件の応答は 5ee9a518 のまま。
        if (state.Profile == ServerProfile.Optimized) parameters["wantRevision"] = "1";

        return SendEditorRaw("call", parameters, ui, timeout_ms, cancellationToken);
    }

    /// <summary>
    /// Same transport as SendEditorAction, but returns the Editor's raw JSON.
    /// tools and call answer with their own keys (tools / masterIndices /
    /// objectIds) rather than the single "data" string.
    /// </summary>
    internal static async Task<IEnumerable<ContentBlock>> SendEditorRaw(
        string action,
        IReadOnlyDictionary<string, string>? parameters,
        UiDispatcher ui,
        int timeout_ms,
        CancellationToken cancellationToken)
    {
        ui.Log($"polyling_{action} timeout={timeout_ms}ms");

        try
        {
            var result = await EditorControlClient.SendAsync(action, timeout_ms, parameters, cancellationToken);

            if (!result.Success)
                return McpContent.Error($"the Unity Editor rejected '{action}': {result.Error}");

            // 画像を返さないコマンドでは Lift は元の文字列をそのまま返すので、
            // これまでの応答と 1 バイトも変わらない。
            var images   = new List<ImageAttachment>();
            var warnings = new List<string>();
            var text     = EditorImage.Lift(result.Raw, images, warnings);

            if (images.Count > 0 && !McpContent.FitsBudget(images, out var budgetError))
            {
                warnings.Add(budgetError);
                images.Clear();
            }

            var advice = ImageBytes.SizeAdvice(images);
            if (advice.Length > 0) warnings.Add(advice);

            if (warnings.Count > 0)
                text += "\n" + string.Join("\n", warnings);

            return McpContent.TextWithImages(text, images);
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return McpContent.Error(StageMessage(ex, timeout_ms));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return McpContent.Error(TimeoutMessage(timeout_ms));
        }
        catch (Exception ex)
        {
            return McpContent.Error($"could not reach the Unity Editor on {EditorControlClient.Endpoint}. "
                                  + $"Is it open? {ex.Message}");
        }
    }

    /// <summary>
    /// どちらの段で落ちたかを文言にする。
    /// ログファイルを開かなくても、道具の応答だけで切り分けられるようにする。
    /// </summary>
    internal static string StageMessage(EditorControlClient.EditorControlException ex, int timeoutMs)
        => ex.Stage == EditorControlClient.FailStage.Connect
            ? $"ERROR: ポートに接続できませんでした（{ex.ElapsedMs}ms / 上限 {timeoutMs}ms）。"
              + $"endpoint={EditorControlClient.Endpoint}; {ex.InnerName}。"
              + "Unity Editor が開いていて、待ち受けが立っているかを確認してください。"
            : $"ERROR: 接続はできましたが、応答が {ex.ElapsedMs}ms 以内に返りませんでした（上限 {timeoutMs}ms）。"
              + $"{ex.InnerName}。Editor がコンパイル中・インポート中でないか確認してください。";

    /// <summary>
    /// Turns the args JSON object into the flat string dictionary the Editor
    /// expects. Numbers and booleans are accepted and written out as text,
    /// because PanelCommandFactory.TryParse reads every value from a string.
    /// Nested objects and arrays are rejected here rather than silently dropped
    /// by the Editor's JsonParser.
    /// </summary>
    private static Dictionary<string, string> FlattenArgs(string? args)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(args)) return result;

        using var doc = System.Text.Json.JsonDocument.Parse(args);
        var root = doc.RootElement;

        if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new InvalidOperationException("the top level must be an object.");

        foreach (var p in root.EnumerateObject())
            result[p.Name] = ScalarText(p.Name, p.Value, allowArray: false);

        return result;
    }

    /// <summary>
    /// JSON の値 1 つを Editor が受け取る文字列へ直す。
    /// allowArray が true なら、スカラーの配列をカンマ区切りの 1 文字列にする
    /// （PanelCommandFactory.TryParse が配列・ベクトルを "1,2,3" で受けるため）。
    /// polyling_call は従来どおり配列を拒否する（allowArray=false）。
    /// 1 コマンド 1 道具（profile=generic）は Schema が配列を宣言しているので受ける。
    /// </summary>
    internal static string ScalarText(string name, System.Text.Json.JsonElement value, bool allowArray)
    {
        switch (value.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                return value.GetString() ?? "";
            case System.Text.Json.JsonValueKind.Number:
                return value.GetRawText();
            case System.Text.Json.JsonValueKind.True:
                return "true";
            case System.Text.Json.JsonValueKind.False:
                return "false";
            case System.Text.Json.JsonValueKind.Array when allowArray:
            {
                var parts = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind is System.Text.Json.JsonValueKind.Array or System.Text.Json.JsonValueKind.Object)
                        throw new InvalidOperationException($"\"{name}\" contains a nested {item.ValueKind}; only arrays of scalars are accepted.");
                    parts.Add(ScalarText(name, item, allowArray: false));
                }
                return string.Join(",", parts);
            }
            default:
                throw new InvalidOperationException(
                    allowArray
                        ? $"\"{name}\" is {value.ValueKind}. Nested values use dotted keys."
                        : $"\"{name}\" is {value.ValueKind}. Send arrays as one comma-separated string, "
                          + "and nested values as dotted keys.");
        }
    }

    internal static string TimeoutMessage(int timeoutMs) =>
        $"ERROR: the Unity Editor did not answer within {timeoutMs} ms. "
        + "It is probably compiling or importing assets; retry once it is idle.";

    private static string? FindProjectFile(ServerState state, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var resolved = PathGuard.ResolveReadablePath(state, requested);
            if (!resolved.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("project_file must point to a .csproj file.");
            return File.Exists(resolved) ? resolved : null;
        }

        var assemblyCSharp = Path.Combine(state.ProjectRoot, "Assembly-CSharp.csproj");
        if (File.Exists(assemblyCSharp))
            return assemblyCSharp;

        return Directory.EnumerateFiles(state.ProjectRoot, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> ExtractErrors(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // Handles typical compiler/MSBuild formats such as:
            // Foo.cs(10,5): error CS1002: ; expected [Assembly-CSharp.csproj]
            // error MSBxxxx: ...
            if (Regex.IsMatch(line, @"\berror\s+(CS\d+|MSB\d+|NETSDK\d+)\s*:", RegexOptions.IgnoreCase))
                result.Add(line);
        }

        return result.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Unity に取り込ませ直す必要がある拡張子。apply_patch の報告に使う。</summary>
    private static readonly HashSet<string> UnityAssetExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs", ".asmdef", ".asmref", ".shader", ".compute", ".uxml", ".uss", ".json", ".unity" };

    /// <summary>変更後の中身のハッシュ。SHA-256 の先頭 16 桁（小文字 16 進）。</summary>
    private static string ShortHash(string text)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') lines++;
        return lines;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;

        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
