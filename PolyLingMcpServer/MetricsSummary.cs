using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PolyLingMcpServer;

/// <summary>
/// 計測ログ（--metrics が書いた JSONL）を集計して Markdown を書く。
/// 起動引数 --summarize-metrics &lt;JSONL...&gt; で、MCP サーバとしては起動せずにこれだけを行う。
///
/// 【何に使うか】
///   ・どのコマンドが実際に使われたかを数え、影響・危険性の値付けを使われた順に進める
///     （PolyLing_利用シーン_カテゴライズ設計方針.md 15.1 の 5）
///   ・課題ごとの MCP 呼び出し数と結果バイトを出す（MCP_ベンチマーク課題.md 5 節の指標）
///   ・--metrics-search で残した検索の記録から、問い合わせと、その後に実行したコマンドを並べる
///     （タグの見直しの材料。方針書 17 節 Phase 5 の 4）
///
/// 【読むもの】
///   kind=call の行（tool / polylingCommand / ok / resultBytes / scene / taskId）と、
///   kind=search の行（query / scene / results / taskId）。kind=list は数えるだけ。
///   読めない行は数えて飛ばす。
/// </summary>
public static class MetricsSummary
{
    private sealed class Tally
    {
        public int Calls;
        public int Failures;
        public long ResultBytes;
        public readonly SortedSet<string> Scenes = new(StringComparer.Ordinal);
    }

    private sealed class TaskTally
    {
        public int Calls;
        public int Failures;
        public long ResultBytes;
        public int Lists;
        public long ListBytes;
    }

    public static int Run(IReadOnlyList<string> paths, TextWriter output)
    {
        var byCommand = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var byTool    = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var byTask    = new SortedDictionary<string, TaskTally>(StringComparer.Ordinal);
        var searches  = new List<(string Task, string Scene, string Query, string Results, string Next)>();
        int lines = 0, bad = 0;

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"ERROR: not found: {path}");
                return 2;
            }

            // 課題ごとに「直前の検索」を覚え、次の polyling_call のコマンド名を結び付ける。
            var pendingSearch = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lines++;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { bad++; continue; }

                using (doc)
                {
                    var r = doc.RootElement;
                    string kind  = Str(r, "kind");
                    string task  = Str(r, "taskId");
                    string scene = Str(r, "scene");
                    string taskKey = task.Length > 0 ? task : "(taskId なし)";

                    if (kind == "list")
                    {
                        var tt = Get(byTask, taskKey);
                        tt.Lists++;
                        tt.ListBytes += Num(r, "resultBytes");
                        continue;
                    }

                    if (kind == "search")
                    {
                        searches.Add((taskKey, scene, Str(r, "query"), Str(r, "results"), ""));
                        pendingSearch[taskKey] = searches.Count - 1;
                        continue;
                    }

                    if (kind != "call") continue;

                    string tool = Str(r, "tool");
                    string command = Str(r, "polylingCommand");
                    bool ok = !r.TryGetProperty("ok", out var okEl) || okEl.ValueKind != JsonValueKind.False;
                    long bytes = Num(r, "resultBytes");

                    Add(Get(byTool, tool), ok, bytes, scene);
                    if (command.Length > 0)
                    {
                        Add(Get(byCommand, command), ok, bytes, scene);
                        if (pendingSearch.TryGetValue(taskKey, out int si) && searches[si].Next.Length == 0)
                        {
                            var s = searches[si];
                            searches[si] = (s.Task, s.Scene, s.Query, s.Results, command);
                            pendingSearch.Remove(taskKey);
                        }
                    }

                    var t = Get(byTask, taskKey);
                    t.Calls++;
                    if (!ok) t.Failures++;
                    t.ResultBytes += bytes;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("# 計測ログの集計\n\n");
        sb.Append($"読んだファイル {paths.Count} / 行 {lines} / 読めなかった行 {bad}\n\n");

        sb.Append("## コマンド別（polyling_call と pl_ 道具で実行した PolyLing コマンド）\n\n");
        WriteTally(sb, "コマンド", byCommand);

        sb.Append("## 道具別\n\n");
        WriteTally(sb, "道具", byTool);

        sb.Append("## 課題別\n\n");
        sb.Append("| 課題 | MCP 呼び出し | 失敗 | 結果バイト | tools/list 回数 | tools/list バイト |\n|---|---:|---:|---:|---:|---:|\n");
        foreach (var kv in byTask)
            sb.Append($"| {Cell(kv.Key)} | {kv.Value.Calls} | {kv.Value.Failures} | {kv.Value.ResultBytes.ToString("N0", CultureInfo.InvariantCulture)} | {kv.Value.Lists} | {kv.Value.ListBytes.ToString("N0", CultureInfo.InvariantCulture)} |\n");
        sb.Append('\n');

        if (searches.Count > 0)
        {
            sb.Append("## 検索と、その後に実行したコマンド（--metrics-search）\n\n");
            sb.Append("次に実行したコマンドが上位の結果に無ければ、そのコマンドのタグや説明の見直し候補である。\n\n");
            sb.Append("| 課題 | 利用シーン | 問い合わせ | 上位の結果 | 次に実行 | 結果に含まれたか |\n|---|---|---|---|---|---|\n");
            foreach (var s in searches)
            {
                string hit = s.Next.Length == 0 ? "—"
                           : (("," + s.Results + ",").Contains("," + s.Next + ",", StringComparison.Ordinal) ? "含まれた" : "**含まれない**");
                sb.Append($"| {Cell(s.Task)} | {Cell(s.Scene)} | {Cell(s.Query)} | {Cell(s.Results)} | {Cell(s.Next)} | {hit} |\n");
            }
            sb.Append('\n');
        }

        output.Write(sb.ToString());
        return 0;
    }

    private static void WriteTally(StringBuilder sb, string head, Dictionary<string, Tally> map)
    {
        sb.Append($"| {head} | 呼び出し | 失敗 | 結果バイト | 利用シーン |\n|---|---:|---:|---:|---|\n");
        foreach (var kv in map.OrderByDescending(k => k.Value.Calls).ThenBy(k => k.Key, StringComparer.Ordinal))
            sb.Append($"| {Cell(kv.Key)} | {kv.Value.Calls} | {kv.Value.Failures} | {kv.Value.ResultBytes.ToString("N0", CultureInfo.InvariantCulture)} | {Cell(string.Join(", ", kv.Value.Scenes))} |\n");
        sb.Append('\n');
    }

    private static void Add(Tally t, bool ok, long bytes, string scene)
    {
        t.Calls++;
        if (!ok) t.Failures++;
        t.ResultBytes += bytes;
        if (scene.Length > 0) t.Scenes.Add(scene);
    }

    private static T Get<T>(IDictionary<string, T> map, string key) where T : new()
    {
        if (!map.TryGetValue(key, out var v)) { v = new T(); map[key] = v; }
        return v;
    }

    private static string Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long Num(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    /// <summary>Markdown の表の 1 マス。縦棒と改行を潰す。</summary>
    private static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
