using System.Globalization;
using System.Text.Json;

namespace QQListener.Protocol;

public sealed record PmhqHealth(int Pid, string Uin, string Uid)
{
    public bool IsLocalPipe { get; init; }
    public bool LoggedIn => Pid > 0 && Uin.Length > 0 && (Uid.Length > 0 || IsLocalPipe);
}

/// <summary>PMHQ v9.0.6 JSON envelope, aligned with LLBot qqProtocol/pmhq.ts.</summary>
public static class PmhqProtocol
{
    public const int MaxJsonBytes = 16 * 1024 * 1024;
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 32 };

    public static PmhqHealth ReadHealth(ReadOnlyMemory<byte> json)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var pid = root.TryGetProperty("qq_pid", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var n) ? n : 0;
        var uin = root.TryGetProperty("uin", out var u) ? u.ValueKind switch
        {
            JsonValueKind.String => u.GetString() ?? "",
            JsonValueKind.Number => u.GetRawText(),
            _ => ""
        } : "";
        if (!ulong.TryParse(uin, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) uin = "";
        return new(Math.Max(0, pid), uin, Text(root, "uid"));
    }

    public static RecvPacket? ReadPush(ReadOnlyMemory<byte> json, string selfUin)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (Text(root, "type") != "recv" || ErrorCode(root) is not (null or 0)
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || Text(data, "echo").Length > 0) return null;
        var command = Text(data, "cmd");
        if (!SsoDecode.MessagePushCmds.Contains(command)) return null;
        return new(0, 0, command, selfUin, Hex(Text(data, "pb")));
    }

    public static byte[] ReadReply(ReadOnlyMemory<byte> json, string echo, string command)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        // An echoed request can still be a failed send. Require explicit success.
        if (ErrorCode(root) != 0) throw new IOException("PMHQ 拒绝请求或尚未登录 QQ");
        if (Text(root, "type") is not ("recv" or "send")
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || Text(data, "echo") != echo || Text(data, "cmd") != command)
            throw new IOException("PMHQ 返回了不匹配的请求响应");
        if (!data.TryGetProperty("pb", out var pb) || pb.ValueKind != JsonValueKind.String)
            throw new IOException("PMHQ 响应缺少协议包");
        return Hex(pb.GetString() ?? "");
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> json)
    {
        if (json.Length > MaxJsonBytes) throw new IOException("PMHQ 数据超过大小限制");
        try
        {
            var document = JsonDocument.Parse(json, Options);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        throw new IOException("PMHQ 返回了无效的 JSON 数据");
    }

    private static int? ErrorCode(JsonElement root) => root.TryGetProperty("code", out var code)
        ? code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var n) ? n : int.MinValue : null;
    private static string Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    private static byte[] Hex(string value)
    {
        try { return Convert.FromHexString(value); }
        catch (FormatException) { throw new IOException("PMHQ 返回了无效的十六进制协议包"); }
    }
}
