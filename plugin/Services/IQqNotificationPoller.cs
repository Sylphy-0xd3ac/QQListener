using QQListener.Models;

namespace QQListener.Services;

public interface IQqNotificationPoller
{
    Task<QqNotificationPollResult> PollAsync(
        bool retryPermission,
        CancellationToken cancellationToken);

    void ResetDetectionState();

    /// <summary>
    /// 有新消息到达时完成的任务，供调用方替代"固定间隔再来问一次"。
    ///
    /// 原生捕获是推送式的：每个 QQ 进程一条后台线程在读管道，消息什么时候到完全
    /// 由对端决定。调用方原本按固定扫描间隔轮询，等于给每条消息平白加上半个间隔
    /// 的延迟；改成等这个任务，消息落地即唤醒。
    ///
    /// 默认实现永不完成——不支持推送的数据源不会因此被提前唤醒，调用方仍按自己的
    /// 心跳节奏走，也不需要为此改动。
    /// </summary>
    Task WaitForMessagesAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.Infinite, cancellationToken);
}
