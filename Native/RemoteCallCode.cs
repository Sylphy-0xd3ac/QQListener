namespace QQListener.Native;

internal readonly record struct RemoteCallResult(ulong Value, uint LastError);

internal static class RemoteCallCode
{
    // Result layout: uint64 return value, uint32 GetLastError, uint32 completion marker.
    public static byte[] Build(ulong function, ulong resultAddress, ulong getLastError, params ulong[] args)
    {
        if (function == 0 || args.Length > 4) throw new ArgumentException("无效的远程调用参数");
        var values = new ulong[4];
        args.CopyTo(values, 0);
        var code = new List<byte> { 0x48, 0x83, 0xec, 0x28 };
        byte[][] registers = [[0x48, 0xb9], [0x48, 0xba], [0x49, 0xb8], [0x49, 0xb9]];
        for (var i = 0; i < 4; i++) { code.AddRange(registers[i]); code.AddRange(BitConverter.GetBytes(values[i])); }
        code.AddRange([0x48, 0xb8]); code.AddRange(BitConverter.GetBytes(function));
        code.AddRange([0xff, 0xd0, 0x49, 0xbb]); code.AddRange(BitConverter.GetBytes(resultAddress));
        code.AddRange([0x49, 0x89, 0x03]); // preserve full HMODULE before GetLastError clobbers RAX
        if (getLastError != 0)
        {
            code.AddRange([0x48, 0xb8]); code.AddRange(BitConverter.GetBytes(getLastError));
            code.AddRange([0xff, 0xd0, 0x49, 0xbb]); code.AddRange(BitConverter.GetBytes(resultAddress));
            code.AddRange([0x41, 0x89, 0x43, 0x08]);
        }
        code.AddRange([0x41, 0xc7, 0x43, 0x0c, 1, 0, 0, 0]);
        code.AddRange([0x31, 0xc0, 0x48, 0x83, 0xc4, 0x28, 0xc3]);
        return [.. code];
    }
}

public sealed class RemoteLibraryLoadException(string path, uint error)
    : InjectionError($"LoadLibraryW 无法加载 {path}：{new System.ComponentModel.Win32Exception((int)error).Message} " +
                     $"(Win32={error}, 0x{error:x8})")
{
    public uint Win32Error { get; } = error;
}
