using System.Text;
using System.Buffers.Binary;
using QQListener.Core;
using QQListener.Native;

namespace QQListener.Protocol.Tests;

public sealed class RemoteLibraryLoaderTests
{
    [Fact]
    public void ErrorFromTheRemoteLoaderIsPreserved()
    {
        var freed = false;
        var error = Assert.Throws<RemoteLibraryLoadException>(() => RemoteLibraryLoader.LoadWithError("bad.dll",
            _ => 0x1000, (_, _) => { }, _ => new(0, 1114), _ => freed = true));
        Assert.Equal(1114u, error.Win32Error);
        Assert.Contains("Win32=1114", error.Message);
        Assert.True(freed);
    }

    [Fact]
    public void WindowsSystemLoaderCapturesBadImageErrorInTheCallingThread()
    {
        if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            != System.Runtime.InteropServices.Architecture.X64) return;
        var path = Path.Combine(Path.GetTempPath(), "QQListener-坏映像-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(path, "not a PE image");
        try
        {
            using var process = new WindowsProcess(Environment.ProcessId);
            var error = Assert.Throws<RemoteLibraryLoadException>(() => { if (OperatingSystem.IsWindows()) process.LoadLibraryFile(path); });
            Assert.Equal(193u, error.Win32Error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WindowsLoadsPinnedPmhqThroughTheProductionRemoteCallPath()
    {
        var fixture = Environment.GetEnvironmentVariable("QQLISTENER_PMHQ_FIXTURE");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(fixture)) return;
        var release = PmhqBundle.PinnedRelease;
        var bytes = File.ReadAllBytes(fixture);
        Assert.Equal(release.OutputSize, bytes.Length);
        Assert.True(QQListener.Updates.DistributionClient.HashMatches(bytes, release.OutputSha256));
        Environment.SetEnvironmentVariable("PMHQ_SRDI_TEST", "1");
        using var process = new WindowsProcess(Environment.ProcessId);
        // The same x64 trampoline, remote-thread LoadLibraryW, and error capture used inside QQ.
        var module = process.LoadLibraryFile(fixture);
        Assert.NotEqual(0UL, module);
        var status = BitConverter.ToUInt32(process.Read(module + 0x106018, 4));
        Assert.Equal(0u, status);
        Console.WriteLine($"PMHQ production loader fixture: wrapper={status}, TLS index={BitConverter.ToUInt32(process.Read(module + 0xf59c0, 4))}");
        // Native workers/callbacks have no shutdown contract. The test process owns the lifetime.
    }

    [Fact]
    public void ImagesWithStaticTlsCannotUseTheManualMapper()
    {
        var bytes = PortableExecutableTests.BuildProbeImage();
        var directory = 0x84 + 20 + 112 + PortablePeImage.ImageDirectoryEntryTls * 8;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory), 0x3100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(directory + 4), 40);
        var pe = PortablePeImage.Parse(bytes);
        Injector.ValidatePe(pe);
        Assert.NotEqual(0u, pe.Directory(PortablePeImage.ImageDirectoryEntryTls).VirtualAddress);
        Assert.Contains("TLS", Assert.Throws<InjectionError>(() => Injector.ValidateManualMapping(pe)).Message);
    }

    [Fact]
    public void SystemLoaderReceivesUnicodePathAndReturnsTheEntireModuleAddress()
    {
        const string path = @"C:\用户\QQListener\pmhq.dll";
        byte[]? written = null;
        var freed = false;
        var result = RemoteLibraryLoader.Load(path, size =>
        {
            Assert.Equal(Encoding.Unicode.GetByteCount(path + "\0"), (int)size);
            return 0x1000;
        }, (address, data) => { Assert.Equal(0x1000UL, address); written = data; }, argument =>
        {
            Assert.Equal(0x1000UL, argument);
            Assert.Equal(path + "\0", Encoding.Unicode.GetString(written!));
            Assert.False(freed);
            return 0x7ffb12340000;
        }, address => { Assert.Equal(0x1000UL, address); freed = true; });
        Assert.Equal(0x7ffb12340000UL, result);
        Assert.True(freed);
    }

    [Fact]
    public void IncompleteRemoteLoadMustRetainItsPathArgument()
    {
        var freed = false;
        Assert.Throws<RemoteCallTimeoutException>(() => RemoteLibraryLoader.Load("fixture.dll", _ => 0x1000,
            (_, _) => { }, _ => throw new RemoteCallTimeoutException("fixture still running"), _ => freed = true));
        Assert.False(freed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedLoadOrWriteReleasesUnusedPathMemory(bool writeFails)
    {
        var freed = false;
        Assert.ThrowsAny<InjectionError>(() => RemoteLibraryLoader.Load("fixture.dll", _ => 0x1000,
            (_, _) => { if (writeFails) throw new InjectionError("write failed"); }, _ => 0, _ => freed = true));
        Assert.True(freed);
    }
}
