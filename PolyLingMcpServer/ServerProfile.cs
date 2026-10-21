namespace PolyLingMcpServer;

/// <summary>
/// 比較用の条件。起動引数 --profile で 1 つ選ぶ。
///
/// 3 条件は同じサーバ・同じ Unity で動き、違うのは「どの道具を見せるか」と
/// 「運用指示（instructions）」だけにする。道具の中身は条件によって変えない。
/// 以後の改善は Optimized にだけ入れ、Generic と Current は固定する。
///
///   Generic   … 一般的な MCP サーバの形。PolyLing の全コマンドを 1 コマンド 1 道具
///               （pl_&lt;コマンド名&gt;）として tools/list に出す。polyling_tools / polyling_call は出さない。
///   Current   … 5ee9a518 時点の 28 道具そのまま。
///   Optimized … 新実装。既定。検索 → 個別記述 → 実行（polyling_search / polyling_describe / polyling_call）。
///               利用シーン（PolyLing 側 SceneLibrary）で見せる道具と検索対象を絞れる。
///               利用シーンは要求の _meta "polyling/scene" で明示する（無ければ --scene）。
/// </summary>
public enum ServerProfile
{
    Generic,
    Current,
    Optimized
}

public static class ProfileRules
{
    /// <summary>Generic で PolyLing コマンドを道具にするときの名前の頭。</summary>
    public const string GenericToolPrefix = "pl_";

    /// <summary>Optimized でだけ見せる道具。</summary>
    private static readonly HashSet<string> OptimizedOnly = new(StringComparer.Ordinal)
    {
        "polyling_search",
        "polyling_describe",
        "polyling_scenes",
        "unity_validate_changes"
    };

    /// <summary>
    /// 共通の道具のうち、Optimized でだけ見せる引数。他の条件では tools/list の Schema から外す
    /// （5ee9a518 の Schema と同じ形を保つため）。外した引数は呼び出しで渡されても既定値のまま動く。
    /// </summary>
    private static readonly Dictionary<string, string[]> OptimizedOnlyParams = new(StringComparer.Ordinal)
    {
        ["list_files"]  = ["max_results", "note_truncation"],
        ["search_text"]    = ["summary"],
        ["unity_capture"]  = ["panel_id", "max_long_edge", "format", "quality"],
        ["read_unity_log"] = ["severity", "contains", "max_entries", "include_stacktrace"]
    };

    /// <summary>
    /// どの条件でも Schema に出さない引数（サーバが入れる内部用）。
    /// </summary>
    private static readonly Dictionary<string, string[]> InternalParams = new(StringComparer.Ordinal)
    {
        ["list_files"]  = ["note_truncation"],
        ["apply_patch"] = ["report_detail"]
    };

    /// <summary>
    /// Optimized で、呼び出しが省いたときにサーバが入れる引数。
    /// 値は初期値で、ベンチマーク（MCP_ベンチマーク課題.md）の結果で調整する。
    /// tools/list の Schema の "default" もこの値に書き換えて見せる。
    /// </summary>
    private static readonly Dictionary<string, (string Key, object Value, bool Visible)[]> OptimizedDefaults = new(StringComparer.Ordinal)
    {
        ["list_files"]   = [("max_results", 200, true), ("note_truncation", true, false)],
        ["search_text"]  = [("max_matches", 50, true), ("max_output_chars", 20000, true)],
        ["apply_patch"]  = [("report_detail", true, false)],
        ["unity_capture"] = [("max_long_edge", 1568, true)],
        ["read_unity_log"] = [("severity", "error", true), ("max_entries", 20, true)]
    };

    /// <summary>この条件で Schema から外す引数。</summary>
    public static IReadOnlyCollection<string> HiddenParams(ServerProfile profile, string toolName)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (InternalParams.TryGetValue(toolName, out var internals)) hidden.UnionWith(internals);
        if (profile != ServerProfile.Optimized && OptimizedOnlyParams.TryGetValue(toolName, out var opt)) hidden.UnionWith(opt);
        return hidden;
    }

    /// <summary>この条件で、呼び出しが省いたときに入れる引数。</summary>
    public static IReadOnlyList<(string Key, object Value, bool Visible)> DefaultsFor(ServerProfile profile, string toolName)
        => profile == ServerProfile.Optimized && OptimizedDefaults.TryGetValue(toolName, out var d) ? d : [];

    /// <summary>
    /// Optimized で利用シーンを指定しても外さない道具。
    /// 利用シーンの tools に書き漏れても、コマンドの発見・実行と利用シーンの切り替え先の確認はできるようにする。
    /// </summary>
    private static readonly HashSet<string> AlwaysInScene = new(StringComparer.Ordinal)
    {
        "polyling_search",
        "polyling_describe",
        "polyling_call",
        "polyling_scenes"
    };

    /// <summary>
    /// 利用シーンで固定の道具を絞るときに、この道具を見せるか。
    /// sceneTools が空なら利用シーンは道具を絞らない。
    /// </summary>
    public static bool IsVisibleInScene(string toolName, IReadOnlyList<string> sceneTools)
        => sceneTools.Count == 0
           || AlwaysInScene.Contains(toolName)
           || sceneTools.Contains(toolName, StringComparer.Ordinal);

    /// <summary>Generic では見せない道具（1 コマンド 1 道具で置き換わるもの）。</summary>
    private static readonly HashSet<string> HiddenInGeneric = new(StringComparer.Ordinal)
    {
        "polyling_tools",
        "polyling_call"
    };

    public static string Name(ServerProfile profile) => profile switch
    {
        ServerProfile.Generic   => "generic",
        ServerProfile.Current   => "current",
        ServerProfile.Optimized => "optimized",
        _ => profile.ToString().ToLowerInvariant()
    };

    public static bool TryParse(string? text, out ServerProfile profile)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "generic":   profile = ServerProfile.Generic;   return true;
            case "current":   profile = ServerProfile.Current;   return true;
            case "optimized": profile = ServerProfile.Optimized; return true;
            default:          profile = ServerProfile.Current;   return false;
        }
    }

    /// <summary>属性で定義した固定の道具を、この条件で見せるか。</summary>
    public static bool IsFixedToolVisible(ServerProfile profile, string toolName) => profile switch
    {
        ServerProfile.Generic   => !OptimizedOnly.Contains(toolName) && !HiddenInGeneric.Contains(toolName),
        ServerProfile.Current   => !OptimizedOnly.Contains(toolName),
        ServerProfile.Optimized => true,
        _ => false
    };

    /// <summary>
    /// 条件ごとに説明文だけを差し替える道具。null なら属性の説明のまま。
    /// 実装は変えない（どの条件でも同じ処理が走る）。
    /// </summary>
    public static string? DescriptionOverride(ServerProfile profile, string toolName)
    {
        if (profile != ServerProfile.Optimized) return null;

        return toolName switch
        {
            "polyling_tools" =>
                "Diagnostics only: returns the full JSON Schema of every PolyLing command in one large response. "
                + "For normal work use polyling_search and polyling_describe instead. Requires the PolyLing panel to be open.",
            "polyling_call" =>
                "Runs one PolyLing command. Find the command with polyling_search and read its arguments with "
                + "polyling_describe before calling. Requires the PolyLing panel to be open.",
            _ => null
        };
    }

    /// <summary>
    /// server/discover（initialize）で返す運用指示。クライアントが system に入れる。
    /// 3 条件とも「コマンドの見つけ方」だけを同じ粒度で書く。
    /// </summary>
    public static string Instructions(ServerProfile profile) => profile switch
    {
        ServerProfile.Generic =>
            "PolyLing commands are exposed as individual tools named pl_<command>. "
            + "Call the matching pl_ tool directly with its arguments. "
            + "Each pl_ tool also accepts model_index (default 0).",
        ServerProfile.Current =>
            "PolyLing commands are run with polyling_call. "
            + "Call polyling_tools first to get every command name and its argument schema, then call polyling_call.",
        ServerProfile.Optimized =>
            "PolyLing commands are run with polyling_call. "
            + "Find candidate commands with polyling_search, read the argument schema of the chosen ones with polyling_describe, "
            + "then call polyling_call. polyling_tools returns every schema at once and is for diagnostics only. "
            + "polyling_search also finds scenarios (saved multi-step procedures, kind \"scenario\") in the same ranked list; "
            + "read one with polyling_describe (scenarios) and run it with polyling_call runScenario. "
            + "A usage scene (利用シーン) is a named preset for one phase of modeling work, such as skinning or morph editing. It sits between a scenario (a whole procedure) and a single command. It is not a Unity scene. "
            + "polyling_scenes lists the usage scenes; pass one as scene to polyling_search to search only the commands it covers. "
            + "After editing scripts, check them with unity_validate_changes (it refreshes Unity and returns only the new errors).",
        _ => string.Empty
    };
}
