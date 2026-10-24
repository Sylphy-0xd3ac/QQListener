using System.Text;
using QQListener.Core;
using QQListener.Native;

namespace QQListener.Protocol.Tests;

public sealed class RemoteLibraryLoaderTests
{
    [Fact]
    public void BundledPmhqCannotUseTheManualMapper()
    {
        using var resource = typeof(PmhqBundle).Assembly.GetManifestResourceStream("QQListener.PMHQ.pmhq.dll")!;
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        var pe = PortablePeImage.Parse(bytes.ToArray());
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
        Assert.Throws<InjectionError>(() => RemoteLibraryLoader.Load("fixture.dll", _ => 0x1000,
            (_, _) => { if (writeFails) throw new InjectionError("write failed"); }, _ => 0, _ => freed = true));
        Assert.True(freed);
    }
}
