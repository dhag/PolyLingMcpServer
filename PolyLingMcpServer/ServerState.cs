namespace PolyLingMcpServer;

/// <summary>
/// 道具が届く場所のひとつ。
///
/// プロジェクトは名前を持たない（Name が空）。道具に渡すパスは今までどおり
/// プロジェクト相対で書く。追加のルートは名前を持ち、"@名前/以下の相対パス"
/// の形で指す。絶対パスは受け付けない（PathGuard.ResolveInside）ので、
/// 届く先を増やしても「どこでも読める」にはならない。
/// </summary>
public sealed record NamedRoot(string Name, string FullPath)
{
    /// <summary>表示パスの頭。プロジェクトは空。</summary>
    public string Prefix => string.IsNullOrEmpty(Name) ? string.Empty : "@" + Name + "/";

    public override string ToString()
        => string.IsNullOrEmpty(Name) ? FullPath : $"@{Name}={FullPath}";
}

public sealed class ServerState
{
    private readonly object _compileLock = new();

    public string ProjectRoot { get; set; } = string.Empty;

    /// <summary>
    /// プロジェクトの外に足した読み取りルート。--root 名前=絶対パス で宣言する。
    /// 例: PolyLing の設定の置き場（%LOCALAPPDATA%Low\HagiharaLab）。
    /// 読めるだけで、書けるかどうかは EditableFolders が決める。
    /// </summary>
    public IReadOnlyList<NamedRoot> ExtraRoots { get; set; } = [];

    /// <summary>
    /// write_file・apply_patch・write_binary・delete_path が触ってよいフォルダ。
    /// 読み取りはここに縛られない。書き込みだけが縛られる。
    ///
    /// 項目は次のどちらか。
    ///   プロジェクト相対のフォルダ   例: Packages/com.haglib.polyling
    ///   "@名前" または "@名前/以下"  例: @settings/PolyLing
    /// </summary>
    public IReadOnlyList<string> EditableFolders { get; set; } = ["Assets"];

    /// <summary>
    /// Absolute path of the Unity Editor log that read_unity_log reads.
    /// Set by Program from --unity-log, or the Windows default location.
    /// 道具がパスを受け取らない唯一の読み先で、起動時に固定される。
    /// ルート機構とは別扱い（1 ファイルだけなので名前を付ける意味がない）。
    /// </summary>
    public string UnityLogPath { get; set; } = string.Empty;

    public CompileResult? LastCompileResult { get; private set; }

    /// <summary>接続先（--target）。起動中は変わらない。</summary>
    public ServerTarget Target { get; set; } = ServerTarget.Editor;

    /// <summary>
    /// 画面キャプチャの保存先（絶対パス、--capture-folder）。
    /// unity_capture の folder はここからの相対で、polyling_call などで uiCapture の
    /// folder を省いたときもここを補う（UnityCaptureTools.ApplyDefaultCaptureFolder）。
    /// </summary>
    public string CaptureFolder { get; set; } = string.Empty;

    /// <summary>unity_focus が探すプロセス名（拡張子なし）。editor は Unity、player は --player-exe。</summary>
    public string FocusProcessName { get; set; } = "Unity";

    /// <summary>比較用の条件。--profile で決まり、起動中は変わらない。</summary>
    public ServerProfile Profile { get; set; } = ServerProfile.Optimized;

    /// <summary>
    /// 要求が利用シーンを明示しないときに使う利用シーン名（--scene）。空なら絞らない。
    /// _meta を送れないクライアント（Claude Desktop など）向け。profile=optimized でだけ効く。
    /// </summary>
    public string DefaultScene { get; set; } = string.Empty;

    /// <summary>
    /// polyling_search の問い合わせ文と上位の結果も計測ログへ書くか（--metrics-search）。既定はオフ。
    /// 問い合わせ文は利用者の言葉なので、量と成否だけを書く他の記録の例外になる。
    /// </summary>
    public bool MetricsSearch { get; set; }

    /// <summary>計測ログ。--metrics が無ければ null（記録しない）。</summary>
    public MetricsLog? Metrics { get; set; }

    /// <summary>profile=generic で最後に tools/list を組み立てた結果。計測ログに載せる。</summary>
    public GenericCommandTools.ListOutcome? LastGenericList { get; set; }

    /// <summary>
    /// 届くルート全部。先頭は必ずプロジェクト（名前なし）。
    /// 表示パスを作るときは前から順に見て、最初に内包したものを使う。
    /// </summary>
    public IReadOnlyList<NamedRoot> Roots
    {
        get
        {
            var list = new List<NamedRoot>();

            if (!string.IsNullOrWhiteSpace(ProjectRoot))
                list.Add(new NamedRoot(string.Empty, Path.GetFullPath(ProjectRoot)));

            foreach (var root in ExtraRoots)
                if (root is not null && !string.IsNullOrWhiteSpace(root.FullPath))
                    list.Add(root);

            return list;
        }
    }

    /// <summary>名前でルートを引く。名前が空ならプロジェクト。</summary>
    public bool TryGetRoot(string name, out NamedRoot root)
    {
        foreach (var candidate in Roots)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                root = candidate;
                return true;
            }
        }

        root = null!;
        return false;
    }

    /// <summary>
    /// EditableFolders の 1 項目を絶対パスへ直す。
    /// 名前の知らない "@名前" は false（起動引数の打ち間違いを黙って通さない）。
    /// </summary>
    public bool TryResolveEditableFolder(string folder, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(folder)) return false;

        if (folder[0] != '@')
        {
            if (string.IsNullOrWhiteSpace(ProjectRoot)) return false;
            fullPath = Path.GetFullPath(Path.Combine(ProjectRoot, folder));
            return true;
        }

        var body = folder[1..];
        int cut = body.IndexOfAny(['/', '\\']);

        var name = cut < 0 ? body : body[..cut];
        var tail = cut < 0 ? string.Empty : body[(cut + 1)..];

        if (!TryGetRoot(name, out var root)) return false;

        fullPath = string.IsNullOrEmpty(tail)
            ? Path.GetFullPath(root.FullPath)
            : Path.GetFullPath(Path.Combine(root.FullPath, tail));
        return true;
    }

    public IReadOnlyList<string> EditableRootFullPaths
    {
        get
        {
            var list = new List<string>();

            foreach (var folder in EditableFolders)
                if (TryResolveEditableFolder(folder, out var full))
                    list.Add(full);

            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    /// <summary>Editable folders as one line, for logs and the status bar.</summary>
    public string EditableFoldersDisplay => EditableFolders.Count == 0
        ? "(none)"
        : string.Join("; ", EditableFolders);

    /// <summary>ルートを 1 行で。起動ログと失敗の文言に出す。</summary>
    public string RootsDisplay => string.Join("; ", Roots.Select(
        root => string.IsNullOrEmpty(root.Name) ? $"(project)={root.FullPath}" : $"@{root.Name}={root.FullPath}"));

    public void SetLastCompileResult(CompileResult result)
    {
        lock (_compileLock)
        {
            LastCompileResult = result;
        }
    }
}

public sealed record CompileResult(
    bool Success,
    int ExitCode,
    string ProjectFile,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<string> Errors,
    string Output);
