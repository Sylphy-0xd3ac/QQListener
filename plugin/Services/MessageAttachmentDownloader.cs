using System.Net.Http;

namespace QQListener.Services;

/// <summary>附件只写入用户指定的目录；完成后才发布最终文件。</summary>
public sealed class MessageAttachmentDownloader(HttpClient client)
{
    public async Task<string> DownloadAsync(
        string url, string directory, string name, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, long maxBytes = 100 * 1024 * 1024)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("没有可下载的 HTTP(S) 地址。");
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("请在设置中填写完整的下载目录。");

        // ResponseHeadersRead 下 HttpClient.Timeout 不覆盖后续流读取。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = timeout.Token;

        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > maxBytes) throw new IOException("附件超过下载大小限制。");

        Directory.CreateDirectory(directory);
        var fileName = SafeName(name);
        if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
            fileName += response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif",
                "image/webp" => ".webp", "application/pdf" => ".pdf", "video/mp4" => ".mp4",
                "audio/mpeg" => ".mp3", "text/plain" => ".txt", _ => ".bin"
            };
        var extension = Path.GetExtension(fileName);
        var path = Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}{extension}");
        var partial = path + ".part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(partial, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new IOException("附件超过下载大小限制。");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    progress?.Report(length > 0 ? (double)total / length.Value : -1);
                }
                if (length.HasValue && total != length.Value)
                    throw new IOException("下载不完整，请重试。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, path);
            progress?.Report(1);
            return path;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    public static string SafeName(string name)
    {
        // 同时处理 Windows 与 Unix 路径分隔符，不能依赖当前测试平台。
        var leaf = name.Replace('\\', '/').Split('/').Last();
        var clean = new string(leaf.Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)
            ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(clean)) return "附件.bin";
        if (clean.Length > 120)
            clean = clean[..100] + Path.GetExtension(clean)[..Math.Min(20, Path.GetExtension(clean).Length)];
        // 添加前缀也避免 CON、NUL 等 Windows 设备名。
        return "QQ-" + clean;
    }
}
