namespace QQListener.Core;

/// <summary>
/// SnowLuma 钩子 DLL 的本地定位。对照 Python 版 <c>src/native/binary_locator.py</c>。
///
/// 只找文件，不下载——下载在 <see cref="KernelInstaller"/>。调用方（设置页/CoreService）
/// 应该按插件自己的数据目录传 <paramref name="searchDirs"/>，默认目录只是给独立场景兜底。
/// </summary>
public static class KernelLocator
{
    public const string HookDllName = "snowluma-win32-x64.dll";

    public static string? FindHookBinary(IReadOnlyList<string>? searchDirs = null)
    {
        foreach (var dir in searchDirs ?? DefaultDirs())
        {
            var candidate = Path.Combine(dir, HookDllName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static string MissingBinaryHelp() =>
        $"未找到 {HookDllName}。\n" +
        "请从 SnowLuma release 获取该文件，放到插件的 native/ 目录下。\n" +
        "该二进制为 SnowLuma 专有组件，QQListener 不随附分发（LICENSE §5）。";

    /// <summary>
    /// 内核二进制的标准安装目录：跟设置持久化目录放一起（可写、每用户维护），
    /// 不依赖插件安装目录是否只读或会被自动更新整体替换。
    ///
    /// 这是唯一一处计算这个路径的地方——设置页的安装向导（下载目标）和
    /// <see cref="CoreService"/> 的运行时定位（查找目标）必须用同一个目录，
    /// 否则会出现"向导显示安装成功，核心却永远找不到文件"的问题。
    /// </summary>
    public static string ResolveNativeDirectory(string? settingsDirectory) => Path.GetFullPath(Path.Combine(
        string.IsNullOrWhiteSpace(settingsDirectory) ? AppContext.BaseDirectory : settingsDirectory,
        "native"));

    private static IReadOnlyList<string> DefaultDirs()
    {
        var root = AppContext.BaseDirectory;
        return [Path.Combine(root, "native"), Path.Combine(root, "asset", "native")];
    }
}
