namespace PolyLingMcpServer;

/// <summary>
/// 接続先。--target で決まり、起動中は変わらない。
///   Editor … Unity Editor（PolyLingEditorControlServer、8770）
///   Player … ビルド版（PolyLingPlayerControlServer、8772）
/// </summary>
public enum ServerTarget
{
    Editor,
    Player
}

/// <summary>
/// 接続先ごとの道具の出し分け。ProfileRules（条件ごとの出し分け）とは独立に効く。
///
/// 【player で隠すもの】
///   ビルド版にはソースもコンパイラも Play モードも無い。
///   ソースの読み書き・検索、コンパイル、Play 制御、Editor.log を読む道具は隠す。
///
/// 【player でも見せる Editor 向けの道具】
///   unity_prefab_export / instantiate / import は Editor（8770）へ直接送る
///   （UnityPrefabTools）。ビルド版で作ったファイルを Editor 拡張でプレファブにできるため。
/// </summary>
public static class TargetRules
{
    /// <summary>--target player で隠す道具。</summary>
    private static readonly HashSet<string> HiddenInPlayer = new(StringComparer.Ordinal)
    {
        "list_files",
        "read_file",
        "write_file",
        "apply_patch",
        "read_lines",
        "search_text",
        "read_binary",
        "write_binary",
        "delete_path",
        "compile",
        "get_compile_errors",
        "unity_play",
        "unity_stop",
        "unity_refresh",
        "unity_recompile",
        "unity_validate_changes",
        "read_unity_log"
    };

    /// <summary>この接続先で固定の道具を見せるか。pl_ 道具は対象外（常に見せる）。</summary>
    public static bool IsFixedToolVisible(ServerTarget target, string toolName)
        => target != ServerTarget.Player || !HiddenInPlayer.Contains(toolName);

    public static string Name(ServerTarget target) => target switch
    {
        ServerTarget.Editor => "editor",
        ServerTarget.Player => "player",
        _ => target.ToString().ToLowerInvariant()
    };

    public static bool TryParse(string? text, out ServerTarget target)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "editor": target = ServerTarget.Editor; return true;
            case "player": target = ServerTarget.Player; return true;
            default:       target = ServerTarget.Editor; return false;
        }
    }

    /// <summary>server/discover の運用指示に足す文。editor では空。</summary>
    public static string InstructionsSuffix(ServerTarget target) => target == ServerTarget.Player
        ? " This server is connected to a built PolyLing player, not the Unity Editor: "
          + "source files, compiling and Play mode control are unavailable. "
          + "unity_prefab_export / unity_prefab_instantiate / unity_prefab_import still go to the Unity Editor if it is open."
        : string.Empty;
}
