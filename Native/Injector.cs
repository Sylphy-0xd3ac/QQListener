using System.Buffers.Binary;

namespace QQListener.Native;

/// <summary>装载器无法安全完成操作。</summary>
public class InjectionError(string message) : Exception(message);

/// <summary>远程线程仍可能执行；调用方不得释放其代码或相关映像。</summary>
public sealed class RemoteCallTimeoutException(string message) : InjectionError(message);

public readonly record struct MapHandle(ulong Base, ulong Entry, ulong ExceptionTable, uint Size);

public readonly record struct SectionMapping(ulong Address, uint Size, uint Protection);

public sealed record PreparedImage(
    byte[] Data,
    ulong Entry,
    ulong ExceptionTable,
    int ExceptionCount,
    IReadOnlyList<SectionMapping> Sections,
    IReadOnlyList<ulong> TlsCallbacks);

/// <summary>把一条导入解析成目标进程里的真实地址；解析失败返回 0。</summary>
public delegate ulong ImportResolver(string module, byte[]? name, int? ordinal);

/// <summary>
/// Windows x64 PE 手动映射装载器的纯算法部分：解析、重定位、打导入表、收集 TLS 回调。
///
/// 不触碰任何 Windows API，支持跨平台验证映像构建逻辑。真正的
/// OpenProcess/VirtualAllocEx/CreateRemoteThread 在 <see cref="WindowsProcess"/>。
/// </summary>
public static class Injector
{
    public const ushort ImageFileMachineAmd64 = 0x8664;
    public const ushort ImageNtOptionalHdr64Magic = 0x20B;
    public const uint ImageFileDll = 0x2000;

    public const int ImageRelBasedAbsolute = 0;
    public const int ImageRelBasedHighLow = 3;
    public const int ImageRelBasedDir64 = 10;

    public const uint ImageScnMemExecute = 0x20000000;
    public const uint ImageScnMemRead = 0x40000000;
    public const uint ImageScnMemWrite = 0x80000000;

    public const uint PageNoAccess = 0x01;
    public const uint PageReadOnly = 0x02;
    public const uint PageReadWrite = 0x04;
    public const uint PageExecute = 0x10;
    public const uint PageExecuteRead = 0x20;
    public const uint PageExecuteReadWrite = 0x40;

    public const int DllProcessDetach = 0;
    public const int DllProcessAttach = 1;

    private const int RuntimeFunctionSize = 12;
    private const uint MaxImageSize = 1024 * 1024 * 1024;
    private const int MaxTlsCallbacks = 4096;

    public static void ValidatePe(PortablePeImage pe)
    {
        if (pe.Machine != ImageFileMachineAmd64)
            throw new InjectionError("只支持 x64 (AMD64) DLL");
        if (pe.OptionalHeaderMagic != ImageNtOptionalHdr64Magic)
            throw new InjectionError("目标文件不是 PE32+ x64 映像");
        if ((pe.FileCharacteristics & ImageFileDll) == 0)
            throw new InjectionError("目标 PE 不是 DLL");
    }

    /// <summary>构建与目标基址匹配的完整内存映像；不调用任何平台 API。</summary>
    public static PreparedImage PrepareImage(
        PortablePeImage pe, byte[] raw, ulong baseAddress, ImportResolver resolver)
    {
        var image = CopyImage(pe, raw);
        ApplyRelocations(pe, image, baseAddress);
        PatchImports(pe, image, resolver);

        var entryRva = pe.EntryPointRva;
        if (entryRva != 0)
            PortablePeImage.RequireRange((int)entryRva, 1, image.Length, "PE 入口点");
        var entry = entryRva != 0 ? baseAddress + entryRva : 0;

        ulong exceptionTable = 0;
        var exceptionCount = 0;
        var exceptionDir = pe.Directory(PortablePeImage.ImageDirectoryEntryException);
        if (exceptionDir.VirtualAddress != 0)
        {
            PortablePeImage.RequireRange(
                (int)exceptionDir.VirtualAddress, (int)exceptionDir.Size, image.Length, "x64 异常表");
            if (exceptionDir.Size % RuntimeFunctionSize != 0)
                throw new InjectionError("x64 异常表大小不是 RUNTIME_FUNCTION 的整数倍");
            exceptionTable = baseAddress + exceptionDir.VirtualAddress;
            exceptionCount = (int)(exceptionDir.Size / RuntimeFunctionSize);
        }

        var sections = new List<SectionMapping>();
        foreach (var section in pe.Sections)
        {
            var size = Math.Max(section.VirtualSize, section.RawSize);
            if (size == 0) continue;
            PortablePeImage.RequireRange((int)section.VirtualAddress, (int)size, image.Length, "PE 节保护范围");
            sections.Add(new SectionMapping(
                baseAddress + section.VirtualAddress, size, SectionProtection(section.Characteristics)));
        }

        return new PreparedImage(
            image, entry, exceptionTable, exceptionCount, sections, TlsCallbacks(pe, image, baseAddress));
    }

    private static uint SectionProtection(uint characteristics)
    {
        var executable = (characteristics & ImageScnMemExecute) != 0;
        var readable = (characteristics & ImageScnMemRead) != 0;
        var writable = (characteristics & ImageScnMemWrite) != 0;
        if (executable)
            return writable ? PageExecuteReadWrite : readable ? PageExecuteRead : PageExecute;
        if (writable) return PageReadWrite;
        return readable ? PageReadOnly : PageNoAccess;
    }

    private static byte[] CopyImage(PortablePeImage pe, byte[] raw)
    {
        var imageSize = pe.SizeOfImage;
        if (imageSize == 0 || imageSize > MaxImageSize)
            throw new InjectionError($"异常的 PE SizeOfImage: {imageSize}");
        var headerSize = pe.SizeOfHeaders;
        if (headerSize == 0 || headerSize > raw.Length)
            throw new InjectionError("PE 头被截断");
        PortablePeImage.RequireRange(0, (int)headerSize, (int)imageSize, "PE 头");

        var image = new byte[imageSize];
        raw.AsSpan(0, (int)headerSize).CopyTo(image);
        foreach (var section in pe.Sections)
        {
            if (section.RawSize == 0) continue;
            PortablePeImage.RequireRange((int)section.RawDataPointer, (int)section.RawSize, raw.Length, "PE 节原始数据");
            PortablePeImage.RequireRange((int)section.VirtualAddress, (int)section.RawSize, (int)imageSize, "PE 节虚拟数据");
            raw.AsSpan((int)section.RawDataPointer, (int)section.RawSize)
                .CopyTo(image.AsSpan((int)section.VirtualAddress));
        }
        return image;
    }

    private static void ApplyRelocations(PortablePeImage pe, byte[] image, ulong baseAddress)
    {
        var delta = unchecked(baseAddress - pe.ImageBase);
        if (delta == 0) return;
        if (pe.Relocations.Count == 0)
            throw new InjectionError("目标地址偏离首选基址，但 PE 没有基址重定位表");

        foreach (var relocation in pe.Relocations)
        {
            switch (relocation.Type)
            {
                case ImageRelBasedAbsolute:
                    continue;
                case ImageRelBasedDir64:
                {
                    PortablePeImage.RequireRange((int)relocation.Rva, 8, image.Length, "DIR64 重定位");
                    var span = image.AsSpan((int)relocation.Rva);
                    var value = BinaryPrimitives.ReadUInt64LittleEndian(span);
                    BinaryPrimitives.WriteUInt64LittleEndian(span, unchecked(value + delta));
                    continue;
                }
                case ImageRelBasedHighLow:
                {
                    PortablePeImage.RequireRange((int)relocation.Rva, 4, image.Length, "HIGHLOW 重定位");
                    var span = image.AsSpan((int)relocation.Rva);
                    var value = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    BinaryPrimitives.WriteUInt32LittleEndian(span, unchecked((uint)(value + delta)));
                    continue;
                }
                default:
                    throw new InjectionError($"不支持的 PE 重定位类型: {relocation.Type}");
            }
        }
    }

    private static void PatchImports(PortablePeImage pe, byte[] image, ImportResolver resolver)
    {
        foreach (var import in pe.Imports)
        {
            var address = resolver(import.Module, import.Name, import.Ordinal);
            if (address == 0)
            {
                var symbol = import.Name is { } name
                    ? System.Text.Encoding.ASCII.GetString(name)
                    : $"#{import.Ordinal}";
                throw new InjectionError($"无法解析导入: {import.Module}!{symbol}");
            }
            PortablePeImage.RequireRange((int)import.IatRva, 8, image.Length, "IAT 项");
            BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan((int)import.IatRva), address);
        }
    }

    /// <summary>
    /// TLS 目录的位置来自解析出的 <paramref name="pe"/>（一个固定 RVA，与基址无关），
    /// 但 AddressOfCallBacks 是一枚已重定位的 VA，要从刚打好补丁的 <paramref name="image"/> 里读。
    /// </summary>
    private static IReadOnlyList<ulong> TlsCallbacks(PortablePeImage pe, byte[] image, ulong baseAddress)
    {
        var dir = pe.Directory(PortablePeImage.ImageDirectoryEntryTls);
        if (dir.VirtualAddress == 0) return [];

        PortablePeImage.RequireRange((int)dir.VirtualAddress, 32, image.Length, "TLS 目录");
        // IMAGE_TLS_DIRECTORY64.AddressOfCallBacks 位于偏移 24。
        var callbacksVa = BinaryPrimitives.ReadUInt64LittleEndian(
            image.AsSpan((int)dir.VirtualAddress + 24));
        if (callbacksVa == 0) return [];

        var callbacksRva = unchecked((long)callbacksVa - (long)baseAddress);
        if (callbacksRva < 0)
            throw new InjectionError("TLS 回调表地址不在映像内");

        return ReadCallbackTable(image, callbacksRva, baseAddress, (ulong)image.Length, "TLS 回调表");
    }

    /// <summary>从仍在目标进程内的 PE 头恢复 TLS 回调，供对称卸载使用（不重新解析本地文件）。</summary>
    public static IReadOnlyList<ulong> RemoteTlsCallbacks(IRemoteMemory process, MapHandle handle)
    {
        var lfanew = BinaryPrimitives.ReadUInt32LittleEndian(process.Read(handle.Base + 0x3C, 4));
        PortablePeImage.RequireRange((int)lfanew, 24, (int)handle.Size, "远程 PE NT 头");
        if (!process.Read(handle.Base + lfanew, 4).AsSpan().SequenceEqual("PE\0\0"u8))
            throw new InjectionError("远程映像的 PE 签名无效");

        var optionalHeader = lfanew + 24;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(process.Read(handle.Base + optionalHeader, 2));
        if (magic != ImageNtOptionalHdr64Magic)
            throw new InjectionError("远程映像不是 PE32+ x64");

        // PE32+ OptionalHeader 的数据目录从偏移 112 开始，每项 8 字节。
        var tlsEntry = optionalHeader + 112 + PortablePeImage.ImageDirectoryEntryTls * 8;
        PortablePeImage.RequireRange((int)tlsEntry, 8, (int)handle.Size, "远程 TLS 数据目录");
        var tlsHeader = process.Read(handle.Base + tlsEntry, 8);
        var tlsRva = BinaryPrimitives.ReadUInt32LittleEndian(tlsHeader);
        var tlsSize = BinaryPrimitives.ReadUInt32LittleEndian(tlsHeader.AsSpan(4));
        if (tlsRva == 0 || tlsSize == 0) return [];

        PortablePeImage.RequireRange((int)tlsRva, 32, (int)handle.Size, "远程 TLS 目录");
        var callbacksVa = BinaryPrimitives.ReadUInt64LittleEndian(
            process.Read(handle.Base + tlsRva + 24, 8));
        if (callbacksVa == 0) return [];

        var callbacksRva = unchecked((long)callbacksVa - (long)handle.Base);
        if (callbacksRva < 0)
            throw new InjectionError("远程 TLS 回调表地址不在映像内");

        return ReadRemoteCallbackTable(process, callbacksRva, handle.Base, handle.Size, "远程 TLS 回调表");
    }

    private static IReadOnlyList<ulong> ReadCallbackTable(
        byte[] image, long tableRva, ulong baseAddress, ulong imageSize, string label)
    {
        var callbacks = new List<ulong>();
        for (var index = 0; index < MaxTlsCallbacks; index++)
        {
            var offset = tableRva + index * 8;
            PortablePeImage.RequireRange(offset, 8, image.Length, label);
            var callback = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)offset));
            if (callback == 0) return callbacks;
            if (callback < baseAddress || callback >= baseAddress + imageSize)
                throw new InjectionError("TLS 回调地址不在映像内");
            callbacks.Add(callback);
        }
        throw new InjectionError($"{label}未终止");
    }

    private static IReadOnlyList<ulong> ReadRemoteCallbackTable(
        IRemoteMemory process, long tableRva, ulong baseAddress, uint imageSize, string label)
    {
        var callbacks = new List<ulong>();
        for (var index = 0; index < MaxTlsCallbacks; index++)
        {
            var offset = tableRva + index * 8;
            // 校验必须用没有截断过的 offset：之前这里校验用 (int)offset，实际读取
            // 却用 baseAddress + (ulong)offset（未截断的原始值）——offset 超过
            // int 范围时两者不是同一个数，截断后的值可能刚好落在合法区间内，
            // 校验形同虚设，可能尝试读取一个离目标模块很远的地址。
            PortablePeImage.RequireRange(offset, 8, imageSize, label);
            var callback = BinaryPrimitives.ReadUInt64LittleEndian(process.Read(baseAddress + (ulong)offset, 8));
            if (callback == 0) return callbacks;
            if (callback < baseAddress || callback >= baseAddress + imageSize)
                throw new InjectionError("远程 TLS 回调地址不在映像内");
            callbacks.Add(callback);
        }
        throw new InjectionError($"{label}未终止");
    }
}

/// <summary>对目标进程内存的只读访问；真实实现见 <see cref="WindowsProcess"/>，测试里可以造假。</summary>
public interface IRemoteMemory
{
    byte[] Read(ulong address, int size);
}
