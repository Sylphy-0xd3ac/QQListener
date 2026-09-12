using System.Text;

namespace QQListener.Protocol.Tests;

/// <summary>
/// 手搓 protobuf 字节的测试助手，对应 Python 测试里的 <c>_len_field</c> / <c>_varint_field</c>。
///
/// 合成帧而不是用真抓包：真帧含真实 QQ 号、uid 和 rkey，不能进仓库。
/// </summary>
internal static class Pb
{
    public static byte[] Varint(ulong value)
    {
        var outBytes = new List<byte>();
        while (true)
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            outBytes.Add((byte)(b | (value != 0 ? 0x80 : 0)));
            if (value == 0) return [.. outBytes];
        }
    }

    public static byte[] LenField(int fieldNo, ReadOnlySpan<byte> payload) =>
    [
        .. Varint((ulong)((fieldNo << 3) | 2)),
        .. Varint((ulong)payload.Length),
        .. payload.ToArray()
    ];

    public static byte[] LenField(int fieldNo, string text) =>
        LenField(fieldNo, Encoding.UTF8.GetBytes(text));

    public static byte[] VarintField(int fieldNo, ulong value) =>
        [.. Varint((ulong)(fieldNo << 3)), .. Varint(value)];

    public static byte[] Cat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var p in parts) result.AddRange(p);
        return [.. result];
    }

    /// <summary>把若干元素包成 RichText。</summary>
    public static byte[] Rich(params byte[][] elems) =>
        Cat([.. elems.Select(e => LenField(RichText.RichTextElems, e))]);

    public static byte[] TextElem(string text, byte[]? pbReserve = null, byte[]? attr6 = null)
    {
        var inner = LenField(1, text);
        if (attr6 is { Length: > 0 }) inner = Cat(inner, LenField(3, attr6));
        if (pbReserve is { Length: > 0 }) inner = Cat(inner, LenField(12, pbReserve));
        return LenField(RichText.ElemText, inner);
    }

    public static byte[] MentionExtra(int type, ulong uin = 0, string uid = "")
    {
        var outBytes = Cat(VarintField(3, (ulong)type), VarintField(4, uin));
        if (uid.Length > 0) outBytes = Cat(outBytes, LenField(9, uid));
        return outBytes;
    }

    public static byte[] SrcMsgElem(ulong seq, ulong senderUin, byte[][] elems, int time = 0)
    {
        var src = Cat(VarintField(1, seq), VarintField(2, senderUin));
        if (time != 0) src = Cat(src, VarintField(3, (ulong)time));
        foreach (var e in elems) src = Cat(src, LenField(5, e));
        return LenField(RichText.ElemSrcMsg, src);
    }

    /// <summary>CommonElem(service=48, business=10) —— NTQQ 现行图片元素。</summary>
    public static byte[] NtImageElem(
        string urlPath = "/download?appid=1407&fileid=FID",
        string domain = "multimedia.nt.qq.com.cn",
        string originalParameter = "&spec=0",
        string md5 = "ea0cd2ae3bf8596f4d75481bd5b544b8",
        string rkey = "",
        string fileUuid = "FID")
    {
        var fileInfo = Cat(
            VarintField(1, 2352855),
            LenField(2, md5),
            LenField(3, "sha1"),
            LenField(4, "IMG.png"),
            VarintField(6, 2264),
            VarintField(7, 1834));

        var index = Cat(LenField(1, fileInfo), LenField(2, fileUuid));
        var picture = Cat(
            LenField(1, urlPath),
            LenField(2, LenField(1, originalParameter)),
            LenField(3, domain));
        var infoBody = Cat(LenField(1, index), LenField(2, picture));

        var msgInfo = LenField(1, infoBody);
        if (rkey.Length > 0)
        {
            var picReserve = LenField(30, rkey);
            var extBiz = LenField(1, LenField(11, picReserve));
            msgInfo = Cat(msgInfo, LenField(2, extBiz));
        }

        var common = Cat(VarintField(1, 48), LenField(2, msgInfo), VarintField(3, 10));
        return LenField(RichText.ElemCommon, common);
    }

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}
