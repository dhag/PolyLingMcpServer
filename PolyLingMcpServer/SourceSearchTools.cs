using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace PolyLingMcpServer;

/// <summary>
/// ソースの一部だけを取り出すための道具。
///
/// read_file は 1 ファイル全文しか返せないため、100KB を超える .cs では
/// 目的の数行を得るために毎回全文を運ぶことになる。
/// ここでは grep 形式の検索（search_text）と行範囲の切り抜き（read_lines）を足し、
/// apply_patch の old_text を作るところまでを最小の往復で済ませる。
///
/// 読み取り経路は UnityProjectTools と同じ PathGuard.ResolveReadablePath。
/// 書き込みは一切行わない。
/// </summary>
[McpServerToolType]
public static class SourceSearchTools
{
    /// <summary>
    /// 走査しないディレクトリ名。生成物と VCS 作業領域だけを外す。
    /// Unity の Library / Temp には .cs の複製が入るため、外さないと
    /// 同じ内容が二重に当たる。
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase) { "Library", "Temp", "obj", "bin", ".vs", "Logs" };

    /// <summary>検索対象にするファイルサイズの上限。これを超えたものは数えて飛ばす。</summary>
    private const long MaxSearchFileBytes = 16L * 1024 * 1024;

    /// <summary>read_lines が全文を読む上限。行数を数えるために一度は全体を見る必要がある。</summary>
    private const long MaxReadFileBytes = 8L * 1024 * 1024;

    /// <summary>1 行が長すぎるときに切り詰める文字数。生成コードの 1 行数万文字を防ぐ。</summary>
    private const int MaxLineChars = 500;

    // ================================================================
    // search_text
    // ================================================================

    [McpServerTool(Name = "search_text", ReadOnly = true),
     Description("Searches text files under the Unity project and returns matching lines in grep format \"path:line:text\". "
               + "Context lines use \"path:line-text\" and blocks are separated by \"--\". "
               + "Use this instead of read_file when looking for where something is declared or used. "
               + "Library, Temp, obj, bin, .vs and Logs are never searched.")]
    public static string SearchText(
        ServerState state,
        UiDispatcher ui,
        [Description("Text to find. A literal substring unless regex is true.")] string query,
        [Description("Project-relative directory to search. Empty means the project root.")] string relative_dir = "",
        [Description("File name pattern such as *.cs. Defaults to *.cs.")] string search_pattern = "*.cs",
        [Description("If true, query is a .NET regular expression.")] bool regex = false,
        [Description("If true, matching ignores case.")] bool ignore_case = false,
        [Description("Lines of context shown before each match. 0 to 50.")] int context_before = 0,
        [Description("Lines of context shown after each match. 0 to 50.")] int context_after = 0,
        [Description("Maximum number of matching lines to return. 1 to 2000.")] int max_matches = 100,
        [Description("Maximum number of characters in the result.")] int max_output_chars = 60000,
        [Description(EncodingDescription)] string encoding = "utf-8",
        [Description("If true, returns only the number of matching lines per file (\"path\tcount\"), not the lines themselves. "
                   + "Use it first when a query may match many places.")] bool summary = false)
    {
        ui.Log($"search_text \"{query}\" dir=\"{relative_dir}\" pattern={search_pattern} regex={regex} encoding={encoding} summary={summary}");

        if (string.IsNullOrEmpty(query))
            return "ERROR: query must not be empty.";

        if (!TryResolveEncoding(encoding, out var fileEncoding, out var encodingError))
            return encodingError;

        context_before   = Math.Clamp(context_before, 0, 50);
        context_after    = Math.Clamp(context_after, 0, 50);
        max_matches      = Math.Clamp(max_matches, 1, 2000);
        max_output_chars = Math.Clamp(max_output_chars, 1000, 400000);

        string root;
        try
        {
            root = PathGuard.ResolveReadablePath(state, relative_dir);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }

        if (!Directory.Exists(root))
            return $"ERROR: Directory not found: {relative_dir}";

        Regex? matcher = null;
        if (regex)
        {
            try
            {
                var options = RegexOptions.CultureInvariant
                            | (ignore_case ? RegexOptions.IgnoreCase : RegexOptions.None);

                // 暴走した後方参照でサーバごと止まらないよう上限を掛ける。
                matcher = new Regex(query, options, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException ex)
            {
                return "ERROR: query is not a valid regular expression: " + ex.Message;
            }
        }

        var comparison = ignore_case ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (summary)
            return SummarizeMatches(state, root, search_pattern, fileEncoding, matcher, query, comparison, max_output_chars);

        var sb = new StringBuilder();
        int matches = 0;
        int filesWithMatch = 0;
        int filesScanned = 0;
        int filesSkipped = 0;
        bool truncatedByChars = false;
        bool truncatedByMatches = false;

        // 直前に出力した位置。ここと連続しない行を出すときだけ "--" を挟む。
        string? lastPath = null;
        int lastLine = 0;

        // 1 行を書き出す。max_output_chars に収まらなければ何も書かずに false を返す。
        // 区切りの "--" と本体は必ず一緒に入るかどうかを判定する。
        // 途中まで書いて止めると、行き場のない "--" が末尾に残る。
        bool Emit(string path, int line, string text, char separator)
        {
            if (truncatedByChars) return false;

            bool needsSeparator =
                lastPath != null
                && (!string.Equals(path, lastPath, StringComparison.Ordinal) || line != lastLine + 1);

            var body = $"{path}:{line}{separator}{Trim(text)}";
            int required = body.Length + 1 + (needsSeparator ? 3 : 0);

            if (sb.Length + required > max_output_chars)
            {
                truncatedByChars = true;
                return false;
            }

            if (needsSeparator) sb.Append("--").Append('\n');
            sb.Append(body).Append('\n');

            lastPath = path;
            lastLine = line;
            return true;
        }

        foreach (var file in EnumerateFiles(root, search_pattern))
        {
            if (truncatedByChars || truncatedByMatches) break;

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                filesSkipped++;
                continue;
            }

            if (length > MaxSearchFileBytes)
            {
                filesSkipped++;
                continue;
            }

            string relative;
            try
            {
                relative = PathGuard.ToDisplayPath(state, file);
            }
            catch (Exception)
            {
                filesSkipped++;
                continue;
            }

            filesScanned++;

            // context_before 用の環状の控え。context_before 行だけ持ち回る。
            var pending = context_before > 0 ? new Queue<(int Line, string Text)>() : null;
            int remainingAfter = 0;
            bool fileHit = false;
            int lineNumber = 0;

            try
            {
                foreach (var line in File.ReadLines(file, fileEncoding))
                {
                    lineNumber++;

                    bool hit = matcher != null
                        ? matcher.IsMatch(line)
                        : line.Contains(query, comparison);

                    if (hit)
                    {
                        if (matches >= max_matches)
                        {
                            truncatedByMatches = true;
                            break;
                        }

                        if (pending != null)
                        {
                            while (pending.Count > 0)
                            {
                                var (n, t) = pending.Dequeue();
                                if (!Emit(relative, n, t, '-')) break;
                            }
                        }

                        if (!Emit(relative, lineNumber, line, ':')) break;
                        matches++;

                        if (!fileHit)
                        {
                            fileHit = true;
                            filesWithMatch++;
                        }

                        remainingAfter = context_after;
                    }
                    else if (remainingAfter > 0)
                    {
                        if (!Emit(relative, lineNumber, line, '-')) break;
                        remainingAfter--;
                    }
                    else if (pending != null)
                    {
                        pending.Enqueue((lineNumber, line));
                        if (pending.Count > context_before) pending.Dequeue();
                    }

                    if (truncatedByChars) break;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return $"ERROR: the regular expression took too long on {relative}:{lineNumber}. Use a simpler query.";
            }
            catch (Exception)
            {
                // 読めないファイル（占有中・不正なエンコード）は数えて次へ。
                filesSkipped++;
            }
        }

        if (matches == 0)
            return $"No matches. files_scanned={filesScanned}; files_skipped={filesSkipped}";

        if (truncatedByMatches)
            sb.Append($"TRUNCATED: matches={max_matches} (max_matches reached).\n");
        else if (truncatedByChars)
            sb.Append($"TRUNCATED: output limit {max_output_chars} chars reached; matches so far={matches}.\n");

        sb.Append($"-- matches={matches}; files_with_match={filesWithMatch}; files_scanned={filesScanned}; files_skipped={filesSkipped}");
        return sb.ToString();
    }

    /// <summary>
    /// search_text の summary=true。ファイルごとの一致行数だけを返す。
    /// 行本文を運ばないので、当たりが多い問い合わせの見当付けに使う。
    /// 出力が max_output_chars を超えるところで止め、止めたことを明示する。
    /// </summary>
    private static string SummarizeMatches(
        ServerState state, string root, string searchPattern, Encoding fileEncoding,
        Regex? matcher, string query, StringComparison comparison, int maxOutputChars)
    {
        var sb = new StringBuilder();
        int totalMatches = 0, filesWithMatch = 0, filesScanned = 0, filesSkipped = 0, filesListed = 0;
        bool truncated = false;

        foreach (var file in EnumerateFiles(root, searchPattern))
        {
            try
            {
                if (new FileInfo(file).Length > MaxSearchFileBytes) { filesSkipped++; continue; }
            }
            catch (Exception) { filesSkipped++; continue; }

            filesScanned++;
            int count = 0;
            try
            {
                foreach (var line in File.ReadLines(file, fileEncoding))
                {
                    bool hit = matcher != null ? matcher.IsMatch(line) : line.Contains(query, comparison);
                    if (hit) count++;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return $"ERROR: the regular expression took too long on {PathGuard.ToDisplayPath(state, file)}. Use a simpler query.";
            }
            catch (Exception) { filesSkipped++; continue; }

            if (count == 0) continue;
            totalMatches += count;
            filesWithMatch++;

            if (truncated) continue;
            var row = $"{PathGuard.ToDisplayPath(state, file)}\t{count}\n";
            if (sb.Length + row.Length > maxOutputChars) { truncated = true; continue; }
            sb.Append(row);
            filesListed++;
        }

        if (totalMatches == 0)
            return $"No matches. files_scanned={filesScanned}; files_skipped={filesSkipped}";

        if (truncated)
            sb.Append($"TRUNCATED: listed {filesListed} of {filesWithMatch} files (output limit {maxOutputChars} chars).\n");

        sb.Append($"-- matches={totalMatches}; files_with_match={filesWithMatch}; files_scanned={filesScanned}; files_skipped={filesSkipped}");
        return sb.ToString();
    }

    // ================================================================
    // read_lines
    // ================================================================

    [McpServerTool(Name = "read_lines", ReadOnly = true),
     Description("Returns one slice of a text file by line number, so a large source file can be inspected without reading all of it. "
               + "The first line of the result reports the total line count and the end-of-line style. "
               + "Set with_line_numbers=false to get text that can be pasted straight into apply_patch as old_text.")]
    public static string ReadLines(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative file path.")] string path,
        [Description("First line to return. 1-based.")] int start_line = 1,
        [Description("How many lines to return. 1 to 2000.")] int line_count = 200,
        [Description("If true, every line is prefixed with its line number.")] bool with_line_numbers = true,
        [Description(EncodingDescription)] string encoding = "utf-8")
    {
        ui.Log($"read_lines {path} start={start_line} count={line_count} encoding={encoding}");

        start_line = Math.Max(start_line, 1);
        line_count = Math.Clamp(line_count, 1, 2000);

        if (!TryResolveEncoding(encoding, out var fileEncoding, out var encodingError))
            return encodingError;

        string fullPath;
        try
        {
            fullPath = PathGuard.ResolveReadablePath(state, path);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }

        if (!File.Exists(fullPath))
            return $"ERROR: File not found: {path}";

        string text;
        string relative;
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxReadFileBytes)
                return $"ERROR: File is too large ({info.Length} bytes). Limit is {MaxReadFileBytes} bytes.";

            text = File.ReadAllText(fullPath, fileEncoding);
            relative = PathGuard.ToDisplayPath(state, fullPath);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }

        var lines = SplitLines(text, out string eol);

        if (lines.Count == 0)
            return $"{relative} (total=0, empty file)";

        if (start_line > lines.Count)
            return $"ERROR: start_line {start_line} is past the end of the file (total={lines.Count}).";

        int lastLine = Math.Min(start_line + line_count - 1, lines.Count);

        // 生テキストとして返すときは元の改行で綴じる。
        // LF で綴じたものを CRLF のファイルへ apply_patch すると一致しない。
        var newline = eol == "CRLF" ? "\r\n" : "\n";

        var sb = new StringBuilder();
        sb.Append($"{relative} (total={lines.Count}, shown={start_line}..{lastLine}, eol={eol})").Append('\n');

        for (int i = start_line; i <= lastLine; i++)
        {
            if (with_line_numbers)
            {
                sb.Append(i.ToString().PadLeft(6)).Append(": ").Append(lines[i - 1]).Append('\n');
            }
            else
            {
                sb.Append(lines[i - 1]);
                if (i < lastLine) sb.Append(newline);
            }
        }

        return sb.ToString();
    }

    // ================================================================
    // 補助
    // ================================================================

    /// <summary>encoding 引数の説明。read_file / read_lines / search_text で共有する。</summary>
    internal const string EncodingDescription =
        "Text encoding used to decode the file, as a .NET encoding name such as utf-8 or shift_jis. "
        + "MQO files are shift_jis. Defaults to utf-8.";

    /// <summary>
    /// encoding 引数を Encoding にする。空なら UTF-8（従来の既定）。
    /// shift_jis などのコードページ系は Program が起動時に
    /// CodePagesEncodingProvider を登録しているので取得できる。
    /// 解決できない名前は error に理由を入れて false を返す。
    /// </summary>
    internal static bool TryResolveEncoding(string? name, out Encoding encoding, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            encoding = Encoding.UTF8;
            return true;
        }

        try
        {
            encoding = Encoding.GetEncoding(name.Trim());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            encoding = Encoding.UTF8;
            error = $"ERROR: unknown encoding '{name}'. Use a .NET encoding name such as utf-8 or shift_jis.";
            return false;
        }
    }

    /// <summary>
    /// 除外ディレクトリを飛ばしながら、名前順にファイルを返す。
    /// Directory.EnumerateFiles の AllDirectories では除外が書けないため自前で降りる。
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "*.cs";

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current, pattern);
            }
            catch (Exception)
            {
                files = [];
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) yield return file;

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                directories = [];
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);

            // Stack なので、名前順に取り出すには逆順で積む。
            for (int i = directories.Length - 1; i >= 0; i--)
            {
                var name = Path.GetFileName(directories[i]);
                if (ExcludedDirectories.Contains(name)) continue;
                stack.Push(directories[i]);
            }
        }
    }

    /// <summary>長すぎる行を切り、切ったことを末尾に書き添える。</summary>
    private static string Trim(string line)
    {
        if (line.Length <= MaxLineChars) return line;
        return string.Concat(line.AsSpan(0, MaxLineChars), $"… (+{line.Length - MaxLineChars} chars)");
    }

    /// <summary>
    /// 改行で分ける。改行そのものは行に含めず、種類だけを eol で返す。
    /// 末尾が改行で終わるファイルでは空の最終行を作らない。
    /// </summary>
    private static List<string> SplitLines(string text, out string eol)
    {
        var lines = new List<string>();
        bool crlf = false, lf = false, cr = false;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\r')
            {
                lines.Add(text.Substring(start, i - start));

                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }

                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(text.Substring(start, i - start));
                lf = true;
                start = i + 1;
            }
        }

        if (start < text.Length) lines.Add(text[start..]);

        int kinds = (crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0);
        eol = kinds > 1 ? "mixed"
            : crlf ? "CRLF"
            : cr ? "CR"
            : "LF";

        return lines;
    }
}
