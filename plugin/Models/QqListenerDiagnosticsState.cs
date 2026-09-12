using CommunityToolkit.Mvvm.ComponentModel;

namespace QQListener.Models;

public partial class QqListenerDiagnosticsState : ObservableObject
{
    [ObservableProperty]
    private string _runtimeSummary = "尚未初始化";

    [ObservableProperty]
    private string _assemblyPath = "未知";

    [ObservableProperty]
    private string _pluginFolderPath = "未知";

    [ObservableProperty]
    private string _settingsPath = "未知";

    [ObservableProperty]
    private string _listenerState = "未启动";

    [ObservableProperty]
    private string _lastAcceptedSummary = "暂无";

    [ObservableProperty]
    private string _lastIgnoredReason = "暂无";

    [ObservableProperty]
    private string _lastFormatSummary = "暂无";

    [ObservableProperty]
    private string _lastErrorSummary = "暂无";

    public void RecordRuntimeInfo(
        string manifestVersion,
        string pluginFolderPath,
        string settingsPath,
        string assemblyPath,
        DateTimeOffset? assemblyWriteTime)
    {
        var version = string.IsNullOrWhiteSpace(manifestVersion) ? "未知版本" : manifestVersion;
        var buildTime = assemblyWriteTime is { } value
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "未知构建时间";

        RuntimeSummary = $"{version} | {buildTime}";
        PluginFolderPath = string.IsNullOrWhiteSpace(pluginFolderPath) ? "未知" : pluginFolderPath;
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath) ? "未知" : settingsPath;
        AssemblyPath = string.IsNullOrWhiteSpace(assemblyPath) ? "未知" : assemblyPath;
    }

    public void MarkStarted(bool enabled)
    {
        ListenerState = enabled ? "运行中" : "已启动，监听关闭";
    }

    public void MarkStopped()
    {
        ListenerState = "已停止";
    }

    public void MarkEnabledChanged(bool enabled)
    {
        ListenerState = enabled ? "运行中" : "监听关闭";
    }

    public void RecordAccepted(MessageEvent messageEvent, string reason)
    {
        LastAcceptedSummary = $"{messageEvent.ReceivedAt:HH:mm:ss} {FormatImportance(messageEvent.Importance)}（{reason}）";
        LastIgnoredReason = "暂无";
    }

    public void RecordIgnored(string reason)
    {
        LastIgnoredReason = string.IsNullOrWhiteSpace(reason) ? "未知原因" : reason;
    }

    public void RecordFormatWarning(string reason)
    {
        LastFormatSummary = string.IsNullOrWhiteSpace(reason) ? "未知格式" : reason;
    }

    public void RecordError(Exception exception, string context)
    {
        LastErrorSummary = $"{DateTimeOffset.Now:HH:mm:ss} {FormatError(exception, context)}";
    }

    internal static string FormatError(Exception exception, string context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        return $"{context}: {exceptionType}";
    }

    public void RecordSettingsWarning(string warning)
    {
        LastErrorSummary = $"{DateTimeOffset.Now:HH:mm:ss} 设置恢复: {warning}";
    }

    public void ClearError()
    {
        LastErrorSummary = "暂无";
    }

    private static string FormatImportance(MessageImportance importance) => importance switch
    {
        MessageImportance.Calling => "呼叫",
        MessageImportance.Important => "重要",
        MessageImportance.Normal => "普通",
        MessageImportance.Ignored => "忽略",
        _ => importance.ToString()
    };
}
