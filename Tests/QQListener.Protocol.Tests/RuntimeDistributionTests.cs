using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using QQListener.Core;
using QQListener.Updates;
using QQListener.Models;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public sealed class RuntimeDistributionTests
{
    [Fact]
    public async Task UnadaptedUpstreamNeverDownloadsOrReplacesInstalledPmhq()
    {
        using var fixture = new Fixture();
        var native = Path.Combine(fixture.Root, "native");
        var old = PmhqBundle.PinnedRelease with { OutputSha256 = Hash([1, 2, 3]), OutputSize = 3 };
        var installed = new PmhqBundle(native, old);
        Directory.CreateDirectory(installed.BinaryDirectory);
        File.WriteAllBytes(installed.BinaryPath, [1, 2, 3]);
        await installed.PrepareAsync();
        File.WriteAllText(Path.Combine(native, KernelLocator.HookDllName), "installed fixture");
        File.WriteAllText(Path.Combine(native, "version.txt"), "1.0.0");
        var settings = new QqListenerSettings { SettingsDirectory = fixture.Root, KernelEulaAccepted = true };
        new LoaderSettings(Fixture.CatalogUrl, "", false).Save(fixture.Root);
        fixture.PublishPmhq(old);
        fixture.Responses["https://api.github.com/repos/linyuchen/PMHQ/releases/latest"] = "{\"tag_name\":\"v99.0.0\"}"u8.ToArray();
        fixture.Responses["https://api.github.com/repos/SnowLuma/SnowLuma/releases/latest"] =
            "{\"tag_name\":\"1.0.0\",\"assets\":[{\"name\":\"snow-win-x64.zip\",\"browser_download_url\":\"https://github.com/example/snow.zip\"}]}"u8.ToArray();
        var service = new EngineInstallationService(settings, native, fixture.Http, fixture.Client);
        await service.CheckAsync();
        Assert.Contains("尚未适配", service.Pmhq.Detail);
        Assert.Equal("v99.0.0", service.Pmhq.Latest);
        await service.InstallBothAsync();
        Assert.Equal(old.RuntimeVersion, service.Pmhq.Installed);
        Assert.DoesNotContain(fixture.Requests, uri => uri.EndsWith(".zip") || uri.EndsWith(".patch"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(installed.BinaryPath));
    }
    [Fact]
    public void CatalogRequiresSignatureAndExactPmhqCompatibility()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var known = PmhqBundle.PinnedRelease;
        var catalog = new DistributionCatalog(1, [known, known with { RuntimeVersion = "10.0.0", UpstreamVersion = "10.0.0", HostAbi = 2 }], []);
        var json = JsonSerializer.SerializeToUtf8Bytes(catalog);
        var signature = key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var read = DistributionCatalog.Verify(json, signature, key.ExportSubjectPublicKeyInfoPem());
        Assert.Equal(known, read.ForPmhq("v9.0.6"));
        Assert.Null(read.ForPmhq("v9.0.7"));
        Assert.Null(read.ForPmhq("10.0.0"));
        Assert.Equal(known, read.LatestCompatiblePmhq);
        json[^1] ^= 1;
        Assert.Throws<IOException>(() => DistributionCatalog.Verify(json, signature, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public async Task ThinLoaderDownloadsSignedCoreThroughProxyAndThenRunsOffline()
    {
        using var fixture = new Fixture();
        var (release, bytes) = fixture.Package(1);
        fixture.Publish(release, bytes);
        var bootstrap = new RuntimeBootstrap(fixture.Store, fixture.Client);
        var selected = await bootstrap.PrepareAsync(fixture.PluginDirectory, new(Fixture.CatalogUrl, "https://proxy.example", true));
        Assert.Equal(release, selected);
        Assert.All(fixture.Requests, uri => Assert.StartsWith("https://proxy.example/https://", uri));
        fixture.Store.Activate(selected);
        fixture.Offline = true;
        Assert.Equal(release, await bootstrap.PrepareAsync(fixture.PluginDirectory, LoaderSettings.Default));
        fixture.Store.Verify(release);
    }

    [Fact]
    public async Task BundledLoaderSeedsPersistentStorageWithoutNetworkAndDoesNotDowngrade()
    {
        using var fixture = new Fixture();
        var (older, bytes) = fixture.Package(1);
        await fixture.Seed(older, bytes);
        var settings = new LoaderSettings(Fixture.CatalogUrl, "", false);
        var bootstrap = new RuntimeBootstrap(fixture.Store, fixture.Client);
        Assert.Equal(older, await bootstrap.PrepareAsync(fixture.PluginDirectory, settings));
        Assert.Empty(fixture.Requests);
        fixture.Store.Activate(older);
        var (newer, next) = fixture.Package(2);
        await fixture.Store.StageAsync(newer, next, default);
        fixture.Store.Activate(newer);
        Assert.Equal(newer, await bootstrap.PrepareAsync(fixture.PluginDirectory, settings));
        Assert.Equal(older, fixture.Store.Previous);
    }

    [Fact]
    public async Task RejectedOrCorruptCoreFallsBackAndPreservesSettings()
    {
        using var fixture = new Fixture();
        var (old, bytes) = fixture.Package(1);
        await fixture.Store.StageAsync(old, bytes, default);
        fixture.Store.Activate(old);
        var (next, payload) = fixture.Package(2);
        await fixture.Store.StageAsync(next, payload, default);
        fixture.Store.Activate(next);
        File.WriteAllText(Path.Combine(fixture.Root, "settings.json"), "keep");
        fixture.Store.Reject(next);
        var selected = await new RuntimeBootstrap(fixture.Store, fixture.Client).PrepareAsync(fixture.PluginDirectory,
            new(Fixture.CatalogUrl, "", false));
        Assert.Equal(old, selected);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Root, "settings.json")));
        File.AppendAllText(Path.Combine(fixture.Store.DirectoryFor(old), "QQListener.deps.json"), "tamper");
        Assert.Throws<IOException>(() => fixture.Store.Verify(old));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("CON.txt")]
    [InlineData("C:/file.dll")]
    [InlineData("nested/.. /file")]
    [InlineData("QQListener.DLL")]
    public async Task UnsafePayloadNeverBecomesActive(string path)
    {
        using var fixture = new Fixture();
        var (release, bytes) = fixture.Package(1, path);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Store.StageAsync(release, bytes, default));
        Assert.Null(fixture.Store.Active);
        Assert.Null(fixture.Store.Pending);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(fixture.Store.Root, "versions"), "*.tmp"));
    }

    [Fact]
    public async Task ChangingManifestCannotBlessModifiedPayload()
    {
        using var fixture = new Fixture();
        var (release, bytes) = fixture.Package(1);
        await fixture.Store.StageAsync(release, bytes, default);
        var path = fixture.Store.DirectoryFor(release);
        File.WriteAllText(Path.Combine(path, "QQListener.deps.json"), "changed");
        var manifest = JsonSerializer.Deserialize<RuntimeFileManifest>(File.ReadAllBytes(Path.Combine(path, "runtime.json")))!;
        manifest.Files["QQListener.deps.json"] = Hash("changed"u8.ToArray());
        File.WriteAllBytes(Path.Combine(path, "runtime.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        Assert.Throws<IOException>(() => fixture.Store.Verify(release));
    }

    [Fact]
    public async Task ConcurrentStagesPublishOneCompleteVersion()
    {
        using var fixture = new Fixture();
        var (release, bytes) = fixture.Package(1);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Store.StageAsync(release, bytes, default)));
        fixture.Store.Verify(release);
        Assert.Equal(release, fixture.Store.Pending);
    }

    [Fact]
    public async Task BadSignatureOrHashCannotReplaceWorkingCore()
    {
        using var fixture = new Fixture();
        var (old, bytes) = fixture.Package(1);
        await fixture.Store.StageAsync(old, bytes, default);
        fixture.Store.Activate(old);
        var (next, payload) = fixture.Package(2);
        fixture.Publish(next, [1, 2]);
        var bootstrap = new RuntimeBootstrap(fixture.Store, fixture.Client);
        Assert.Equal(old, await bootstrap.PrepareAsync(fixture.PluginDirectory, new(Fixture.CatalogUrl, "", true)));
        fixture.Publish(next, payload);
        fixture.Responses[Fixture.CatalogUrl + ".sig"][0] ^= 1;
        Assert.Equal(old, await bootstrap.PrepareAsync(fixture.PluginDirectory, new(Fixture.CatalogUrl, "", true)));
        Assert.Equal(old, fixture.Store.Active);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public const string CatalogUrl = "https://github.com/example/catalog.json";
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "qql-runtime-test-" + Guid.NewGuid().ToString("N"));
        public string PluginDirectory => Path.Combine(Root, "plugin");
        public RuntimeStore Store { get; }
        public DistributionClient Client { get; }
        public List<string> Requests { get; } = [];
        public Dictionary<string, byte[]> Responses { get; } = [];
        public bool Offline;
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly HttpClient _http;
        public HttpClient Http => _http;
        public Fixture()
        {
            Directory.CreateDirectory(PluginDirectory);
            Store = new(Root);
            _http = new(new Handler(request =>
            {
                var uri = request.RequestUri!.AbsoluteUri;
                Requests.Add(uri);
                if (Offline) throw new HttpRequestException("offline");
                uri = uri.Replace("https://proxy.example/", "");
                return Responses.TryGetValue(uri, out var bytes)
                    ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new(HttpStatusCode.NotFound);
            }));
            Client = new(_http, _key.ExportSubjectPublicKeyInfoPem());
        }
        public (CoreRelease Release, byte[] Bytes) Package(long build, string? extraPath = null)
        {
            var files = new Dictionary<string, byte[]> { ["QQListener.dll"] = File.ReadAllBytes(typeof(PmhqBundle).Assembly.Location),
                ["QQListener.deps.json"] = System.Text.Encoding.UTF8.GetBytes($"{{\"build\":{build}}}") };
            var manifest = new RuntimeFileManifest("2.3.0-beta.2", files.ToDictionary(p => p.Key, p => Hash(p.Value)));
            files["runtime.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (extraPath is not null) files[extraPath] = [1];
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                foreach (var (name, data) in files) { using var entry = zip.CreateEntry(name).Open(); entry.Write(data); }
            var bytes = buffer.ToArray();
            var hash = Hash(bytes);
            return (new("2.3.0-beta.2", 1, new("https://github.com/example/" + hash + ".zip", hash, bytes.Length), build), bytes);
        }
        public void Publish(CoreRelease release, byte[] bytes)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, [], [release]));
            Responses[CatalogUrl] = json;
            Responses[CatalogUrl + ".sig"] = _key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            Responses[release.Package.Url] = bytes;
        }
        public void PublishPmhq(PmhqRelease release)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new DistributionCatalog(1, [release], []));
            Responses[CatalogUrl] = json;
            Responses[CatalogUrl + ".sig"] = _key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        public async Task Seed(CoreRelease release, byte[] bytes)
        {
            await File.WriteAllBytesAsync(Path.Combine(PluginDirectory, "QQListener.Core.zip"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(PluginDirectory, "core-seed.json"), JsonSerializer.SerializeToUtf8Bytes(release));
        }
        public void Dispose() { _http.Dispose(); _key.Dispose(); Directory.Delete(Root, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(action(request));
    }
}
