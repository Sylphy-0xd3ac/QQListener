using System.Xml.Linq;
using Xunit;

namespace QQListener.Tests.Release;

public sealed class DocumentationTests
{
    [Fact]
    public void UserDocumentationCoversSupportedScopeAndRecovery()
    {
        var root = FindProjectRoot();
        var readme = File.ReadAllText(Path.Combine(root, "docs", "usage.md"));
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));

        Assert.Contains("## 安装", readme);
        Assert.Contains("NT QQ", readme);
        Assert.Contains("TIM", readme);
        Assert.Contains("故障排查", readme);
        Assert.Contains("接收管道", readme);
        Assert.Contains("确定时回复消息", readme);
        Assert.Contains("自动下载文件", readme);
        Assert.Contains("QQListener.NormalMessageReceived", readme);
        Assert.Contains("QQListener.ClearLatestMessage", readme);
        Assert.Contains("2.2.0.0", changelog);
    }

    [Fact]
    public void RepositoryReadmeLinksToPluginDocumentation()
    {
        var repositoryRoot = FindProjectRoot();
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        foreach (var relativePath in new[] { "docs/usage.md", "DEVELOPMENT.md", "CHANGELOG.md" })
        {
            Assert.Contains($"({relativePath})", readme);
            Assert.True(File.Exists(Path.Combine(repositoryRoot, relativePath)));
        }
    }

    [Fact]
    public void RepositoryContainsOnlyTheClassIslandPluginBuild()
    {
        var root = FindProjectRoot();
        Assert.True(File.Exists(Path.Combine(root, "QQListener.csproj")));
        foreach (var relativePath in new[]
                 {
                     "main.py", "pyproject.toml", "uv.lock", ".python-version", "1.iss", "setting.json",
                     ".github/workflows/plugin.yml", "docs/standalone.md"
                 })
            Assert.False(File.Exists(Path.Combine(root, relativePath)), relativePath);
        foreach (var relativePath in new[] { "plugin", "src", "translations" })
            Assert.False(Directory.Exists(Path.Combine(root, relativePath)), relativePath);
    }

    [Fact]
    public void MetadataAndCurrentBuildOutputUseVersion230Beta1()
    {
        var root = FindProjectRoot();
        var project = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        var projectVersion = project
            .Descendants()
            .Single(element => element.Name.LocalName == "QQListenerVersion")
            .Value;
        var manifest = File.ReadAllText(Path.Combine(root, "manifest.yml"));
        var buildOutput = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "CHANGELOG.md");

        Assert.Equal("2.3.0.1", projectVersion);
        Assert.Equal("2.3.0-beta.1", project.Descendants().Single(e => e.Name.LocalName == "QQListenerReleaseVersion").Value);
        Assert.Contains("version: 2.3.0.1", manifest);
        Assert.Equal(new Version(2, 3, 0, 1), typeof(Plugin).Assembly.GetName().Version);
        Assert.True(File.Exists(buildOutput), $"Expected current build output '{buildOutput}'.");
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QQListener.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to find QQListener.sln.");
    }
}
