using QQListener.Models;
using QQListener.Oidb;
using QQListener.Pipes;

namespace QQListener.Services;

/// <summary>
/// 把 <see cref="MessageEvent"/> 变成一次回复请求，通过 control 管道发出去。
///
/// 只有原生捕获来源（<see cref="MessageEvent.CanReply"/>）才带有回复所需的路由信息
/// （所在 QQ 进程、场景、对端号/uid、原始序列号）；toast 来源没法回复。
/// 回复带上原消息引用（SrcMsg）——跟 <see cref="QqControlApi"/>
/// 本身的语义一致。
/// </summary>
public sealed class MessageReplyService(Func<int, IPipeTransport>? connect = null)
{
    // 复用 QqControlApi.OverNamedPipes() 而不是自己再拼一次 NamedPipeTransport——
    // 之前这里独立构造，漏传了控制管道的读超时，等于绕开了 QqControlApi 本身
    // 特意加的"内核卡死时不永久占用线程池线程"这道保护。只有一处构造逻辑，
    // 以后就不会再出现第三个绕过点。
    private readonly QqControlApi _api = connect is null ? QqControlApi.OverNamedPipes() : new QqControlApi(connect);

    public static ReplyRoute BuildRoute(MessageEvent message) => new(
        Pid: message.SourcePid,
        Scene: message.Scene,
        PeerId: message.ConversationId,
        PeerUid: message.PeerUid,
        PeerName: message.Conversation,
        QuoteSeq: message.RawSeq,
        QuoteSenderId: message.SenderId,
        QuoteTime: message.ReceivedAt.ToUnixTimeSeconds());

    /// <summary>发送一条文本回复；在线程池上跑，避免 control 管道的往返阻塞 UI 线程。</summary>
    public Task<SendResult> SendAsync(MessageEvent message, string text, CancellationToken cancellationToken = default)
    {
        if (!message.CanReply)
            throw new InvalidOperationException("这条消息不支持回复（不是原生捕获来源，或缺少路由信息）");
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("回复内容不能为空", nameof(text));

        var route = BuildRoute(message);
        return Task.Run(() => _api.SendTextReply(route, text), cancellationToken);
    }
}
