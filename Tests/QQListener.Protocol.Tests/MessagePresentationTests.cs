using QQListener.Models;
using QQListener.Protocol;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public class MessagePresentationTests
{
    private static MessageEvent Message(params Segment[] segments) => new(
        "test", "QQ", "张三", "高三2班", "legacy body", DateTimeOffset.UnixEpoch,
        MessageImportance.Normal, [], "test") { Segments = segments };

    [Fact]
    public void PlainToastRetainsItsBody() => Assert.Equal("legacy body",
        Assert.Single(MessageContentPresentation.Create(Message())).Text);

    [Fact]
    public void TextAndMentionsStayTogetherButImagesRemainImages()
    {
        var blocks = MessageContentPresentation.Create(Message(
            new() { Type = "text", Text = "请 " },
            new() { Type = "at", Text = "@李四 " },
            new() { Type = "text", Text = "看图" },
            new() { Type = "image", Url = "https://example.invalid/image.png" },
            new() { Type = "file", Name = "任务.pdf" }));
        Assert.Equal(3, blocks.Count);
        Assert.Equal("请 @李四 看图", blocks[0].Text);
        Assert.Equal("image", blocks[1].Kind);
        Assert.Equal("https://example.invalid/image.png", blocks[1].Url);
        Assert.Equal("任务.pdf", blocks[2].Text);
    }

    [Fact]
    public void QuoteAcceptsReadOnlyArraysAndPreservesNestedMedia()
    {
        var quote = Assert.Single(MessageContentPresentation.Create(Message(new Segment
        {
            Type = "reply", Extra = new()
            {
                ["sender_name"] = "老师",
                ["segments"] = new Segment[] { new() { Type = "image", Url = "https://example.invalid/a.png" } }
            }
        })));
        Assert.Equal("quote", quote.Kind);
        Assert.Equal("老师", quote.Text);
        Assert.Equal("image", Assert.Single(quote.Children!).Kind);
    }

    [Fact]
    public void QuotedImageKeepsTheSourceAccountAndConversationScene()
    {
        var message = Message(new Segment
        {
            Type = "reply", Extra = new()
            {
                ["segments"] = new Segment[] { new() { Type = "image", Url = "https://example.invalid/a.png" } }
            }
        }) with { SourcePid = 123, Scene = "group" };
        var image = Assert.Single(Assert.Single(MessageContentPresentation.Create(message)).Children!);
        Assert.Equal(123, image.SourcePid);
        Assert.True(image.IsGroup);
    }

    [Fact]
    public void DigestImagesKeepTheirOwnSourceAccountsAndScenes()
    {
        var first = Message(new Segment { Type = "image", Url = "https://example.invalid/group.png" })
            with { SourcePid = 123, Scene = "group" };
        var second = Message(new Segment { Type = "image", Url = "https://example.invalid/private.png" })
            with { SourcePid = 456, Scene = "private" };
        var digest = PendingMessageDigest.Create([first, second], 0)!;
        var blocks = MessageContentPresentation.Create(digest);
        var firstImage = Assert.Single(blocks[0].Children!);
        var secondImage = Assert.Single(blocks[1].Children!);
        Assert.Equal(123, firstImage.SourcePid);
        Assert.True(firstImage.IsGroup);
        Assert.Equal(456, secondImage.SourcePid);
        Assert.False(secondImage.IsGroup);
    }

    [Fact]
    public void CyclicQuoteIsBounded()
    {
        var segments = new List<Segment>();
        segments.Add(new() { Type = "reply", Extra = new() { ["segments"] = segments } });
        var block = Assert.Single(MessageContentPresentation.Create(Message(segments[0])));
        for (var i = 0; i < 8; i++) block = Assert.Single(block.Children!);
        Assert.Equal("（引用层数过多）", block.Text);
    }

    [Fact]
    public void ElementBudgetIsSharedAcrossQuotes()
    {
        var children = Enumerable.Range(0, 1000).Select(_ => new Segment { Type = "image" }).ToArray();
        var blocks = MessageContentPresentation.Create(Message(
            new() { Type = "reply", Extra = new() { ["segments"] = children } },
            new() { Type = "text", Text = "after budget" }));
        Assert.Equal(512, blocks[0].Children!.Count);
        Assert.DoesNotContain(blocks, b => b.Text == "after budget");
    }

    [Theory]
    [InlineData(MessagePreviewKind.Text)]
    [InlineData(MessagePreviewKind.Media)]
    [InlineData(MessagePreviewKind.Quote)]
    public void SamplesAreOfflineAndContainTheExpectedContent(MessagePreviewKind kind)
    {
        var message = MessagePreviewSamples.Create(kind, MessageImportance.Normal);
        Assert.NotEmpty(MessageContentPresentation.Create(message));
        Assert.All(message.Segments, s => Assert.False(s.Url.StartsWith("http")));
        if (kind == MessagePreviewKind.Media) Assert.Contains(message.Segments, s => s.Type == "image");
        if (kind == MessagePreviewKind.Quote) Assert.Contains(message.Segments, s => s.Type == "reply");
        Assert.NotEqual(message.DeduplicationKey,
            MessagePreviewSamples.Create(kind, MessageImportance.Normal).DeduplicationKey);
    }
}
