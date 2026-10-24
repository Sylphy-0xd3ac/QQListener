using System.Net;
using QQListener.Models;
using QQListener.Oidb;
using QQListener.Protocol;

namespace QQListener.Services;

public sealed class QqImageDownloadException(int? code = null) : IOException(
    code.HasValue ? $"QQ 图片下载凭证失效或访问被拒绝（{code.Value}），请重试"
        : "QQ 返回的不是图片，可能是下载凭证失效，请重试");

public sealed class MessageImageDownloader(
    MessageAttachmentDownloader downloader, Func<int, IReadOnlyList<RKeyInfo>>? fetchRKeys = null,
    Func<MessageContentBlock, IReadOnlyList<RKeyInfo>>? fetchForImage = null)
{
    private readonly Func<int, IReadOnlyList<RKeyInfo>> _fetch = fetchRKeys
        ?? QqControlApi.OverNamedPipes().FetchRKeys;

    public async Task<string> DownloadAsync(MessageContentBlock image, string directory,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var url = Elements.MakeImageUrl(image.Url);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "http" && uri.Host.Equals("gchat.qpic.cn", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/gchatpic_new/", StringComparison.OrdinalIgnoreCase))
            url = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri;
        try
        {
            return await Download(url);
        }
        catch (Exception exception) when (image.SourcePid > 0 && RKey.IsMediaUrl(url)
            && (exception is QqImageDownloadException
                || exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden or HttpStatusCode.NotFound }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<RKeyInfo> keys;
            try
            {
                keys = await Task.Run(() => fetchForImage is null ? _fetch(image.SourcePid) : fetchForImage(image), cancellationToken)
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                throw new IOException("无法刷新 QQ 图片下载凭证，请确认对应 QQ 在线后重试");
            }
            var type = RKey.TypeForUrl(url, image.IsGroup);
            var key = keys.FirstOrDefault(item => item.Type == type && Usable(item))
                ?? keys.FirstOrDefault(item => item.Type == RKey.TypeFallbackImage && Usable(item));
            if (key is null) throw new IOException("QQ 未返回可用的图片下载凭证，请重试");
            return await Download(RichText.AppendRkey(url, key.RKey, replace: true));
        }

        Task<string> Download(string address) => downloader.DownloadAsync(address, directory, image.Text,
            progress, cancellationToken, 12 * 1024 * 1024, requireImage: true);
    }

    private static bool Usable(RKeyInfo key) => !string.IsNullOrWhiteSpace(key.RKey)
        && (key.TtlSeconds == 0 || key.TtlSeconds > 60);
}
