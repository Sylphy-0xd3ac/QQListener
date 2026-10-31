using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using QQListener.Updates;

if (args.Length == 3 && args[0] == "--pmhq-fixture")
{
    var fixtureRoot = Path.GetFullPath(args[1]);
    var release = JsonSerializer.Deserialize<PmhqRelease>(File.ReadAllBytes(Path.Combine(fixtureRoot,
        "Distribution/pmhq/9.0.6-local-pipe.3.json")), DistributionCatalog.JsonOptions)!;
    DistributionCatalog.Validate(release);
    var archiveBytes = await new DistributionClient().AssetAsync(release.Archive, null, default);
    using var archive = new ZipArchive(new MemoryStream(archiveBytes));
    var entry = archive.Entries.Single(e => e.FullName == "pmhq.dll");
    if (entry.Length != release.OriginalSize) throw new IOException("Unexpected upstream DLL size.");
    using var source = entry.Open();
    var original = new byte[release.OriginalSize];
    source.ReadExactly(original);
    if (source.ReadByte() != -1 || !DistributionClient.HashMatches(original, release.OriginalSha256))
        throw new IOException("Unexpected upstream DLL hash.");
    var patch = File.ReadAllBytes(Path.Combine(fixtureRoot, "Assets/PMHQ/win-x64/pmhq-v" + release.RuntimeVersion + ".patch"));
    if (patch.Length != release.Patch.Size || !DistributionClient.HashMatches(patch, release.Patch.Sha256))
        throw new IOException("Unexpected patch hash.");
    var result = QQListener.Core.BinaryDelta.Apply(original, patch);
    if (result.Length != release.OutputSize || !DistributionClient.HashMatches(result, release.OutputSha256))
        throw new IOException("Unexpected reconstructed DLL hash.");
    using (var image = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(result)))
    {
        var header = image.PEHeaders.PEHeader ?? throw new IOException("Not a PE image.");
        var expected = (header.SizeOfHeaders + header.SectionAlignment - 1) & -header.SectionAlignment;
        foreach (var section in image.PEHeaders.SectionHeaders)
        {
            if (section.VirtualAddress != expected) throw new IOException("Non-adjacent PE sections violate the Windows image format.");
            expected = (section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData)
                        + header.SectionAlignment - 1) & -header.SectionAlignment;
        }
        if (expected != header.SizeOfImage) throw new IOException("PE image size mismatch.");
        using var upstream = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(original));
        var upstreamIat = upstream.PEHeaders.PEHeader!.ImportAddressTableDirectory;
        if (header.ImportAddressTableDirectory.RelativeVirtualAddress != upstreamIat.RelativeVirtualAddress
            || header.ImportAddressTableDirectory.Size != upstreamIat.Size)
            throw new IOException("The pinned original IAT directory must be preserved.");
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, "upstream.dll"), original);
    var legacyPatch = File.ReadAllBytes(Path.Combine(fixtureRoot, "Assets/PMHQ/win-x64/pmhq-v9.0.6-local-pipe.1.patch"));
    File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!, "legacy.dll"), QQListener.Core.BinaryDelta.Apply(original, legacyPatch));
    File.WriteAllBytes(args[2], result);
    Console.WriteLine("Pinned PMHQ native fixture verified: " + release.OutputSha256);
    return;
}

if (args.Length != 5) throw new ArgumentException("Usage: PackageBuilder <root> <core-output> <loader-output> <version> <build-number>");
var root = Path.GetFullPath(args[0]);
var coreOutput = Path.GetFullPath(args[1]);
var loaderOutput = Path.GetFullPath(args[2]);
var version = args[3];
var build = long.Parse(args[4]);
var output = Path.Combine(root, "cipx");
Directory.CreateDirectory(output);
var temporary = Path.Combine(Path.GetTempPath(), "qqlistener-package-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var core = Path.Combine(temporary, "core");
    Directory.CreateDirectory(core);
    foreach (var file in Directory.EnumerateFiles(coreOutput, "*", SearchOption.AllDirectories))
    {
        var relative = Path.GetRelativePath(coreOutput, file).Replace('\\', '/');
        if (relative.StartsWith("Bootstrap/", StringComparison.Ordinal) || relative.Contains("QQListener.Loader")
            || relative.EndsWith(".pdb") || relative is "manifest.yml" or "core-seed.json" or "QQListener.Core.zip") continue;
        var target = Path.Combine(core, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
    var files = Directory.EnumerateFiles(core, "*", SearchOption.AllDirectories).ToDictionary(
        p => Path.GetRelativePath(core, p).Replace('\\', '/'), Hash);
    File.WriteAllBytes(Path.Combine(core, "runtime.json"), JsonSerializer.SerializeToUtf8Bytes(new RuntimeFileManifest(version, files)));
    var coreZip = Path.Combine(output, "QQListener.Core.zip");
    Zip(core, coreZip);
    var coreHash = Hash(coreZip);
    var assetName = $"QQListener.Core-{coreHash}.zip";
    var release = new CoreRelease(version, DistributionCatalog.HostAbi, new DistributionAsset(
        "https://github.com/Sylphy-0xd3ac/QQListener/releases/download/runtime-builds/" + assetName,
        coreHash, checked((int)new FileInfo(coreZip).Length)), build);
    File.WriteAllBytes(Path.Combine(output, "core-release.json"), JsonSerializer.SerializeToUtf8Bytes(release, DistributionCatalog.JsonOptions));
    var shell = Path.Combine(temporary, "loader");
    Directory.CreateDirectory(shell);
    foreach (var name in new[] { "QQListener.Loader.dll", "QQListener.Loader.deps.json" })
        File.Copy(Path.Combine(loaderOutput, name), Path.Combine(shell, name));
    foreach (var name in new[] { "manifest.yml", "README.md", "CHANGELOG.md", "LICENSE", "icon.png" })
        File.Copy(Path.Combine(root, name), Path.Combine(shell, name));
    Zip(shell, Path.Combine(output, "QQListener.Download.cipx"));
    File.Copy(coreZip, Path.Combine(shell, "QQListener.Core.zip"));
    File.Copy(Path.Combine(output, "core-release.json"), Path.Combine(shell, "core-seed.json"));
    Zip(shell, Path.Combine(output, "QQListener.cipx"));
    var hashes = new Dictionary<string, string>();
    foreach (var name in new[] { "QQListener.cipx", "QQListener.Download.cipx" })
    {
        using var input = File.OpenRead(Path.Combine(output, name));
        hashes[name] = Convert.ToHexString(MD5.HashData(input)).ToLowerInvariant();
    }
    File.WriteAllText(Path.Combine(output, "checksums.md"), "<!-- CLASSISLAND_PKG_MD5 " + JsonSerializer.Serialize(hashes) + " -->\n");
    // Exercise the same install path as the production loader, including payload hashes and entry metadata.
    var store = new RuntimeStore(Path.Combine(temporary, "verify"));
    await store.StageAsync(release, File.ReadAllBytes(coreZip), default);
    store.Verify(release);
    Console.WriteLine($"Built bundled/download CIPX and core {version}, build {build}, SHA256 {coreHash}");
}
finally { Directory.Delete(temporary, true); }

static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}
static void Zip(string source, string path)
{
    if (File.Exists(path)) File.Delete(path);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var entry = archive.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var target = entry.Open();
        using var input = File.OpenRead(file);
        input.CopyTo(target);
    }
}
