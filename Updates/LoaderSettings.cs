using System.Text.Json;

namespace QQListener.Updates;

public sealed record LoaderSettings(string CatalogUrl, string Proxy, bool AutoUpdate)
{
    public static LoaderSettings Default { get; } = new(DistributionCatalog.DefaultUrl, "", true);
    public static LoaderSettings Read(string directory)
    {
        var path = Path.Combine(directory, "loader-settings.json");
        if (!File.Exists(path))
        {
            var oldPath = Path.Combine(directory, "settings.json");
            if (File.Exists(oldPath) && new FileInfo(oldPath).Length <= 1024 * 1024)
            {
                try
                {
                    using var old = JsonDocument.Parse(File.ReadAllBytes(oldPath));
                    if (old.RootElement.TryGetProperty("KernelDownloadProxy", out var proxy) && proxy.ValueKind == JsonValueKind.String)
                        return Default with { Proxy = proxy.GetString() ?? "" };
                }
                catch (JsonException) { }
            }
            return Default;
        }
        if (new FileInfo(path).Length > 16384) throw new IOException("引导器设置过大");
        return JsonSerializer.Deserialize<LoaderSettings>(File.ReadAllBytes(path), DistributionCatalog.JsonOptions) ?? Default;
    }
    public void Save(string directory) => DistributionClient.WriteAtomicAsync(Path.Combine(directory, "loader-settings.json"),
        JsonSerializer.SerializeToUtf8Bytes(this, DistributionCatalog.JsonOptions), default).GetAwaiter().GetResult();
}
