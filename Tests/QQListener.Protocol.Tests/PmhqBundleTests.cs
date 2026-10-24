using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using QQListener.Core;

namespace QQListener.Protocol.Tests;

public sealed class PmhqBundleTests
{
    [Fact]
    public void PluginEmbedsOnlyThePinnedDelta()
    {
        var assembly = typeof(PmhqBundle).Assembly;
        Assert.DoesNotContain("QQListener.PMHQ.pmhq.dll", assembly.GetManifestResourceNames());
        using var source = assembly.GetManifestResourceStream("QQListener.PMHQ.local-pipe.patch")!;
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        Assert.Equal(41_788, bytes.Length);
        Assert.Equal(PmhqBundle.PatchHash, Hash(bytes.ToArray()));
    }

    [Fact]
    public async Task InstallsFromPinnedUrlThenRepairsOfflineWithoutExtractingLauncher()
    {
        using var fixture = new Fixture();
        var bundle = fixture.Bundle();
        await bundle.PrepareAsync("https://proxy.example/");
        Assert.Equal("https://proxy.example/" + PmhqBundle.DownloadUrl, fixture.RequestUri);
        Assert.Equal(fixture.Patched, File.ReadAllBytes(bundle.BinaryPath));
        Assert.False(File.Exists(Path.Combine(bundle.BinaryDirectory, "pmhq-win-x64.exe")));
        File.WriteAllText(bundle.ConfigurationPath, "keep-user-configuration");
        File.WriteAllText(bundle.BinaryPath, "damaged");
        await bundle.PrepareAsync();
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(fixture.Patched, File.ReadAllBytes(bundle.BinaryPath));
        Assert.Equal("keep-user-configuration", File.ReadAllText(bundle.ConfigurationPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        bundle.VerifyInstalled();
    }

    [Fact]
    public async Task ExistingReviewedDllWorksOfflineAndConcurrentInstallsShareTheCache()
    {
        using var fixture = new Fixture();
        var bundle = fixture.Bundle();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fixture.Bundle().PrepareAsync()));
        Assert.Equal(1, fixture.Requests);
        File.Delete(bundle.OriginalPath);
        await fixture.Bundle().PrepareAsync();
        Assert.Equal(1, fixture.Requests);
        Assert.Equal(fixture.Patched, File.ReadAllBytes(bundle.BinaryPath));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("original")]
    [InlineData("patch")]
    [InlineData("result")]
    public async Task EveryHashIsCheckedBeforePublishingTheDll(string stage)
    {
        using var fixture = new Fixture();
        var badHash = new string('0', 64);
        var spec = stage switch
        {
            "archive" => fixture.Spec with { ArchiveHash = badHash },
            "original" => fixture.Spec with { OriginalHash = badHash },
            "patch" => fixture.Spec with { PatchHash = badHash },
            _ => fixture.Spec with { DllHash = badHash }
        };
        var bundle = fixture.Bundle(spec);
        Directory.CreateDirectory(bundle.BinaryDirectory);
        File.WriteAllText(bundle.BinaryPath, "previous-file");
        await Assert.ThrowsAsync<IOException>(() => bundle.PrepareAsync());
        Assert.Equal("previous-file", File.ReadAllText(bundle.BinaryPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptOriginalCacheIsDownloadedAgain()
    {
        using var fixture = new Fixture();
        var bundle = fixture.Bundle();
        await bundle.PrepareAsync();
        File.WriteAllText(bundle.OriginalPath, "corrupt");
        File.Delete(bundle.BinaryPath);
        await bundle.PrepareAsync();
        Assert.Equal(2, fixture.Requests);
        Assert.Equal(fixture.Patched, File.ReadAllBytes(bundle.BinaryPath));
    }

    [Fact]
    public async Task CancellationDuringDownloadDoesNotPublishAndCanRetry()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        fixture.Reply = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        };
        var bundle = fixture.Bundle();
        var install = bundle.PrepareAsync(ct: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.False(File.Exists(bundle.BinaryPath));
        Assert.False(File.Exists(bundle.OriginalPath));
        fixture.Reply = null;
        await bundle.PrepareAsync();
        bundle.VerifyInstalled();
    }

    [Fact]
    public async Task CancellationAfterResponseHeadersStopsBodyRead()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reply = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new WaitingStream(entered)) });
        using var cancellation = new CancellationTokenSource();
        var bundle = fixture.Bundle();
        var install = bundle.PrepareAsync(ct: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.False(File.Exists(bundle.BinaryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrOversizedResponsesAreRejected(bool oversized)
    {
        using var fixture = new Fixture();
        fixture.Reply = (_, _) =>
        {
            var response = new HttpResponseMessage(oversized ? HttpStatusCode.OK : HttpStatusCode.NotFound)
                { Content = new ByteArrayContent([]) };
            if (oversized) response.Content.Headers.ContentLength = 33 * 1024 * 1024;
            return Task.FromResult(response);
        };
        var bundle = fixture.Bundle();
        await Assert.ThrowsAnyAsync<Exception>(() => bundle.PrepareAsync());
        Assert.False(File.Exists(bundle.BinaryPath));
    }

    [Fact]
    public void DeltaPreservesBaseAndRejectsMalformedRanges()
    {
        var original = "abcdef"u8.ToArray();
        var delta = Delta(original.Length, 8, (2, "XY"u8.ToArray()), (6, "!!"u8.ToArray()));
        Assert.Equal("abXYef!!"u8.ToArray(), BinaryDelta.Apply(original, delta));
        Assert.Equal("abcdef"u8.ToArray(), original);
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(original, delta[..^1]));
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(original, [.. delta, 0]));
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(original, Delta(6, 8, (7, new byte[2]))));
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(original, Delta(6, 8, (2, new byte[2]), (3, new byte[1]))));
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(original, Delta(7, 8)));
        Assert.Throws<InvalidDataException>(() => BinaryDelta.Apply(original, Delta(6, int.MaxValue)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Delta(int originalLength, int targetLength, params (int Offset, byte[] Data)[] ranges)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write("QQLDIFF1"u8);
        writer.Write(originalLength);
        writer.Write(targetLength);
        writer.Write(ranges.Length);
        foreach (var (offset, data) in ranges) { writer.Write(offset); writer.Write(data.Length); writer.Write(data); }
        return bytes.ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "qql-patch-test-" + Guid.NewGuid().ToString("N"));
        public readonly byte[] Original = "abcde"u8.ToArray();
        public readonly byte[] Patched = "abXYe!!"u8.ToArray();
        private readonly byte[] _patch = Delta(5, 7, (2, "XY"u8.ToArray()), (5, "!!"u8.ToArray()));
        private readonly byte[] _archive;
        private readonly HttpClient _http;
        public readonly PmhqBundle.Distribution Spec;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Reply;
        public int Requests;
        public string? RequestUri;

        public Fixture()
        {
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            {
                using (var entry = archive.CreateEntry("pmhq.dll").Open()) entry.Write(Original);
                using (var launcher = archive.CreateEntry("pmhq-win-x64.exe").Open()) launcher.Write("never extract"u8);
            }
            _archive = bytes.ToArray();
            Spec = new(Hash(_archive), Hash(Original), Original.Length, Hash(Patched), Patched.Length, Hash(_patch));
            _http = new HttpClient(new Handler(async (request, ct) =>
            {
                Interlocked.Increment(ref Requests);
                RequestUri = request.RequestUri!.AbsoluteUri;
                return Reply is null ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_archive) }
                    : await Reply(request, ct);
            }));
        }
        public PmhqBundle Bundle(PmhqBundle.Distribution? spec = null) => new(Root, _http, spec ?? Spec, () => _patch);
        public void Dispose() { _http.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class WaitingStream(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
