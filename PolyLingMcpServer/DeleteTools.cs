using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Globalization;

namespace PolyLingMcpServer;

/// <summary>
/// ファイル・フォルダを消す道具。
///
/// 【消さずに移す】
///   本当に消すと取り返しがつかない。ルート直下の _McpTrash/&lt;日時&gt;/ へ、
///   ルートからの相対パスを保ったまま移す。戻すのは手で動かすだけでよい。
///   ゴミ箱は編集可能フォルダの外に置くので、この道具では触れない。
///   溜まったら人が消す。
///
/// 【なぜ OS のごみ箱を使わないか】
///   .NET から Windows のごみ箱へ入れるには Microsoft.VisualBasic.FileIO の
///   削除 API を使うことになる。あれは状況によってダイアログを出すが、
///   このサーバは stdin/stdout しか持たない子プロセスで、誰も押せない。
///   出た時点で応答が返らなくなる。
///
/// 【届く範囲は書き込みと同じ】
///   編集可能フォルダの中だけ（PathGuard.ResolveDeletablePath）。
///   作れないものは消せない、という 1 本の線で揃える。
///   拡張子では絞らない。フォルダと .meta は書き込み用の表に載らないため。
///
/// 【.meta】
///   Unity は Assets / Packages の中の各項目に &lt;名前&gt;.meta を持たせる。
///   本体だけ移すと Unity が孤児の .meta を消すので、戻したときに
///   GUID が変わり、参照が切れる。対の .meta があれば一緒に移す。
/// </summary>
[McpServerToolType]
public static class DeleteTools
{
    /// <summary>ゴミ箱の名前。ルート直下に作る。</summary>
    public const string TrashFolderName = "_McpTrash";

    [McpServerTool(Name = "delete_path", Destructive = true),
     Description("Deletes a file or folder inside one of the configured editable folders by moving it to a trash "
               + "folder, so it can be put back by hand. The trash is <root>/" + TrashFolderName + "/<timestamp>/, "
               + "which keeps the path it had inside that root. A matching Unity .meta file is moved with it. "
               + "A folder that still has anything in it needs recursive=true. "
               + "Reach is the same as for writing: what this server cannot create, it cannot delete.")]
    public static string DeletePath(
        ServerState state,
        UiDispatcher ui,
        [Description("Project-relative path, or @<root>/path, of the file or folder to delete.")] string path,
        [Description("Must be true to delete a folder that is not empty. Defaults to false.")] bool recursive = false)
    {
        ui.Log($"delete_path {path} recursive={recursive}");

        try
        {
            var fullPath = PathGuard.ResolveDeletablePath(state, path);

            bool isDirectory = Directory.Exists(fullPath);
            bool isFile      = File.Exists(fullPath);

            if (!isDirectory && !isFile)
                return $"ERROR: Not found: {path}";

            if (PathGuard.IsRootItself(state, fullPath))
                return $"ERROR: {path} is a root folder itself. Delete something inside it instead.";

            if (PathGuard.IsEditableRootItself(state, fullPath))
                return $"ERROR: {path} is one of the editable folders itself. Delete something inside it instead.";

            if (IsInTrash(fullPath))
                return $"ERROR: {path} is already in {TrashFolderName}. Empty the trash by hand.";

            if (isDirectory && !recursive && HasAnyEntry(fullPath))
                return $"ERROR: {path} is a folder that is not empty. Set recursive=true to delete it with its contents.";

            if (!PathGuard.TryGetContainingRoot(state, fullPath, out var root))
                return $"ERROR: {path} is not inside any known root.";

            // ゴミ箱は「その項目が入っていたルート」の直下に作る。
            // 別のルートへ移すと、ルートが別のドライブにあるときに
            // Directory.Move / File.Move が失敗する（巻き戻しの要らない移動でなくなる）。
            var stamp     = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var inside    = Path.GetRelativePath(root.FullPath, fullPath);
            var trashPath = Path.Combine(root.FullPath, TrashFolderName, stamp, inside);

            var trashParent = Path.GetDirectoryName(trashPath);
            if (string.IsNullOrEmpty(trashParent))
                return $"ERROR: could not work out where to move {path}.";

            if (Directory.Exists(trashPath) || File.Exists(trashPath))
                return $"ERROR: {PathGuard.ToDisplayPath(state, trashPath)} is already taken. Try again in a second.";

            Directory.CreateDirectory(trashParent);

            if (isDirectory) Directory.Move(fullPath, trashPath);
            else             File.Move(fullPath, trashPath);

            // 対の .meta。無ければ何もしない。
            string movedMeta = string.Empty;
            var metaSource = fullPath + ".meta";
            if (File.Exists(metaSource))
            {
                var metaTarget = trashPath + ".meta";
                if (!File.Exists(metaTarget))
                {
                    File.Move(metaSource, metaTarget);
                    movedMeta = " (with its .meta)";
                }
            }

            var from = PathGuard.ToDisplayPath(state, fullPath);
            var to   = PathGuard.ToDisplayPath(state, trashPath);
            var kind = isDirectory ? "folder" : "file";

            ui.Log($"delete_path moved {from} -> {to}");
            return $"OK: moved the {kind} {from}{movedMeta} to {to}. "
                 + "It is not gone; move it back from there to undo this.";
        }
        catch (Exception ex)
        {
            return "ERROR: " + ex.Message;
        }
    }

    /// <summary>中身が 1 つでもあるか。</summary>
    private static bool HasAnyEntry(string directory)
        => Directory.EnumerateFileSystemEntries(directory).Any();

    /// <summary>経路のどこかに _McpTrash を含むか。</summary>
    private static bool IsInTrash(string fullPath)
    {
        for (string? cursor = Path.GetFullPath(fullPath);
             !string.IsNullOrEmpty(cursor);
             cursor = Path.GetDirectoryName(cursor))
        {
            if (string.Equals(Path.GetFileName(cursor), TrashFolderName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
