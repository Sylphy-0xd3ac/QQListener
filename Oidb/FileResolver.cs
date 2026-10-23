using System.Text.RegularExpressions;
using QQListener.Protocol;

namespace QQListener.Oidb;

/// <summary>
/// 文件下载地址解析。
///
/// 群文件推送里没有下载 URL，要用 fileId/fileKey 发 OIDB 0x6d6_2 换；
/// 私聊文件走 0xe37_1200。两者的响应结构完全不同。
/// </summary>
public static partial class FileResolver
{
    // \A/\z 而不是 ^/$：正则的 $ 匹配"字符串结尾或结尾换行之前"，一个以 \n 收尾的
    // host（服务端 bug 或者恶意构造——这条链路上的数据全部来自被注入进程，应按
    // 不可信处理）会被 ^...$ 放过，把控制字符原样拼进最终请求的 URL 里。
    [GeneratedRegex(@"\A[A-Za-z0-9.-]+\z")]
    private static partial Regex HostRegex();

    /// <summary>path 只校验了开头是不是 '/'；控制字符（比如夹带的 \r\n）同样不能放过，
    /// 拼进 URL 之后可能被下游当成额外的请求头或者破坏请求结构。</summary>
    private static bool IsSafePath(string path) =>
        path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Any(char.IsControl);

    // ---- 群文件 0x6d6_2 ----

    public static byte[] BuildGroupRequest(ulong groupUin, string fileId, ulong busId = 102)
    {
        var download = Wire.Concat(
            Wire.EncodeVarintField(1, groupUin),
            Wire.EncodeVarintField(2, 7),
            Wire.EncodeVarintField(3, busId),
            Wire.EncodeBytesField(4, fileId));

        return OidbEnvelope.Build(0x6D6, 2, Wire.EncodeBytesField(3, download), uinForm: true);
    }

    public static string ParseGroupResponse(ReadOnlySpan<byte> data, string fileId)
    {
        var response = OidbEnvelope.Unwrap(data, "group file");
        var download = Wire.DecodeFields(Wire.FieldBytes(response, 3));

        var retCode = (int)Wire.FieldInt(download, 1); // 负数错误码，见 OidbEnvelope.Unwrap 的注释
        if (retCode != 0)
            throw new IOException($"group file URL error: {retCode}");

        var host = Wire.FieldString(download, 5);
        if (host.Length == 0) host = Wire.FieldString(download, 4);
        var downloadKey = Convert.ToHexString(Wire.FieldBytes(download, 6));

        if (host.Length == 0 || !HostRegex().IsMatch(host) || downloadKey.Length == 0)
            throw new InvalidDataException("invalid group file URL response");

        return $"https://{host}/ftn_handler/{downloadKey}/?fname={Uri.EscapeDataString(fileId)}";
    }

    // ---- 私聊文件 0xe37_1200 ----

    public static byte[] BuildPrivateRequest(string accountUid, string fileId, string fileHash)
    {
        var requestBody = Wire.Concat(
            Wire.EncodeBytesField(10, accountUid),
            Wire.EncodeBytesField(20, fileId),
            Wire.EncodeVarintField(30, 2),
            Wire.EncodeBytesField(60, fileHash),
            Wire.EncodeVarintField(601, 0));

        var request = Wire.Concat(
            Wire.EncodeVarintField(1, 1200),
            Wire.EncodeVarintField(2, 1),
            Wire.EncodeBytesField(14, requestBody),
            Wire.EncodeVarintField(101, 3),
            Wire.EncodeVarintField(102, 103),
            Wire.EncodeVarintField(200, 1),
            Wire.EncodeBytesField(99999, [0xC0, 0x85, 0x2C, 0x01]));

        return OidbEnvelope.Build(0xE37, 1200, request, uinForm: false);
    }

    public static string ParsePrivateResponse(ReadOnlySpan<byte> data)
    {
        var response = OidbEnvelope.Unwrap(data, "private file");
        var body = Wire.DecodeFields(Wire.FieldBytes(response, 14));
        var result = Wire.DecodeFields(Wire.FieldBytes(body, 30));

        var host = Wire.FieldString(result, 20);
        var port = Wire.FieldInt(result, 40);
        var path = Wire.FieldString(result, 50);

        // 拆成三种可区分的失败信息——这条路径目前没有服务端返回码可读（不像群
        // 文件那边字段 1 有 retCode），把 host/port/path 三类问题混在同一句话里，
        // 用户和日志都无从判断是文件过期、协议解析写错了，还是别的什么原因。
        if (host.Length == 0 || !HostRegex().IsMatch(host))
            throw new InvalidDataException("private file URL host missing or invalid（服务端未返回可用主机名，常见于文件已过期或无权限）");
        if (port == 0 || port > 65535)
            throw new InvalidDataException($"private file URL port invalid: {port}");
        if (!IsSafePath(path))
            throw new InvalidDataException("private file URL path invalid");

        var separator = path.Contains('?') ? "&" : "?";
        return $"http://{host}:{port}{path}{separator}isthumb=0";
    }
}

/// <summary>好友资料（备注 / 昵称）。OIDB 0xFE1_2。</summary>
public static class ProfileResolver
{
    private const int PropertyNickname = 20002;
    private const int PropertyRemark = 103;
    private static readonly int[] ProfileKeys = [PropertyNickname, PropertyRemark];

    public sealed record UserProfileNames(string Nickname = "", string Remark = "");

    public static byte[] BuildRequest(string userId)
    {
        var normalized = (userId ?? "").Trim();
        if (normalized.Length == 0)
            throw new ArgumentException("user id is empty", nameof(userId));

        byte[] body;
        bool uinForm;
        if (ulong.TryParse(normalized, out var uin) && uin > 0)
        {
            body = Wire.EncodeVarintField(1, uin);
            uinForm = true;
        }
        else
        {
            body = Wire.EncodeBytesField(1, normalized);
            uinForm = false;
        }

        foreach (var key in ProfileKeys)
            body = Wire.Concat(body, Wire.EncodeBytesField(3, Wire.EncodeVarintField(1, (ulong)key)));

        return OidbEnvelope.Build(0xFE1, 2, body, uinForm);
    }

    public static UserProfileNames ParseResponse(ReadOnlySpan<byte> data)
    {
        var response = OidbEnvelope.Unwrap(data, "profile");

        var bodyData = Wire.FieldBytes(response, 1);
        if (bodyData.Length == 0)
            throw new InvalidDataException("profile response body missing");

        var body = Wire.DecodeFields(bodyData);
        var propertiesData = Wire.FieldBytes(body, 2);
        var properties = propertiesData.Length > 0 ? Wire.DecodeFields(propertiesData) : [];

        var values = new Dictionary<int, string>();
        foreach (var item in Wire.Repeated(properties, 2))
        {
            var prop = Wire.DecodeFields(Wire.AsBytes(item));
            var code = (int)Wire.FieldInt(prop, 1);
            var value = Wire.FieldBytes(prop, 2);
            if (Array.IndexOf(ProfileKeys, code) >= 0 && value.Length > 0)
                values[code] = System.Text.Encoding.UTF8.GetString(value).Trim();
        }

        return new UserProfileNames(
            values.GetValueOrDefault(PropertyNickname, ""),
            values.GetValueOrDefault(PropertyRemark, ""));
    }
}
