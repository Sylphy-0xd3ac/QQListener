using System.Reflection;
using System.Security.Cryptography;

namespace QQListener.Updates;

public sealed class DistributionClient(HttpClient? http = null, string? trustedPublicKey = null)
{
    private static readonly HttpClient SharedHttp = new();
    private readonly HttpClient _http = http ?? SharedHttp;

    public static string PublicKey
    {
        get
        {
            using var stream = typeof(DistributionClient).Assembly.GetManifestResourceStream("QQListener.Updates.public.pem")
                               ?? throw new IOException("缺少更新校验公钥");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public async Task<DistributionCatalog> CatalogAsync(string url, string? proxy, CancellationToken ct,
        string? cacheDirectory = null)
    {
        var json = await GetAsync(url, proxy, 1024 * 1024, ct).ConfigureAwait(false);
        var signature = await GetAsync(url + ".sig", proxy, 1024, ct).ConfigureAwait(false);
        var catalog = DistributionCatalog.Verify(json, signature, trustedPublicKey ?? PublicKey);
        if (cacheDirectory is not null)
        {
            Directory.CreateDirectory(cacheDirectory);
            // A single envelope prevents a crash between updating the JSON and signature.
            var envelope = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CatalogEnvelope(json, signature));
            await WriteAtomicAsync(Path.Combine(cacheDirectory, "catalog-cache.json"), envelope, ct).ConfigureAwait(false);
        }
        return catalog;
    }

    public static DistributionCatalog? CachedCatalog(string directory)
    {
        var path = Path.Combine(directory, "catalog-cache.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("缓存清单过大");
        var envelope = System.Text.Json.JsonSerializer.Deserialize<CatalogEnvelope>(File.ReadAllBytes(path))
                       ?? throw new IOException("缓存清单无效");
        return DistributionCatalog.Verify(envelope.Json, envelope.Signature, PublicKey);
    }

    private sealed record CatalogEnvelope(byte[] Json, byte[] Signature);

    public async Task<byte[]> AssetAsync(DistributionAsset asset, string? proxy, CancellationToken ct,
        IProgress<DownloadProgress>? progress = null, string stage = "下载")
    {
        var bytes = await GetAsync(asset.Url, proxy, asset.Size, ct, progress, stage).ConfigureAwait(false);
        if (bytes.Length != asset.Size || !HashMatches(bytes, asset.Sha256)) throw new IOException($"{stage}校验失败");
        return bytes;
    }

    public async Task<byte[]> GetAsync(string url, string? proxy, int maximum, CancellationToken ct,
        IProgress<DownloadProgress>? progress = null, string stage = "下载")
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, ApplyProxy(url, proxy));
        request.Headers.UserAgent.ParseAdd("QQListener-Updater/1.0");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length > maximum) throw new IOException("下载内容超过预期大小");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (result.Length + read > maximum) throw new IOException("下载内容超过大小上限");
            result.Write(buffer, 0, read);
            progress?.Report(new(stage, result.Length, length));
        }
        return result.ToArray();
    }

    public static string ApplyProxy(string url, string? proxy)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https")
            throw new IOException("更新地址必须使用 HTTPS");
        if (string.IsNullOrWhiteSpace(proxy)) return url;
        var prefix = proxy.Trim().TrimEnd('/');
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var value) || value.Scheme != "https")
            throw new IOException("下载代理必须使用 HTTPS");
        return prefix + "/" + url;
    }

    public static bool HashMatches(byte[] bytes, string expected) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase);

    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
