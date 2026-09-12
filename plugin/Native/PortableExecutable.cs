using System.Buffers.Binary;
using System.Text;

namespace QQListener.Native;

/// <summary>一个 PE 节（节表项）。</summary>
public sealed record PeSection(
    uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawDataPointer, uint Characteristics);

/// <summary>一条数据目录项（VirtualAddress=0 表示不存在）。</summary>
public readonly record struct PeDataDirectory(uint VirtualAddress, uint Size);

/// <summary>一条基址重定位；<see cref="Rva"/> 已经是页 RVA 加页内偏移后的绝对 RVA。</summary>
public readonly record struct PeRelocation(int Type, uint Rva);

/// <summary>一个导入项。<see cref="Name"/> 为空则按 <see cref="Ordinal"/> 导入。</summary>
public sealed record PeImportThunk(string Module, byte[]? Name, int? Ordinal, uint IatRva);

/// <summary>
/// 手写的最小 PE32+ 解析器：只取手动映射需要的字段（节、重定位、导入、数据目录），
/// 不依赖 pefile 这类第三方库——跟 <c>Protocol/Wire.cs</c> 同样的取舍。
///
/// 字段偏移量对照 Microsoft PE/COFF 规范；同时也是
/// <c>tests/native/test_injector_windows.py</c> 里手搓探针 DLL 用的同一套布局，
/// 移植时两边可以互相校验。
/// </summary>
public sealed class PortablePeImage
{
    public const int ImageDirectoryEntryExport = 0;
    public const int ImageDirectoryEntryImport = 1;
    public const int ImageDirectoryEntryException = 3;
    public const int ImageDirectoryEntryBaseReloc = 5;
    public const int ImageDirectoryEntryTls = 9;

    public required ulong ImageBase { get; init; }
    public required uint SizeOfImage { get; init; }
    public required uint SizeOfHeaders { get; init; }
    public required uint EntryPointRva { get; init; }
    public required ushort Machine { get; init; }
    public required ushort OptionalHeaderMagic { get; init; }
    public required uint FileCharacteristics { get; init; }
    public required IReadOnlyList<PeSection> Sections { get; init; }

    /// <summary>固定 16 项；不存在的目录 VirtualAddress=0。按 <c>ImageDirectoryEntry*</c> 索引。</summary>
    public required IReadOnlyList<PeDataDirectory> DataDirectories { get; init; }

    public required IReadOnlyList<PeRelocation> Relocations { get; init; }
    public required IReadOnlyList<PeImportThunk> Imports { get; init; }

    public PeDataDirectory Directory(int index) =>
        index < DataDirectories.Count ? DataDirectories[index] : default;

    public static PortablePeImage Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(raw) != 0x5A4D)
            throw new InjectionError("缺少 MZ 签名");

        var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[0x3C..]);
        RequireRange(peOffset, 24, raw.Length, "PE 头偏移");
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw[peOffset..]) != 0x00004550) // "PE\0\0"
            throw new InjectionError("PE 签名无效");

        var coff = peOffset + 4;
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(raw[coff..]);
        var numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(raw[(coff + 2)..]);
        var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(raw[(coff + 16)..]);
        var fileCharacteristics = BinaryPrimitives.ReadUInt16LittleEndian(raw[(coff + 18)..]);

        var optional = coff + 20;
        RequireRange(optional, sizeOfOptionalHeader, raw.Length, "可选头");
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(raw[optional..]);
        var entryRva = BinaryPrimitives.ReadUInt32LittleEndian(raw[(optional + 16)..]);
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(raw[(optional + 24)..]);
        var sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(raw[(optional + 56)..]);
        var sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(raw[(optional + 60)..]);
        var numberOfRvaAndSizes = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[(optional + 108)..]);

        var directoriesOffset = optional + 112;
        var directories = new List<PeDataDirectory>(16);
        var directoryCount = Math.Clamp(numberOfRvaAndSizes, 0, 16);
        for (var i = 0; i < 16; i++)
        {
            if (i >= directoryCount) { directories.Add(default); continue; }
            var entryOffset = directoriesOffset + i * 8;
            RequireRange(entryOffset, 8, raw.Length, "数据目录");
            directories.Add(new PeDataDirectory(
                BinaryPrimitives.ReadUInt32LittleEndian(raw[entryOffset..]),
                BinaryPrimitives.ReadUInt32LittleEndian(raw[(entryOffset + 4)..])));
        }

        var sectionsOffset = optional + sizeOfOptionalHeader;
        var sections = new List<PeSection>(numberOfSections);
        for (var i = 0; i < numberOfSections; i++)
        {
            var entryOffset = sectionsOffset + i * 40;
            RequireRange(entryOffset, 40, raw.Length, "节表");
            sections.Add(new PeSection(
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(raw[(entryOffset + 12)..]),
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(raw[(entryOffset + 8)..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(raw[(entryOffset + 16)..]),
                RawDataPointer: BinaryPrimitives.ReadUInt32LittleEndian(raw[(entryOffset + 20)..]),
                Characteristics: BinaryPrimitives.ReadUInt32LittleEndian(raw[(entryOffset + 36)..])));
        }

        return new PortablePeImage
        {
            ImageBase = imageBase,
            SizeOfImage = sizeOfImage,
            SizeOfHeaders = sizeOfHeaders,
            EntryPointRva = entryRva,
            Machine = machine,
            OptionalHeaderMagic = magic,
            FileCharacteristics = fileCharacteristics,
            Sections = sections,
            DataDirectories = directories,
            Relocations = ParseRelocations(raw, sections, directories[ImageDirectoryEntryBaseReloc]),
            Imports = ParseImports(raw, sections, directories[ImageDirectoryEntryImport]),
        };
    }

    /// <summary>
    /// RVA → 文件偏移。这两张表在磁盘上的原始文件里，跟 SectionAlignment/FileAlignment
    /// 通常不同——不能假设 RVA 数值本身就是文件偏移（那要等 <c>Injector.CopyImage</c>
    /// 把节搬进虚拟映像之后才成立）。落在任何节区间之外的 RVA 按头部处理，
    /// 头部在文件和虚拟映像里是同一份布局，RVA 就是文件偏移。
    /// </summary>
    private static int RvaToFileOffset(IReadOnlyList<PeSection> sections, uint rva)
    {
        foreach (var section in sections)
        {
            var size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < size)
                return (int)(section.RawDataPointer + (rva - section.VirtualAddress));
        }
        return (int)rva;
    }

    /// <summary>
    /// IMAGE_BASE_RELOCATION 块序列：每块 8 字节头（PageRVA, BlockSize）后跟若干 16 位条目
    /// （高 4 位类型 + 低 12 位页内偏移）。块本身连续排布在同一个节内，用文件偏移走块头，
    /// 但输出的 <see cref="PeRelocation.Rva"/> 仍是逻辑 RVA——后续要拿它去索引虚拟映像。
    /// </summary>
    private static List<PeRelocation> ParseRelocations(
        ReadOnlySpan<byte> raw, IReadOnlyList<PeSection> sections, PeDataDirectory dir)
    {
        var result = new List<PeRelocation>();
        if (dir.VirtualAddress == 0 || dir.Size == 0) return result;

        var pos = RvaToFileOffset(sections, dir.VirtualAddress);
        var end = pos + (int)dir.Size;
        RequireRange(pos, (int)dir.Size, raw.Length, "基址重定位表");

        while (pos < end)
        {
            RequireRange(pos, 8, raw.Length, "基址重定位块头");
            var pageRva = BinaryPrimitives.ReadUInt32LittleEndian(raw[pos..]);
            var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[(pos + 4)..]);
            // 只校验下限时，>= 0x80000000 的 blockSize 转 int 会变成负数：
            // pos += (int)blockSize 让游标往回走而不是向前推进，会在同一段/相邻
            // 区间反复重新解析，直到 pos 减到负数才被下一轮 RequireRange 拦下——
            // 不是死循环，但这本该在这里一次性清晰拒绝。用 end-pos（表里剩余的
            // 字节数）做上限，同时保证 pos+blockSize 不会跑出这张表本身。
            if (blockSize < 8 || blockSize > (uint)(end - pos))
                throw new InjectionError("基址重定位块大小非法");

            var entryCount = (int)(blockSize - 8) / 2;
            for (var i = 0; i < entryCount; i++)
            {
                var entryOffset = pos + 8 + i * 2;
                RequireRange(entryOffset, 2, raw.Length, "基址重定位条目");
                var entry = BinaryPrimitives.ReadUInt16LittleEndian(raw[entryOffset..]);
                result.Add(new PeRelocation(entry >> 12, pageRva + (uint)(entry & 0xFFF)));
            }
            pos += (int)blockSize;
        }
        return result;
    }

    /// <summary>
    /// IMAGE_IMPORT_DESCRIPTOR 数组，全零条目结束。优先用 OriginalFirstThunk（INT，不受绑定影响）
    /// 取名称/序号，补丁目标地址永远是 FirstThunk（IAT）——跟系统加载器的语义一致。
    /// 名称表、模块名、提示表各自可能落在不同的节，每次解引用 RVA 都要单独换算文件偏移；
    /// 唯独 <see cref="PeImportThunk.IatRva"/> 保持逻辑 RVA 不换算，用于之后打补丁到虚拟映像。
    /// </summary>
    private static List<PeImportThunk> ParseImports(
        ReadOnlySpan<byte> raw, IReadOnlyList<PeSection> sections, PeDataDirectory dir)
    {
        var result = new List<PeImportThunk>();
        if (dir.VirtualAddress == 0) return result;

        var pos = RvaToFileOffset(sections, dir.VirtualAddress);
        while (true)
        {
            RequireRange(pos, 20, raw.Length, "导入描述符");
            var originalFirstThunk = BinaryPrimitives.ReadUInt32LittleEndian(raw[pos..]);
            var nameRva = BinaryPrimitives.ReadUInt32LittleEndian(raw[(pos + 12)..]);
            var firstThunk = BinaryPrimitives.ReadUInt32LittleEndian(raw[(pos + 16)..]);
            if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0) break;

            var moduleName = ReadAsciiZ(raw, RvaToFileOffset(sections, nameRva));
            var thunkTableRva = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            var thunkTableOffset = RvaToFileOffset(sections, thunkTableRva);

            var index = 0;
            while (true)
            {
                var thunkOffset = thunkTableOffset + index * 8;
                RequireRange(thunkOffset, 8, raw.Length, "导入名称表");
                var thunk = BinaryPrimitives.ReadUInt64LittleEndian(raw[thunkOffset..]);
                if (thunk == 0) break;

                byte[]? name = null;
                int? ordinal = null;
                if ((thunk & 0x8000000000000000UL) != 0)
                {
                    ordinal = (int)(thunk & 0xFFFF);
                }
                else
                {
                    var hintNameOffset = RvaToFileOffset(sections, (uint)thunk);
                    RequireRange(hintNameOffset, 2, raw.Length, "导入提示表");
                    name = ReadAsciiZBytes(raw, hintNameOffset + 2);
                }

                var iatRva = (uint)((int)firstThunk + index * 8);
                result.Add(new PeImportThunk(moduleName, name, ordinal, iatRva));
                index++;
            }
            pos += 20;
        }
        return result;
    }

    private static string ReadAsciiZ(ReadOnlySpan<byte> raw, int offset) =>
        Encoding.ASCII.GetString(ReadAsciiZBytes(raw, offset));

    private static byte[] ReadAsciiZBytes(ReadOnlySpan<byte> raw, int offset)
    {
        RequireRange(offset, 0, raw.Length, "以零结尾的字符串");
        var end = offset;
        while (end < raw.Length && raw[end] != 0) end++;
        return raw[offset..end].ToArray();
    }

    internal static void RequireRange(int offset, int size, int limit, string label)
    {
        if (offset < 0 || size < 0 || offset > limit || size > limit - offset)
            throw new InjectionError($"{label} 超出 PE 映像范围");
    }

    /// <summary>
    /// 校验一个来自"重定位过的虚拟地址减基址"这类计算、可能超出 int 范围的偏移量。
    ///
    /// 调用方如果先把 offset 截断成 int 再校验，截断和实际使用的是两个不同的值——
    /// 校验可能因为截断而"误判通过"，之后按未截断的原始值去访问，等于校验形同
    /// 虚设。这里全程用 long 比较，只有确认落在范围内之后，调用方截断成 int
    /// 才是安全的。
    /// </summary>
    internal static void RequireRange(long offset, int size, long limit, string label)
    {
        if (offset < 0 || size < 0 || offset > limit || size > limit - offset)
            throw new InjectionError($"{label} 超出 PE 映像范围");
    }
}
