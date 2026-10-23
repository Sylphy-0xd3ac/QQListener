using QQListener.Models;
using QQListener.Protocol;

namespace QQListener.Services;

public enum MessagePreviewKind { Text, Media, Quote }

public static class MessagePreviewSamples
{
    public static MessageEvent Create(MessagePreviewKind kind, MessageImportance importance)
    {
        IReadOnlyList<Segment> segments = kind switch
        {
            MessagePreviewKind.Media =>
            [
                new() { Type = "text", Text = "明天记得带上材料。下面是图片和附件的展示效果。" },
                new() { Type = "image", Name = "图片预览", Url = "avares://QQListener/icon.png" },
                new() { Type = "file", Name = "课堂材料.pdf" },
                new() { Type = "video", Name = "实验演示.mp4" }
            ],
            MessagePreviewKind.Quote =>
            [
                new() { Type = "text", Text = "收到，明天交到讲台。" },
                new()
                {
                    Type = "reply", Extra = new()
                    {
                        ["sender_name"] = "班主任",
                        ["segments"] = new List<Segment>
                        {
                            new() { Type = "at", Text = "@全体成员" },
                            new() { Type = "text", Text = "请按图片上的要求准备材料。" },
                            new() { Type = "image", Name = "引用中的图片", Url = "avares://QQListener/icon.png" }
                        }
                    }
                }
            ],
            _ => [new() { Type = "text", Text = "明天带好课本和练习册，别忘了。\n这是插件实际使用的消息窗口。" }]
        };
        var body = MessageText.Render(segments);
        return new MessageEvent("qq.preview", "QQ", "张三", "高三2班", body,
            DateTimeOffset.Now, importance, ["张三", "高三2班", body], $"preview.{Guid.NewGuid():N}")
        {
            Segments = segments, SenderId = "10001", ConversationId = "20002"
        };
    }
}
