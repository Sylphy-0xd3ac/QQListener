using QQListener.Models;
using QQListener.Models.ComponentSettings;

namespace QQListener.Components;

public sealed record LatestQqMessagePresentation(
    bool IsVisible,
    bool HasDisplayedMessage,
    string StatusText,
    string SenderText,
    string MessageText,
    bool IsStatusVisible,
    bool IsSenderVisible,
    bool IsMessageVisible);

public static class LatestQqMessagePresenter
{
    public static LatestQqMessagePresentation Create(
        LatestMessageState? latestMessage,
        LatestQqMessageComponentSettings? settings)
    {
        settings ??= new LatestQqMessageComponentSettings();
        if (latestMessage is not { HasMessage: true }
            || latestMessage.Importance < settings.MinimumImportance)
        {
            return CreatePlaceholder(settings);
        }

        var statusText = GetStatusText(latestMessage.Importance);
        if (settings.HideSensitiveContent)
        {
            var privacyMessageText =
                $"{GetSource(latestMessage.Source)} {GetImportanceName(latestMessage.Importance)}消息";
            return new LatestQqMessagePresentation(
                true,
                true,
                statusText,
                "",
                privacyMessageText,
                settings.ShowStatusTag && !string.IsNullOrEmpty(statusText),
                false,
                !string.IsNullOrEmpty(privacyMessageText));
        }

        var senderText = latestMessage.Sender;
        var messageText = settings.ShowMessageBody ? GetMessageText(latestMessage.Message) : "";
        return new LatestQqMessagePresentation(
            true,
            true,
            statusText,
            senderText,
            messageText,
            settings.ShowStatusTag && !string.IsNullOrEmpty(statusText),
            settings.ShowSender && !string.IsNullOrWhiteSpace(senderText),
            !string.IsNullOrWhiteSpace(messageText));
    }

    private static LatestQqMessagePresentation CreatePlaceholder(LatestQqMessageComponentSettings settings)
    {
        var messageText = settings.ShowPlaceholder ? settings.PlaceholderText : "";
        return new LatestQqMessagePresentation(
            settings.ShowPlaceholder,
            false,
            "",
            "",
            messageText,
            false,
            false,
            settings.ShowPlaceholder && !string.IsNullOrEmpty(messageText));
    }

    /// <summary>
    /// 正文为空时不借用发送人顶替——发送人已经单独显示的话，消息区再显示一遍会
    /// 变成"张三 张三"这种重复；发送人被设置成不显示的话，消息区偷偷透出发送人
    /// 名字又违反了用户的隐私设置意图。两种情况下都应该老实留空。
    /// </summary>
    private static string GetMessageText(string body) =>
        string.IsNullOrWhiteSpace(body) ? "" : body;

    private static string GetSource(string source) =>
        string.IsNullOrWhiteSpace(source) ? "QQ" : source;

    private static string GetStatusText(MessageImportance importance) => importance switch
    {
        MessageImportance.Calling => "呼叫",
        MessageImportance.Important => "重要",
        _ => ""
    };

    private static string GetImportanceName(MessageImportance importance) => importance switch
    {
        MessageImportance.Calling => "呼叫",
        MessageImportance.Important => "重要",
        _ => "普通"
    };
}
