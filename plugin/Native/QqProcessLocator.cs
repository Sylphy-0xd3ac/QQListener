namespace QQListener.Native;

/// <summary>一个候选进程：名字 + 命令行（拿不到命令行时为 null）。</summary>
public sealed record QqProcessInfo(int Pid, string Name, IReadOnlyList<string>? CommandLine);

/// <summary>枚举候选进程的来源；生产环境用 WMI，测试用固定列表。</summary>
public delegate IEnumerable<QqProcessInfo> QqProcessSource();

/// <summary>
/// 定位承载 QQ-NT 的主进程。
///
/// QQ 是 Electron 应用：主进程之外还有一堆 <c>--type=renderer/utility/gpu-process</c>
/// 的子进程，同名同路径。只有主进程持有 mojo 管道，注入子进程要么找不到管道，
/// 要么打中错误的地址空间。对照 Python 版 <c>src/native/capture.py</c>。
/// </summary>
public static class QqProcessLocator
{
    public static bool IsMainQqProcess(string name, IReadOnlyList<string>? commandLine)
    {
        if (!string.Equals(name, "qq.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (commandLine is null or []) return false;

        for (var i = 1; i < commandLine.Count; i++)
        {
            var arg = commandLine[i];
            if (arg == "--type" || arg.StartsWith("--type=", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    public static IReadOnlyList<int> EnumerateQqPids(QqProcessSource source)
    {
        var pids = new List<int>();
        foreach (var process in source())
        {
            if (IsMainQqProcess(process.Name, process.CommandLine))
                pids.Add(process.Pid);
        }
        pids.Sort();
        return pids;
    }
}
