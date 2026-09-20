using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace PolyLingMcpServer;

/// <summary>
/// 画像を MCP の image content として返す道具。
///
/// 【切り分けのための 2 段】
///   image_selftest … 埋め込みの 150 バイトの PNG を返すだけ。
///                     ファイルにも Unity にも Win32 にも触らない。
///                     これが通らなければ原因は伝送路か SDK の使い方にあり、
///                     キャプチャ側を疑う意味はない。
///   read_image     … プロジェクト内の画像ファイルを返す。
///                     経路に PathGuard と容量検査が加わる。
///
///   キャプチャはこの 2 つが通ってから足す。バイト列さえ手に入れば
///   ImageBytes.TryCreate から先は同じ道を通るので、失敗したときに
///   「撮れていない」のか「送れていない」のかを取り違えずに済む。
///
/// 【read_binary との違い】
///   read_binary は base64 を「文字列」として返す。文字列である以上、
///   Claude はそれを画像として見られない。加えて base64 は 3 文字前後で
///   1 トークンを食うので、500KB の画像なら 20 万トークン近くを
///   何も見えないまま使い切る。画像を見せたいときに read_binary を
///   使ってはいけない。
/// </summary>
[McpServerToolType]
public static class ImageFileTools
{
    /// <summary>
    /// 64x64 の PNG。左上=赤・右上=緑・左下=青・右下=白の 4 分割。
    /// 模様に意味を持たせてあるので、Claude が「赤緑青白の 4 分割」と
    /// 答えられれば、画素が本当に届いていると確認できる。
    /// 文字列として素通ししただけでは、この答えは出てこない。
    /// </summary>
    private const string SelfTestPngBase64 =
          "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAXUlEQVR42u3PMREAIAwEQeS8CJSgEzmxQY+GFOn25gzsqmT03D36"
        + "AgAAAAAAAAAAAAAAAAAAAAAAAAAAAABoA3Jq9DccAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEC7D4SxBYeP7OpQAAAAAElFTkSuQmCC";

    [McpServerTool(Name = "image_selftest", ReadOnly = true),
     Description("Returns a small built-in test image together with a line of text. It touches no file and no "
               + "external process, so it checks only whether this server can deliver image content at all. "
               + "The image is a 64x64 square split into four quadrants: red top-left, green top-right, "
               + "blue bottom-left, white bottom-right.")]
    public static IEnumerable<ContentBlock> ImageSelfTest(UiDispatcher ui)
    {
        ui.Log("image_selftest");

        var bytes = Convert.FromBase64String(SelfTestPngBase64);

        if (!ImageBytes.TryCreate(bytes, "image_selftest.png", out var attachment, out var error))
            return McpContent.Error(error);

        var text = "image_selftest: " + attachment!.Describe()
                 + "\nIf the image arrived, it shows four quadrants: red top-left, green top-right, "
                 + "blue bottom-left, white bottom-right.";

        return McpContent.TextWithImages(text, [attachment]);
    }

    [McpServerTool(Name = "read_image", ReadOnly = true),
     Description("Reads an image file from the configured Unity project and returns it as image content the model "
               + "can actually look at, plus a line of text with its format and size. "
               + "PNG, JPEG, GIF and WebP only. Use this instead of read_binary whenever the point is to see the image: "
               + "read_binary returns base64 text, which the model cannot view and which costs a very large number of tokens.")]
    public static IEnumerable<ContentBlock> ReadImage(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative path of the image file.")] string path)
    {
        ui.Log($"read_image {path}");

        byte[] bytes;
        string relative;

        try
        {
            var fullPath = PathGuard.ResolveReadablePath(state, path);

            if (!File.Exists(fullPath))
                return McpContent.Error($"File not found: {path}");

            // 上限を超えるファイルは読み込む前に断る。600KB の判断のために
            // 数十MB を確保しても捨てるだけになる。
            var info = new FileInfo(fullPath);
            if (info.Length > McpContent.MaxImageBytes)
                return McpContent.Error(
                    $"{path} is {info.Length} bytes, over the {McpContent.MaxImageBytes} byte limit for one tool "
                    + "result (Claude Desktop rejects responses larger than about 1MB). "
                    + "Save a smaller copy, or read it with read_binary if you only need the bytes.");

            bytes = File.ReadAllBytes(fullPath);
            relative = PathGuard.ToDisplayPath(state, fullPath);
        }
        catch (Exception ex)
        {
            return McpContent.Error(ex.Message);
        }

        if (!ImageBytes.TryCreate(bytes, relative, out var attachment, out var error))
            return McpContent.Error(error);

        var images = new[] { attachment! };
        var advice = ImageBytes.SizeAdvice(images);

        var text = attachment!.Describe() + (advice.Length > 0 ? "\n" + advice : string.Empty);

        return McpContent.TextWithImages(text, images);
    }
}
