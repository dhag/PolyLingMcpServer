using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyLingMcpServer;

/// <summary>
/// 道具呼び出し 1 回ごとの計測を JSONL で書き足す。
///
/// 【何を記録するか】
///   量と成否だけ。引数・結果の本文、画像、ソース、API キーは書かない。
///   比較（generic / current / optimized）で見たいのは「何バイトを何回運んだか」だからである。
///
/// 【taskId】
///   クライアントが要求の _meta に "polyling/taskId" を入れて渡す（WebClient が付ける）。
///   付いていなければ空。Claude Desktop からの呼び出しは空になる。
///
/// 【書き込み】
///   1 行ずつ追記し、そのたびに閉じる。サーバが落ちても書けた行は残る。
///   同時の呼び出しは lock で直列にする。
/// </summary>
public sealed class MetricsLog
{
    /// <summary>_meta のキー。WebClient の METRICS_TASK_META_KEY と一致させる。</summary>
    public const string TaskIdMetaKey = "polyling/taskId";

    private readonly object _lock = new();
    private readonly string _path;

    public MetricsLog(string path)
    {
        _path = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public string FullPath => _path;

    /// <summary>
    /// 日本語や "+" を \u 形式にしないで書く。ファイルを目で読むため。
    /// JSON としての正しさは変わらない。
    /// </summary>
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void Write(JsonObject record)
    {
        // 時刻を先頭に置く。
        var ordered = new JsonObject { ["ts"] = DateTimeOffset.Now.ToString("o") };
        foreach (var kv in record.ToList())
        {
            record.Remove(kv.Key);
            ordered[kv.Key] = kv.Value;
        }

        var line = ordered.ToJsonString(LineOptions) + "\n";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // 計測の失敗で道具を止めない。stderr にだけ残す。
                Console.Error.WriteLine($"metrics write failed: {ex.Message}");
            }
        }
    }

    /// <summary>_meta のキー。利用シーン名。WebClient の SCENE_META_KEY と一致させる。</summary>
    public const string SceneMetaKey = "polyling/scene";

    /// <summary>要求の _meta から taskId を取り出す。無ければ空。</summary>
    public static string TaskIdOf(JsonObject? meta) => StringOf(meta, TaskIdMetaKey);

    /// <summary>要求の _meta から利用シーン名を取り出す。無ければ空。</summary>
    public static string SceneOf(JsonObject? meta) => StringOf(meta, SceneMetaKey).Trim();

    private static string StringOf(JsonObject? meta, string key)
    {
        if (meta is null) return string.Empty;
        if (!meta.TryGetPropertyValue(key, out var node) || node is null) return string.Empty;
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : string.Empty;
    }

    /// <summary>UTF-8 のバイト数。</summary>
    public static int Utf8Bytes(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
}
