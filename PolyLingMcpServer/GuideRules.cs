using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace PolyLingMcpServer;

/// <summary>
/// AI への案内を道具の応答に添える。
///
/// 【なぜ応答に添えるか】
///   初期化の instructions は、クライアントによっては AI まで届かない
///   （Claude の remote-devices 経由では届かなかった。2026-09-30 実測）。
///   道具の説明も、道具が「名前だけ」で渡されるクライアントでは呼ぶまで見えない。
///   道具の応答だけは、どのクライアントでも必ず AI が読む。
///   操作者が指定を忘れても・知らなくても同じ案内が出るよう、サーバ側で押し出す。
///
/// 【出すもの】
///   ・入門（primer.md）… セッションの最初の応答に 1 回だけ添える。
///                        基本の使い方（Play で起動・キャプチャで見る等）と文書の索引。
///   ・きっかけ（hints.tsv）… 道具名と引数が規則に合ったとき、その場で 1 回だけ添える。
///                        対応する文書をこのセッションで読んでいれば出さない。
///
/// 【データの置き場】
///   &lt;project&gt;/Packages/com.haglib.polyling/McpGuide/（--guide で変更可）。
///   ファイルの更新時刻が変わったら次の呼び出しで読み直すので、書き換えにサーバの
///   再ビルド・再起動は要らない。ファイルが無ければ何も添えない。
///
/// 【添え方】
///   応答の末尾に TextContentBlock を足す。先頭の本体（JSON など）は変えない
///   （先頭ブロックを読む計測・クライアントを壊さないため）。
/// </summary>
public static class GuideRules
{
    public const string PrimerFile = "primer.md";
    public const string HintsFile  = "hints.txt";   // タブ区切り（書き込み道具が .tsv を受け付けないため .txt）

    private sealed class Hint
    {
        public string Id = "";
        public Regex Tool = null!;
        public Regex Args = null!;
        public string SkipIfRead = "";
        public string Message = "";
    }

    private sealed class Loaded
    {
        public bool Initialized;
        public DateTime PrimerStamp;
        public DateTime HintsStamp;
        public string Primer = "";
        public List<Hint> Hints = [];
        public List<string> Problems = [];
    }

    private sealed class SessionGuide
    {
        public bool Primed;
        public readonly HashSet<string> Fired    = new(StringComparer.Ordinal);
        public readonly HashSet<string> ReadDocs = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object LoadLock = new();
    private static Loaded _loaded = new();
    private static readonly ConcurrentDictionary<string, SessionGuide> Sessions = new();

    /// <summary>案内データのフォルダ（絶対パス）。空なら案内しない。</summary>
    public static string GuideFolder { get; set; } = string.Empty;

    /// <summary>既定の置き場。プロジェクトが無ければ空。</summary>
    public static string DefaultFolder(string projectRoot)
        => string.IsNullOrWhiteSpace(projectRoot)
            ? string.Empty
            : Path.Combine(projectRoot, "Packages", "com.haglib.polyling", "McpGuide");

    /// <summary>入門の本文。instructions にも載せる（届くクライアント向け）。</summary>
    public static string PrimerText()
    {
        Refresh();
        return _loaded.Primer;
    }

    /// <summary>
    /// 応答へ案内を足す。道具の実行後、計測を書いた後に呼ぶ。
    /// </summary>
    public static void Decorate(string sessionKey, string toolName,
                                IDictionary<string, JsonElement>? arguments, CallToolResult result)
    {
        if (string.IsNullOrEmpty(GuideFolder)) return;
        Refresh();
        var data = _loaded;

        var session = Sessions.GetOrAdd(string.IsNullOrEmpty(sessionKey) ? "stdio" : sessionKey, _ => new SessionGuide());
        var argsText = ArgumentsText(arguments);
        var notes = new List<string>();

        lock (session)
        {
            RecordDocRead(session, toolName, arguments);

            if (!session.Primed && data.Primer.Length > 0)
            {
                session.Primed = true;
                notes.Add("[PolyLing 案内・セッション最初の 1 回だけ表示]\n" + data.Primer);
                if (data.Problems.Count > 0)
                    notes.Add("[案内データの読み込みで問題]\n" + string.Join("\n", data.Problems));
            }

            foreach (var hint in data.Hints)
            {
                if (session.Fired.Contains(hint.Id)) continue;
                if (!hint.Tool.IsMatch(toolName)) continue;
                if (!hint.Args.IsMatch(argsText)) continue;
                if (hint.SkipIfRead.Length > 0 && session.ReadDocs.Contains(hint.SkipIfRead)) continue;

                session.Fired.Add(hint.Id);
                notes.Add("[PolyLing 案内] " + hint.Message);
            }
        }

        if (notes.Count == 0) return;

        var blocks = result.Content?.ToList() ?? [];
        foreach (var note in notes) blocks.Add(new TextContentBlock { Text = note });
        result.Content = blocks;
    }

    // ================================================================

    /// <summary>文書を読んだことを覚える（read_file / read_lines の .md）。</summary>
    private static void RecordDocRead(SessionGuide session, string toolName, IDictionary<string, JsonElement>? arguments)
    {
        if (toolName is not ("read_file" or "read_lines")) return;
        if (arguments is null || !arguments.TryGetValue("path", out var p) || p.ValueKind != JsonValueKind.String) return;

        var path = p.GetString() ?? "";
        if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return;
        session.ReadDocs.Add(Path.GetFileName(path.Replace('\\', '/')));
    }

    /// <summary>引数を 1 行にする。文字列はそのまま、それ以外は JSON。polyling_call の args も展開される。</summary>
    private static string ArgumentsText(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var (key, value) in arguments)
        {
            sb.Append(key).Append('=');
            sb.Append(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void Refresh()
    {
        if (string.IsNullOrEmpty(GuideFolder)) return;

        var primerPath = Path.Combine(GuideFolder, PrimerFile);
        var hintsPath  = Path.Combine(GuideFolder, HintsFile);
        var primerStamp = File.Exists(primerPath) ? File.GetLastWriteTimeUtc(primerPath) : DateTime.MinValue;
        var hintsStamp  = File.Exists(hintsPath)  ? File.GetLastWriteTimeUtc(hintsPath)  : DateTime.MinValue;

        var current = _loaded;
        if (current.Initialized && current.PrimerStamp == primerStamp && current.HintsStamp == hintsStamp)
            return;

        lock (LoadLock)
        {
            if (_loaded.Initialized && _loaded.PrimerStamp == primerStamp && _loaded.HintsStamp == hintsStamp) return;

            var next = new Loaded { Initialized = true, PrimerStamp = primerStamp, HintsStamp = hintsStamp };
            try
            {
                if (primerStamp != DateTime.MinValue)
                    next.Primer = File.ReadAllText(primerPath, Encoding.UTF8).Trim();
            }
            catch (Exception ex) { next.Problems.Add($"{PrimerFile}: {ex.Message}"); }

            try
            {
                if (hintsStamp != DateTime.MinValue)
                    ParseHints(File.ReadAllLines(hintsPath, Encoding.UTF8), next);
            }
            catch (Exception ex) { next.Problems.Add($"{HintsFile}: {ex.Message}"); }

            _loaded = next;
        }
    }

    /// <summary>
    /// hints.tsv：1 行 1 規則、タブ区切り 5 列。# で始まる行と空行は読み飛ばす。
    ///   id  道具名の正規表現  引数の正規表現  読んでいれば出さない文書名(.md・空可)  出す文
    /// 引数の正規表現は「キー=値」を改行で並べた文字列に当てる（大文字小文字は区別しない）。
    /// 文中の \n は改行になる。
    /// </summary>
    private static void ParseHints(string[] lines, Loaded into)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var cols = line.Split('\t');
            if (cols.Length < 5)
            {
                into.Problems.Add($"{HintsFile}:{i + 1}: 列が 5 つない（タブ区切り）");
                continue;
            }

            try
            {
                into.Hints.Add(new Hint
                {
                    Id         = cols[0].Trim(),
                    Tool       = new Regex(cols[1].Trim(), RegexOptions.CultureInvariant),
                    Args       = new Regex(cols[2].Trim(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    SkipIfRead = cols[3].Trim(),
                    Message    = string.Join("\t", cols.Skip(4)).Replace("\\n", "\n")
                });
            }
            catch (ArgumentException ex)
            {
                into.Problems.Add($"{HintsFile}:{i + 1}: 正規表現が不正: {ex.Message}");
            }
        }
    }
}
