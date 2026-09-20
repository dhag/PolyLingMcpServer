using System.Buffers.Binary;
using System.Text;

namespace PolyLingMcpServer;

/// <summary>
/// MCP へ載せる 1 枚の画像。バイト列と、説明テキストに出す素性をまとめて持つ。
/// 生成は ImageBytes.TryCreate だけが行う。検査を通っていない画像を
/// 作れないようにして、容量と形式の判断を 1 か所に閉じ込める。
/// </summary>
public sealed record ImageAttachment(
    ReadOnlyMemory<byte> Bytes, string MimeType, int Width, int Height, string Label)
{
    /// <summary>寸法が読めなかったときは 0 が入る。</summary>
    public bool HasSize => Width > 0 && Height > 0;

    /// <summary>長辺が Claude 側の縮小閾値を超えているか。</summary>
    public bool IsOversized => HasSize && Math.Max(Width, Height) > McpContent.RecommendedLongEdge;

    /// <summary>説明テキストに出す 1 行。</summary>
    public string Describe()
    {
        var size = HasSize ? $"{Width}x{Height}" : "size unknown";
        return $"{Label} ({MimeType}, {size}, {Bytes.Length} bytes)";
    }
}

/// <summary>
/// バイト列から画像形式と寸法を見分ける。
///
/// 【なぜ拡張子で判断しないか】
///   MCP の image content は mimeType を必須にしており、ここを間違えると
///   Claude 側で復号に失敗する。拡張子は呼び出し側の申告でしかなく、
///   キャプチャ結果のようにファイル名を持たない経路もある。先頭バイトで見る。
///
/// 【なぜ寸法まで読むか】
///   長辺が 1568px を超える画像は Claude 側で縮小されるだけで、
///   文字が読みやすくなるわけではない。送る前に気づけるよう応答に出す。
///   縮小そのものはキャプチャ側の仕事なので、ここでは判断材料を出すに留める。
///
/// 対応形式は Claude が受け取れる JPEG / PNG / GIF / WebP のみ。
/// BMP は受け取れないため、名指しで断る（GDI+ の既定出力を素通しすると
/// 「なぜか認識されない画像」になって切り分けが難しくなる）。
/// </summary>
public static class ImageBytes
{
    public const string Png  = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif  = "image/gif";
    public const string WebP = "image/webp";

    /// <summary>
    /// 検査済みの ImageAttachment を作る。作れない理由は error に入れて false を返す。
    /// </summary>
    public static bool TryCreate(
        ReadOnlyMemory<byte> bytes, string label, out ImageAttachment? attachment, out string error)
    {
        attachment = null;

        if (bytes.Length == 0)
        {
            error = $"{label} is empty.";
            return false;
        }

        if (bytes.Length > McpContent.MaxImageBytes)
        {
            error = $"{label} is {bytes.Length} bytes, over the {McpContent.MaxImageBytes} byte limit for one "
                  + "tool result (Claude Desktop rejects responses larger than about 1MB). "
                  + "Shrink the image or lower its quality before returning it.";
            return false;
        }

        var span = bytes.Span;

        if (!TryIdentify(span, out var mimeType, out var rejected))
        {
            error = rejected.Length > 0
                ? $"{label} is {rejected}, which Claude cannot read. Use PNG, JPEG, GIF or WebP."
                : $"{label} is not a PNG, JPEG, GIF or WebP image.";
            return false;
        }

        TryReadSize(span, mimeType, out int width, out int height);

        attachment = new ImageAttachment(bytes, mimeType, width, height, label);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 先頭バイトから MIME を決める。読めた形式のうち Claude が受け取れないものは
    /// rejected にその名前を入れて false を返す（「不明」と区別する）。
    /// </summary>
    public static bool TryIdentify(ReadOnlySpan<byte> data, out string mimeType, out string rejected)
    {
        mimeType = string.Empty;
        rejected = string.Empty;

        if (data.Length >= 8 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            mimeType = Png;
            return true;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            mimeType = Jpeg;
            return true;
        }

        if (data.Length >= 6 && data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F')
        {
            mimeType = Gif;
            return true;
        }

        if (data.Length >= 12 &&
            Matches(data, 0, "RIFF") && Matches(data, 8, "WEBP"))
        {
            mimeType = WebP;
            return true;
        }

        if (data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            rejected = "a BMP";
            return false;
        }

        if (data.Length >= 4 && Matches(data, 0, "II*\0"))
        {
            rejected = "a TIFF";
            return false;
        }

        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01 && data[3] == 0x00)
        {
            rejected = "an ICO";
            return false;
        }

        return false;
    }

    /// <summary>
    /// ヘッダだけから幅と高さを読む。読めなければ 0 を返す。
    /// 復号はしないので、壊れた画像でも寸法表示のためだけに時間を使わない。
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> data, string mimeType, out int width, out int height)
    {
        width = 0;
        height = 0;

        return mimeType switch
        {
            Png  => TryReadPngSize(data, out width, out height),
            Jpeg => TryReadJpegSize(data, out width, out height),
            Gif  => TryReadGifSize(data, out width, out height),
            WebP => TryReadWebPSize(data, out width, out height),
            _    => false,
        };
    }

    /// <summary>IHDR は署名の直後に必ず来る。幅と高さはビッグエンディアン。</summary>
    private static bool TryReadPngSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (d.Length < 24) return false;

        width  = BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4));
        return width > 0 && height > 0;
    }

    /// <summary>
    /// SOFn セグメントを探す。EXIF などの前置きは長さを見て読み飛ばす。
    /// SOS（0xDA）に達したら画素データなので、そこで打ち切る。
    /// </summary>
    private static bool TryReadJpegSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = 0;
        height = 0;

        int i = 2;

        while (i + 3 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }

            byte marker = d[i + 1];

            // 詰め物の 0xFF と、長さを持たないマーカー。
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xD9 || marker == 0xDA) return false;

            int length = (d[i + 2] << 8) | d[i + 3];
            if (length < 2) return false;

            bool isStartOfFrame =
                   (marker >= 0xC0 && marker <= 0xC3)
                || (marker >= 0xC5 && marker <= 0xC7)
                || (marker >= 0xC9 && marker <= 0xCB)
                || (marker >= 0xCD && marker <= 0xCF);

            if (isStartOfFrame)
            {
                if (i + 8 >= d.Length) return false;

                height = (d[i + 5] << 8) | d[i + 6];
                width  = (d[i + 7] << 8) | d[i + 8];
                return width > 0 && height > 0;
            }

            i += 2 + length;
        }

        return false;
    }

    /// <summary>論理画面の大きさ。リトルエンディアン。</summary>
    private static bool TryReadGifSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (d.Length < 10) return false;

        width  = d[6] | (d[7] << 8);
        height = d[8] | (d[9] << 8);
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 先頭チャンクの種類で読み方が変わる。
    /// VP8X は canvas の大きさ、VP8 / VP8L はフレームの大きさを持つ。
    /// </summary>
    private static bool TryReadWebPSize(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (d.Length < 30) return false;

        // 12 からチャンク ID、16 からチャンク長、20 から中身。
        if (Matches(d, 12, "VP8X"))
        {
            width  = (d[24] | (d[25] << 8) | (d[26] << 16)) + 1;
            height = (d[27] | (d[28] << 8) | (d[29] << 16)) + 1;
            return true;
        }

        if (Matches(d, 12, "VP8 "))
        {
            // 中身の先頭 3 バイトがフレームタグ、続く 3 バイトが開始コード。
            if (d.Length < 30 || d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return false;

            width  = (d[26] | (d[27] << 8)) & 0x3FFF;
            height = (d[28] | (d[29] << 8)) & 0x3FFF;
            return width > 0 && height > 0;
        }

        if (Matches(d, 12, "VP8L"))
        {
            if (d.Length < 25 || d[20] != 0x2F) return false;

            int bits = d[21] | (d[22] << 8) | (d[23] << 16) | (d[24] << 24);
            width  = (bits & 0x3FFF) + 1;
            height = ((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        return false;
    }

    private static bool Matches(ReadOnlySpan<byte> data, int offset, string ascii)
    {
        if (offset + ascii.Length > data.Length) return false;

        for (int i = 0; i < ascii.Length; i++)
            if (data[offset + i] != (byte)ascii[i]) return false;

        return true;
    }

    /// <summary>説明テキストに足す注意書き。無ければ空文字。</summary>
    public static string SizeAdvice(IReadOnlyList<ImageAttachment> images)
    {
        var oversized = images.Where(image => image.IsOversized).ToArray();
        if (oversized.Length == 0) return string.Empty;

        var note = new StringBuilder();
        note.Append($"NOTE: {oversized.Length} image(s) have a long edge over {McpContent.RecommendedLongEdge}px "
                  + "and will be scaled down before the model sees them, which costs bytes without adding detail.");

        return note.ToString();
    }
}
