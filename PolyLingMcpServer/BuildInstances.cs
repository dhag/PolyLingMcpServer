using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyLingMcpServer;

// ============================================================
// ビルド構成と接続先の解決（PolyLing_クライアント種別とビルド構成計画.md 段 4）
// ============================================================
//
// 【構成ファイル】 Editor 側 Packages/com.haglib.polyling/Editor/Build/PolyLingBuildConfig.cs と同じ順・同じ形・同じ検査。
//   1. Assets/PolyLingBuild/ビルド構成.json
//   2. Packages/com.haglib.polyling/Editor/Build/既定ビルド構成.json
//   { "version":1, "editor":{"name"}, "builds":[{"name","scenes","exe","role","port","user"}] }
//   editor.port は書けない（書くと拒否。段 6）。Editor の受付は EditorControlClient.Port（8770）固定で、
//   unity_play・unity_refresh・unity_prefab_* など Editor 専用の道具も構成を読まずにそこへ送るため。
//   Editor 側は JsonUtility で読む（キーは大文字小文字を区別し、無いキーは初期値のまま、知らないキーは無視）。
//   ここも同じになるよう、プロパティ名を JsonPropertyName で固定し、初期値を Editor 側のフィールド初期値に合わせた。
//
// 【instance】 polyling_call などの道具の引数。構成名。
//   省略 … 起動時の --target のまま（editor は 8770・プロセス Unity、player は 8772・--player-exe）。構成ファイルは読まない。
//   editor.name と同じ … Editor。ポートは EditorControlClient.Port（8770）、前面化するプロセスは Unity。
//   builds の name    … そのビルド版。ポートは port、exe は exe。
//   MCP サーバは呼び出しごとに別プロセスになることがあるので、起動したプロセスを控えず、毎回ファイルから引く。
//
// 【MCP の受付があるか】
//   受付（PolyLingPlayerControlServer）を立てるのは本体ビューア（PolyLingPlayerViewer が作る PolyLingPlayerViewerCore）だけ。
//   構成のシーン（空なら Build Settings の有効なシーン）のどれも PolyLingPlayerViewer を参照していなければ、
//   リストクライアントだけの構成として、送る前に理由付きで断る（RequireMcpAcceptor）。
//   参照の有無は、シーンと、そこから m_SourcePrefab で入れ子に参照されるプレファブのファイルに、
//   PolyLingPlayerViewer.cs.meta の guid が書かれているかで見る。調べられないとき（ファイルが無いなど）は断らずに送る。
// ============================================================

/// <summary>ビルド構成ファイル全体。Editor 側 PolyLingBuildConfigFile と同じ形。</summary>
public sealed class BuildConfigFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("editor")]  public EditorInstanceConfig? Editor { get; set; } = new();
    [JsonPropertyName("builds")]  public List<BuildConfigEntry?>? Builds { get; set; } = new();
}

/// <summary>
/// Editor の受付。Editor 側 PolyLingEditorInstance と同じ形。
/// ポートは EditorControlClient.Port（8770）固定で構成には書かない（段 6）。
/// Port は書かれていないことを確かめるためだけに置く（null のまま＝書かれていない。値があれば検査で拒否）。
/// </summary>
public sealed class EditorInstanceConfig
{
    [JsonPropertyName("name")] public string? Name { get; set; } = "editor";
    [JsonPropertyName("port")] public int? Port { get; set; }
}

/// <summary>ビルド構成 1 つ。Editor 側 PolyLingBuildConfig と同じ形。</summary>
public sealed class BuildConfigEntry
{
    [JsonPropertyName("name")]   public string? Name { get; set; }
    [JsonPropertyName("scenes")] public List<string?>? Scenes { get; set; } = new();
    [JsonPropertyName("exe")]    public string? Exe { get; set; }
    [JsonPropertyName("role")]   public string? Role { get; set; }
    [JsonPropertyName("port")]   public int Port { get; set; }
    [JsonPropertyName("user")]   public string? User { get; set; } = "";
}

/// <summary>ビルド構成ファイルの読み込みと検査。Editor 側 PolyLingBuildConfigLoader と同じ順・同じ検査。</summary>
public static class BuildConfigLoader
{
    /// <summary>ProjectLayout.BuildConfig。Editor 側 PolyLingBuildConfig.ProjectConfigPath と同じ場所。</summary>
    public static string ProjectConfigPath => ProjectLayout.Current.BuildConfig;

    /// <summary>ProjectLayout.DefaultBuildConfig。Editor 側 PolyLingBuildConfig.DefaultConfigPath と同じ場所。</summary>
    public static string DefaultConfigPath => ProjectLayout.Current.DefaultBuildConfig;

    /// <summary>role に書ける値。</summary>
    public static readonly string[] Roles = { "server", "host", "collaborator", "separate", "none" };

    /// <summary>構成を読む。Assets 側があればそちら、無ければパッケージ既定。sourcePath はプロジェクト相対。</summary>
    public static bool TryLoad(string projectRoot, out BuildConfigFile config, out string sourcePath, out string error)
    {
        config = null!;
        error  = string.Empty;

        string projectFile = Path.Combine(projectRoot, ProjectConfigPath);
        string defaultFile = Path.Combine(projectRoot, DefaultConfigPath);

        bool useProject = File.Exists(projectFile);
        sourcePath = useProject ? ProjectConfigPath : DefaultConfigPath;
        string full = useProject ? projectFile : defaultFile;

        if (!File.Exists(full))
        {
            error = $"ビルド構成ファイルがありません: {ProjectConfigPath} も {DefaultConfigPath} も見つかりません";
            return false;
        }

        string text;
        try { text = File.ReadAllText(full, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            error = $"{sourcePath} を読めません: {ex.Message}";
            return false;
        }

        BuildConfigFile? parsed;
        try { parsed = JsonSerializer.Deserialize<BuildConfigFile>(text); }
        catch (Exception ex)
        {
            error = $"{sourcePath} の JSON を解釈できません: {ex.Message}";
            return false;
        }
        if (parsed == null)
        {
            error = $"{sourcePath} が空です";
            return false;
        }

        if (!Validate(parsed, projectRoot, out string reason))
        {
            error = $"{sourcePath}: {reason}";
            return false;
        }

        config = parsed;
        return true;
    }

    /// <summary>構成名で引く。大文字小文字は区別しない。</summary>
    public static BuildConfigEntry? Find(BuildConfigFile config, string name)
    {
        if (config.Builds == null || string.IsNullOrWhiteSpace(name)) return null;
        foreach (var b in config.Builds)
            if (b != null && string.Equals(b.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>instance に書ける名前の一覧（editor を先頭に）。</summary>
    public static string InstanceNames(BuildConfigFile config)
    {
        var names = new List<string>();
        if (config.Editor?.Name != null) names.Add(config.Editor.Name.Trim());
        if (config.Builds != null)
            foreach (var b in config.Builds)
                if (b?.Name != null) names.Add(b.Name);
        return string.Join(", ", names);
    }

    private static bool Validate(BuildConfigFile config, string projectRoot, out string reason)
    {
        reason = string.Empty;

        if (config.Editor == null || string.IsNullOrWhiteSpace(config.Editor.Name))
        {
            reason = "editor.name がありません";
            return false;
        }
        if (config.Editor.Port != null)
        {
            reason = $"editor.port は書けません（Editor の受付は PolyLingEditorControlServer.Port＝{EditorControlClient.Port} 固定）: {config.Editor.Port}";
            return false;
        }
        if (config.Builds == null || config.Builds.Count == 0)
        {
            reason = "builds に構成がありません";
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.Editor.Name.Trim() };
        var ports = new HashSet<int> { EditorControlClient.Port };
        var exes  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(projectRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

        for (int i = 0; i < config.Builds.Count; i++)
        {
            var b = config.Builds[i];
            string at = $"builds[{i}]";
            if (b == null) { reason = $"{at} が空です"; return false; }

            if (string.IsNullOrWhiteSpace(b.Name)) { reason = $"{at}.name がありません"; return false; }
            b.Name = b.Name.Trim();
            at = $"builds[{i}]（{b.Name}）";
            if (!names.Add(b.Name)) { reason = $"{at}.name が editor か他の構成と重複しています"; return false; }

            if (string.IsNullOrWhiteSpace(b.Exe)) { reason = $"{at}.exe がありません"; return false; }
            b.Exe = b.Exe.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(b.Exe)) { reason = $"{at}.exe はプロジェクト直下からの相対で書いてください: {b.Exe}"; return false; }
            if (!b.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { reason = $"{at}.exe が .exe ではありません: {b.Exe}"; return false; }
            string exeFull = Path.GetFullPath(Path.Combine(projectRoot, b.Exe));
            if (!exeFull.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { reason = $"{at}.exe がプロジェクトの外です: {b.Exe}"; return false; }
            if (!exes.Add(exeFull)) { reason = $"{at}.exe が他の構成と重複しています: {b.Exe}"; return false; }

            b.Role = (b.Role ?? string.Empty).Trim().ToLowerInvariant();
            if (Array.IndexOf(Roles, b.Role) < 0)
            {
                reason = $"{at}.role は {string.Join(" / ", Roles)} のどれかです: {b.Role}";
                return false;
            }

            b.User = (b.User ?? string.Empty).Trim();

            if (!IsPort(b.Port)) { reason = $"{at}.port が 1〜65535 ではありません: {b.Port}"; return false; }
            if (!ports.Add(b.Port)) { reason = $"{at}.port が editor か他の構成と重複しています: {b.Port}"; return false; }

            b.Scenes ??= new List<string?>();
            for (int s = 0; s < b.Scenes.Count; s++)
            {
                string scene = (b.Scenes[s] ?? string.Empty).Trim().Replace('\\', '/');
                b.Scenes[s] = scene;
                if (!scene.StartsWith("Assets/", StringComparison.Ordinal)
                    || !scene.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"{at}.scenes[{s}] は Assets/... .unity で書いてください: {scene}";
                    return false;
                }
                if (!File.Exists(Path.Combine(projectRoot, scene)))
                {
                    reason = $"{at}.scenes[{s}] のシーンがありません: {scene}";
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsPort(int port) => port >= 1 && port <= 65535;
}

/// <summary>
/// 道具の接続先。instance を引いた結果。
///   Build が null … Editor か、instance 省略時の --target player。
///   ExeFullPath   … ビルド構成の exe（絶対パス）。instance 省略時は null。
///   ConfigFile    … 引いた構成ファイル（プロジェクト相対）。instance 省略時は空。
/// </summary>
public sealed record ResolvedInstance(
    string Name,
    bool IsEditor,
    int Port,
    string ProcessName,
    string? ExeFullPath,
    BuildConfigEntry? Build,
    string ConfigFile)
{
    public string Endpoint => EditorControlClient.EndpointOf(Port);

    /// <summary>文言に出す接続先の説明。</summary>
    public string Describe => ConfigFile.Length == 0
        ? $"--target {Name} ({Endpoint})"
        : $"instance '{Name}' ({Endpoint}, {ConfigFile})";
}

public static class InstanceResolver
{
    /// <summary>PolyLingPlayerViewer.cs.meta（ProjectLayout.ViewerScriptMeta）。guid でシーンからの参照を探す。</summary>
    private static string ViewerScriptMetaPath => ProjectLayout.Current.ViewerScriptMeta;

    /// <summary>Build Settings（プロジェクト相対）。構成の scenes が空のときに使う。</summary>
    private const string EditorBuildSettingsPath = "ProjectSettings/EditorBuildSettings.asset";

    /// <summary>プレファブの入れ子をたどる深さの上限。</summary>
    private const int MaxPrefabDepth = 8;

    /// <summary>
    /// instance を接続先にする。省略時は --target のまま（構成ファイルは読まない）。
    /// 失敗の文言は "ERROR: " で始まる。
    /// </summary>
    public static bool TryResolve(ServerState state, string? instance, out ResolvedInstance resolved, out string error)
    {
        resolved = null!;
        error    = string.Empty;

        if (string.IsNullOrWhiteSpace(instance))
        {
            resolved = state.Target == ServerTarget.Player
                ? new ResolvedInstance("player", false, EditorControlClient.PlayerPort, state.FocusProcessName, null, null, string.Empty)
                : new ResolvedInstance("editor", true, EditorControlClient.Port, "Unity", null, null, string.Empty);
            return true;
        }

        if (!state.HasProject)
        {
            error = "ERROR: instance needs the build configuration file in the Unity project, but this server was started without --project.";
            return false;
        }

        if (!BuildConfigLoader.TryLoad(state.ProjectRoot, out var config, out var configFile, out var loadError))
        {
            error = "ERROR: " + loadError;
            return false;
        }

        string name = instance.Trim();

        if (string.Equals(config.Editor!.Name!.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            resolved = new ResolvedInstance(config.Editor.Name.Trim(), true, EditorControlClient.Port, "Unity", null, null, configFile);
            return true;
        }

        var build = BuildConfigLoader.Find(config, name);
        if (build == null)
        {
            error = $"ERROR: instance '{name}' is not in {configFile}. Names: {BuildConfigLoader.InstanceNames(config)}";
            return false;
        }

        string exe = Path.GetFullPath(Path.Combine(state.ProjectRoot, build.Exe!));
        resolved = new ResolvedInstance(build.Name!, false, build.Port, Path.GetFileNameWithoutExtension(exe), exe, build, configFile);
        return true;
    }

    /// <summary>
    /// TryResolve に加え、MCP の受付が無いと分かっている構成（リストクライアントだけのシーン）を断る。
    /// polyling_call・unity_capture など、受付ポートへ送る道具が使う。
    /// </summary>
    public static bool TryResolveReachable(ServerState state, string? instance, out ResolvedInstance resolved, out string error)
    {
        if (!TryResolve(state, instance, out resolved, out error)) return false;
        if (resolved.Build == null) return true;

        var scenes = BuildScenes(state.ProjectRoot, resolved.Build);
        if (scenes == null) return true; // シーンを特定できない。断らずに送る。

        string? viewerGuid = ReadGuid(Path.Combine(state.ProjectRoot, ViewerScriptMetaPath));
        if (viewerGuid == null) return true; // 本体ビューアの guid が引けない。断らずに送る。

        var prefabs = new Lazy<Dictionary<string, string>>(() => PrefabsByGuid(state.ProjectRoot));
        foreach (var scene in scenes)
        {
            bool? found = References(Path.Combine(state.ProjectRoot, scene), viewerGuid, prefabs, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
            if (found != false) return true; // 見つかった、または調べられなかった。
        }

        error = $"ERROR: {resolved.Describe} has no MCP port to talk to. "
              + $"Its scenes ({string.Join(", ", scenes)}) contain no PolyLingPlayerViewer, and only the main viewer opens the MCP port "
              + $"(PolyLingPlayerControlServer), so nothing listens on port {resolved.Port} even though the configuration names it. "
              + $"This is a list-client-only configuration (role {resolved.Build.Role}); polyling_call, unity_capture and the other commands cannot reach it. "
              + "player_launch, player_stop and unity_focus still work with it.";
        return false;
    }

    /// <summary>構成のシーン（プロジェクト相対）。scenes が空なら Build Settings の有効なシーン。特定できなければ null。</summary>
    private static List<string>? BuildScenes(string projectRoot, BuildConfigEntry build)
    {
        if (build.Scenes != null && build.Scenes.Count > 0)
            return build.Scenes.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

        var guids = EnabledBuildSettingsSceneGuids(Path.Combine(projectRoot, EditorBuildSettingsPath));
        if (guids == null || guids.Count == 0) return null;

        var assets = Path.Combine(projectRoot, "Assets");
        if (!Directory.Exists(assets)) return null;

        var byGuid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in Directory.EnumerateFiles(assets, "*.unity.meta", SearchOption.AllDirectories))
        {
            var g = ReadGuid(meta);
            if (g != null) byGuid[g] = meta[..^".meta".Length];
        }

        var scenes = new List<string>();
        foreach (var g in guids)
        {
            if (!byGuid.TryGetValue(g, out var full)) return null; // 消えたシーン。特定できない。
            scenes.Add(Path.GetRelativePath(projectRoot, full).Replace('\\', '/'));
        }
        return scenes;
    }

    /// <summary>
    /// EditorBuildSettings.asset の m_Scenes から enabled: 1 の guid を順に拾う。読めなければ null。
    /// 形（2026-11-01 の ProjectSettings/EditorBuildSettings.asset）:
    ///   m_Scenes:
    ///   - enabled: 1
    ///     path: "..."（長いと次の行へ折り返す）
    ///     guid: 8e2999d877a699d45a9d09a8172b4155
    ///   m_configObjects: ...
    /// </summary>
    private static List<string>? EnabledBuildSettingsSceneGuids(string file)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch { return null; }

        var guids = new List<string>();
        bool inScenes = false;
        bool enabled  = false;

        foreach (var line in lines)
        {
            if (!inScenes)
            {
                if (line.Trim() == "m_Scenes:") inScenes = true;
                continue;
            }

            // 2 字下げの次のキー（m_configObjects など）で m_Scenes が終わる。
            if (line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal) && char.IsLetter(line[2])) break;

            var t = line.Trim();
            if (t.StartsWith("- enabled:", StringComparison.Ordinal))
                enabled = t["- enabled:".Length..].Trim() == "1";
            else if (t.StartsWith("guid:", StringComparison.Ordinal) && enabled)
                guids.Add(t["guid:".Length..].Trim());
        }
        return guids;
    }

    /// <summary>
    /// ファイル（シーンかプレファブ）が guid のスクリプトを参照しているか。
    /// m_SourcePrefab で参照するプレファブも深さ MaxPrefabDepth までたどる。
    /// true=参照あり、false=無い、null=調べられなかった（ファイルが読めない・引けないプレファブがある・深すぎる）。
    /// </summary>
    private static bool? References(string file, string guid, Lazy<Dictionary<string, string>> prefabs, HashSet<string> visited, int depth)
    {
        if (!visited.Add(file)) return false;
        if (depth > MaxPrefabDepth) return null;

        string text;
        try { text = File.ReadAllText(file); }
        catch { return null; }

        if (text.Contains("guid: " + guid, StringComparison.OrdinalIgnoreCase)) return true;

        bool unknown = false;
        const string Marker = "m_SourcePrefab:";
        int at = 0;
        while ((at = text.IndexOf(Marker, at, StringComparison.Ordinal)) >= 0)
        {
            int lineEnd = text.IndexOf('\n', at);
            string line = lineEnd < 0 ? text[at..] : text[at..lineEnd];
            at += Marker.Length;

            int g = line.IndexOf("guid:", StringComparison.Ordinal);
            if (g < 0) continue;
            string prefabGuid = new string(line[(g + 5)..].Trim().TakeWhile(char.IsLetterOrDigit).ToArray());
            if (prefabGuid.Length == 0) continue;

            if (!prefabs.Value.TryGetValue(prefabGuid, out var prefabFile)) { unknown = true; continue; }

            bool? inner = References(prefabFile, guid, prefabs, visited, depth + 1);
            if (inner == true) return true;
            if (inner == null) unknown = true;
        }
        return unknown ? null : false;
    }

    /// <summary>Assets と Packages の .prefab を guid で引く表。</summary>
    private static Dictionary<string, string> PrefabsByGuid(string projectRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var top in new[] { "Assets", "Packages" })
        {
            var dir = Path.Combine(projectRoot, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var meta in Directory.EnumerateFiles(dir, "*.prefab.meta", SearchOption.AllDirectories))
            {
                var g = ReadGuid(meta);
                if (g != null) map[g] = meta[..^".meta".Length];
            }
        }
        return map;
    }

    /// <summary>.meta の guid。読めなければ null。</summary>
    private static string? ReadGuid(string metaFile)
    {
        try
        {
            foreach (var line in File.ReadLines(metaFile))
            {
                var t = line.Trim();
                if (t.StartsWith("guid:", StringComparison.Ordinal)) return t[5..].Trim();
            }
        }
        catch { }
        return null;
    }
}
