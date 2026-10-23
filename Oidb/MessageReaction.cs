using QQListener.Protocol;

namespace QQListener.Oidb;

public static class MessageReaction
{
    public const string Command = "OidbSvcTrpcTcp.0x9082_1";
    public const string OkFaceCode = "124";

    public static byte[] BuildRequest(long groupUin, long sequence)
    {
        if (groupUin is <= 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(groupUin));
        if (sequence is <= 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(sequence));
        return OidbEnvelope.Build(0x9082, 1, Wire.Concat(
            Wire.EncodeVarintField(2, (ulong)groupUin),
            Wire.EncodeVarintField(3, (ulong)sequence),
            Wire.EncodeBytesField(4, System.Text.Encoding.UTF8.GetBytes(OkFaceCode)),
            Wire.EncodeVarintField(5, 1),
            Wire.EncodeVarintField(6, 0),
            Wire.EncodeVarintField(7, 0)), uinForm: false);
    }

    public static void ValidateResponse(ReadOnlySpan<byte> data)
    {
        var envelope = Wire.DecodeFields(data);
        if (Wire.First(envelope, 3) is { Type: not WireType.Varint })
            throw new InvalidDataException("QQ 表情回应结果格式不正确");
        if (Wire.First(envelope, 1) is not null && Wire.FieldInt(envelope, 1) != 0x9082
            || Wire.First(envelope, 2) is not null && Wire.FieldInt(envelope, 2) != 1)
            throw new InvalidDataException("QQ 表情回应响应命令不匹配");
        if (Wire.First(envelope, 3) is null && Wire.FieldInt(envelope, 1) != 0x9082)
            throw new InvalidDataException("QQ 表情回应响应缺少结果");
        var code = (int)Wire.FieldInt(envelope, 3);
        if (code != 0) throw new IOException($"QQ 拒绝表情回应（{code}）");
    }
}
