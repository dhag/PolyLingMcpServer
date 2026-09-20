using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyLingMcpServer;

/// <summary>
/// Editor が返した JSON から画像を取り出し、MCP の image content へ移す。
///
/// 【なぜ必要か】
///   polyling_call は Editor の応答 JSON をそのまま文字列で返している。
///   Editor がコマンドの戻り値として画像を入れるようになったとき、
///   そのままでは base64 が JSON の中の文字列として Claude に届く。
///   これは二重に困る。
///     1. 文字列である以上、Claude は画像として見られない。
///     2. base64 はおよそ 3 文字で 1 トークン。500KB の画像で 20 万トークン近くを、
///        何も見えないまま使い切る。文脈が一度で埋まる。
///   そこで MCP の境界で JSON から抜き、image content へ移し替える。
///   JSON 側には枚数だけを残す。抜いたものを文字列としても残すと、
///   トークンを二重に払うことになる。
///
/// 【Editor 側の約束】
///   応答オブジェクトの直下に、次のどちらかを置く。
///     "image":  {"mimeType":"image/png","base64":"…"}
///     "images": [{"mimeType":"image/png","base64":"…"}, …]
///   キー名は mimeType / mime_type / mime、base64 / data のいずれでもよい。
///   mimeType は無くてもよい。先頭バイトから判定するので、
///   申告と中身が食い違った場合は中身を採る。
///
/// 【伝送路について】
///   EditorControlClient は 1 行 1 JSON で往復する。画像を載せると
///   1 行が数十万文字になる。TcpDuplexChannel 側の受信バッファと
///   行の長さ上限が耐えられるかは、この層とは別に確かめること。
///   ここは「届いた JSON をどう扱うか」だけを引き受ける。
/// </summary>
public static class EditorImage
{
    private static readonly string[] SingleKeys = ["image"];
    private static readonly string[] ArrayKeys  = ["images"];

    private static readonly string[] MimeKeys   = ["mimeType", "mime_type", "mime"];
    private static readonly string[] DataKeys   = ["base64", "data"];

    /// <summary>
    /// 画像を抜いた JSON 文字列を返し、抜いた画像を images に積む。
    ///
    /// 画像キーが無ければ、元の文字列をそのまま返す。整形しなおさないので、
    /// 画像を返さないコマンドの応答はこれまでと 1 バイトも変わらない。
    /// JSON として読めない場合も同じく素通しする（Editor の応答の形を
    /// 判断するのはここの仕事ではない）。
    /// </summary>
    public static string Lift(string rawJson, List<ImageAttachment> images, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return rawJson;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            return rawJson;
        }

        if (root is not JsonObject obj) return rawJson;

        bool touched = false;

        foreach (var key in SingleKeys)
        {
            if (obj[key] is not JsonObject entry) continue;

            TryTake(entry, $"{key}", images, warnings);
            obj.Remove(key);
            touched = true;
        }

        foreach (var key in ArrayKeys)
        {
            if (obj[key] is not JsonArray array) continue;

            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonObject entry)
                    TryTake(entry, $"{key}[{i}]", images, warnings);
                else
                    warnings.Add($"{key}[{i}] is not an object, so it was dropped.");
            }

            obj.Remove(key);
            touched = true;
        }

        if (!touched) return rawJson;

        // 抜いたことを JSON 側にも残す。Claude が「画像は来なかった」と
        // 誤解しないようにするためで、中身は戻さない。
        obj["images_moved_to_image_content"] = images.Count;

        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void TryTake(JsonObject entry, string label, List<ImageAttachment> images, List<string> warnings)
    {
        var encoded = FirstString(entry, DataKeys);

        if (string.IsNullOrWhiteSpace(encoded))
        {
            warnings.Add($"{label} has no base64 payload, so it was dropped.");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            warnings.Add($"{label} is not valid base64 ({ex.Message}), so it was dropped.");
            return;
        }

        // 申告された MIME は名前にだけ使う。実際の MIME は中身から決める。
        var declared = FirstString(entry, MimeKeys);

        if (!ImageBytes.TryCreate(bytes, label, out var attachment, out var error))
        {
            warnings.Add(error);
            return;
        }

        if (!string.IsNullOrWhiteSpace(declared)
            && !string.Equals(declared, attachment!.MimeType, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{label} declared {declared} but the bytes are {attachment.MimeType}; "
                       + "the bytes were used.");
        }

        images.Add(attachment!);
    }

    private static string? FirstString(JsonObject entry, string[] keys)
    {
        foreach (var key in keys)
        {
            if (entry[key] is JsonValue value && value.TryGetValue<string>(out var text))
                return text;
        }

        return null;
    }
}
