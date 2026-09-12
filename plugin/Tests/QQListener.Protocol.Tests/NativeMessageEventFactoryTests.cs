using QQListener.Models;
using QQListener.Protocol;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public class NativeMessageEventFactoryTests
{
    private static CapturedMessage GroupMessage(
        string remark = "", string groupCard = "", string nickname = "", string senderName = "") => new()
    {
        Scene = "group",
        PeerId = "123456",
        PeerName = "高三2班",
        SenderId = "1001",
        SenderName = senderName,
        SenderNickname = nickname,
        SenderRemark = remark,
        SenderGroupCard = groupCard,
        RawSeq = 42,
        Timestamp = 1700000000,
        Segments = [new Segment { Type = "text", Text = "收到" }],
    };

    [Fact]
    public void PrefersRemarkOverEverythingElse()
    {
        var msg = GroupMessage(remark: "备注名", groupCard: "群名片", nickname: "昵称", senderName: "原始名");
        Assert.Equal("备注名", NativeMessageEventFactory.From(msg).Sender);
    }

    [Fact]
    public void FallsBackToGroupCardThenNicknameThenRawName()
    {
        Assert.Equal("群名片", NativeMessageEventFactory.From(GroupMessage(groupCard: "群名片", nickname: "昵称")).Sender);
        Assert.Equal("昵称", NativeMessageEventFactory.From(GroupMessage(nickname: "昵称", senderName: "原始名")).Sender);
        Assert.Equal("原始名", NativeMessageEventFactory.From(GroupMessage(senderName: "原始名")).Sender);
    }

    [Fact]
    public void FallsBackToBareIdWhenNoNameIsKnown()
    {
        Assert.Equal("1001", NativeMessageEventFactory.From(GroupMessage()).Sender);
    }

    [Fact]
    public void GroupConversationIsThePeerNameNotTheSender()
    {
        var evt = NativeMessageEventFactory.From(GroupMessage(nickname: "张三"));

        Assert.Equal("高三2班", evt.Conversation);
        Assert.Equal("张三", evt.Sender);
        Assert.Equal("123456", evt.ConversationId);
        Assert.Equal("1001", evt.SenderId);
    }

    [Fact]
    public void PrivateConversationCollapsesIntoTheSenderName()
    {
        var msg = new CapturedMessage
        {
            Scene = "c2c",
            PeerId = "10001",
            SenderId = "10001",
            SenderNickname = "张三",
            RawSeq = 1,
            Segments = [new Segment { Type = "text", Text = "在吗" }],
        };

        var evt = NativeMessageEventFactory.From(msg);

        Assert.Equal("张三", evt.Sender);
        Assert.Equal("张三", evt.Conversation);
        Assert.Equal("张三", evt.DisplaySender); // 相同会自动去重，不会显示成 "张三 / 张三"
    }

    [Fact]
    public void CarriesSegmentsThroughUntouchedForRichRendering()
    {
        var msg = GroupMessage();
        var evt = NativeMessageEventFactory.From(msg);

        Assert.Same(msg.Segments, evt.Segments);
        Assert.Equal("收到", evt.Body);
        Assert.Equal(MessageImportance.Normal, evt.Importance);
    }

    [Fact]
    public void UsesPushTimestampWhenPresentAndNowOtherwise()
    {
        var withTimestamp = NativeMessageEventFactory.From(GroupMessage());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), withTimestamp.ReceivedAt);

        var msg = GroupMessage();
        var noTimestamp = new CapturedMessage
        {
            Scene = msg.Scene, PeerId = msg.PeerId, PeerName = msg.PeerName,
            SenderId = msg.SenderId, SenderNickname = "张三", RawSeq = msg.RawSeq,
            Segments = msg.Segments, Timestamp = 0,
        };
        var before = DateTimeOffset.UtcNow;
        var evt = NativeMessageEventFactory.From(noTimestamp);
        Assert.InRange(evt.ReceivedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public void ReceivedAtUsesLocalOffsetNotUtc()
    {
        // MessageRuleEvaluator 的时间窗判定直接读 ReceivedAt.TimeOfDay——这个属性
        // 取的是 DateTimeOffset 自己偏移量下的挂钟时刻，不做额外时区换算。用户在
        // 设置里填的时间点是本地时间，UTC 时间戳留着不转会让实际生效的时间窗跟
        // 用户配置相差一个时区。用 TimeZoneInfo.Local 现算期望偏移量，不硬编码
        // 具体时区，在任何 CI 机器的本地时区下都成立。
        var evt = NativeMessageEventFactory.From(GroupMessage());

        var expectedOffset = TimeZoneInfo.Local.GetUtcOffset(evt.ReceivedAt.UtcDateTime);
        Assert.Equal(expectedOffset, evt.ReceivedAt.Offset);
    }

    [Fact]
    public void MentionsSelfIsTrueWhenAnAtSegmentTargetsTheLoggedInAccount()
    {
        var msg = new CapturedMessage
        {
            Scene = "group", PeerId = "123456", SenderId = "1001", SelfUin = 20001,
            Segments = [new Segment { Type = "at", Text = "@我", TargetId = "20001" }],
        };

        Assert.True(NativeMessageEventFactory.From(msg).MentionsSelf);
    }

    [Fact]
    public void MentionsSelfIsFalseWhenAtSegmentTargetsSomeoneElse()
    {
        var msg = new CapturedMessage
        {
            Scene = "group", PeerId = "123456", SenderId = "1001", SelfUin = 20001,
            Segments = [new Segment { Type = "at", Text = "@李四", TargetId = "20002" }],
        };

        Assert.False(NativeMessageEventFactory.From(msg).MentionsSelf);
    }

    [Fact]
    public void MentionsSelfIsFalseWhenSelfUinCouldNotBeResolved()
    {
        var msg = new CapturedMessage
        {
            Scene = "group", PeerId = "123456", SenderId = "1001", SelfUin = 0,
            Segments = [new Segment { Type = "at", Text = "@全体成员", TargetId = "all" }],
        };

        Assert.False(NativeMessageEventFactory.From(msg).MentionsSelf);
    }

    [Fact]
    public void DeduplicationKeyDistinguishesConversationAndSequence()
    {
        var a = NativeMessageEventFactory.From(GroupMessage());
        var b = NativeMessageEventFactory.From(new CapturedMessage
        {
            Scene = "group", PeerId = "123456", PeerName = "高三2班", SenderId = "1001",
            SenderNickname = "x", RawSeq = 43, Segments = [],
        });

        Assert.NotEqual(a.DeduplicationKey, b.DeduplicationKey);
    }

    [Fact]
    public void DeduplicationKeyIsStableForRedeliveryOfTheSameMessageWhenRawSeqIsReal()
    {
        // RawSeq 非 0 时它本身已经唯一标识一条消息：键里不该再掺进任何别的字段——
        // 掺了反而会让"同一条消息被重复投递、但两次时间戳有任何出入"漏判成不重复。
        // 这里用两个字段完全相同的 CapturedMessage 模拟"同一条消息被重复投递"。
        var a = NativeMessageEventFactory.From(GroupMessage());
        var b = NativeMessageEventFactory.From(GroupMessage());

        Assert.Equal(a.DeduplicationKey, b.DeduplicationKey);
    }

    [Fact]
    public void DeduplicationKeyDistinguishesMessagesThatShareTheZeroSequenceFallback()
    {
        // 协议没给到序列号时 RawSeq 统一留 0（SsoDecode 不再借用传输层计数器顶替）；
        // 光靠 "会话+发送者+0" 会把同一个人连续发的好几条这种消息全部去重成一条。
        var first = NativeMessageEventFactory.From(new CapturedMessage
        {
            Scene = "group", PeerId = "123456", SenderId = "1001", RawSeq = 0,
            Timestamp = 1700000000, Segments = [],
        });
        var second = NativeMessageEventFactory.From(new CapturedMessage
        {
            Scene = "group", PeerId = "123456", SenderId = "1001", RawSeq = 0,
            Timestamp = 1700000001, Segments = [],
        });

        Assert.NotEqual(first.DeduplicationKey, second.DeduplicationKey);
    }
}
