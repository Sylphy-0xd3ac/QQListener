using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace QQListener.Updates;

public sealed record RuntimeFileManifest(string Version, Dictionary<string, string> Files);

/// <summary>Versioned payloads live beside settings, never in the replaceable plugin directory.</summary>
public sealed class RuntimeStore(string configDirectory)
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    public string Root { get; } = Path.Combine(Path.GetFullPath(configDirectory), "runtime");
    public string DirectoryFor(CoreRelease release)
    {
        DistributionCatalog.Validate(release.Package, 64 * 1024 * 1024);
        return Path.Combine(Root, "versions", release.Package.Sha256.ToLowerInvariant());
    }

    public CoreRelease? Active => ReadPointer("active.json");
    public CoreRelease? Previous => ReadPointer("previous.json");
    public CoreRelease? Pending => ReadPointer("pending.json");

    public bool IsRejected(CoreRelease release)
        => File.Exists(Path.Combine(Root, "rejected", release.Package.Sha256.ToLowerInvariant()));

    private CoreRelease? ReadPointer(string name)
    {
        var path = Path.Combine(Root, name);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new IOException("核心版本记录过大");
        var release = JsonSerializer.Deserialize<CoreRelease>(File.ReadAllBytes(path), DistributionCatalog.JsonOptions);
        if (release is null || release.LoaderAbi != DistributionCatalog.HostAbi) return null;
        DistributionCatalog.RequireVersion(release.Version);
        DistributionCatalog.Validate(release.Package, 64 * 1024 * 1024);
        return release;
    }

    public void Verify(CoreRelease release)
    {
        var root = DirectoryFor(release);
        var packagePath = root + ".zip";
        if (!File.Exists(packagePath) || new FileInfo(packagePath).Length != release.Package.Size
            || !DistributionClient.HashMatches(File.ReadAllBytes(packagePath), release.Package.Sha256))
            throw new IOException("核心安装记录校验失败");
        using var archive = ZipFile.OpenRead(packagePath);
        var manifests = archive.Entries.Where(e => e.FullName == "runtime.json").ToArray();
        if (manifests.Length != 1 || manifests[0].Length > 1024 * 1024) throw new IOException("核心文件清单无效");
        using var manifestStream = manifests[0].Open();
        var pinnedManifest = new byte[checked((int)manifests[0].Length)];
        manifestStream.ReadExactly(pinnedManifest);
        if (manifestStream.ReadByte() != -1) throw new IOException("核心文件清单过大");
        var manifestPath = Path.Combine(root, "runtime.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 1024 * 1024)
            throw new IOException("核心文件清单缺失或无效");
        if (!File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(pinnedManifest)) throw new IOException("核心文件清单已改变");
        var manifest = JsonSerializer.Deserialize<RuntimeFileManifest>(pinnedManifest, DistributionCatalog.JsonOptions)
                       ?? throw new IOException("核心文件清单无效");
        if (manifest.Version != release.Version || manifest.Files is null || manifest.Files.Count > 512
            || !manifest.Files.ContainsKey("QQListener.dll") || !manifest.Files.ContainsKey("QQListener.deps.json"))
            throw new IOException("核心版本或入口无效");
        long total = 0;
        foreach (var (name, hash) in manifest.Files)
        {
            var path = SafePath(root, name);
            if (!File.Exists(path) || HasReparseParent(root, path))
                throw new IOException($"核心文件缺失：{name}");
            total += new FileInfo(path).Length;
            if (total > 256 * 1024 * 1024) throw new IOException("核心文件过大");
            if (!DistributionClient.HashMatches(File.ReadAllBytes(path), hash)) throw new IOException($"核心文件校验失败：{name}");
        }
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => p != "runtime.json").ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(manifest.Files.Keys)) throw new IOException("核心目录包含未知文件");
        if (AssemblyName.GetAssemblyName(Path.Combine(root, "QQListener.dll")).Name != "QQListener")
            throw new IOException("核心程序集名称不匹配");
    }

    public async Task StageAsync(CoreRelease release, byte[] package, CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try { await StageCoreAsync(release, package, ct).ConfigureAwait(false); }
        finally { InstallGate.Release(); }
    }

    private async Task StageCoreAsync(CoreRelease release, byte[] package, CancellationToken ct)
    {
        var destination = DirectoryFor(release);
        if (release.LoaderAbi != DistributionCatalog.HostAbi || package.Length != release.Package.Size
            || !DistributionClient.HashMatches(package, release.Package.Sha256)) throw new IOException("核心安装包校验失败");
        if (Directory.Exists(destination))
        {
            Verify(release);
            await PointerAsync("pending.json", release, ct).ConfigureAwait(false);
            return;
        }
        Directory.CreateDirectory(Path.Combine(Root, "versions"));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            if (archive.Entries.Count > 512) throw new IOException("核心安装包文件过多");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!names.Add(entry.FullName) || entry.Length > 64 * 1024 * 1024
                    || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new IOException("核心安装包包含无效条目");
                var path = SafePath(temporary, entry.FullName.TrimEnd('/'));
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                total += entry.Length;
                if (total > 256 * 1024 * 1024) throw new IOException("核心安装包解压大小超限");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var source = entry.Open();
                await using var target = new FileStream(path, FileMode.CreateNew);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > entry.Length) throw new IOException("核心文件解压大小不匹配");
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                if (written != entry.Length) throw new IOException("核心文件不完整");
            }
            ct.ThrowIfCancellationRequested();
            await DistributionClient.WriteAtomicAsync(destination + ".zip", package, ct).ConfigureAwait(false);
            Directory.Move(temporary, destination);
            try { Verify(release); }
            catch { Directory.Delete(destination, true); throw; }
            await PointerAsync("pending.json", release, ct).ConfigureAwait(false);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public void Activate(CoreRelease release)
    {
        Verify(release);
        var active = Active;
        if (active is not null && active.Package.Sha256 != release.Package.Sha256)
            PointerAsync("previous.json", active, default).GetAwaiter().GetResult();
        PointerAsync("active.json", release, default).GetAwaiter().GetResult();
        File.Delete(Path.Combine(Root, "pending.json"));
    }

    public void Reject(CoreRelease release)
    {
        var directory = Path.Combine(Root, "rejected");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, release.Package.Sha256.ToLowerInvariant()), DateTimeOffset.UtcNow.ToString("O"));
        File.Delete(Path.Combine(Root, "pending.json"));
    }

    private Task PointerAsync(string name, CoreRelease release, CancellationToken ct) =>
        DistributionClient.WriteAtomicAsync(Path.Combine(Root, name), JsonSerializer.SerializeToUtf8Bytes(release), ct);

    internal static string SafePath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':')
            || name.StartsWith('/') || name.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*') || IsDeviceName(p)))
            throw new IOException("核心安装包路径无效");
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("核心安装包路径越界");
        return full;
    }

    private static bool IsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "NUL" or "AUX" or "PRN" or "CONIN$" or "CONOUT$"
               || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }

    private static bool HasReparseParent(string root, string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (current == root) return false;
        }
        return true;
    }
}
