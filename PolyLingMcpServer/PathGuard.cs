namespace PolyLingMcpServer;

/// <summary>
/// 道具が触れる場所を決める唯一の場所。
///
/// 【ルート】
///   プロジェクトは名前を持たず、パスは今までどおりプロジェクト相対で書く。
///   --root で足したフォルダは名前を持ち、"@名前/以下の相対パス" で指す。
///   絶対パスは受け付けない。ルートを増やしても「どこでも読める」にはならない。
///
/// 【読みと書きの範囲が違う】
///   読み  … どれかのルートの中なら全部。
///   書き  … EditableFolders に挙げたフォルダの中だけ。項目はプロジェクト相対でも
///           "@名前/以下" でもよい（ServerState.TryResolveEditableFolder）。
///   削除  … 書きと同じ範囲。ただし拡張子の表は使わない（下の注記）。
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// write_file / apply_patch が書ける拡張子。
    /// .csv / .mqo はテキスト形式なので確認用のファイルを置けるよう加えた
    /// （write_file は UTF-8 で書く。MQO を日本語入りで書くときは write_binary で Shift_JIS のバイト列を渡す）。
    /// .html / .js はモーションスタジオ（データとツール/VRMビュワー/polyling_motion_studio）の編集用。
    /// .unity はシーン内 GameObject のアクティブ等を直すため（YAML テキスト。Unity で開いているシーンは
    /// 書き換え後にエディタ側で再読込が要る）。
    /// </summary>
    private static readonly string[] TextWritableExtensions =
        [".cs", ".asmdef", ".asmref", ".json", ".txt", ".md", ".doc", ".compute", ".csv", ".mqo", ".html", ".js", ".py", ".unity"];

    /// <summary>
    /// write_binary が書ける拡張子。テキスト用に加えて、PolyLing 自身が書く形式
    /// （Packages/com.haglib.polyling/Runtime/Poly_Ling_Main/Core/Config/PLSandbox.cs の
    /// WritableExtensions と同じ並び）を許す。
    /// PLSandbox は RecentPaths 経由で UnityEngine に依存するため、この
    /// プロジェクトからは参照できない。変えるときは両方を合わせること。
    /// .dll / .exe などの実行形式は入れない。
    /// </summary>
    private static readonly string[] BinaryWritableExtensions =
    [
        ".cs", ".asmdef", ".asmref", ".json", ".txt", ".md",
        ".csv", ".pmx", ".mqo", ".obj", ".stl", ".vrm", ".vrma", ".vmd", ".fbx", ".plrf", ".mfproj",
        ".png", ".jpg", ".jpeg", ".html", ".py", ".unity",
    ];

    // ================================================================
    // ルートの切り出し
    // ================================================================

    /// <summary>
    /// 道具に渡されたパスを、ルートとその中の相対パスに分ける。
    ///
    /// "@名前/以下" … その名前のルート。名前だけ（"@名前"）はルート自身。
    /// それ以外     … プロジェクト。
    ///
    /// 知らない名前は例外にする。黙ってプロジェクト相対として扱うと、
    /// 打ち間違いが「ファイルが無い」に化けて原因が分からなくなる。
    /// </summary>
    public static void SplitRoot(ServerState state, string path, out NamedRoot root, out string relative)
    {
        path ??= string.Empty;

        if (path.Length == 0 || path[0] != '@')
        {
            if (!state.TryGetRoot(string.Empty, out root))
                throw new InvalidOperationException("Unity project folder is not configured.");

            relative = path;
            return;
        }

        var body = path[1..];
        int cut = body.IndexOfAny(['/', '\\']);

        var name = cut < 0 ? body : body[..cut];
        relative  = cut < 0 ? string.Empty : body[(cut + 1)..];

        if (name.Length == 0)
            throw new InvalidOperationException("A path that starts with '@' must name a root, as in @settings/file.csv.");

        if (!state.TryGetRoot(name, out root))
            throw new InvalidOperationException(
                $"No root named '{name}'. Known roots: {state.RootsDisplay}");
    }

    // ================================================================
    // 読み
    // ================================================================

    public static string ResolveReadablePath(ServerState state, string relativePath)
    {
        EnsureProjectConfigured(state);

        SplitRoot(state, relativePath, out var root, out var inside);
        EnsureRootExists(root);

        return ResolveInside(root.FullPath, inside);
    }

    /// <summary>
    /// Editor が返した絶対パスを読み取り用に解決する。
    ///
    /// ResolveReadablePath と分けてあるのは、この経路の入力が道具の引数ではなく
    /// Editor の応答だからで、相対パスに直しようがない。内包検査は同じ IsInside を
    /// 通すので、リンクを辿った先がどのルートの外にもあれば拒否される。
    /// </summary>
    public static string ResolveReadableAbsolutePath(ServerState state, string absolutePath)
    {
        EnsureProjectConfigured(state);

        if (string.IsNullOrWhiteSpace(absolutePath))
            throw new InvalidOperationException("The Unity Editor returned an empty path.");

        if (!Path.IsPathRooted(absolutePath))
            throw new InvalidOperationException($"Not an absolute path: {absolutePath}");

        var fullPath = Path.GetFullPath(absolutePath);

        foreach (var root in state.Roots)
            if (IsInside(root.FullPath, fullPath))
                return fullPath;

        throw new UnauthorizedAccessException(
            $"{fullPath} is outside every folder this server may read. Known roots: {state.RootsDisplay}. "
            + "Point the capture at a folder under one of them, or read the file yourself.");
    }

    // ================================================================
    // 書き
    // ================================================================

    /// <summary>write_file / apply_patch 用。編集可能フォルダの中で、テキスト用の拡張子だけ。</summary>
    public static string ResolveWritablePath(ServerState state, string relativePath)
        => ResolveWritablePathCore(state, relativePath, TextWritableExtensions);

    /// <summary>write_binary 用。編集可能フォルダの中で、バイナリ用の拡張子だけ。</summary>
    public static string ResolveBinaryWritablePath(ServerState state, string relativePath)
        => ResolveWritablePathCore(state, relativePath, BinaryWritableExtensions);

    /// <summary>
    /// delete_path 用。編集可能フォルダの中なら拡張子を問わない。
    ///
    /// 【なぜ拡張子で絞らないか】
    ///   消す対象にはフォルダと .meta が入る。どちらも書き込み用の表に無い。
    ///   表に足すと「書けないのに消せる」拡張子が表に混ざって意味がぼやける。
    ///   本当の関門は編集可能フォルダへの内包検査であって、拡張子ではない。
    /// </summary>
    public static string ResolveDeletablePath(ServerState state, string relativePath)
        => ResolveWritablePathCore(state, relativePath, allowedExtensions: null);

    /// <param name="allowedExtensions">null なら拡張子を見ない。</param>
    private static string ResolveWritablePathCore(
        ServerState state, string relativePath, string[]? allowedExtensions)
    {
        EnsureProjectConfigured(state);

        var editableRoots = state.EditableRootFullPaths;
        if (editableRoots.Count == 0)
            throw new InvalidOperationException("No editable folder is configured.");

        SplitRoot(state, relativePath, out var root, out var inside);
        EnsureRootExists(root);

        var fullPath = ResolveInside(root.FullPath, inside);

        // A write is allowed when it lands inside any one of the editable roots.
        if (!editableRoots.Any(editable => IsInside(editable, fullPath)))
            throw new UnauthorizedAccessException(
                "The requested path is outside the allowed folders. Allowed: "
                + state.EditableFoldersDisplay);

        if (allowedExtensions is not null)
            EnsureWritableExtension(fullPath, allowedExtensions);

        return fullPath;
    }

    // ================================================================
    // 表示
    // ================================================================

    /// <summary>
    /// 絶対パスを道具へ渡せる形に直す。プロジェクト内はそのままの相対パス、
    /// 追加ルート内は "@名前/…"。どのルートにも入っていなければ絶対パスのまま。
    ///
    /// 前から順に見るので、プロジェクトが常に優先される。
    /// </summary>
    public static string ToDisplayPath(ServerState state, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);

        foreach (var root in state.Roots)
        {
            if (!IsInside(root.FullPath, full)) continue;

            var inside = Path.GetRelativePath(root.FullPath, full).Replace('\\', '/');
            if (inside == ".") inside = string.Empty;

            return root.Prefix + inside;
        }

        return full;
    }

    /// <summary>この絶対パスがルートそのものか。</summary>
    public static bool IsRootItself(ServerState state, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);

        foreach (var root in state.Roots)
            if (SamePath(root.FullPath, full)) return true;

        return false;
    }

    /// <summary>この絶対パスが編集可能フォルダそのものか。</summary>
    public static bool IsEditableRootItself(ServerState state, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);

        foreach (var editable in state.EditableRootFullPaths)
            if (SamePath(editable, full)) return true;

        return false;
    }

    /// <summary>この絶対パスを含むルート。どれにも入っていなければ false。</summary>
    public static bool TryGetContainingRoot(ServerState state, string fullPath, out NamedRoot root)
    {
        var full = Path.GetFullPath(fullPath);

        foreach (var candidate in state.Roots)
        {
            if (!IsInside(candidate.FullPath, full)) continue;
            root = candidate;
            return true;
        }

        root = null!;
        return false;
    }

    // ================================================================
    // 内包検査
    // ================================================================

    private static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            relativePath = ".";

        if (Path.IsPathRooted(relativePath))
            throw new InvalidOperationException(
                "Absolute paths are not allowed. Use a project-relative path, or @root/path for another root.");

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        EnsureInside(fullRoot, fullPath);
        return fullPath;
    }

    /// <summary>
    /// Resolves every component of the path through symbolic links, directory
    /// junctions and mount points. Path.GetFullPath is purely lexical: it never
    /// touches the filesystem, so on its own it cannot see that Assets/shared is
    /// a link pointing outside the project.
    /// </summary>
    private static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(root))
            return full;

        // Walk from the leaf up to the root collecting components. Done with
        // GetDirectoryName rather than string.Split so there is no separator
        // array to disambiguate, and so UNC roots and alternate separators are
        // handled by the framework instead of by hand.
        var components = new Stack<string>();

        for (string? cursor = full;
             cursor is not null && cursor.Length > root.Length;
             cursor = Path.GetDirectoryName(cursor))
        {
            components.Push(Path.GetFileName(cursor));
        }

        var current = root;

        while (components.Count > 0)
        {
            current = Path.Combine(current, components.Pop());

            FileSystemInfo? info =
                Directory.Exists(current) ? new DirectoryInfo(current)
                : File.Exists(current) ? new FileInfo(current)
                : null;

            // Non-existent tail components cannot be links; keep them literal.
            if (info is null)
                continue;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = Path.GetFullPath(target.FullName);
        }

        return current;
    }

    private static bool IsInside(string root, string candidate)
    {
        var fullRoot = ResolveRealPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var fullCandidate = ResolveRealPath(candidate);

        // Exact root is allowed, as are descendants of the root.
        var exactRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullCandidate, exactRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>リンクを辿ったうえで同じ場所を指すか。</summary>
    private static bool SamePath(string a, string b)
        => string.Equals(
            ResolveRealPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            ResolveRealPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void EnsureInside(string root, string candidate)
    {
        if (!IsInside(root, candidate))
            throw new UnauthorizedAccessException("The requested path is outside the allowed folder.");
    }

    private static void EnsureWritableExtension(string fullPath, string[] allowed)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();

        if (!allowed.Contains(ext))
            throw new UnauthorizedAccessException($"Writing files with extension '{ext}' is not allowed.");
    }

    private static void EnsureProjectConfigured(ServerState state)
    {
        if (string.IsNullOrWhiteSpace(state.ProjectRoot))
            throw new InvalidOperationException("Unity project folder is not configured.");

        if (!Directory.Exists(state.ProjectRoot))
            throw new DirectoryNotFoundException($"Project folder not found: {state.ProjectRoot}");
    }

    private static void EnsureRootExists(NamedRoot root)
    {
        if (Directory.Exists(root.FullPath)) return;

        throw new DirectoryNotFoundException(
            string.IsNullOrEmpty(root.Name)
                ? $"Project folder not found: {root.FullPath}"
                : $"Root @{root.Name} not found: {root.FullPath}");
    }
}
