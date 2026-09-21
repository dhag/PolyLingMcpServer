using System.Text;
using System.Text.Json;
using HagLib.NET.Duplex;

namespace PolyLingMcpServer;

/// <summary>
/// Talks to the Unity Editor over a loopback TCP port.
///
/// The transport files from com.haglib.net_duplexchannel are compiled into this
/// project by csproj Link items; they have no Unity and no third-party dependency.
///
/// 【なぜ名前付きパイプをやめたか】 2026-09-08
///   PipeDuplexServer では 3 回に 2 回、ConnectAsync は成功するのにサーバの
///   WaitForConnectionAsync が返らず、要求が誰にも読まれない状態になった。
///   Editor 側の accepted カウンタで受理数を数えて確定した。
///   TcpDuplexServer は Listen(100) のバックログを持つので取りこぼしが起きない。
///
/// Wire format, one line of JSON each way:
///   request  {"type":"command","action":"ping"}
///   request  {"type":"command","action":"call","params":{"command":"…","modelIndex":"0"}}
///   response {"type":"response","success":true,"action":"ping","data":"OK"}
///            {"type":"response","success":false,"action":"ping","error":"..."}
///
/// The Editor side parses "params" with JsonParser.ParseFlat, which drops any
/// value starting with '[' or '{'. Array arguments must therefore be sent as a
/// single string such as "1,2,3".
/// </summary>
public static class EditorControlClient
{
    /// <summary>Must match PolyLingEditorControlServer.Port on the Unity side.</summary>
    public const int Port = 8770;

    /// <summary>
    /// ビルド版（Player）の受信ポート。Unity 側の PolyLingPlayerControlServer.Port と一致させること。
    /// Editor と別にしてあるのは、ビルド版から Editor 拡張へ送る経路（sendHierarchyBundle や
    /// unity_prefab_*）を使うときに両方を同時に動かすため。
    /// </summary>
    public const int PlayerPort = 8772;

    /// <summary>
    /// SendAsync の既定の接続先。--target player のとき Program が PlayerPort にする。
    /// Editor にしか無い操作（unity_prefab_*）は SendToEditorAsync で Port へ送る。
    /// </summary>
    public static int TargetPort { get; set; } = Port;

    /// <summary>Loopback only. The Editor binds IPAddress.Loopback.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>ログや文言に出す接続先。</summary>
    public static string Endpoint => $"{Host}:{TargetPort}";

    /// <summary>Editor の接続先。ログや文言に出す。</summary>
    public static string EditorEndpoint => $"{Host}:{Port}";

    /// <summary>
    /// Which half of the exchange failed. The caller turns this into a message,
    /// so the tool result alone says whether the port could be opened at all.
    /// </summary>
    public enum FailStage { Connect, Reply }

    /// <summary>
    /// Raised instead of the bare TimeoutException / OperationCanceledException
    /// so the caller can tell a failed connect from a missing reply without
    /// reading any log file.
    /// </summary>
    public sealed class EditorControlException : Exception
    {
        public FailStage Stage      { get; }
        public long      ElapsedMs  { get; }
        public string    InnerName  { get; }

        public EditorControlException(FailStage stage, long elapsedMs, Exception? inner)
            : base(inner?.Message ?? stage.ToString(), inner)
        {
            Stage     = stage;
            ElapsedMs = elapsedMs;
            InnerName = inner?.GetType().Name ?? "(none)";
        }
    }

    /// <summary>既定の接続先（TargetPort）へ送る。</summary>
    public static Task<EditorControlResult> SendAsync(
        string action,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
        => SendToAsync(TargetPort, action, timeoutMs, parameters, cancellationToken);

    /// <summary>--target にかかわらず Editor（Port）へ送る。Editor にしか無い操作に使う。</summary>
    public static Task<EditorControlResult> SendToEditorAsync(
        string action,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? parameters = null,
        CancellationToken cancellationToken = default)
        => SendToAsync(Port, action, timeoutMs, parameters, cancellationToken);

    /// <summary>One connection per call. The Editor domain reloads often, so no channel is kept open.</summary>
    private static async Task<EditorControlResult> SendToAsync(
        int port,
        string action,
        int timeoutMs,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken)
    {
        timeoutMs = Math.Clamp(timeoutMs, 100, 60_000);

        using var client = new TcpDuplexClient(Host, port);

        // 接続と応答待ちを別々に測る。どちらで落ちたかが分からないと、
        // ポートが開いていないのか Editor が黙っているのかを切り分けられない。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // TcpDuplexClient.ConnectAsync は timeout 引数を持たないので
            // CancellationToken で上限を掛ける。
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(timeoutMs);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            sw.Stop();
            throw new EditorControlException(FailStage.Connect, sw.ElapsedMilliseconds, ex);
        }
        sw.Stop();
        long connectMs = sw.ElapsedMilliseconds;

        // The reply has to be bounded separately: connecting succeeds as soon as
        // the port is listening, but the Editor answers on its main thread, which
        // is blocked while it compiles or imports assets.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        DuplexMessage? reply;
        try
        {
            reply = await client.SendAndReceiveAsync(BuildRequest(action, parameters), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            sw2.Stop();
            throw new EditorControlException(FailStage.Reply, sw2.ElapsedMilliseconds, ex);
        }
        sw2.Stop();

        return Parse(reply?.PayloadString) with { ConnectMs = connectMs, ReplyMs = sw2.ElapsedMilliseconds };
    }

    private static string BuildRequest(string action, IReadOnlyDictionary<string, string>? parameters)
    {
        // JsonSerializer.Serialize escapes the action, so an odd string cannot
        // break out of the JSON document. The same applies to every key and
        // value of the parameter object.
        var encoded = JsonSerializer.Serialize(action);

        if (parameters is null || parameters.Count == 0)
            return $"{{\"type\":\"command\",\"action\":{encoded}}}";

        var sb = new StringBuilder();
        sb.Append("{\"type\":\"command\",\"action\":").Append(encoded).Append(",\"params\":{");

        bool first = true;
        foreach (var kv in parameters)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(kv.Key))
              .Append(':')
              .Append(JsonSerializer.Serialize(kv.Value ?? ""));
        }

        sb.Append("}}");
        return sb.ToString();
    }

    private static EditorControlResult Parse(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            return new EditorControlResult(false, string.Empty, string.Empty, "empty response from the Unity Editor.", "");

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var action = root.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
        var data = root.TryGetProperty("data", out var d) ? d.GetString() ?? string.Empty : string.Empty;
        var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? string.Empty : string.Empty;

        // tools / call は data ではなく独自のキーで返す。
        // 呼び出し側がそのまま扱えるよう、応答の生 JSON も渡す。
        return new EditorControlResult(success, action, data, error, responseJson);
    }
}

public sealed record EditorControlResult(
    bool Success, string Action, string Data, string Error, string Raw)
{
    /// <summary>接続にかかった時間[ms]。</summary>
    public long ConnectMs { get; init; }

    /// <summary>応答が返るまでの時間[ms]。</summary>
    public long ReplyMs { get; init; }
}
