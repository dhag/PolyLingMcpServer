using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyLingMcpServer;

/// <summary>
/// PolyLing の画面を 1 回の道具呼び出しで撮って見る。
///
/// 【なぜ 1 つにまとめるか】
///   手でやると unity_focus → uiCapture → uiCaptureStatus → read_image の
///   4 往復になる。往復そのものより困るのは次の 2 点。
///     1. uiCaptureStatus が返すのは絶対パスで、read_image はプロジェクト相対を取る。
///        変換を呼び出し側が毎回手で行うことになる。
///     2. 撮影はフレーム終端で行われる。Unity が背面で Run In Background がオフだと
///        フレームが進まず、状態は pending のまま返らない。つまり focus は
///        「忘れないようにする心がけ」ではなく撮影手順の一部。
///   どちらもここで閉じれば、呼び出し側が間違えようがなくなる。
///
/// 【画像はファイル経由で受け取る】
///   Editor に base64 を応答へ載せてもらう道（EditorImage.Lift）もあるが、
///   EditorControlClient は 1 行 1 JSON で往復するので、1 行が数十万文字になる。
///   撮影結果は PolyLing 側がどのみち PNG として保存するため、
///   そのファイルをこのサーバが読む方が伝送路に負荷をかけない。
///
/// 【縮小はしない】
///   600,000 バイトを超えたら断るだけで、縮めて送り直すことはしない。
///   このプロジェクトは画像ライブラリを参照しておらず、ImageBytes も
///   ヘッダを読むだけで復号はしない。縮小を入れるなら撮影側（uiCapture）の仕事。
/// </summary>
[McpServerToolType]
public static class UnityCaptureTools
{
    /// <summary>状態を見に行く間隔。撮影はフレーム 1 回分なので、たいてい初回で完了している。</summary>
    private const int PollIntervalMs = 100;

    /// <summary>Editor との 1 往復に許す時間。全体の残り時間と小さい方を使う。</summary>
    private const int EditorCallTimeoutMs = 10_000;

    /// <summary>uiCapture / uiCaptureStatus は現在のモデルを見ないが、経路は modelIndex を要求する。</summary>
    private const string UnusedModelIndex = "0";

    [McpServerTool(Name = "unity_capture", Destructive = false),
     Description("Photographs the PolyLing screen and returns the picture as image content the model can look at. "
               + "It brings the Unity window to the front, takes the shot, waits for it, and reads the resulting PNG, "
               + "so one call replaces unity_focus, uiCapture, uiCaptureStatus and read_image. "
               + "Requires the PolyLing panel to be open and the Editor to be in Play mode. "
               + "The file is kept, and its project-relative path is reported, so read_image can show it again later.")]
    public static async Task<IEnumerable<ContentBlock>> UnityCapture(
        ServerState state,
        UiDispatcher ui,
        [Description("What to photograph: MainView (the main 3D view), TriView (the viewport area including the three orthographic views), or Window (the whole window). "
                   + "Window is the only one that shows the panels, but it is also the one that exceeds the size limits first.")]
        string target = "MainView",
        [Description("Destination folder, relative to the PolyLing work folder; '.' is the work folder itself, and missing folders are created. "
                   + "It has to end up inside one of this server's roots, because that is all it may read. "
                   + "An empty string means the folder set in the capture panel, which may well be somewhere else.")]
        string folder = ".",
        [Description("Base of the file name. A serial number and .png are appended.")]
        string base_name = "PolyLingCapture",
        [Description("Bring the Unity window to the front before shooting. Leave this on unless the window is known to be in front already: "
                   + "the shot is taken at the end of a frame, and a background Editor with Run In Background off never gets there.")]
        bool focus = true,
        [Description("Panel id to photograph instead of target, as listed by the PolyLing command uiDescribe. "
                   + "The panel has to be open. Use it to check one panel without the whole window.")]
        string panel_id = "",
        [Description("Longest edge in pixels. A larger shot is scaled down to this before it is saved. 0 keeps the original size. "
                   + "Anything above 1568 is scaled down again on the way to the model, so it only costs bytes.")]
        int max_long_edge = 0,
        [Description("File format: png (the default) or jpeg. jpeg is smaller for shaded views but blurs thin lines and text.")]
        string format = "png",
        [Description("jpeg quality, 1 to 100. Ignored for png.")]
        int quality = 90,
        [Description("How long to wait for the whole thing, in milliseconds.")]
        int timeout_ms = 15000,
        CancellationToken cancellationToken = default)
    {
        ui.Log($"unity_capture target={target} panel_id={panel_id} folder={folder} base_name={base_name} "
             + $"max_long_edge={max_long_edge} format={format} focus={focus}");

        if (timeout_ms < 1000) timeout_ms = 1000;

        var watch = Stopwatch.StartNew();
        var deadline = TimeSpan.FromMilliseconds(timeout_ms);

        // 前面化の結果は失敗したときだけ文言に混ぜる。成功したときの診断文字列は長く、
        // 毎回付けると画像の説明が読みにくくなる。
        string focusDetail = "focus=skipped";
        if (focus)
        {
            var focusResult = UnityWindowTools.UnityFocus(ui);
            focusDetail = focusResult.StartsWith("ERROR:", StringComparison.Ordinal)
                ? "focus failed: " + focusResult
                : "focus=ok";
        }

        // ---- 撮影を始める ----

        var startArgs = new Dictionary<string, string>
        {
            ["command"]    = "uiCapture",
            ["modelIndex"] = UnusedModelIndex,
            ["target"]      = target,
            ["baseName"]    = base_name,
            ["folder"]      = folder ?? "",
            ["panelId"]     = panel_id ?? "",
            ["maxLongEdge"] = Math.Max(0, max_long_edge).ToString(),
            ["format"]      = string.IsNullOrWhiteSpace(format) ? "png" : format.Trim(),
            ["quality"]     = Math.Clamp(quality, 1, 100).ToString(),
        };

        var start = await CallEditorAsync(startArgs, Remaining(watch, deadline), cancellationToken)
                          .ConfigureAwait(false);

        if (!start.Ok)
            return McpContent.Error($"uiCapture failed: {start.Error} ({focusDetail})");

        var captureId = Text(start.Result, "captureId");
        if (string.IsNullOrEmpty(captureId))
            return McpContent.Error($"uiCapture returned no captureId. Raw answer: {start.Raw}");

        // ---- 終わるのを待つ ----

        var statusArgs = new Dictionary<string, string>
        {
            ["command"]    = "uiCaptureStatus",
            ["modelIndex"] = UnusedModelIndex,
            ["captureId"]  = captureId,
        };

        string status = Text(start.Result, "status");
        string path   = string.Empty;
        string shotError = string.Empty;
        int polls = 0;

        while (!string.Equals(status, "completed", StringComparison.Ordinal)
            && !string.Equals(status, "failed",    StringComparison.Ordinal))
        {
            if (watch.Elapsed >= deadline)
                return McpContent.Error(
                    $"the shot was still pending after {watch.ElapsedMilliseconds}ms ({polls} checks). "
                    + "The picture is taken at the end of a frame, so this usually means no frame is finishing: "
                    + "the Unity window is not in front and Run In Background is off, or the Editor is paused or not playing. "
                    + $"captureId={captureId}; {focusDetail}");

            await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            polls++;

            var poll = await CallEditorAsync(statusArgs, Remaining(watch, deadline), cancellationToken)
                             .ConfigureAwait(false);

            if (!poll.Ok)
                return McpContent.Error($"uiCaptureStatus failed: {poll.Error} (captureId={captureId})");

            status    = Text(poll.Result, "status");
            path      = Text(poll.Result, "path");
            shotError = Text(poll.Result, "error");
        }

        if (string.Equals(status, "failed", StringComparison.Ordinal))
            return McpContent.Error($"the shot failed: {shotError} ({focusDetail})");

        if (string.IsNullOrEmpty(path))
            return McpContent.Error($"the shot completed but reported no path. captureId={captureId}");

        // ---- 撮れたファイルを読む ----

        byte[] bytes;
        string relative;

        try
        {
            var fullPath = PathGuard.ResolveReadableAbsolutePath(state, path);

            if (!File.Exists(fullPath))
                return McpContent.Error($"the shot reported {path}, but the file is not there.");

            // 読む前に大きさで断る。600KB を判断するために数MB を確保しても捨てるだけになる。
            var info = new FileInfo(fullPath);
            if (info.Length > McpContent.MaxImageBytes)
                return McpContent.Error(
                    $"{path} is {info.Length} bytes, over the {McpContent.MaxImageBytes} byte limit for one tool result. "
                    + "The file is on disk and can be opened directly. "
                    + "For a picture this tool can return, shoot target=MainView or target=TriView instead of Window, "
                    + "or make the Unity window smaller before shooting.");

            bytes    = File.ReadAllBytes(fullPath);
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

        var text = attachment!.Describe()
                 + $"\ntarget={target}; {watch.ElapsedMilliseconds}ms; {polls} status check(s)"
                 + (focusDetail.StartsWith("focus failed", StringComparison.Ordinal) ? $"\n{focusDetail}" : string.Empty)
                 + (advice.Length > 0 ? "\n" + advice : string.Empty);

        return McpContent.TextWithImages(text, images);
    }

    /// <summary>全体の期限から、この 1 往復に許す時間を決める。</summary>
    private static int Remaining(Stopwatch watch, TimeSpan deadline)
    {
        var left = (int)(deadline - watch.Elapsed).TotalMilliseconds;
        return Math.Clamp(left, 1000, EditorCallTimeoutMs);
    }

    /// <summary>
    /// PolyLing のコマンドを 1 つ実行し、応答の "result" を返す。
    ///
    /// polyling_call と同じ経路（action="call"）を通るが、あちらは生 JSON を
    /// 文字列として返すのが仕事なのに対し、こちらは中の値を読む。
    /// </summary>
    private static async Task<EditorCall> CallEditorAsync(
        IReadOnlyDictionary<string, string> parameters, int timeoutMs, CancellationToken cancellationToken)
    {
        EditorControlResult result;

        try
        {
            result = await EditorControlClient.SendAsync("call", timeoutMs, parameters, cancellationToken)
                                              .ConfigureAwait(false);
        }
        catch (EditorControlClient.EditorControlException ex)
        {
            var where = ex.Stage == EditorControlClient.FailStage.Connect
                ? $"could not connect to {EditorControlClient.Endpoint} within {ex.ElapsedMs}ms. Is the Unity Editor open?"
                : $"the Unity Editor did not answer within {ex.ElapsedMs}ms. Is it compiling or importing?";

            return EditorCall.Failed(where);
        }
        catch (Exception ex)
        {
            return EditorCall.Failed($"could not reach the Unity Editor on {EditorControlClient.Endpoint}. {ex.Message}");
        }

        if (!result.Success)
            return EditorCall.Failed(string.IsNullOrEmpty(result.Error) ? "the Editor rejected the command." : result.Error);

        JsonObject? payload = null;
        try
        {
            if (JsonNode.Parse(result.Raw) is JsonObject root)
                payload = root["result"] as JsonObject;
        }
        catch (JsonException)
        {
            // 読めない応答は payload なしで返す。呼び出し側が Raw を文言に出す。
        }

        return new EditorCall(true, payload, string.Empty, result.Raw);
    }

    private static string Text(JsonObject? result, string key)
        => result?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private readonly record struct EditorCall(bool Ok, JsonObject? Result, string Error, string Raw)
    {
        public static EditorCall Failed(string error) => new(false, null, error, string.Empty);
    }
}
