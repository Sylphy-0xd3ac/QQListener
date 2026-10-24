using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using QQListener.Core;
using QQListener.Services;

namespace QQListener.Protocol.Tests;

public sealed class PmhqClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handle(request);
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private const string Health = """{"qq_pid":42,"uin":"10000","uid":"u_self"}""";

    [Fact]
    public void NoCommandIsSentUntilTheLocalPatchIsConfirmed()
    {
        var calls = 0;
        using var client = new PmhqClient(new Uri("http://127.0.0.1:13941/"), "test", 42,
            new Handler(_ => { calls++; return Task.FromResult(Response(Health)); }),
            isLocalSignerReady: () => false);
        Assert.Throws<IOException>(() => client.Send(42, "test", []));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RuntimeQueueIsBoundedAndDisablingDeliveryDoesNotAccumulateMessages()
    {
        var enabled = true;
        using var source = new NativeQqMessageSource(() => [], _ => throw new InvalidOperationException(),
            isEnabled: () => enabled);
        for (var i = 0; i < 1000; ++i)
            source.Accept(new CapturedMessage { Scene = "group", SourcePid = 42, SenderId = "1001", PeerId = "123456",
                RawSeq = (ulong)i + 1, Segments = [new Segment { Type = "text", Text = i.ToString() }] });
        Assert.Equal(500, (await source.PollAsync(false, default)).AcceptedEvents.Count);
        enabled = false;
        source.Accept(new CapturedMessage { Scene = "group", Segments = [new Segment { Type = "text", Text = "disabled" }] });
        enabled = true;
        Assert.Empty((await source.PollAsync(false, default)).AcceptedEvents);
    }

    [Fact]
    public void CommandsUseAuthenticatedHttpFallbackAndRejectFailedSendsWithoutRetry()
    {
        var sends = 0;
        using var client = new PmhqClient(new Uri("http://127.0.0.1:13941/"), "test-token", 42,
            new Handler(async request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
                if (request.Method == HttpMethod.Get) return Response(Health);
                sends++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var data = json.RootElement.GetProperty("data");
                Assert.Equal("0001ABFF", data.GetProperty("pb").GetString());
                return Response(JsonSerializer.Serialize(new { type = "recv", code = -100,
                    data = new { echo = data.GetProperty("echo").GetString(), cmd = "test", pb = "" } }));
            }));
        Assert.Throws<IOException>(() => client.Send(42, "test", [0, 1, 0xab, 0xff]));
        Assert.Equal(1, sends);
        Assert.Throws<IOException>(() => client.Send(43, "test", []));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task AccountChangesInvalidateAnExistingClient()
    {
        var health = Health;
        using var client = new PmhqClient(new Uri("http://127.0.0.1:13941/"), "test", 42,
            new Handler(_ => Task.FromResult(Response(health))));
        Assert.True((await client.GetHealthAsync(default)).LoggedIn);
        health = """{"qq_pid":42,"uin":"20000","uid":"u_other"}""";
        await Assert.ThrowsAsync<PmhqAccountChangedException>(() => client.GetHealthAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FragmentedPmhqPushReachesTheExistingNotificationSourceWithReplyRoutes(bool initiallyPending)
    {
        var head = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, Pb.Cat(Pb.VarintField(1, 123456), Pb.LenField(7, "测试群"))));
        var content = Pb.Cat(Pb.VarintField(1, 82), Pb.VarintField(5, 6243));
        var body = Pb.LenField(1, Pb.Rich(Pb.TextElem("实验引擎测试")));
        var push = Pb.LenField(1, Pb.Cat(Pb.LenField(1, head), Pb.LenField(2, content), Pb.LenField(3, body)));
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "recv", data = new { cmd = "trpc.msg.olpush.OlPushService.MsgPush", pb = Convert.ToHexString(push) } }));
        var healthCalls = 0;
        using var source = new NativeQqMessageSource(() => [], _ => throw new InvalidOperationException());
        using var client = new PmhqClient(new Uri("http://127.0.0.1:13941/"), "test", 42,
            new Handler(_ => Task.FromResult(Response(initiallyPending && Interlocked.Increment(ref healthCalls) == 1
                ? """{"qq_pid":null,"uin":null,"uid":null}""" : Health))),
            _ => Task.FromResult<WebSocket>(new FragmentedSocket(json)));
        await client.ReceiveAsync(source.Accept, _ => { }, default);
        var result = await source.PollAsync(false, default);
        var message = Assert.Single(result.AcceptedEvents);
        Assert.Equal("实验引擎测试", message.Body);
        Assert.Equal(42, message.SourcePid);
        Assert.Equal("123456", message.ConversationId);
        Assert.Equal(6243, message.RawSeq);
        Assert.True(message.CanReply);
    }

    private sealed class FragmentedSocket(byte[] payload) : WebSocket
    {
        private int _offset;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (_offset == payload.Length) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            var size = Math.Min(7, payload.Length - _offset);
            payload.AsSpan(_offset, size).CopyTo(buffer.AsSpan());
            _offset += size;
            return Task.FromResult(new WebSocketReceiveResult(size, WebSocketMessageType.Text, _offset == payload.Length));
        }
    }
}
