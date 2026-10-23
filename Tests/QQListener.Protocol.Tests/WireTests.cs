using QQListener.Protocol;

namespace QQListener.Protocol.Tests;

/// <summary>
/// 验证 protobuf 基础线格式与固定字节向量的解码结果。
/// </summary>
public class WireTests
{
    [Fact]
    public void ReadVarint_Multibyte()
    {
        // 300 = 0xAC 0x02
        var (value, next) = Wire.ReadVarint([0xAC, 0x02], 0);
        Assert.Equal(300UL, value);
        Assert.Equal(2, next);
    }

    [Fact]
    public void DecodeField_Varint()
    {
        // field 2 varint 300: tag=(2<<3)|0=0x10
        var fields = Wire.DecodeFields([0x10, 0xAC, 0x02]);
        Assert.Equal(300UL, Wire.AsInt(fields[2][0]));
        Assert.Equal(WireType.Varint, fields[2][0].Type);
    }

    [Fact]
    public void DecodeField_String()
    {
        // field 1 LEN "hi": tag=0x0A len=2
        var fields = Wire.DecodeFields([0x0A, 0x02, (byte)'h', (byte)'i']);
        Assert.Equal(WireType.Len, fields[1][0].Type);
        Assert.Equal("hi", Wire.AsString(fields[1][0]));
        Assert.Equal("hi"u8.ToArray(), Wire.AsBytes(fields[1][0]));
    }

    [Fact]
    public void DecodeField_HighFieldNumber()
    {
        // field 15 LEN "u": tag=(15<<3)|2=0x7A len=1
        var fields = Wire.DecodeFields([0x7A, 0x01, (byte)'u']);
        Assert.Equal("u", Wire.AsString(fields[15][0]));
    }

    [Fact]
    public void RepeatedFields_AggregateInOrder()
    {
        var fields = Wire.DecodeFields([0x08, 0x01, 0x08, 0x02]);
        Assert.Equal([1UL, 2UL], fields[1].Select(Wire.AsInt));
    }

    [Fact]
    public void NestedMessage_DecodesViaAsBytes()
    {
        // field 3 LEN 包住 {field 1 LEN "x"}
        byte[] inner = [0x0A, 0x01, (byte)'x'];
        byte[] outer = [(3 << 3) | 2, (byte)inner.Length, .. inner];
        var fields = Wire.DecodeFields(outer);
        var sub = Wire.DecodeFields(Wire.AsBytes(fields[3][0]));
        Assert.Equal("x", Wire.AsString(sub[1][0]));
    }

    [Fact]
    public void TruncatedVarint_Throws()
    {
        // 0x80 表示"还有后续字节"，但数据到此为止
        Assert.Throws<InvalidDataException>(() => Wire.DecodeFields([0x08, 0x80]));
    }

    [Fact]
    public void EncodeRoundTrips()
    {
        var encoded = Wire.Concat(
            Wire.EncodeVarintField(2, 300),
            Wire.EncodeBytesField(1, "hi"));
        var fields = Wire.DecodeFields(encoded);
        Assert.Equal(300UL, Wire.FieldInt(fields, 2));
        Assert.Equal("hi", Wire.FieldString(fields, 1));
    }

    [Fact]
    public void MissingFields_YieldZeroValues()
    {
        var fields = Wire.DecodeFields([]);
        Assert.Equal(0UL, Wire.FieldInt(fields, 7));
        Assert.Equal("", Wire.FieldString(fields, 7));
        Assert.Empty(Wire.FieldBytes(fields, 7));
        Assert.Empty(Wire.Repeated(fields, 7));
    }

    [Fact]
    public void HugeLenLength_TakesWhatsAvailableInsteadOfThrowingOrCorrupting()
    {
        // field 1 LEN，声明长度是 0x7FFFFFFF，但缓冲区只剩 0 字节——(int) 强转前不夹住
        // 会让游标算出负数，要么抛出跟协议无关的异常，要么原地打转。
        var fields = Wire.DecodeFields([0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07]);
        Assert.Empty(Wire.FieldBytes(fields, 1));
    }

    [Fact]
    public void LenLengthNearUInt32Boundary_DoesNotSilentlyTreatRemainingBytesAsANewField()
    {
        // 声明长度 2^32：旧实现里 (int)length 低 32 位为 0，pos 原地不动，剩下的字节
        // 会被当成一个全新的 tag 继续解析，产生完全错误（要么抛出跟"长度过大"毫不
        // 相干的异常，要么在更长的输入下静默解出垃圾字段）的结果——而不是被当成
        // 这个字段截断后拿到的内容。
        byte[] hugeLen = [0x0A, .. Wire.EncodeVarint(1UL << 32)];
        var fields = Wire.DecodeFields([.. hugeLen, (byte)'x']);

        Assert.Single(fields); // 只有一个字段：'x' 没有被拆成第二个 tag
        Assert.Equal("x"u8.ToArray(), Wire.FieldBytes(fields, 1));
    }

    [Fact]
    public void FieldBytes_RejectsNonLenWireType()
    {
        // field 1 varint：FieldBytes 只认 LEN，类型不对时应该当成"字段缺失"，
        // 不能把 varint 的原始字节盲目交出去让上层当成嵌套消息二次解析。
        var fields = Wire.DecodeFields([0x08, 0x01]);
        Assert.Empty(Wire.FieldBytes(fields, 1));
    }

    [Fact]
    public void FieldInt_RejectsLenWireType()
    {
        // field 1 LEN "hi"：FieldInt 只认 Varint/I32/I64，不能把字符串字节
        // 硬解成一个"看起来合理"的整数。
        var fields = Wire.DecodeFields([0x0A, 0x02, (byte)'h', (byte)'i']);
        Assert.Equal(0UL, Wire.FieldInt(fields, 1));
    }

    [Fact]
    public void InvalidFieldNumber_ThrowsInsteadOfWrappingAround()
    {
        // tag 的字段号部分算出 0——不是合法的 protobuf 字段号。
        Assert.Throws<InvalidDataException>(() => Wire.DecodeFields([0x00]));
    }

    [Fact]
    public void EncodeVarintField_RejectsNonPositiveFieldNumber() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Wire.EncodeVarintField(0, 1));

    [Fact]
    public void EncodeVarintField_LargeFieldNumberDoesNotOverflowIntoAWrongTag()
    {
        // fieldNo << 3 在 int 宽度内会先溢出成负数；转 ulong 前必须先转好类型。
        const int fieldNo = 0x0FFFFFFF;
        var encoded = Wire.EncodeVarintField(fieldNo, 1);
        var fields = Wire.DecodeFields(encoded);
        Assert.Equal(1UL, Wire.FieldInt(fields, fieldNo));
    }
}
