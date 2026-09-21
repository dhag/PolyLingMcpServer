using System.Collections.Concurrent;
using System.Text.Json;

namespace PolyLingMcpServer;

/// <summary>
/// 利用シーン（PolyLing 側 SceneLibrary）の定義をサーバ側で引くための写し。
///
/// 【正本は PolyLing 側】
///   利用シーンの定義は PolyLing の scenes.csv が正本で、作る・変えるのは setScene / deleteScene。
///   ここは Editor の "scenes" で取った写しを名前で持つだけで、自分では書き換えない。
///
/// 【なぜ写しを持つか】
///   tools/call のたびに Editor へ問い合わせると、コンパイル中（Editor が応答しない）の
///   unity_refresh などが問い合わせの待ちで止まる。tools/list のときに取り直して保持し、
///   tools/call ではその写しを使う。会話ごとの状態ではない（どの要求も名前を明示する）。
/// </summary>
public static class SceneCatalog
{
    /// <summary>"scenes" の応答待ち。</summary>
    private const int FetchTimeoutMs = 5000;

    /// <summary>利用シーン 1 つぶんのうち、サーバが使う部分。</summary>
    public sealed record SceneInfo(string Name, string Description, IReadOnlyList<string> Tools);

    private static readonly ConcurrentDictionary<string, SceneInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public enum FetchStatus { Found, NotFound, Unreachable }

    /// <summary>Editor から全利用シーンを取り直し、name を引く。</summary>
    public static async Task<(FetchStatus Status, SceneInfo? Scene, string Problem)> RefreshAndGetAsync(
        string name, CancellationToken cancellationToken)
    {
        EditorControlResult reply;
        try
        {
            reply = await EditorControlClient.SendAsync("scenes", FetchTimeoutMs, null, cancellationToken);
        }
        catch (Exception ex)
        {
            return (FetchStatus.Unreachable, TryGetCached(name), $"Unity Editor unreachable ({ex.GetType().Name}) while reading scene '{name}'.");
        }

        if (!reply.Success)
            return (FetchStatus.Unreachable, TryGetCached(name), $"scenes rejected: {reply.Error}");

        var fresh = new Dictionary<string, SceneInfo>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(reply.Raw))
        {
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
                return (FetchStatus.Unreachable, TryGetCached(name),
                        "scenes returned no result.scenes. Is PolyLingEditorControlServer.cs up to date?");

            foreach (var s in scenes.EnumerateArray())
            {
                string n = s.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
                if (n.Length == 0) continue;
                string d = s.TryGetProperty("description", out var dd) ? dd.GetString() ?? "" : "";
                var tools = new List<string>();
                if (s.TryGetProperty("tools", out var tt) && tt.ValueKind == JsonValueKind.Array)
                    foreach (var t in tt.EnumerateArray())
                        if (t.GetString() is { Length: > 0 } tool) tools.Add(tool);
                fresh[n] = new SceneInfo(n, d, tools);
            }
        }

        Cache.Clear();
        foreach (var kv in fresh) Cache[kv.Key] = kv.Value;

        return fresh.TryGetValue(name, out var found)
            ? (FetchStatus.Found, found, string.Empty)
            : (FetchStatus.NotFound, null, $"unknown scene: {name}");
    }

    /// <summary>最後に取った写しから引く。取っていなければ null。</summary>
    public static SceneInfo? TryGetCached(string name)
        => string.IsNullOrEmpty(name) ? null : Cache.TryGetValue(name, out var s) ? s : null;
}
