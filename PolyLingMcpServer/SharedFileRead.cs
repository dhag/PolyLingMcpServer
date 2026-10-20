using System.Text;

namespace PolyLingMcpServer;

/// <summary>
/// 読み取り系ツール（read_file / read_lines / search_text）用のファイル読み込み。
/// 他プロセスが書き込み中のファイル（実行中 Player の Player.log など）も読めるよう、
/// FileShare.ReadWrite | FileShare.Delete で開く。
/// File.ReadAllText と同じく BOM があればそれに従う。
/// </summary>
internal static class SharedFileRead
{
    private const FileShare Share = FileShare.ReadWrite | FileShare.Delete;

    private static FileStream Open(string path, bool async = false) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, Share, 4096,
                       async ? FileOptions.Asynchronous : FileOptions.None);

    public static string ReadAllText(string path, Encoding encoding)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static async Task<string> ReadAllTextAsync(string path, Encoding encoding, CancellationToken cancellationToken)
    {
        await using var stream = Open(path, async: true);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public static IEnumerable<string> ReadLines(string path, Encoding encoding)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }
}
