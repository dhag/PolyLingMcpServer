using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyLingMcpServer;

/// <summary>
/// このサーバが前提にするプロジェクト内の置き場所（すべてプロジェクト相対）。
///
/// 【どこから読むか】
///   exe のフォルダから親へたどり、最初に見つかった ProjectLayout.json（FileName）。
///   exe と同じフォルダに置けばそれが使われる。置かなければ、bin/Debug/net8.0 で動かしたとき
///   3 つ上のソースフォルダの同名ファイル（正本）が見つかる（WebClient と同じ探し方）。
///   どこにも無ければ下の既定値を使う。書いてある項目だけが既定値を上書きする。
///   形の誤り・知らない項目名・絶対パスは起動を止める（黙って既定値に落とすと、
///   書き換えが効いていないことに気づけない）。
///
/// 【PolyLing 本体との関係】
///   PolyLing 側にも同じ名前の置き場所の設定がある（PLProjectLayout、persistentDataPath/PolyLing/ProjectLayout.json）。
///   使う場所が同じというだけで別の設定であり、互いに読み合わない。
///
/// 【道具の説明文】
///   [Description] 属性はコンパイル時の定数しか書けないので、説明文には {layout:項目名} を書いておき、
///   tools/list のときに McpSetup.ListFilter が Expand で置き換える。
/// </summary>
public sealed class ProjectLayout
{
    public const string FileName = "ProjectLayout.json";

    /// <summary>直下に新規作成させないフォルダ。</summary>
    public string DataFolder { get; set; } = "データとツール";

    /// <summary>シナリオ・ランタイムの生成物の置き場（DataFolder の中の名前）。</summary>
    public string ScenarioSandboxFolder { get; set; } = "サンドボックス_シナリオ用";

    /// <summary>デバッグ作業の置き場。</summary>
    public string DebugSandboxFolder { get; set; } = "SandBox";

    /// <summary>--capture-folder を省いたときの保存先。</summary>
    public string CaptureFolder { get; set; } = "SandBox/Captures";

    /// <summary>delete_path のゴミ箱の名前（各ルートの直下に作る）。</summary>
    public string TrashFolder { get; set; } = "_McpTrash";

    /// <summary>--guide を省いたときの AI 案内データのフォルダ。</summary>
    public string GuideFolder { get; set; } = "Packages/com.haglib.polyling/McpGuide";

    /// <summary>このサーバ自身のソースフォルダ（道具の説明文で AI に知らせる）。</summary>
    public string McpServerSource { get; set; } = "データとツール/ツール/PolyLingMcpServer/PolyLingMcpServer";

    /// <summary>ビルド構成（プロジェクト側）。Editor 側 PolyLingBuildConfig.ProjectConfigPath と同じ場所。</summary>
    public string BuildConfig { get; set; } = "Assets/PolyLingBuild/ビルド構成.json";

    /// <summary>ビルド構成（パッケージ既定）。Editor 側 PolyLingBuildConfig.DefaultConfigPath と同じ場所。</summary>
    public string DefaultBuildConfig { get; set; } = "Packages/com.haglib.polyling/Editor/Build/既定ビルド構成.json";

    /// <summary>PolyLingPlayerViewer.cs.meta。guid でシーンからの参照を探す。</summary>
    public string ViewerScriptMeta { get; set; } = "Packages/com.haglib.polyling/Runtime/Poly_Ling_Player/View/Core/PolyLingPlayerViewer.cs.meta";

    /// <summary>今の設定。Program.Run の最初に Load で決まる。</summary>
    public static ProjectLayout Current { get; private set; } = new();

    /// <summary>読んだファイル（絶対パス）。既定値のままなら空。</summary>
    public static string SourceFile { get; private set; } = string.Empty;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>exe のフォルダから親へたどって最初の ProjectLayout.json。無ければ null。</summary>
    private static string? FindFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>ProjectLayout.json を読んで Current にする。失敗なら false と理由。</summary>
    public static bool Load(out string error)
    {
        error = string.Empty;
        var path = FindFile();

        if (path is null)
        {
            Current = new ProjectLayout();
            SourceFile = string.Empty;
            return true;
        }

        ProjectLayout? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ProjectLayout>(File.ReadAllText(path, System.Text.Encoding.UTF8), ReadOptions);
        }
        catch (Exception ex)
        {
            error = $"{path}: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = $"{path} is empty.";
            return false;
        }

        foreach (var (name, value) in parsed.Items())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{path}: {name} is empty.";
                return false;
            }
            if (Path.IsPathRooted(value))
            {
                error = $"{path}: {name} must be project-relative: {value}";
                return false;
            }
        }

        if (parsed.ScenarioSandboxFolder.IndexOfAny(['/', '\\']) >= 0)
        {
            error = $"{path}: ScenarioSandboxFolder is a folder name inside DataFolder, not a path: {parsed.ScenarioSandboxFolder}";
            return false;
        }

        if (parsed.TrashFolder.IndexOfAny(['/', '\\']) >= 0)
        {
            error = $"{path}: TrashFolder is a folder name, not a path: {parsed.TrashFolder}";
            return false;
        }

        Current = parsed;
        SourceFile = path;
        return true;
    }

    /// <summary>全項目（名前と値）。検査と Expand で使う。</summary>
    private IEnumerable<(string Name, string Value)> Items()
    {
        yield return (nameof(DataFolder), DataFolder);
        yield return (nameof(ScenarioSandboxFolder), ScenarioSandboxFolder);
        yield return (nameof(DebugSandboxFolder), DebugSandboxFolder);
        yield return (nameof(CaptureFolder), CaptureFolder);
        yield return (nameof(TrashFolder), TrashFolder);
        yield return (nameof(GuideFolder), GuideFolder);
        yield return (nameof(McpServerSource), McpServerSource);
        yield return (nameof(BuildConfig), BuildConfig);
        yield return (nameof(DefaultBuildConfig), DefaultBuildConfig);
        yield return (nameof(ViewerScriptMeta), ViewerScriptMeta);
    }

    /// <summary>目印の書き出し方。説明文の定数ではこの形で書く（例 "{layout:McpServerSource}"）。</summary>
    public const string MarkPrefix = "{layout:";

    /// <summary>text の {layout:項目名} を今の値に置き換える。目印が無ければ text をそのまま返す。</summary>
    public static string Expand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(MarkPrefix, StringComparison.Ordinal)) return text;

        foreach (var (name, value) in Current.Items())
            text = text.Replace(MarkPrefix + name + "}", value, StringComparison.Ordinal);

        return text;
    }

    /// <summary>JSON 文字列の中に値を入れるとき用。値を JSON の文字列としてエスケープしてから置き換える。</summary>
    public static string ExpandInJson(string json)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains(MarkPrefix, StringComparison.Ordinal)) return json;

        foreach (var (name, value) in Current.Items())
        {
            var escaped = JsonSerializer.Serialize(value)[1..^1];
            json = json.Replace(MarkPrefix + name + "}", escaped, StringComparison.Ordinal);
        }

        return json;
    }
}
