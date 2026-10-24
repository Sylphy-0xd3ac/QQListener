using System.Buffers.Binary;
using System.Text;
using QQListener.Native;

namespace QQListener.Protocol.Tests;

/// <summary>
/// PE32+ 字节级解析。手搭一个最小但结构合法的 x64 DLL（无代码，只有头/节/导入/重定位），
/// 校验 <see cref="PortablePeImage.Parse"/> 抠出的字段跟手写进去的完全对得上。
///
/// 合成映像按 PE/COFF 布局构造，验证字段偏移与取值。
/// </summary>
public class PortableExecutableTests
{
    private const ulong ImageBase = 0x140000000;
    private const uint TextRva = 0x1000, TextRawPointer = 0x400, TextRawSize = 0x200;
    private const uint DataRva = 0x3000, DataRawPointer = 0x600, DataSize = 0x2000;
    private const uint ImportDescRva = DataRva;             // 0x3000
    private const uint IntRva = DataRva + 0x40;              // 0x3040
    private const uint IatRva = DataRva + 0x60;               // 0x3060
    private const uint ModuleNameRva = DataRva + 0x80;        // 0x3080
    private const uint HintNameRva = DataRva + 0xA0;          // 0x30A0
    private const uint RelocRva = 0x4000;

    internal static byte[] BuildProbeImage()
    {
        var raw = new byte[0x2600];
        var span = raw.AsSpan();

        "MZ"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x3C..], 0x80);

        "PE\0\0"u8.CopyTo(span[0x80..]);
        const int coff = 0x84;
        BinaryPrimitives.WriteUInt16LittleEndian(span[coff..], Injector.ImageFileMachineAmd64);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(coff + 2)..], 2); // NumberOfSections
        BinaryPrimitives.WriteUInt16LittleEndian(span[(coff + 16)..], 0xF0); // SizeOfOptionalHeader
        BinaryPrimitives.WriteUInt16LittleEndian(span[(coff + 18)..], 0x2002); // EXECUTABLE_IMAGE|DLL

        const int optional = coff + 20; // 0xA8
        BinaryPrimitives.WriteUInt16LittleEndian(span[optional..], Injector.ImageNtOptionalHdr64Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(optional + 16)..], TextRva); // AddressOfEntryPoint
        BinaryPrimitives.WriteUInt64LittleEndian(span[(optional + 24)..], ImageBase);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(optional + 56)..], 0x6000); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(span[(optional + 60)..], 0x200); // SizeOfHeaders
        BinaryPrimitives.WriteUInt32LittleEndian(span[(optional + 108)..], 16); // NumberOfRvaAndSizes

        var directories = optional + 112; // 0x118
        WriteDirectory(span, directories, PortablePeImage.ImageDirectoryEntryImport, ImportDescRva, 40);
        WriteDirectory(span, directories, PortablePeImage.ImageDirectoryEntryBaseReloc, RelocRva, 12);

        var sections = optional + 0xF0; // 0x198
        WriteSection(span, sections, TextRva, 0x50, TextRawSize, TextRawPointer,
            Injector.ImageScnMemExecute | Injector.ImageScnMemRead);
        WriteSection(span, sections + 40, DataRva, DataSize, DataSize, DataRawPointer,
            Injector.ImageScnMemRead | Injector.ImageScnMemWrite);

        // 导入描述符：KERNEL32.dll!Sleep（按名）+ KERNEL32.dll!#17（按序号），终止描述符置零即可。
        var descriptor = DataRawPointer;
        BinaryPrimitives.WriteUInt32LittleEndian(span[(int)descriptor..], IntRva);
        BinaryPrimitives.WriteUInt32LittleEndian(span[((int)descriptor + 12)..], ModuleNameRva);
        BinaryPrimitives.WriteUInt32LittleEndian(span[((int)descriptor + 16)..], IatRva);

        var intOffset = DataRawPointer + (IntRva - DataRva);
        var iatOffset = DataRawPointer + (IatRva - DataRva);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(int)intOffset..], HintNameRva);
        BinaryPrimitives.WriteUInt64LittleEndian(span[((int)intOffset + 8)..], 0x8000000000000011UL);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(int)iatOffset..], HintNameRva);
        BinaryPrimitives.WriteUInt64LittleEndian(span[((int)iatOffset + 8)..], 0x8000000000000011UL);

        var moduleNameOffset = DataRawPointer + (ModuleNameRva - DataRva);
        Encoding.ASCII.GetBytes("KERNEL32.dll\0").CopyTo(span[(int)moduleNameOffset..]);

        var hintNameOffset = DataRawPointer + (HintNameRva - DataRva);
        Encoding.ASCII.GetBytes("Sleep\0").CopyTo(span[((int)hintNameOffset + 2)..]);

        // 一个基址重定位块：HIGHLOW@0x10、DIR64@0x20，都挂在页 RVA 0x1000 下。
        var relocOffset = DataRawPointer + (RelocRva - DataRva);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(int)relocOffset..], 0x1000); // PageRVA
        BinaryPrimitives.WriteUInt32LittleEndian(span[((int)relocOffset + 4)..], 12); // BlockSize
        BinaryPrimitives.WriteUInt16LittleEndian(span[((int)relocOffset + 8)..], (ushort)((3 << 12) | 0x10));
        BinaryPrimitives.WriteUInt16LittleEndian(span[((int)relocOffset + 10)..], (ushort)((10 << 12) | 0x20));

        return raw;
    }

    private static void WriteDirectory(Span<byte> span, int directoriesOffset, int index, uint rva, uint size)
    {
        var entry = directoriesOffset + index * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(span[entry..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], size);
    }

    private static void WriteSection(
        Span<byte> span, int offset, uint rva, uint virtualSize, uint rawSize, uint rawPointer, uint characteristics)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 8)..], virtualSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 12)..], rva);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 16)..], rawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 20)..], rawPointer);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 36)..], characteristics);
    }

    [Fact]
    public void ParsesHeaderFieldsAndSections()
    {
        var pe = PortablePeImage.Parse(BuildProbeImage());

        Assert.Equal(ImageBase, pe.ImageBase);
        Assert.Equal(0x6000U, pe.SizeOfImage);
        Assert.Equal(0x200U, pe.SizeOfHeaders);
        Assert.Equal(TextRva, pe.EntryPointRva);
        Assert.Equal(Injector.ImageFileMachineAmd64, pe.Machine);
        Assert.Equal(Injector.ImageNtOptionalHdr64Magic, pe.OptionalHeaderMagic);
        Assert.Equal(Injector.ImageFileDll, pe.FileCharacteristics & Injector.ImageFileDll);

        Assert.Equal(2, pe.Sections.Count);
        Assert.Equal(TextRva, pe.Sections[0].VirtualAddress);
        Assert.Equal(TextRawPointer, pe.Sections[0].RawDataPointer);
        Assert.Equal(DataRva, pe.Sections[1].VirtualAddress);
    }

    [Fact]
    public void ParsesImportsWithNameAndOrdinal()
    {
        var pe = PortablePeImage.Parse(BuildProbeImage());

        Assert.Equal(2, pe.Imports.Count);
        Assert.Equal("KERNEL32.dll", pe.Imports[0].Module);
        Assert.Equal("Sleep"u8.ToArray(), pe.Imports[0].Name);
        Assert.Null(pe.Imports[0].Ordinal);
        Assert.Equal(IatRva, pe.Imports[0].IatRva);

        Assert.Equal("KERNEL32.dll", pe.Imports[1].Module);
        Assert.Null(pe.Imports[1].Name);
        Assert.Equal(17, pe.Imports[1].Ordinal);
        Assert.Equal(IatRva + 8, pe.Imports[1].IatRva);
    }

    [Fact]
    public void ParsesBaseRelocationsIntoAbsoluteRvas()
    {
        var pe = PortablePeImage.Parse(BuildProbeImage());

        Assert.Equal(2, pe.Relocations.Count);
        Assert.Equal(new PeRelocation(Injector.ImageRelBasedHighLow, 0x1010), pe.Relocations[0]);
        Assert.Equal(new PeRelocation(Injector.ImageRelBasedDir64, 0x1020), pe.Relocations[1]);
    }

    [Fact]
    public void RejectsOversizedRelocationBlockInsteadOfWalkingBackwards()
    {
        // 之前只校验了下限（< 8）；>= 0x80000000 的 blockSize 转 int 会变成负数，
        // 让游标在重定位块之间往回走而不是向前推进，本该在这里一次性清晰拒绝。
        var raw = BuildProbeImage();
        var relocOffset = (int)(DataRawPointer + (RelocRva - DataRva));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(relocOffset + 4), 0x80000000);

        Assert.Throws<InjectionError>(() => PortablePeImage.Parse(raw));
    }

    [Fact]
    public void RejectsMissingMzSignature()
    {
        var raw = new byte[0x40];
        Assert.Throws<InjectionError>(() => PortablePeImage.Parse(raw));
    }

    [Fact]
    public void RejectsBadPeSignature()
    {
        var raw = BuildProbeImage();
        raw[0x80] = (byte)'X'; // 破坏 "PE\0\0"
        Assert.Throws<InjectionError>(() => PortablePeImage.Parse(raw));
    }
}
