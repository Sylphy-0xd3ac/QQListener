using System.IO.Compression;
using QQListener.Core;

namespace QQListener.Protocol.Tests;

/// <summary>对应 Python 侧 test_core_updater.py 里跟平台无关的部分——证书/urlopen 的那几个
/// 测试是 Python stdlib 特有的坑（.NET 的 HttpClient 已经原生走系统信任链），不移植。</summary>
public class KernelInstallerTests
{
    [Fact]
    public void IsNewer()
    {
        Assert.True(KernelInstaller.IsNewer("v1.13.0", null));
        Assert.True(KernelInstaller.IsNewer("v1.13.0", "v1.12.9"));
        Assert.False(KernelInstaller.IsNewer("v1.13.0", "v1.13.0"));
        Assert.False(KernelInstaller.IsNewer("v1.13.0", "v1.14.0"));
    }

    [Fact]
    public void ParseVersionToleratesPrefixAndSuffix()
    {
        Assert.Equal([1, 13, 0], KernelInstaller.ParseVersion("v1.13.0"));
        Assert.Equal([1, 13, 0], KernelInstaller.ParseVersion("1.13.0-beta"));
    }

    [Fact]
    public void ApplyProxy()
    {
        const string url = "https://github.com/SnowLuma/SnowLuma/releases/download/v1/x.zip";
        Assert.Equal(url, KernelInstaller.ApplyProxy(url, null));
        Assert.Equal(url, KernelInstaller.ApplyProxy(url, ""));
        Assert.Equal($"https://ghfast.top/{url}", KernelInstaller.ApplyProxy(url, "https://ghfast.top"));
        Assert.Equal($"https://ghfast.top/{url}", KernelInstaller.ApplyProxy(url, "https://ghfast.top/"));
    }

    [Fact]
    public void SelectAssetPrefersFullWinX64()
    {
        (string, string)[] assets =
        [
            ("SnowLuma-v1.13.0-linux-x64.tar.gz", ""),
            ("SnowLuma-v1.13.0-win-x64-lite.zip", ""),
            ("SnowLuma-v1.13.0-win-x64.zip", ""),
        ];

        var picked = KernelInstaller.SelectAsset(assets, "win-x64");

        Assert.Equal("SnowLuma-v1.13.0-win-x64.zip", picked?.Name);
    }

    [Fact]
    public void SelectAssetNoneWhenAbsent()
    {
        Assert.Null(KernelInstaller.SelectAsset([("SnowLuma-v1.13.0-linux-x64.tar.gz", "")], "win-x64"));
    }

    [Fact]
    public void ExtractCoreBinariesKeepsOnlyKnownDllsFlattened()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "SnowLuma/native/snowluma-win32-x64.dll", "MZ-dll"u8);
            WriteEntry(archive, "SnowLuma/native/snowluma-win32-x64.node", "node-bin"u8);
            WriteEntry(archive, "SnowLuma/README.md", "readme"u8);
        }

        var destDir = Directory.CreateTempSubdirectory("qql-kernel-installer-test").FullName;
        try
        {
            var installed = KernelInstaller.ExtractCoreBinaries(stream.ToArray(), destDir);

            Assert.Equal(["snowluma-win32-x64.dll"], installed);
            Assert.Equal("MZ-dll", File.ReadAllText(Path.Combine(destDir, "snowluma-win32-x64.dll")));
            Assert.False(File.Exists(Path.Combine(destDir, "snowluma-win32-x64.node")));
            Assert.False(File.Exists(Path.Combine(destDir, "README.md")));
        }
        finally
        {
            Directory.Delete(destDir, recursive: true);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, ReadOnlySpan<byte> content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = entry.Open();
        writer.Write(content);
    }
}
