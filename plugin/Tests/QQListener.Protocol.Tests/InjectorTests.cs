using System.Buffers.Binary;
using QQListener.Native;

namespace QQListener.Protocol.Tests;

/// <summary>
/// 手动映射装载器的纯算法部分。对应 Python 侧 test_injector.py。
///
/// 直接构造 <see cref="PortablePeImage"/> 而不是拼字节再解析——跟 Python 版用
/// SimpleNamespace 伪造 pefile.PE 对象是同一个取舍：只测「拿到解析结果之后
/// 怎么打映像」，PE 字节级解析单独在 <see cref="PortableExecutableTests"/> 里验。
/// </summary>
public class PrepareImageTests
{
    private const ulong DefaultImageBase = 0x180000000;

    private static PortablePeImage FakePe(bool withImports = false)
    {
        var imports = new List<PeImportThunk>();
        if (withImports)
        {
            imports.Add(new PeImportThunk("KERNEL32.dll", "Sleep"u8.ToArray(), null, 0x1080));
            imports.Add(new PeImportThunk("KERNEL32.dll", null, 17, 0x1088));
        }

        return new PortablePeImage
        {
            ImageBase = DefaultImageBase,
            SizeOfImage = 0x3000,
            SizeOfHeaders = 0x200,
            EntryPointRva = 0x1010,
            Machine = Injector.ImageFileMachineAmd64,
            OptionalHeaderMagic = Injector.ImageNtOptionalHdr64Magic,
            FileCharacteristics = Injector.ImageFileDll,
            Sections =
            [
                new PeSection(
                    VirtualAddress: 0x1000, VirtualSize: 0x100, RawSize: 4, RawDataPointer: 0x200,
                    Characteristics: Injector.ImageScnMemRead | Injector.ImageScnMemExecute)
            ],
            DataDirectories = Enumerable.Repeat(default(PeDataDirectory), 16).ToList(),
            Relocations = [],
            Imports = imports,
        };
    }

    private static PortablePeImage WithDirectory(PortablePeImage pe, int index, PeDataDirectory directory)
    {
        var directories = pe.DataDirectories.ToList();
        directories[index] = directory;
        return new PortablePeImage
        {
            ImageBase = pe.ImageBase,
            SizeOfImage = pe.SizeOfImage,
            SizeOfHeaders = pe.SizeOfHeaders,
            EntryPointRva = pe.EntryPointRva,
            Machine = pe.Machine,
            OptionalHeaderMagic = pe.OptionalHeaderMagic,
            FileCharacteristics = pe.FileCharacteristics,
            Sections = pe.Sections,
            DataDirectories = directories,
            Relocations = pe.Relocations,
            Imports = pe.Imports,
        };
    }

    [Fact]
    public void MapHandleShape()
    {
        var handle = new MapHandle(0x1000, 0x1100, 0x1200, 0x3000);
        Assert.Equal((0x1000UL, 0x1100UL, 0x1200UL, 0x3000U),
            (handle.Base, handle.Entry, handle.ExceptionTable, handle.Size));
    }

    [Fact]
    public void CopiesHeadersSectionsAndReportsEntry()
    {
        var pe = FakePe();
        var raw = new byte[0x204];
        "MZ!!"u8.CopyTo(raw);
        "CODE"u8.CopyTo(raw.AsSpan(0x200));

        var prepared = Injector.PrepareImage(pe, raw, DefaultImageBase, (_, _, _) => 0);

        Assert.Equal("MZ!!"u8.ToArray(), prepared.Data[..4]);
        Assert.Equal("CODE"u8.ToArray(), prepared.Data[0x1000..0x1004]);
        Assert.Equal(0x180001010UL, prepared.Entry);
        Assert.Equal(0UL, prepared.ExceptionTable);
        Assert.Equal(0, prepared.ExceptionCount);
        Assert.Equal(0x180001000UL, prepared.Sections[0].Address);
    }

    [Fact]
    public void AppliesDir64Relocation()
    {
        var pe = FakePe();
        var sections = new List<PeSection> { pe.Sections[0] with { RawSize = 0x100 } };
        pe = new PortablePeImage
        {
            ImageBase = pe.ImageBase, SizeOfImage = pe.SizeOfImage, SizeOfHeaders = pe.SizeOfHeaders,
            EntryPointRva = pe.EntryPointRva, Machine = pe.Machine, OptionalHeaderMagic = pe.OptionalHeaderMagic,
            FileCharacteristics = pe.FileCharacteristics, Sections = sections,
            DataDirectories = pe.DataDirectories,
            Relocations = [new PeRelocation(Injector.ImageRelBasedDir64, 0x1020)],
            Imports = pe.Imports,
        };

        var raw = new byte[0x300];
        // 指针值放在虚拟节区间内（rva 0x1020 落在 0x1000..0x1100 节区间），
        // 走 CopyImage 从原始节数据搬进映像。
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x220), pe.ImageBase + 0x1550);

        var targetBase = 0x190000000UL;
        var prepared = Injector.PrepareImage(pe, raw, targetBase, (_, _, _) => 0);

        Assert.Equal(0x190001550UL, BinaryPrimitives.ReadUInt64LittleEndian(prepared.Data.AsSpan(0x1020)));
    }

    [Fact]
    public void PatchesNamedAndOrdinalImports()
    {
        var pe = FakePe(withImports: true);
        var raw = new byte[0x300];
        var seen = new List<(string Module, byte[]? Name, int? Ordinal)>();

        ulong Resolve(string module, byte[]? name, int? ordinal)
        {
            seen.Add((module, name, ordinal));
            return 0x7FFF00001000UL + (ulong)seen.Count;
        }

        var prepared = Injector.PrepareImage(pe, raw, pe.ImageBase, Resolve);

        Assert.Equal(2, seen.Count);
        Assert.Equal("KERNEL32.dll", seen[0].Module);
        Assert.Equal("Sleep"u8.ToArray(), seen[0].Name);
        Assert.Null(seen[0].Ordinal);
        Assert.Equal("KERNEL32.dll", seen[1].Module);
        Assert.Null(seen[1].Name);
        Assert.Equal(17, seen[1].Ordinal);

        Assert.Equal(0x7FFF00001001UL, BinaryPrimitives.ReadUInt64LittleEndian(prepared.Data.AsSpan(0x1080)));
        Assert.Equal(0x7FFF00001002UL, BinaryPrimitives.ReadUInt64LittleEndian(prepared.Data.AsSpan(0x1088)));
    }

    [Fact]
    public void ReportsExceptionDirectory()
    {
        var pe = WithDirectory(FakePe(), PortablePeImage.ImageDirectoryEntryException,
            new PeDataDirectory(0x1200, 24));

        var prepared = Injector.PrepareImage(pe, new byte[0x300], pe.ImageBase, (_, _, _) => 0);

        Assert.Equal(pe.ImageBase + 0x1200, prepared.ExceptionTable);
        Assert.Equal(2, prepared.ExceptionCount);
    }

    [Fact]
    public void CollectsTlsCallbacks()
    {
        var pe = FakePe();
        var sections = new List<PeSection> { pe.Sections[0] with { RawSize = 0x100 } };
        pe = WithDirectory(
            new PortablePeImage
            {
                ImageBase = pe.ImageBase, SizeOfImage = pe.SizeOfImage, SizeOfHeaders = pe.SizeOfHeaders,
                EntryPointRva = pe.EntryPointRva, Machine = pe.Machine, OptionalHeaderMagic = pe.OptionalHeaderMagic,
                FileCharacteristics = pe.FileCharacteristics, Sections = sections,
                DataDirectories = pe.DataDirectories, Relocations = pe.Relocations, Imports = pe.Imports,
            },
            PortablePeImage.ImageDirectoryEntryTls, new PeDataDirectory(0x1040, 40));

        var raw = new byte[0x300];
        // IMAGE_TLS_DIRECTORY64.AddressOfCallBacks 位于 TLS 目录偏移 24。
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x240 + 24), pe.ImageBase + 0x1080);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(0x280), pe.ImageBase + 0x1090);

        var prepared = Injector.PrepareImage(pe, raw, pe.ImageBase, (_, _, _) => 0);

        Assert.Equal([pe.ImageBase + 0x1090], prepared.TlsCallbacks);
    }
}

public class RemoteTlsCallbacksTests
{
    /// <summary>本地字节数组模拟远程地址空间；<see cref="Read"/> 按 address-base 换算成本地偏移。</summary>
    private sealed class FakeRemoteMemory(byte[] image, ulong baseAddress) : IRemoteMemory
    {
        public byte[] Read(ulong address, int size)
        {
            var offset = (int)(address - baseAddress);
            return image[offset..(offset + size)];
        }
    }

    [Fact]
    public void RecoveredFromMappedHeaders()
    {
        const ulong baseAddress = 0x180000000;
        var image = new byte[0x3000];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        var optionalHeader = 0x80 + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalHeader), Injector.ImageNtOptionalHdr64Magic);
        var tlsEntry = optionalHeader + 112 + PortablePeImage.ImageDirectoryEntryTls * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tlsEntry), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tlsEntry + 4), 40);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x1000 + 24), baseAddress + 0x1100);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x1100), baseAddress + 0x1200);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x1108), 0);

        var handle = new MapHandle(baseAddress, baseAddress + 0x1010, 0, (uint)image.Length);

        var callbacks = Injector.RemoteTlsCallbacks(new FakeRemoteMemory(image, baseAddress), handle);

        Assert.Equal([baseAddress + 0x1200], callbacks);
    }

    [Fact]
    public void RejectsCallbackTableOffsetThatOverflowsWhenTruncatedToInt()
    {
        // AddressOfCallBacks 减基址算出的偏移量超过 int 范围时，旧版本的校验先把
        // 它截断成 int 再跟镜像大小比较——截断后的低 32 位可能刚好落在合法区间内，
        // 校验形同虚设，实际读取却仍然用未截断的原始偏移量，可能读到一个离目标
        // 模块很远的地址。
        const ulong baseAddress = 0x180000000;
        var image = new byte[0x3000];
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        var optionalHeader = 0x80 + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optionalHeader), Injector.ImageNtOptionalHdr64Magic);
        var tlsEntry = optionalHeader + 112 + PortablePeImage.ImageDirectoryEntryTls * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tlsEntry), 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(tlsEntry + 4), 40);

        const long hugeOffset = 4294969600; // 低 32 位是 0x1200，落在镜像范围内，高位却早已越界
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x1000 + 24), baseAddress + (ulong)hugeOffset);

        var handle = new MapHandle(baseAddress, baseAddress + 0x1010, 0, (uint)image.Length);

        Assert.Throws<InjectionError>(
            () => Injector.RemoteTlsCallbacks(new FakeRemoteMemory(image, baseAddress), handle));
    }
}

public class ManualMapInjectorPlatformGuardTests
{
    [Fact]
    public void InjectRejectsNonWindowsBeforeTouchingDll()
    {
        // 只验证非 Windows 平台边界；真机上的整条注入流程见 test_injector_windows.py
        // 描述的傀儡进程集成测试，那部分需要真实 Windows 才能跑。
        if (OperatingSystem.IsWindows()) return;

#pragma warning disable CA1416 // 分析器看不出上面这行 return 已经把非 Windows 分支隔开了
        Assert.Throws<PlatformNotSupportedException>(
            () => ManualMapInjector.Inject(1234, "/nonexistent/missing.dll"));
#pragma warning restore CA1416
    }
}
