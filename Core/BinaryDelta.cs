using System.Buffers.Binary;

namespace QQListener.Core;

/// <summary>QQLDIFF1: copy the base, resize, then apply ordered literal ranges.</summary>
internal static class BinaryDelta
{
    private const int MaximumBytes = 16 * 1024 * 1024;

    public static byte[] Apply(ReadOnlySpan<byte> original, ReadOnlySpan<byte> delta)
    {
        if (delta.Length < 20 || delta.Length > MaximumBytes || !delta[..8].SequenceEqual("QQLDIFF1"u8))
            throw new InvalidDataException("PMHQ 补丁头无效");
        var sourceLength = BinaryPrimitives.ReadUInt32LittleEndian(delta[8..]);
        var targetLength = BinaryPrimitives.ReadUInt32LittleEndian(delta[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(delta[16..]);
        if (sourceLength != original.Length || targetLength == 0 || targetLength > MaximumBytes || count > 4096)
            throw new InvalidDataException("PMHQ 补丁长度无效");
        var result = new byte[(int)targetLength];
        original[..Math.Min(original.Length, result.Length)].CopyTo(result);
        var cursor = 20;
        uint previousEnd = 0;
        for (var index = 0; index < count; index++)
        {
            if (delta.Length - cursor < 8) throw new InvalidDataException("PMHQ 补丁不完整");
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(delta[cursor..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(delta[(cursor + 4)..]);
            cursor += 8;
            if (offset < previousEnd || offset > targetLength || length == 0
                || length > targetLength - offset || length > delta.Length - cursor)
                throw new InvalidDataException("PMHQ 补丁区间无效");
            delta.Slice(cursor, (int)length).CopyTo(result.AsSpan((int)offset));
            cursor += (int)length;
            previousEnd = offset + length;
        }
        if (cursor != delta.Length) throw new InvalidDataException("PMHQ 补丁包含多余数据");
        return result;
    }
}
