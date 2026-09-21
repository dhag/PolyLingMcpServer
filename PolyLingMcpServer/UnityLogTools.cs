using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace PolyLingMcpServer;

/// <summary>
/// Unity Editor のログファイル（Editor.log）を読む道具。
///
/// 【なぜ要るか】
///   Unity は Console に出るメッセージ・警告・エラーをすべてログファイルにも書く
///   （Unity マニュアル「Log files」）。コンパイルエラーも実行時の例外もここで読める。
///   Editor.log はプロジェクトの外（既定は %LOCALAPPDATA%\Unity\Editor\Editor.log）に
///   あるため、read_file では読めない。
///
/// 【読むのは 1 ファイルだけ】
///   ServerState.UnityLogPath（--unity-log。省略時は既定の場所）だけを読む。
///   パスは道具の引数では受けない。PathGuard の方針（プロジェクト外は読まない）の例外はこの 1 つ。
///
/// 【Unity の受信口を通らない】
///   ファイルを直接読むので、コンパイル中・ドメインリロード中でも読める。
///
/// 【開き方】
///   Unity が書き込み中のファイルを開くため、FileShare.ReadWrite | FileShare.Delete を付ける。
///
/// 【範囲】
///   read_binary と同じく offset と length で区切る。offset を省略（負）すると末尾から length 分。
///   本文は行の途中で始めず、行の途中で終えない。
///     ・末尾から読んで途中から始まったときは、最初の改行の直後から返す
///     ・終わりは最後の改行の直後で切る。書きかけの行は次回に回す
///     ・改行が 1 つも無いとき、length で切れた（1 行が length より長い）なら読んだ分を返す。
///       ファイル末尾まで改行が無い（書きかけの 1 行だけ）なら何も返さず、next_offset を進めない
///   next_offset を次の offset に渡せば、その後に書かれた分だけを読める。
///   UTF-8 と Shift_JIS では 0x0A が多バイト文字の途中に現れないので、改行で切れば文字も割れない。
/// </summary>
[McpServerToolType]
public static class UnityLogTools
{
    /// <summary>既定の読み取り量。search_text の既定出力上限（60000 文字）に揃える。</summary>
    private const int DefaultReadBytes = 60000;

    /// <summary>読み取り量の上限。search_text の出力上限（400000 文字）に揃える。</summary>
    private const int MaxReadBytes = 400000;

    [McpServerTool(Name = "read_unity_log", ReadOnly = true),
     Description("Reads the Unity Editor log file (Editor.log). It contains everything the Unity Console shows, "
               + "including compile errors and exceptions with stack traces. "
               + "The first line reports path, size, offset, length, next_offset and remaining bytes; the rest is the log text. "
               + "Omit offset to read the end of the log. To read only what is written after an operation, "
               + "call with length=0 before it, keep next_offset, and pass it as offset afterwards. "
               + "Works while the Editor is compiling or reloading, because it reads the file directly.")]
    public static string ReadUnityLog(
        ServerState state,
        UiDispatcher ui,
        [Description("Byte offset to start reading from. Negative (the default) reads the last 'length' bytes.")] long offset = -1,
        [Description("How many bytes to read, 0 to 400000. With offset omitted, 0 returns only the first line, whose next_offset is the current end of the log.")] int length = DefaultReadBytes,
        [Description("Text encoding of the log, as a .NET encoding name. Defaults to utf-8.")] string encoding = "utf-8",
        [Description("Keep only entries of this kind: all (the default), error (compile errors, Debug.LogError and exceptions), "
                   + "warning (compile warnings and Debug.LogWarning), exception, or log (plain Debug.Log messages).")] string severity = "all",
        [Description("Keep only entries whose message or stack trace contains this text. Empty keeps every entry.")] string contains = "",
        [Description("Maximum number of entries to keep. 0 (the default) keeps them all.")] int max_entries = 0,
        [Description("If true, the lines that follow an entry (its stack trace) are kept too.")] bool include_stacktrace = false)
    {
        ui.Log($"read_unity_log offset={offset} length={length} encoding={encoding} severity={severity} max_entries={max_entries}");

        if (!TryParseSeverity(severity, out var wanted))
            return $"ERROR: unknown severity '{severity}'. Use all, error, warning, exception or log.";

        if (!SourceSearchTools.TryResolveEncoding(encoding, out var textEncoding, out var encodingError))
            return encodingError;

        length = Math.Clamp(length, 0, MaxReadBytes);

        var path = state.UnityLogPath;
        if (string.IsNullOrWhiteSpace(path))
            return "ERROR: the Unity log path is not configured.";

        try
        {
            if (!File.Exists(path))
                return $"ERROR: Unity log not found: {path}. "
                     + "If the Editor is started with -logFile, pass the same path to this server with --unity-log.";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            long size = stream.Length;
            if (offset > size)
                return $"ERROR: offset {offset} is past the end of the log (size={size}). Read again without offset, or with offset=0.";

            bool fromTail = offset < 0;
            long start    = fromTail ? Math.Max(0, size - length) : offset;
            int  count    = (int)Math.Min(length, size - start);

            var buffer = new byte[count];
            stream.Seek(start, SeekOrigin.Begin);

            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0) break;
                read += n;
            }

            int begin = 0;

            // 末尾から読んで途中から始まったときは、書きかけの前の行を捨てる。
            if (fromTail && start > 0 && read > 0)
            {
                int firstNewline = Array.IndexOf(buffer, (byte)'\n', 0, read);
                if (firstNewline >= 0) begin = firstNewline + 1;
            }

            // ファイル先頭から読むときは BOM を本文に含めない。
            if (start + begin == 0 && StartsWith(buffer, begin, read, textEncoding.GetPreamble()))
                begin += textEncoding.GetPreamble().Length;

            // 最後の改行の直後で切る。書きかけの行は次回に回す。
            int end = begin;
            if (read > begin)
            {
                int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read - begin);
                if (lastNewline >= begin)
                    end = lastNewline + 1;
                else if (start + read < size)
                    end = read;   // 1 行が length より長い。止まらないよう読んだ分を返す。
                // それ以外はファイル末尾まで書きかけの 1 行だけ。何も返さない。
            }

            long nextOffset = start + end;
            long remaining  = size - nextOffset;
            string text     = textEncoding.GetString(buffer, begin, end - begin);

            string header = $"{path} (size={size}, offset={start + begin}, length={end - begin}, "
                          + $"next_offset={nextOffset}, remaining={remaining})";

            // 絞り込みを 1 つも指定していないときは、これまでどおり本文をそのまま返す
            // （include_stacktrace はここでは見ない。既定のまま全文を返すため）。
            if (wanted == Severity.All && contains.Length == 0 && max_entries <= 0)
                return header + "\n" + text;

            var (filtered, kept, total) = Filter(text, wanted, contains, max_entries, include_stacktrace);
            return header + $" filtered: kind={severity}; entries={kept} of {total}\n" + filtered;
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    // ================================================================
    // 絞り込み
    // ================================================================
    //
    // 【Editor.log の 1 件の形】
    //   本文（1 行以上）
    //   スタック（UnityEngine.Debug:Log… などの行）
    //   空行
    //   (Filename: … Line: …)
    //   空行
    //   空行で区切ったまとまりを 1 件とし、"(Filename:" で始まるまとまりは直前の件に付ける。
    //   本文の中に空行を含むログは、その空行で 2 件に分かれる（Editor.log からは見分けられない）。
    //
    // 【種別】
    //   1 行目とスタックから決める。Editor.log には種別の印が無いため。
    //     exception … 1 行目が "XxxException:"、またはスタックに Debug:LogException
    //     error     … 1 行目にコンパイルエラー、またはスタックに Debug:LogError
    //     warning   … 1 行目にコンパイル警告、またはスタックに Debug:LogWarning
    //     log       … どれにも当たらない通常のログ（Debug.Log）
    //   コンパイルエラー・警告は 1 つのまとまりに複数行並ぶことがあるので、その行を 1 行ずつ 1 件にする。
    //
    // 【severity】
    //   all … 全種別 / error … error と exception / warning / exception / log
    // ================================================================

    private enum Severity { All, Error, Warning, Exception, Log }

    private enum Kind { Log, Warning, Error, Exception }

    private sealed record Entry(Kind Kind, string Head, List<string> Rest);

    private static readonly Regex CompileError   = new(@"\berror\s+CS\d+\s*:", RegexOptions.IgnoreCase);
    private static readonly Regex CompileWarning = new(@"\bwarning\s+CS\d+\s*:", RegexOptions.IgnoreCase);
    private static readonly Regex ExceptionLine  = new(@"^\s*[\w.]*Exception\s*:");

    private static bool TryParseSeverity(string? text, out Severity severity)
    {
        switch ((text ?? "all").Trim().ToLowerInvariant())
        {
            case "":
            case "all":       severity = Severity.All;       return true;
            case "error":     severity = Severity.Error;     return true;
            case "warning":   severity = Severity.Warning;   return true;
            case "exception": severity = Severity.Exception; return true;
            case "log":       severity = Severity.Log;       return true;
            default:          severity = Severity.All;       return false;
        }
    }

    private static bool Wanted(Kind kind, Severity severity) => severity switch
    {
        Severity.All       => true,
        Severity.Error     => kind is Kind.Error or Kind.Exception,
        Severity.Warning   => kind == Kind.Warning,
        Severity.Exception => kind == Kind.Exception,
        Severity.Log       => kind == Kind.Log,
        _                  => false
    };

    /// <summary>ログ本文を 1 件ずつに分ける。</summary>
    private static List<Entry> SplitEntries(string text)
    {
        var entries = new List<Entry>();
        var block = new List<string>();

        void Flush()
        {
            if (block.Count == 0) return;

            // "(Filename: …)" のまとまりは直前の件のスタックの続き。
            if (block[0].StartsWith("(Filename:", StringComparison.Ordinal) && entries.Count > 0)
            {
                entries[^1].Rest.AddRange(block);
                block.Clear();
                return;
            }

            // コンパイルエラー・警告の行が並ぶまとまりは、その行を 1 件ずつにする。
            bool compileBlock = block.Any(l => CompileError.IsMatch(l) || CompileWarning.IsMatch(l));
            if (compileBlock)
            {
                foreach (var l in block)
                {
                    if (CompileError.IsMatch(l))        entries.Add(new Entry(Kind.Error,   l, new List<string>()));
                    else if (CompileWarning.IsMatch(l)) entries.Add(new Entry(Kind.Warning, l, new List<string>()));
                    else if (entries.Count > 0)         entries[^1].Rest.Add(l);
                }
                block.Clear();
                return;
            }

            string head = block[0];
            var rest = block.Skip(1).ToList();
            entries.Add(new Entry(Classify(head, rest), head, rest));
            block.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { Flush(); continue; }
            block.Add(line);
        }
        Flush();
        return entries;
    }

    private static Kind Classify(string head, List<string> rest)
    {
        if (ExceptionLine.IsMatch(head) || rest.Any(l => l.Contains("UnityEngine.Debug:LogException", StringComparison.Ordinal)))
            return Kind.Exception;
        if (CompileError.IsMatch(head) || rest.Any(l => l.Contains("UnityEngine.Debug:LogError", StringComparison.Ordinal)))
            return Kind.Error;
        if (CompileWarning.IsMatch(head) || rest.Any(l => l.Contains("UnityEngine.Debug:LogWarning", StringComparison.Ordinal)))
            return Kind.Warning;
        return Kind.Log;
    }

    private static (string Text, int Kept, int Total) Filter(
        string text, Severity severity, string contains, int maxEntries, bool includeStacktrace)
    {
        var sb = new StringBuilder();
        int kept = 0, total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in SplitEntries(text))
        {
            if (!Wanted(e.Kind, severity)) continue;

            // contains は 1 件の本文（1 行目とスタック）のどこかに含まれれば当たり。
            if (contains.Length > 0
                && e.Head.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0
                && !e.Rest.Any(l => l.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0))
                continue;

            // Unity は同じ行を何度も書く。1 行目が同じものは 1 件にする。
            if (!seen.Add(e.Kind + "|" + e.Head.Trim())) continue;

            total++;
            if (maxEntries > 0 && kept >= maxEntries) continue;

            sb.Append('[').Append(e.Kind.ToString().ToLowerInvariant()).Append("] ").Append(e.Head.Trim()).Append('\n');
            if (includeStacktrace)
                foreach (var l in e.Rest) sb.Append("    ").Append(l.Trim()).Append('\n');
            kept++;
        }

        if (maxEntries > 0 && total > kept)
            sb.Append($"TRUNCATED: kept {kept} of {total} entries. Raise max_entries or read again with offset.\n");

        return (sb.ToString(), kept, total);
    }

    /// <summary>buffer[begin..end) が prefix で始まるか。</summary>
    private static bool StartsWith(byte[] buffer, int begin, int end, byte[] prefix)
    {
        if (prefix.Length == 0 || end - begin < prefix.Length) return false;

        for (int i = 0; i < prefix.Length; i++)
            if (buffer[begin + i] != prefix[i]) return false;

        return true;
    }
}
