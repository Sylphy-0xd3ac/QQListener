using System.Security.Cryptography;
using System.Text.Json;

namespace QQListener.Updates;

public sealed record DownloadProgress(string Stage, long Received = 0, long? Total = null)
{
    public double Percent => Total > 0 ? Math.Clamp(100.0 * Received / Total.Value, 0, 100) : 0;
}

public sealed record DistributionAsset(string Url, string Sha256, int Size);
public sealed record PmhqLayout(uint InitializedRva, uint AccountRva);
public sealed record PmhqRelease(string UpstreamVersion, string RuntimeVersion, int HostAbi,
    DistributionAsset Archive, string OriginalSha256, int OriginalSize, DistributionAsset Patch,
    string OutputSha256, int OutputSize, PmhqLayout Layout);
public sealed record CoreRelease(string Version, int LoaderAbi, DistributionAsset Package, long BuildNumber = 0);
public sealed record DistributionCatalog(int Schema, PmhqRelease[] Pmhq, CoreRelease[] Core)
{
    public const string DefaultUrl = "https://github.com/Sylphy-0xd3ac/QQListener/releases/download/runtime-builds/catalog.json";
    public const int HostAbi = 1;
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static DistributionCatalog Verify(byte[] json, byte[] signature, string publicKey)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKey);
        if (!key.VerifyData(json, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new IOException("更新清单签名无效");
        var catalog = JsonSerializer.Deserialize<DistributionCatalog>(json, JsonOptions)
                      ?? throw new IOException("更新清单为空");
        if (catalog.Schema != 1 || catalog.Pmhq is null || catalog.Core is null
            || catalog.Pmhq.Length > 128 || catalog.Core.Length > 128) throw new IOException("不支持的更新清单");
        foreach (var release in catalog.Pmhq) Validate(release);
        foreach (var release in catalog.Core)
        {
            RequireVersion(release.Version);
            Validate(release.Package, 64 * 1024 * 1024);
        }
        if (catalog.Pmhq.Select(p => p.RuntimeVersion).Distinct(StringComparer.Ordinal).Count() != catalog.Pmhq.Length
            || catalog.Core.Select(p => p.Package.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Core.Length)
            throw new IOException("更新清单包含重复版本");
        return catalog;
    }

    public static void Validate(PmhqRelease release)
    {
        RequireVersion(release.UpstreamVersion.TrimStart('v'));
        RequireVersion(release.RuntimeVersion);
        Validate(release.Archive, 64 * 1024 * 1024);
        Validate(release.Patch, 16 * 1024 * 1024);
        RequireHash(release.OriginalSha256); RequireHash(release.OutputSha256);
        if (release.OriginalSize is <= 0 or > 16 * 1024 * 1024 || release.OutputSize is <= 0 or > 16 * 1024 * 1024
            || release.Layout is null || release.Layout.InitializedRva == 0 || release.Layout.AccountRva == 0
            || release.Layout.InitializedRva > 64 * 1024 * 1024 || release.Layout.AccountRva > 64 * 1024 * 1024)
            throw new IOException("PMHQ 版本记录无效");
    }

    public static void Validate(DistributionAsset asset, int maximum)
    {
        if (asset is null || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || asset.Size <= 0 || asset.Size > maximum)
            throw new IOException("下载文件记录无效");
        RequireHash(asset.Sha256);
    }

    private static void RequireHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new IOException("SHA-256 无效");
    }

    public static void RequireVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 80
            || version.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            || !char.IsAsciiDigit(version[0]) || version.Contains("..")) throw new IOException("版本号无效");
    }

    public PmhqRelease? ForPmhq(string upstreamVersion) => Pmhq
        .Where(p => p.HostAbi == HostAbi && p.UpstreamVersion.TrimStart('v') == upstreamVersion.TrimStart('v'))
        .OrderByDescending(p => p.RuntimeVersion, ReleaseVersionComparer.Instance).FirstOrDefault();

    public PmhqRelease? LatestCompatiblePmhq => Pmhq.Where(p => p.HostAbi == HostAbi)
        .OrderByDescending(p => p.RuntimeVersion, ReleaseVersionComparer.Instance).FirstOrDefault();
    public CoreRelease? LatestCore => Core.Where(p => p.LoaderAbi == HostAbi)
        .OrderByDescending(p => p.BuildNumber).ThenByDescending(p => p.Version, ReleaseVersionComparer.Instance).FirstOrDefault();
}

public sealed class ReleaseVersionComparer : IComparer<string>
{
    public static readonly ReleaseVersionComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        if (x == y) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var a = x.TrimStart('v').Split('-', 2); var b = y.TrimStart('v').Split('-', 2);
        var left = a[0].Split('.'); var right = b[0].Split('.');
        for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            _ = ulong.TryParse(i < left.Length ? left[i] : "0", out var av);
            _ = ulong.TryParse(i < right.Length ? right[i] : "0", out var bv);
            if (av != bv) return av.CompareTo(bv);
        }
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return 0;
        left = a[1].Split('.'); right = b[1].Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var an = ulong.TryParse(left[i], out var av); var bn = ulong.TryParse(right[i], out var bv);
            var result = an && bn ? av.CompareTo(bv) : an != bn ? an ? -1 : 1 : string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
}
