using ModelContextProtocol.Server;
using System.ComponentModel;

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
        [Description("Text encoding of the log, as a .NET encoding name. Defaults to utf-8.")] string encoding = "utf-8")
    {
        ui.Log($"read_unity_log offset={offset} length={length} encoding={encoding}");

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

            return $"{path} (size={size}, offset={start + begin}, length={end - begin}, "
                 + $"next_offset={nextOffset}, remaining={remaining})\n"
                 + text;
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
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
