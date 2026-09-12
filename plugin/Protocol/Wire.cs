namespace QQListener.Protocol;

/// <summary>protobuf 线类型。只用到这四种，其余一律报错而不是静默跳过。</summary>
public enum WireType
{
    Varint = 0,
    I64 = 1,
    Len = 2,
    I32 = 5
}

/// <summary>
/// 一个字段的原始值。
///
/// varint 也按**小端最小字节**存进 <see cref="Raw"/>，这样 <see cref="Wire.AsInt"/>
/// 对 varint / I32 / I64 可以用同一套小端读法——与 Python 侧 wire.py 的语义逐位一致，
/// 移植过来的 golden vectors 才对得上。
/// </summary>
public readonly record struct WireValue(WireType Type, byte[] Raw);

/// <summary>
/// 手写的 protobuf 读取器：只按已知字段号取值，不需要 .proto 也不需要代码生成。
///
/// QQ 的消息结构层层嵌套且字段号来自逆向整理，用生成式 protobuf 反而更难维护——
/// 这里保持与 Python 版 <c>src/native/proto/wire.py</c> 相同的设计。
/// </summary>
public static class Wire
{
    /// <summary>读一个 varint，返回值与新的偏移量。</summary>
    public static (ulong Value, int Next) ReadVarint(ReadOnlySpan<byte> data, int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= data.Length)
                throw new InvalidDataException("truncated varint");

            var b = data[pos];
            pos++;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return (result, pos);

            shift += 7;
            if (shift > 63)
                throw new InvalidDataException("varint too long");
        }
    }

    /// <summary>把一段 protobuf 拆成 字段号 → 值列表（repeated 字段会有多个值）。</summary>
    public static Dictionary<int, List<WireValue>> DecodeFields(ReadOnlySpan<byte> data)
    {
        var fields = new Dictionary<int, List<WireValue>>();
        var pos = 0;
        while (pos < data.Length)
        {
            var (tag, next) = ReadVarint(data, pos);
            pos = next;
            var fieldRaw = tag >> 3;
            // protobuf 字段号合法区间是 1..2^29-1；越界值截成 int 时可能回绕成一个
            // 完全无关但"看起来合法"的字段号，让垃圾数据被当成真实字段解析。
            if (fieldRaw is 0 or > 0x1FFFFFFF)
                throw new InvalidDataException($"invalid field number {fieldRaw}");
            var fieldNo = (int)fieldRaw;
            var wireType = (WireType)(tag & 0x07);

            byte[] raw;
            switch (wireType)
            {
                case WireType.Varint:
                {
                    var (value, after) = ReadVarint(data, pos);
                    pos = after;
                    raw = MinimalLittleEndian(value);
                    break;
                }
                case WireType.I64:
                    (raw, _) = Slice(data, pos, 8);
                    pos += 8;
                    break;
                case WireType.Len:
                {
                    var (length, after) = ReadVarint(data, pos);
                    pos = after;
                    // length 来自 varint，最大 2^64-1；(int) 强转前必须先在安全范围内
                    // 夹住，否则要么直接抛出跟协议无关的异常，要么在截断为负数之后
                    // 让 pos 往回走、原地重复解析同一段数据（都在 Slice 里一并处理）。
                    var requested = length > (ulong)long.MaxValue ? long.MaxValue : (long)length;
                    var (sliceData, consumed) = Slice(data, pos, requested);
                    raw = sliceData;
                    pos += consumed;
                    break;
                }
                case WireType.I32:
                    (raw, _) = Slice(data, pos, 4);
                    pos += 4;
                    break;
                default:
                    throw new InvalidDataException(
                        $"unsupported wire_type={(int)wireType} field={fieldNo}");
            }

            if (!fields.TryGetValue(fieldNo, out var list))
            {
                list = [];
                fields[fieldNo] = list;
            }
            list.Add(new WireValue(wireType, raw));
        }
        return fields;
    }

    public static ulong AsInt(WireValue v)
    {
        ulong result = 0;
        var count = Math.Min(v.Raw.Length, 8);
        for (var i = 0; i < count; i++)
            result |= (ulong)v.Raw[i] << (8 * i);
        return result;
    }

    /// <summary>UTF-8 解码，坏字节替换而不是抛异常——线上偶尔有半截字符。</summary>
    public static string AsString(WireValue v) =>
        System.Text.Encoding.UTF8.GetString(v.Raw);

    public static byte[] AsBytes(WireValue v) => v.Raw;

    // ---- 取值助手：字段不存在时给零值，调用方不必层层判空 ----

    public static WireValue? First(Dictionary<int, List<WireValue>> fields, int fieldNo) =>
        fields.TryGetValue(fieldNo, out var list) && list.Count > 0 ? list[0] : null;

    // 三个取值助手都按 wire type 收紧："字段存在但类型不对" 和 "字段缺失" 同等对待
    // （给零值），而不是把 LEN 的字节盲目摊平成整数、或者把 varint 的字节硬解成字符串。
    // 对端把本该是 LEN 的字段编码成别的类型时，宁可"看起来缺失"也不要"看起来解出来了
    // 但其实是垃圾"——后者会让上层继续往下解一层，产生一堆无意义的错误。

    public static byte[] FieldBytes(Dictionary<int, List<WireValue>> fields, int fieldNo) =>
        First(fields, fieldNo) is { Type: WireType.Len } v ? AsBytes(v) : [];

    public static ulong FieldInt(Dictionary<int, List<WireValue>> fields, int fieldNo) =>
        First(fields, fieldNo) is { Type: WireType.Varint or WireType.I32 or WireType.I64 } v ? AsInt(v) : 0;

    public static string FieldString(Dictionary<int, List<WireValue>> fields, int fieldNo) =>
        First(fields, fieldNo) is { Type: WireType.Len } v ? AsString(v) : "";

    /// <summary>把协议里读到的 ulong 安全收窄成 int：垃圾/超范围值直接 (int) 强转会
    /// 回绕成一个"看起来合理"的错误值（时间戳变成 1901 年附近、类型码恰好撞上某个
    /// 合法值），这里改成夹到 int 的上界而不是截断。</summary>
    public static int ClampToInt32(ulong value) => value > int.MaxValue ? int.MaxValue : (int)value;

    public static IReadOnlyList<WireValue> Repeated(
        Dictionary<int, List<WireValue>> fields, int fieldNo) =>
        fields.TryGetValue(fieldNo, out var list) ? list : [];

    // ---- 编码：发送侧要用 ----

    public static byte[] EncodeVarint(ulong value)
    {
        var out_ = new List<byte>(10);
        while (true)
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            out_.Add((byte)(b | (value != 0 ? 0x80 : 0)));
            if (value == 0)
                return [.. out_];
        }
    }

    public static byte[] EncodeVarintField(int fieldNo, ulong value)
    {
        ValidateFieldNo(fieldNo);
        // 先转 ulong 再左移：fieldNo 是 int，(fieldNo << 3) 在字段号较大时会先在 32 位
        // 宽度内溢出成负数，转 ulong 时再符号扩展成一个跟真实字段号毫不相干的 tag。
        return [.. EncodeVarint(((ulong)fieldNo << 3) | (ulong)WireType.Varint), .. EncodeVarint(value)];
    }

    public static byte[] EncodeBytesField(int fieldNo, ReadOnlySpan<byte> value)
    {
        ValidateFieldNo(fieldNo);
        return
        [
            .. EncodeVarint(((ulong)fieldNo << 3) | (ulong)WireType.Len),
            .. EncodeVarint((ulong)value.Length),
            .. value.ToArray()
        ];
    }

    private static void ValidateFieldNo(int fieldNo)
    {
        if (fieldNo is <= 0 or > 0x1FFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(fieldNo), fieldNo, "字段号必须落在 1..2^29-1");
    }

    public static byte[] EncodeBytesField(int fieldNo, string value) =>
        EncodeBytesField(fieldNo, System.Text.Encoding.UTF8.GetBytes(value));

    public static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(result, offset);
            offset += p.Length;
        }
        return result;
    }

    private static byte[] MinimalLittleEndian(ulong value)
    {
        if (value == 0) return [0];
        var bytes = new List<byte>(8);
        while (value != 0)
        {
            bytes.Add((byte)(value & 0xFF));
            value >>= 8;
        }
        return [.. bytes];
    }

    /// <summary>
    /// 截取一段字节；返回实际截到的数据和实际消耗的长度。
    ///
    /// 线上帧可能被截断，或者某个字段声明的长度超过实际剩余字节——按 Python 版的
    /// 行为取到哪算哪，不抛异常。全程用 long 做加法：<paramref name="length"/> 来自
    /// 协议里的 varint，调用方在转 long 之前已经把 ulong 的超大值夹到
    /// <see cref="long.MaxValue"/>，这里再夹一次到"剩余字节数"，两次夹取都不可能
    /// 溢出——旧实现里 (int)length 在夹取之前就做窄化转换，超大声明值会截断成
    /// 负数，让游标原地打转甚至抛出跟协议错误无关的异常。
    /// </summary>
    private static (byte[] Data, int Consumed) Slice(ReadOnlySpan<byte> data, int pos, long length)
    {
        if (pos < 0 || length < 0) throw new InvalidDataException("negative offset or length");
        var available = Math.Max(0L, (long)data.Length - pos);
        var take = (int)Math.Min(length, available);
        return take <= 0 ? ([], 0) : (data.Slice(pos, take).ToArray(), take);
    }
}
