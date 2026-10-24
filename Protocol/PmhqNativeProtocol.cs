using System.Globalization;
using System.Text.Json;

namespace QQListener.Protocol;

/// <summary>The DLL pipe protocol differs from the Rust launcher's HTTP envelopes.</summary>
public static class PmhqNativeProtocol
{
    public sealed record Frame(string Type, ulong Id, int Code, string Command, string Uin, string Echo, byte[] Body);

    public static byte[] Request(ulong id, string command, byte[] body)
    {
        if (id == 0 || command.Length is 0 or > 512 || body.Length > PmhqProtocol.MaxJsonBytes / 2 - 2048)
            throw new IOException("PMHQ 请求参数或大小无效");
        return [.. JsonSerializer.SerializeToUtf8Bytes(new { id, cmd = command, body = Convert.ToHexString(body) }), (byte)'\n'];
    }

    public static Frame Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > PmhqProtocol.MaxJsonBytes) throw new IOException("PMHQ 管道数据超过大小限制");
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            string Text(string field) => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : "";
            var type = Text("kind") == "resp" ? "resp" : Text("type");
            if (type == "resp")
            {
                if (!root.TryGetProperty("id", out var id) || !id.TryGetUInt64(out var correlation) || correlation == 0
                    || !root.TryGetProperty("code", out var status) || !status.TryGetInt32(out var code)) throw new JsonException();
                // A failure body is diagnostic text, never protobuf and never included in exceptions.
                if (code == 0 && (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String)) throw new JsonException();
                return new(type, correlation, code, "", "", "", code == 0 ? Convert.FromHexString(Text("data")) : []);
            }
            var uin = root.TryGetProperty("uin", out var account) ? account.ValueKind switch
            {
                JsonValueKind.String => account.GetString() ?? "",
                JsonValueKind.Number => account.GetRawText(),
                _ => ""
            } : "";
            if (!ulong.TryParse(uin, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n == 0) uin = "";
            var command = Text("cmd");
            var echo = Text("echo");
            var body = type == "recv" && echo.Length == 0 && SsoDecode.MessagePushCmds.Contains(command)
                ? Convert.FromHexString(Text("pb")) : [];
            return new(type, 0, 0, command, uin, echo, body);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            throw new IOException("PMHQ 管道返回了无效数据");
        }
    }
}
