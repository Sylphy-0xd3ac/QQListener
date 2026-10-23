using QQListener.Native;

namespace QQListener.Protocol.Tests;

/// <summary>QQ 主进程的定位与子进程过滤。</summary>
public class QqProcessLocatorTests
{
    [Fact]
    public void MainQqProcessFilterSkipsElectronChildren()
    {
        Assert.True(QqProcessLocator.IsMainQqProcess("QQ.exe", [@"C:\QQ\QQ.exe"]));
        Assert.True(QqProcessLocator.IsMainQqProcess("qq.EXE", [@"C:\QQ\QQ.exe", "--no-sandbox"]));
        Assert.False(QqProcessLocator.IsMainQqProcess("QQ.exe", [@"C:\QQ\QQ.exe", "--type=renderer"]));
        Assert.False(QqProcessLocator.IsMainQqProcess("QQ.exe", [@"C:\QQ\QQ.exe", "--type", "utility"]));
        Assert.False(QqProcessLocator.IsMainQqProcess("QQ.exe", null));
        Assert.False(QqProcessLocator.IsMainQqProcess("QQMusic.exe", [@"C:\QQMusic.exe"]));
    }

    [Fact]
    public void EnumerateQqPidsReturnsOnlySortedMainProcesses()
    {
        IEnumerable<QqProcessInfo> Source() =>
        [
            new QqProcessInfo(30, "QQ.exe", ["QQ.exe"]),
            new QqProcessInfo(20, "QQ.exe", ["QQ.exe", "--type=renderer"]),
            new QqProcessInfo(10, "qq.EXE", ["QQ.exe"]),
        ];

        Assert.Equal([10, 30], QqProcessLocator.EnumerateQqPids(Source));
    }
}
