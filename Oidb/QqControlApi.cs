using QQListener.Pipes;

namespace QQListener.Oidb;

/// <summary>
/// 通过 control 管道向被注入的 QQ 发请求。
///
/// 每次调用临时连一次管道：control 管道是请求/应答式的，
/// 长连着反而要处理乱序与半死连接，得不偿失。
/// 传输由 <paramref name="connect"/> 提供，测试里换成假传输（<c>FakeTransport</c>，定义在测试项目里）。
/// </summary>
public sealed class QqControlApi(Func<int, IPipeTransport> connect)
{
    public const string CmdRKey = "OidbSvcTrpcTcp.0x9067_202";
    public const string CmdGroupFile = "OidbSvcTrpcTcp.0x6d6_2";
    public const string CmdPrivateFile = "OidbSvcTrpcTcp.0xe37_1200";
    public const string CmdProfile = "OidbSvcTrpcTcp.0xfe1_2";

    // 进程级单例，序列号靠单调递增去重。UI 层每弹一个新窗口就 new 一个 QqControlApi（见
    // MessageReplyBar/MessageReplyService），如果各自持有自己的 SequenceSource，
    // 种子都基于挂钟毫秒，同一毫秒内两个窗口回复会撞出相同的 clientSequence，
    // QQ 按这个字段去重，第二条消息服务端直接丢弃但我们这边显示"已发送"。
    private static readonly SequenceSource Sequences = new();

    /// <summary>control 管道是请求/应答式的短连接；给读操作一个上限，避免内核卡死时
    /// 永久占用一个线程池线程（recv 管道是长连接，不能套用这个超时）。</summary>
    private const int ControlReadTimeoutMs = 15_000;

    public static QqControlApi OverNamedPipes() =>
        new(pid => new NamedPipeTransport(PipeNames.Control(pid), readTimeoutMs: ControlReadTimeoutMs));

    private byte[] Request(int pid, string cmd, byte[] body)
    {
        using var client = new ControlClient(connect(pid));
        var reply = client.Send(cmd, body);
        if (reply.Error != 0)
            throw new IOException(reply.Message.Length > 0
                ? reply.Message
                : $"QQ request error: {reply.Error}");
        return reply.Body;
    }

    public IReadOnlyList<RKeyInfo> FetchRKeys(int pid) =>
        RKey.ParseResponse(Request(pid, CmdRKey, RKey.BuildRequest()));

    public string ResolveGroupFileUrl(int pid, long groupUin, string fileId, long busId = 102) =>
        FileResolver.ParseGroupResponse(
            Request(pid, CmdGroupFile,
                FileResolver.BuildGroupRequest((ulong)groupUin, fileId, (ulong)busId)),
            fileId);

    public string ResolvePrivateFileUrl(int pid, string accountUid, string fileId, string fileHash) =>
        FileResolver.ParsePrivateResponse(
            Request(pid, CmdPrivateFile,
                FileResolver.BuildPrivateRequest(accountUid, fileId, fileHash)));

    public ProfileResolver.UserProfileNames ResolveProfile(int pid, string userId) =>
        ProfileResolver.ParseResponse(
            Request(pid, CmdProfile, ProfileResolver.BuildRequest(userId)));

    public void SendOkReaction(ReplyRoute route)
    {
        if (route.Pid <= 0 || route.Scene != "group")
            throw new InvalidOperationException("OK 表情回应仅支持已连接的群消息");
        var request = MessageReaction.BuildRequest(ParseId(route.PeerId), route.QuoteSeq);
        MessageReaction.ValidateResponse(Request(route.Pid, MessageReaction.Command, request));
    }

    /// <summary>发一条文本回复；群聊带引用，私聊发普通消息。</summary>
    public SendResult SendTextReply(ReplyRoute route, string text)
    {
        if (route.Pid == 0)
            throw new InvalidOperationException("尚未连接到 QQ 进程");

        var isGroup = route.Scene == "group";
        // 只有群聊支持按序列号引用；私聊的 origSeqs 语义不同，直接发普通消息。
        var quoteSeq = isGroup ? route.QuoteSeq : 0;
        var quoteSender = isGroup ? ParseId(route.QuoteSenderId) : 0;

        var request = MessageSender.BuildSendTextRequest(
            scene: route.Scene,
            peerUin: ParseId(route.PeerId),
            text: text,
            peerUid: route.PeerUid,
            clientSequence: isGroup ? 0 : Sequences.NextClientSequence(),
            messageRandom: Sequences.NextRandom(),
            quoteSeq: quoteSeq,
            quoteSenderUin: quoteSender,
            quoteTime: route.QuoteTime);

        var result = MessageSender.ParseResponse(
            Request(route.Pid, MessageSender.SendMsgCmd, request));

        if (result.Result != 0)
            throw new IOException(result.ErrorMessage.Length > 0
                ? result.ErrorMessage
                : $"send message rejected: result={result.Result}");
        return result;
    }

    private static long ParseId(string value) =>
        long.TryParse((value ?? "").Trim(), out var id) ? id : 0;
}
