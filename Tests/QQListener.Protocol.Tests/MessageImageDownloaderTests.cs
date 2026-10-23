using System.Net;
using System.Net.Http;
using QQListener.Models;
using QQListener.Oidb;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public sealed class MessageImageDownloaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qq-image-" + Guid.NewGuid());
    private static readonly byte[] ImageBytes = [137, 80, 78, 71, 13, 10, 26, 10];
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
    private static HttpResponseMessage Image() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes) };
    private static MessageContentBlock Block(string url, bool group = true, int pid = 7) =>
        new("image", "图片", url, SourcePid: pid, IsGroup: group);

    [Theory]
    [InlineData(1407, 20, false)]
    [InlineData(1406, 10, true)]
    public async Task InvalidRkeyJsonRefreshesTheImagesOwnScopeOnceWithoutSavingTheError(int appid, int type, bool group)
    {
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.Query);
            return requests.Count == 1
                ? new(HttpStatusCode.OK) { Content = new StringContent("{\"retcode\":-5503010,\"retmsg\":\"invalid rkey SECRET\"}") }
                : Image();
        }));
        var fetches = 0;
        var downloader = new MessageImageDownloader(new(client), pid =>
        {
            Assert.Equal(7, pid);
            fetches++;
            return [new("&rkey=NEW", type, 3600), new("WRONG", type == 10 ? 20 : 10, 3600)];
        });
        var path = await downloader.DownloadAsync(Block($"https://multimedia.nt.qq.com.cn/download?appid={appid}&fileid=FID&rkey=OLD", group), _directory);
        Assert.Equal(ImageBytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, fetches);
        Assert.Equal(2, requests.Count);
        Assert.Contains("rkey=NEW", requests[1]);
        Assert.DoesNotContain("OLD", requests[1]);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RepeatedInvalidJsonGetsOnlyOneRetryAndNeverLeaksTokensInTheDisplayedError()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"retcode\":-5503010,\"retmsg\":\"SECRET\"}") };
        }));
        var downloader = new MessageImageDownloader(new(client), _ => [new("SECRET", 20, 3600)]);
        var error = await Assert.ThrowsAsync<QqImageDownloadException>(() => downloader.DownloadAsync(
            Block("https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=FID&rkey=SECRET"), _directory));
        Assert.Equal(2, calls);
        Assert.DoesNotContain("SECRET", error.Message);
        Assert.Contains("-5503010", error.Message);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Http403AlsoRefreshesAnUnsignedGroupImageBeforeRetrying()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return new(HttpStatusCode.Forbidden);
            Assert.Contains("rkey=GROUP", request.RequestUri!.Query);
            return Image();
        }));
        var downloader = new MessageImageDownloader(new(client), _ => [new("GROUP", 20, 3600)]);
        await downloader.DownloadAsync(Block("https://multimedia.nt.qq.com.cn/download?appid=1407&fileid=FID"), _directory);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnrelatedHostsNeverReceiveQqCredentials()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden)));
        var downloader = new MessageImageDownloader(new(client), _ => throw new Exception("must not fetch"));
        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            Block("https://untrusted.invalid/download?appid=1407"), _directory));
    }

    [Fact]
    public async Task NonNumericErrorCodeIsStillRejectedWithoutLeakingTheErrorBody()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"retcode\":\"SECRET\"}")
        }));
        var downloader = new MessageImageDownloader(new(client), _ => throw new Exception("must not fetch"));
        var exception = await Assert.ThrowsAsync<QqImageDownloadException>(() => downloader.DownloadAsync(
            Block("https://example.invalid/image"), _directory));
        Assert.DoesNotContain("SECRET", exception.Message);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task LegacyGroupImageUsesHttpsWithoutFetchingRkeys()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            return Image();
        }));
        var downloader = new MessageImageDownloader(new(client), _ => throw new Exception("must not fetch"));
        await downloader.DownloadAsync(Block("http://gchat.qpic.cn/gchatpic_new/0/0-0-MD5/0"), _directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
