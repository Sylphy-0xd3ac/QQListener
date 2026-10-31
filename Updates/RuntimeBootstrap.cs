using System.Text.Json;

namespace QQListener.Updates;

public sealed class RuntimeBootstrap(RuntimeStore store, DistributionClient client)
{
    public async Task<CoreRelease> PrepareAsync(string pluginDirectory, LoaderSettings settings,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var available = new List<CoreRelease>();
        void Add(Func<CoreRelease?> read)
        {
            try
            {
                var candidate = read();
                if (candidate is null || store.IsRejected(candidate)) return;
                store.Verify(candidate);
                available.Add(candidate);
            }
            catch (Exception e) when (e is IOException or JsonException or BadImageFormatException) { log?.Invoke(e.Message); }
        }
        Add(() => store.Pending); Add(() => store.Active); Add(() => store.Previous);
        var seedPath = Path.Combine(pluginDirectory, "core-seed.json");
        if (File.Exists(seedPath))
        {
            try
            {
                if (new FileInfo(seedPath).Length > 16384) throw new IOException("内置核心记录过大");
                var seed = JsonSerializer.Deserialize<CoreRelease>(File.ReadAllBytes(seedPath), DistributionCatalog.JsonOptions)
                           ?? throw new IOException("内置核心记录无效");
                if (!store.IsRejected(seed))
                {
                    var packagePath = Path.Combine(pluginDirectory, "QQListener.Core.zip");
                    DistributionCatalog.Validate(seed.Package, 64 * 1024 * 1024);
                    if (new FileInfo(packagePath).Length != seed.Package.Size) throw new IOException("内置核心大小不匹配");
                    await store.StageAsync(seed, await File.ReadAllBytesAsync(packagePath, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    available.Add(seed);
                }
            }
            catch (Exception e) when (e is IOException or JsonException or BadImageFormatException) { log?.Invoke(e.Message); }
        }
        // The bundled version bootstraps offline; installed versions never wait indefinitely on a proxy.
        if (settings.AutoUpdate || available.Count == 0)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(available.Count == 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(8));
            try
            {
                var catalog = await client.CatalogAsync(settings.CatalogUrl, settings.Proxy, timeout.Token, store.Root).ConfigureAwait(false);
                var latest = catalog.LatestCore;
                if (latest is not null && !store.IsRejected(latest)
                    && !available.Any(r => r.BuildNumber > latest.BuildNumber || r.Package.Sha256 == latest.Package.Sha256))
                {
                    var bytes = await client.AssetAsync(latest.Package, settings.Proxy, timeout.Token).ConfigureAwait(false);
                    await store.StageAsync(latest, bytes, timeout.Token).ConfigureAwait(false);
                    available.Add(latest);
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException
                                       or JsonException or System.Security.Cryptography.CryptographicException)
            { ct.ThrowIfCancellationRequested(); log?.Invoke("更新检查失败：" + e.Message); }
        }
        return available.OrderByDescending(r => r.BuildNumber)
                   .ThenByDescending(r => r.Version, ReleaseVersionComparer.Instance).FirstOrDefault()
               ?? throw new IOException("未安装 QQListener 核心。请检查网络或 loader-settings.json 中的 Proxy，或安装内置核心版 CIPX。");
    }
}
