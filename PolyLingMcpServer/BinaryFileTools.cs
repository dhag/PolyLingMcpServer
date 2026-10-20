using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PolyLingMcpServer;

/// <summary>
/// バイナリファイルを base64 で読み書きする道具。
///
/// read_file / write_file は文字列として扱うため、PMX・VRM・画像などの
/// バイト列は運べない（文字として解釈した時点で壊れる）。
/// ここではバイト列をそのまま base64 にして運ぶ。
///
/// 【読み取り】
///   範囲は read_file と同じ PathGuard.ResolveReadablePath（プロジェクト内）。
///   1 回で返す量は length で区切る。base64 にした長さが search_text の
///   出力上限（既定 60000 / 上限 400000 文字）と揃うよう、
///   既定 45000 / 上限 300000 バイトとする。大きいファイルは offset をずらして読む。
///
/// 【書き込み】
///   範囲は編集可能フォルダの中、拡張子は PathGuard のバイナリ用許可表。
///   既存ファイルを変える（置き換え・追記）には overwrite=true が要る。
///   write_file と既定が逆なのは、編集可能フォルダ（データとツール）に
///   手作業で作った PMX などが置かれており、取り違えで上書きしないため。
/// </summary>
[McpServerToolType]
public static class BinaryFileTools
{
    /// <summary>read_binary の既定の読み取り量。base64 で 60000 文字（search_text の既定出力上限）。</summary>
    private const int DefaultReadBytes = 45000;

    /// <summary>read_binary の読み取り量の上限。base64 で 400000 文字（search_text の出力上限）。</summary>
    private const int MaxReadBytes = 300000;

    [McpServerTool(Name = "read_binary", ReadOnly = true),
     Description("Reads bytes from a file in the configured Unity project and returns them as base64. "
               + "The first line reports size, offset, length and remaining bytes; the second line is the base64 data. "
               + "Read a large file in pieces by advancing offset.")]
    public static string ReadBinary(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative file path.")] string path,
        [Description("Byte offset to start reading from. 0-based.")] long offset = 0,
        [Description("How many bytes to read. 1 to 300000.")] int length = DefaultReadBytes)
    {
        ui.Log($"read_binary {path} offset={offset} length={length}");

        if (offset < 0)
            return "ERROR: offset must not be negative.";

        length = Math.Clamp(length, 1, MaxReadBytes);

        try
        {
            var fullPath = PathGuard.ResolveReadablePath(state, path);
            if (!File.Exists(fullPath))
                return $"ERROR: File not found: {path}";

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            long size = stream.Length;
            if (offset > size)
                return $"ERROR: offset {offset} is past the end of the file (size={size}).";

            int count = (int)Math.Min(length, size - offset);
            var buffer = new byte[count];

            stream.Seek(offset, SeekOrigin.Begin);

            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0) break;
                read += n;
            }

            var relative = PathGuard.ToDisplayPath(state, fullPath);
            long remaining = size - offset - read;

            return $"{relative} (size={size}, offset={offset}, length={read}, remaining={remaining})\n"
                 + Convert.ToBase64String(buffer, 0, read);
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    [McpServerTool(Name = "write_binary", Destructive = true),
     Description("Writes base64-encoded bytes to a file inside one of the configured editable folders. "
               + "Changing a file that already exists, by replacing or appending, requires overwrite=true. "
               + "Send a large file in pieces: create it with the first piece, then add the rest with append=true and overwrite=true.")]
    public static string WriteBinary(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative destination path. It must be inside one of the editable folders.")] string path,
        [Description("The bytes to write, base64-encoded.")] string content_base64,
        [Description("Must be true to change a file that already exists. Defaults to false.")] bool overwrite = false,
        [Description("If true, the bytes are added to the end of the file instead of replacing it.")] bool append = false)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(content_base64 ?? string.Empty);
        }
        catch (FormatException ex)
        {
            return "ERROR: content_base64 is not valid base64: " + ex.Message;
        }

        ui.Log($"write_binary {path} bytes={bytes.Length} overwrite={overwrite} append={append}");

        try
        {
            var fullPath = PathGuard.ResolveBinaryWritablePath(state, path);

            if (File.Exists(fullPath) && !overwrite)
                return $"ERROR: File already exists: {path}. Set overwrite=true to change it.";

            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using (var stream = new FileStream(
                       fullPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            long size = new FileInfo(fullPath).Length;
            var written = PathGuard.ToDisplayPath(state, fullPath);

            return $"OK: wrote {bytes.Length} bytes to {written}{(append ? " (appended)" : "")}; size={size}";
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }
}
