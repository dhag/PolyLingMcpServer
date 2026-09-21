using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace PolyLingMcpServer;

/// <summary>
/// スクリプトを直したあとの定型（停止 → 取り込み・コンパイル → エラー確認 → 必要なら再生）を 1 回で行う。
/// profile=optimized でだけ見せる（ProfileRules）。
///
/// 【エラーの取り方】
///   開始時点の Editor.log の大きさを控え、終わったあとにそこから後ろだけを読む。
///   read_unity_log の next_offset と同じ考え方で、前の作業のエラーを拾わない。
///   ログが開始時点より小さくなっていたら（Editor の再起動などで作り直された）、先頭から読む。
///
/// 【抜き出す行】
///   ・コンパイルエラー    … "error CSxxxx:" を含む行
///   ・コンパイル警告      … "warning CSxxxx:"（include_warnings のときだけ）
///   ・例外                … "XxxException:" で始まる行
///   同じ行は 1 回だけ数える（Unity は同じエラーを複数回書くことがある）。
///
/// compile（dotnet build）は置き換えない。Unity のコンパイルとは一致しないため、
/// 安い事前確認は compile、Unity での最終確認はこの道具、と使い分ける。
/// </summary>
[McpServerToolType]
public static class UnityValidateTools
{
    private static readonly Regex CompileError   = new(@"\berror\s+CS\d+\s*:", RegexOptions.IgnoreCase);
    private static readonly Regex CompileWarning = new(@"\bwarning\s+CS\d+\s*:", RegexOptions.IgnoreCase);
    private static readonly Regex ExceptionLine  = new(@"^\s*[\w.]*Exception\s*:");
    private static readonly Regex IsPlaying      = new(@"\bisPlaying=(true|false)");
    private static readonly Regex IsChanging     = new(@"\bisPlayingOrWillChangePlaymode=(true|false)");

    /// <summary>Play の出入りを待つときに state を読み直す間隔。</summary>
    private const int PollIntervalMs = 500;

    /// <summary>1 行の表示上限。長いスタックの 1 行で応答が膨らむのを防ぐ。</summary>
    private const int MaxLineChars = 300;

    [McpServerTool(Name = "unity_validate_changes", Destructive = false),
     Description("Checks edited scripts in the Unity Editor in one call: leaves Play mode if needed, refreshes and waits for "
               + "importing and compiling, then returns only the compile errors and exceptions logged during this call "
               + "(count and the first max_errors lines). Optionally returns to Play mode. "
               + "Use compile (dotnet build) for a cheap pre-check; use this for the final check in Unity.")]
    public static async Task<string> UnityValidateChanges(
        ServerState state,
        UiDispatcher ui,
        [Description("\"if_needed\" leaves Play mode before refreshing; \"never\" fails instead when the Editor is playing.")] string stop_play_mode = "if_needed",
        [Description("If true and the Editor was playing, enters Play mode again after a successful check.")] bool restore_play_mode = false,
        [Description("If true, compile warnings are reported too.")] bool include_warnings = false,
        [Description("Maximum number of error lines to return. 1 to 200.")] int max_errors = 20,
        [Description("How long the whole check may take, in milliseconds.")] int timeout_ms = UnityProjectTools.DefaultRefreshTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        ui.Log($"unity_validate_changes stop={stop_play_mode} restore={restore_play_mode}");

        max_errors = Math.Clamp(max_errors, 1, 200);
        timeout_ms = Math.Clamp(timeout_ms, 5000, 600_000);
        var sw = Stopwatch.StartNew();

        if (stop_play_mode is not ("if_needed" or "never"))
            return "ERROR: stop_play_mode must be \"if_needed\" or \"never\".";

        var logPath = state.UnityLogPath;
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return $"ERROR: Unity log not found: {logPath}. Pass the Editor's -logFile path to this server with --unity-log.";

        long logStart = new FileInfo(logPath).Length;

        // ---- 1. Play 中なら止める
        var first = await ReadStateAsync(cancellationToken);
        if (first is null)
            return $"ERROR: could not read the Unity Editor state on {EditorControlClient.Endpoint}. Is it open?";

        bool wasPlaying = Flag(IsPlaying, first) || Flag(IsChanging, first);
        if (wasPlaying)
        {
            if (stop_play_mode == "never")
                return "ERROR: the Unity Editor is in Play mode and stop_play_mode is \"never\".";

            var stop = await SendAsync("stop", cancellationToken);
            if (stop.Length > 0) return stop;

            if (!await WaitPlayingAsync(false, sw, timeout_ms, cancellationToken))
                return $"ERROR: the Unity Editor did not leave Play mode within {timeout_ms} ms.";
        }

        // ---- 2. 取り込み・コンパイルを待つ
        int remaining = (int)Math.Max(1000, timeout_ms - sw.ElapsedMilliseconds);
        var refresh = await UnityProjectTools.SendEditorActionAndWait("refresh", ui, remaining, cancellationToken);
        bool refreshed = refresh.StartsWith("OK:", StringComparison.Ordinal);

        // ---- 3. 今回ぶんのログだけを読む
        var (errors, warnings, exceptions, lines) = ScanLog(logPath, logStart, include_warnings);

        var sb = new StringBuilder();
        int problems = errors + exceptions + (include_warnings ? warnings : 0);

        sb.Append(problems == 0 && refreshed ? "OK: " : "ERROR: ");
        sb.Append($"compile_errors={errors}; exceptions={exceptions}");
        if (include_warnings) sb.Append($"; warnings={warnings}");
        sb.Append($"; was_playing={(wasPlaying ? "true" : "false")}; elapsed_ms={sw.ElapsedMilliseconds}\n");

        if (!refreshed) sb.Append("refresh: ").Append(refresh).Append('\n');

        foreach (var line in lines.Take(max_errors)) sb.Append(line).Append('\n');
        if (lines.Count > max_errors)
            sb.Append($"TRUNCATED: shown {max_errors} of {lines.Count} lines. Read the rest with read_unity_log.\n");

        // ---- 4. 必要なら Play へ戻す（問題が無いときだけ）
        if (restore_play_mode && wasPlaying && refreshed && problems == 0)
        {
            var play = await SendAsync("play", cancellationToken);
            if (play.Length > 0) sb.Append("restore: ").Append(play).Append('\n');
            else if (!await WaitPlayingAsync(true, sw, timeout_ms, cancellationToken))
                sb.Append($"restore: the Unity Editor did not enter Play mode within {timeout_ms} ms.\n");
            else
                sb.Append("restore: playing\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static (int Errors, int Warnings, int Exceptions, List<string> Lines) ScanLog(
        string path, long start, bool includeWarnings)
    {
        int errors = 0, warnings = 0, exceptions = 0;
        var lines = new List<string>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < start) start = 0;   // ログが作り直された
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string kind;
            if (CompileError.IsMatch(line)) kind = "error";
            else if (ExceptionLine.IsMatch(line)) kind = "exception";
            else if (includeWarnings && CompileWarning.IsMatch(line)) kind = "warning";
            else continue;

            var text = line.Trim();
            if (!seen.Add(text)) continue;

            switch (kind)
            {
                case "error":     errors++;     break;
                case "exception": exceptions++; break;
                default:          warnings++;   break;
            }

            if (text.Length > MaxLineChars) text = text[..MaxLineChars] + $"… (+{text.Length - MaxLineChars} chars)";
            lines.Add(text);
        }

        return (errors, warnings, exceptions, lines);
    }

    private static bool Flag(Regex pattern, string state)
    {
        var m = pattern.Match(state);
        return m.Success && m.Groups[1].Value == "true";
    }

    /// <summary>state を 1 回読む。読めなければ null（ドメインリロード中など）。</summary>
    private static async Task<string?> ReadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var r = await EditorControlClient.SendAsync("state", 3000, null, cancellationToken);
            return r.Success ? r.Data : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>play / stop を送る。受け付けられたら空文字、断られたら "ERROR: …"。</summary>
    private static async Task<string> SendAsync(string action, CancellationToken cancellationToken)
    {
        try
        {
            var r = await EditorControlClient.SendAsync(action, 5000, null, cancellationToken);
            return r.Success ? string.Empty : $"ERROR: the Unity Editor rejected '{action}': {r.Error}";
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            return UnityProjectTools.StageMessage(ex, 5000);
        }
    }

    /// <summary>
    /// Play の出入りが終わるのを待つ。遷移中はドメインリロードで応答が返らないことがあるので、
    /// 読めない回は飛ばして上限まで待つ。
    /// </summary>
    private static async Task<bool> WaitPlayingAsync(bool playing, Stopwatch sw, int timeoutMs, CancellationToken cancellationToken)
    {
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(PollIntervalMs, cancellationToken);
            var s = await ReadStateAsync(cancellationToken);
            if (s is null) continue;
            if (Flag(IsPlaying, s) == playing && !(Flag(IsChanging, s) && !playing)) return true;
        }
        return false;
    }
}
