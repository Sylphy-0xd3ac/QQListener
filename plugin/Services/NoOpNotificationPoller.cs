using QQListener.Models;

namespace QQListener.Services;

/// <summary>
/// 非 Windows 构建下的占位数据源。
///
/// 原生捕获（命名管道、进程枚举）依赖真实的 Windows API，这里只是让插件在其他
/// 平台上也能装载、跑单测——提醒 UI 本身仍然可以用设置页的"测试提醒"按钮验证，
/// 只是收不到真实 QQ 消息。
/// </summary>
public sealed class NoOpNotificationPoller : IQqNotificationPoller
{
    public Task<QqNotificationPollResult> PollAsync(bool retryPermission, CancellationToken cancellationToken) =>
        Task.FromResult(new QqNotificationPollResult(WindowsNotificationReadStatus.Success, [], ""));

    public void ResetDetectionState()
    {
    }
}
