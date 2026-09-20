using ModelContextProtocol.Protocol;

namespace PolyLingMcpServer;

/// <summary>
/// 道具の戻り値を MCP の ContentBlock 列へ組み立てる唯一の場所。
///
/// 【なぜ層を分けるか】
///   これまでの道具はすべて string を返しており、SDK がそれを
///   TextContentBlock 1 個へ包んでいた（McpServerTool の戻り値変換表）。
///   文字列しか返せない以上、画像は原理的に送れない。
///   画像を返す道具は IEnumerable&lt;ContentBlock&gt; を返す必要があるが、
///   各道具がそれぞれ ContentBlock を組み立て始めると、
///   容量上限と失敗時の文言が道具の数だけ散らばる。ここに集約する。
///
/// 【失敗の表し方】
///   既存の道具は "ERROR: ..." で始まる文字列を返す約束になっている。
///   ContentBlock を返す道具でもその約束を崩さない（Error がそれを守る）。
///   プロトコル本来の CallToolResult.IsError へ切り替えるなら、
///   この 1 メソッドを書き換えれば全道具が一度に変わる。
///
/// 【画像を先に置く理由】
///   Anthropic の vision ガイドが「画像はテキストより前に置いた方が
///   結果が良い」としているため、画像ブロックを先、説明テキストを後にする。
/// </summary>
public static class McpContent
{
    /// <summary>
    /// 1 回の道具応答に載せてよい画像バイト数（base64 に直す前）の上限。
    ///
    /// Claude Desktop は道具の応答全体におよそ 1MB の上限を持つ。
    /// base64 は 3 バイトを 4 文字にするので、600000 バイトで 800000 文字。
    /// 残りを JSON-RPC の枠と説明テキストの取り分として空けてある。
    /// 複数枚を返す場合は合計に対して効く。
    /// </summary>
    public const int MaxImageBytes = 600_000;

    /// <summary>
    /// これを超える長辺は Claude 側で縮小されるだけで、情報は増えない。
    /// 送信は止めず、説明テキストに注意書きを足すためだけに使う。
    /// </summary>
    public const int RecommendedLongEdge = 1568;

    /// <summary>テキストだけを返す。従来どおりの応答。</summary>
    public static IEnumerable<ContentBlock> Text(string text)
        => [new TextContentBlock { Text = text }];

    /// <summary>失敗を返す。既存の道具と同じ "ERROR: " 始まりに揃える。</summary>
    public static IEnumerable<ContentBlock> Error(string message)
        => Text(message.StartsWith("ERROR:", StringComparison.Ordinal) ? message : "ERROR: " + message);

    /// <summary>
    /// 画像とテキストを一度に返す。images が空ならテキストだけの応答と同じになる。
    /// </summary>
    public static IEnumerable<ContentBlock> TextWithImages(string text, IReadOnlyList<ImageAttachment> images)
    {
        var blocks = new List<ContentBlock>(images.Count + 1);

        foreach (var image in images)
            blocks.Add(ImageContentBlock.FromBytes(image.Bytes, image.MimeType));

        blocks.Add(new TextContentBlock { Text = text });
        return blocks;
    }

    /// <summary>
    /// 合計が容量上限に収まるかを見る。1 枚ずつの検査は ImageBytes.TryCreate が
    /// 済ませているので、ここは複数枚のときだけ意味を持つ。
    /// </summary>
    public static bool FitsBudget(IReadOnlyList<ImageAttachment> images, out string error)
    {
        long total = 0;
        foreach (var image in images)
            total += image.Bytes.Length;

        if (total <= MaxImageBytes)
        {
            error = string.Empty;
            return true;
        }

        error = $"the images total {total} bytes, over the {MaxImageBytes} byte limit for one tool result "
              + "(Claude Desktop rejects responses larger than about 1MB). Return fewer or smaller images.";
        return false;
    }
}
