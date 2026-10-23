using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public sealed class AttachmentDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qq-attachments-" + Guid.NewGuid());

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response());
    }

    private static HttpClient Client(Func<HttpContent> content) => new(new Handler(() =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = content() }));

    [Fact]
    public async Task DownloadKeepsContentAndNeverOverwritesAnExistingAttachment()
    {
        using var client = Client(() => new ByteArrayContent([1, 2, 3]));
        var downloader = new MessageAttachmentDownloader(client);
        var first = await downloader.DownloadAsync("https://example.invalid/a", _directory, "../任务.pdf");
        var second = await downloader.DownloadAsync("https://example.invalid/a", _directory, "../任务.pdf");
        Assert.Equal(_directory, Path.GetDirectoryName(first));
        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(first));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.part"));
    }

    [Theory]
    [InlineData("../../outside.pdf")]
    [InlineData("C:\\Windows\\outside.pdf")]
    [InlineData("CON")]
    [InlineData("..")]
    [InlineData("bad:name?.pdf")]
    public void FilenameIsPortableAndStaysInsideDownloadDirectory(string name)
    {
        var safe = MessageAttachmentDownloader.SafeName(name);
        Assert.DoesNotContain('/', safe);
        Assert.DoesNotContain('\\', safe);
        Assert.DoesNotContain(':', safe);
        Assert.DoesNotContain('?', safe);
        Assert.NotEqual("CON", safe);
        Assert.False(safe.EndsWith('.'));
    }

    [Fact]
    public async Task ImageWithoutFilenameGetsItsMediaExtension()
    {
        using var client = Client(() =>
        {
            var content = new ByteArrayContent([1]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return content;
        });
        var path = await new MessageAttachmentDownloader(client).DownloadAsync(
            "https://example.invalid/a", _directory, "图片");
        Assert.EndsWith(".png", path);
    }

    [Theory]
    [InlineData("file:///tmp/private")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task UnsupportedAddressesDoNotTouchDisk(string address)
    {
        using var client = Client(() => throw new Exception("Should not make a request"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MessageAttachmentDownloader(client).DownloadAsync(address, _directory, "file"));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task OversizeHeaderDoesNotCreateAFile()
    {
        using var client = Client(() => new ByteArrayContent([1, 2, 3]));
        await Assert.ThrowsAsync<IOException>(() => new MessageAttachmentDownloader(client)
            .DownloadAsync("https://example.invalid/a", _directory, "file", maxBytes: 2));
        Assert.False(Directory.Exists(_directory));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task ChunkedOversizeDownloadCleansUpPartialFile()
    {
        using var client = Client(() => new StreamContent(new NonSeekableStream([1, 2, 3])));
        await Assert.ThrowsAsync<IOException>(() => new MessageAttachmentDownloader(client)
            .DownloadAsync("https://example.invalid/a", _directory, "file", maxBytes: 2));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<double>
    {
        public void Report(double value) => source.Cancel();
    }

    [Fact]
    public async Task CancelDuringDownloadDoesNotPublishPartialContent()
    {
        using var client = Client(() => new ByteArrayContent(new byte[131072]));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MessageAttachmentDownloader(client)
            .DownloadAsync("https://example.invalid/a", _directory, "file",
                new CancelProgress(cancellation), cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
