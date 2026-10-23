using QQListener.Protocol;

namespace QQListener.Oidb;

/// <summary>
/// OIDB 请求信封。
///
/// 结构对照 SnowLuma <c>packages/proto-defs/src/oidb.ts</c> 的 OidbBase：
/// command=1, subCommand=2, errorCode=3, body=4, errorMsg=5, reserved=12。
/// </summary>
public static class OidbEnvelope
{
    public static byte[] Build(int command, int subCommand, byte[] body, bool uinForm) =>
        Wire.Concat(
            Wire.EncodeVarintField(1, (ulong)command),
            Wire.EncodeVarintField(2, (ulong)subCommand),
            Wire.EncodeVarintField(3, 0),
            Wire.EncodeBytesField(4, body),
            Wire.EncodeBytesField(5, []),
            Wire.EncodeVarintField(12, uinForm ? 1UL : 0UL));

    /// <summary>拆信封并检查 errorCode；返回 body（字段 4）。</summary>
    public static Dictionary<int, List<WireValue>> Unwrap(ReadOnlySpan<byte> data, string what)
    {
        var envelope = Wire.DecodeFields(data);
        // OIDB 的 errorCode 是 proto int32，QQ 给的通常是负数（比如 -5503010）；
        // Wire.FieldInt 返回 ulong 是为了同时兼容各种宽度的整数字段，这里显示前
        // 转回 int，否则负数会被当成天文数字打印出来，日志完全没法跟错误码表对上。
        var errorCode = (int)Wire.FieldInt(envelope, 3);
        if (errorCode != 0)
        {
            var msg = Wire.FieldString(envelope, 5);
            throw new IOException(msg.Length > 0
                ? $"{what} OIDB error {errorCode}: {msg}"
                : $"{what} OIDB error: {errorCode}");
        }

        // errorCode == 0 只代表"服务端没报错"，不代表 body 真的存在——应答被截断时
        // 字段 4 可能完全没出现在解出来的字典里。不在这里统一拦下的话，四个不同的
        // 调用方（rkey/群文件/私聊文件/资料）各自对着一个空字典摸索，抛出四种
        // 互相矛盾、都不说实话的错误信息。
        if (Wire.First(envelope, 4) is not { Type: WireType.Len } bodyValue)
            throw new InvalidDataException($"{what} OIDB response body missing");
        return Wire.DecodeFields(Wire.AsBytes(bodyValue));
    }
}
