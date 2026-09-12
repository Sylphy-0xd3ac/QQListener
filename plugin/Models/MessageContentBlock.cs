using System.Text;
using QQListener.Protocol;

namespace QQListener.Models;

public sealed record MessageContentBlock(
    string Kind, string Text, string Url = "", IReadOnlyList<MessageContentBlock>? Children = null);

/// <summary>只负责展示：保留媒体与引用结构，并限制异常深度和元素数量。</summary>
public static class MessageContentPresentation
{
    public static IReadOnlyList<MessageContentBlock> Create(MessageEvent message)
    {
        if (message.IsPendingDigest)
            return message.PendingMessages.Select(item => new MessageContentBlock(
                "message", item.DisplaySender, Children: Create(item))).ToArray();
        if (message.Segments.Count == 0)
            return [new("text", string.IsNullOrWhiteSpace(message.Body) ? "（空消息）" : message.Body)];
        var remaining = 512;
        return Build(message.Segments, 0, ref remaining);
    }

    private static IReadOnlyList<MessageContentBlock> Build(
        IReadOnlyList<Segment> segments, int depth, ref int remaining)
    {
        if (depth >= 8) return [new("text", "（引用层数过多）")];
        var blocks = new List<MessageContentBlock>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            blocks.Add(new("text", text.ToString()));
            text.Clear();
        }

        foreach (var segment in segments)
        {
            if (remaining-- <= 0)
            {
                Flush();
                blocks.Add(new("text", "（其余内容已折叠）"));
                break;
            }
            if (segment.Type is "text" or "at")
            {
                text.Append(segment.Type == "at"
                    ? "@" + segment.Text.Trim().TrimStart('@') + " "
                    : segment.Text);
                continue;
            }
            Flush();
            if (segment.Type == "reply")
            {
                var quoted = segment.Get<IReadOnlyList<Segment>>("segments") ?? [];
                // 未解析的数字 ID 不作为昵称显示。
                blocks.Add(new("quote", segment.Get<string>("sender_name") ?? "",
                    Children: Build(quoted, depth + 1, ref remaining)));
            }
            else if (segment.Type is "image" or "file" or "video" or "record")
            {
                var label = segment.Type switch
                {
                    "image" => "图片", "video" => "视频", "record" => "语音", _ => "文件"
                };
                blocks.Add(new(segment.Type,
                    string.IsNullOrWhiteSpace(segment.Name) ? label : segment.Name, segment.Url));
            }
            else blocks.Add(new("text", string.IsNullOrEmpty(segment.Text)
                ? $"[{segment.Type}]" : segment.Text));
        }
        Flush();
        return blocks.Count == 0 ? [new("text", "（空消息）")] : blocks;
    }
}
