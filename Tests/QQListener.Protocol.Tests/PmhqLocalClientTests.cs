using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using QQListener.Core;

namespace QQListener.Protocol.Tests;

public sealed class PmhqLocalClientTests
{
    [Theory]
    [InlineData("{\"kind\":\"resp\",\"id\":1,\"code\":-1,\"data\":\"\",\"msg\":\"fixture-private\"}", -1)]
    [InlineData("{\"kind\":\"resp\",\"id\":1,\"code\":0,\"data\":\"0001abff\"}", 0)]
    public void ParsesTheNativeResponseCapturedFromTheOfficialDll(string json, int code)
    {
        var frame = PmhqNativeProtocol.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("resp", frame.Type);
        Assert.Equal(1UL, frame.Id);
        Assert.Equal(code, frame.Code);
        Assert.Equal(code == 0 ? new byte[] { 0, 1, 0xab, 0xff } : [], frame.Body);
    }

    [Theory]
    [InlineData("{\"kind\":\"resp\",\"id\":0,\"code\":0,\"data\":\"00\"}")]
    [InlineData("{\"kind\":\"resp\",\"id\":1,\"data\":\"00\"}")]
    [InlineData("{\"kind\":\"resp\",\"id\":1,\"code\":0,\"data\":\"xx\"}")]
    [InlineData("{\"kind\":\"resp\",\"id\":1,\"code\":0}")]
    [InlineData("{\"kind\":\"resp\",\"id\":1,\"code\":0,\"data\":null}")]
    [InlineData("[]")]
    public void RejectsMalformedNativeResponses(string json) =>
        Assert.Throws<IOException>(() => PmhqNativeProtocol.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public async Task PipeCarriesNumericIdsAndBodiesWithoutAnyStartupCredential()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.WriteAsync("""{"kind":"trace","type":"send","uin":10000,"cmd":"fixture"}""");
        await fixture.Login.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var send = Task.Run(() => fixture.Client.Send(42, "fixture.cmd", [0, 1, 0xab, 0xff]));
        using var request = JsonDocument.Parse(await fixture.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "");
        var root = request.RootElement;
        Assert.Equal("fixture.cmd", root.GetProperty("cmd").GetString());
        Assert.Equal("0001ABFF", root.GetProperty("body").GetString());
        Assert.False(root.TryGetProperty("auth_token", out _));
        Assert.False(root.TryGetProperty("type", out _));
        await fixture.WriteAsync(JsonSerializer.Serialize(new { kind = "resp", id = root.GetProperty("id").GetUInt64(), code = 0, data = "abcd" }));
        Assert.Equal(new byte[] { 0xab, 0xcd }, await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True((await fixture.Client.GetHealthAsync(default)).LoggedIn);
        Assert.Empty((await fixture.Client.GetHealthAsync(default)).Uid);
    }

    [Fact]
    public async Task NativeFailuresDoNotLeakMessagesOrReplayRequests()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.WriteAsync("""{"kind":"trace","type":"recv","uin":10000,"cmd":"other"}""");
        await fixture.Login.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var send = Task.Run(() => fixture.Client.Send(42, "fixture.cmd", [0]));
        using var request = JsonDocument.Parse(await fixture.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "");
        await fixture.WriteAsync(JsonSerializer.Serialize(new { kind = "resp", id = request.RootElement.GetProperty("id").GetUInt64(), code = -1, data = "", msg = "fixture-private" }));
        var error = await Assert.ThrowsAsync<IOException>(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("fixture-private", error.Message);
        Assert.Throws<IOException>(() => fixture.Client.Send(43, "wrong.pid", []));
    }

    [Fact]
    public async Task EnrichmentCanSendWhileTheSamePipeReadsItsReply()
    {
        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture((client, message) =>
        {
            Assert.Equal(42, message.SourcePid);
            Assert.Equal(6243UL, message.RawSeq);
            delivered.TrySetResult(client.Send(42, "fixture.enrichment", [1]));
        });
        await fixture.ConnectAsync();
        var head = Pb.Cat(Pb.VarintField(1, 1001), Pb.LenField(8, Pb.Cat(Pb.VarintField(1, 123456), Pb.LenField(7, "测试群"))));
        var content = Pb.Cat(Pb.VarintField(1, 82), Pb.VarintField(5, 6243));
        var push = Pb.LenField(1, Pb.Cat(Pb.LenField(1, head), Pb.LenField(2, content), Pb.LenField(3, Pb.LenField(1, Pb.Rich(Pb.TextElem("测试"))))));
        var line = JsonSerializer.Serialize(new { kind = "trace", type = "recv", uin = 10000,
            cmd = "trpc.msg.olpush.OlPushService.MsgPush", pb = Convert.ToHexString(push) });
        // Split both the JSON and UTF-8 text across pipe reads.
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        for (var offset = 0; offset < bytes.Length; offset += 7)
            await fixture.Native.WriteAsync(bytes.AsMemory(offset, Math.Min(7, bytes.Length - offset)));
        using var request = JsonDocument.Parse(await fixture.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "");
        await fixture.WriteAsync(JsonSerializer.Serialize(new { kind = "resp", id = request.RootElement.GetProperty("id").GetUInt64(), code = 0, data = "02" }));
        Assert.Equal(new byte[] { 2 }, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequestsQueuedBeforeDisconnectNeverMoveToANewConnection()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.WriteAsync("""{"kind":"trace","type":"send","uin":10000,"cmd":"fixture"}""");
        await fixture.Login.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var paused = new PausedContext();
        var previous = SynchronizationContext.Current;
        Task<byte[]> first;
        Task<byte[]> queued;
        SynchronizationContext.SetSynchronizationContext(paused);
        try
        {
            // Fill the old connection's write buffer while its native peer does not read.
            first = fixture.Client.SendAsync(42, "fixture.blocked", new byte[4 * 1024 * 1024]);
            queued = fixture.Client.SendAsync(42, "must.not.replay", [1]);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.False(first.IsCompleted);
        Assert.False(queued.IsCompleted);
        await fixture.ReconnectAsync();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (!first.IsCompleted || !queued.IsCompleted)
            {
                paused.Drain();
                await Task.Delay(1, deadline.Token);
            }
        }
        Assert.NotNull(await Record.ExceptionAsync(async () => await first));
        await Assert.ThrowsAsync<IOException>(async () => await queued);
        using (var noReplay = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Reader.ReadLineAsync(noReplay.Token));

        // Completion of the old write must not mark the new connection as disconnected.
        var fresh = fixture.Client.SendAsync(42, "fixture.fresh", [2]);
        using var request = JsonDocument.Parse(await fixture.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "");
        Assert.Equal("fixture.fresh", request.RootElement.GetProperty("cmd").GetString());
        await fixture.WriteAsync(JsonSerializer.Serialize(new { kind = "resp", id = request.RootElement.GetProperty("id").GetUInt64(), code = 0, data = "03" }));
        Assert.Equal(new byte[] { 3 }, await fresh.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class PausedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void Drain() { while (_callbacks.TryDequeue(out var item)) item.Callback(item.State); }
    }

    [Fact]
    public async Task ASecondReaderCannotReplaceTheActiveConnection()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.ReceiveAsync(_ => { }, _ => { }, default));
        Assert.True(fixture.Client.IsConnected);
    }

    [Fact]
    public async Task DifferentConnectingProcessIsRejected()
    {
        await using var fixture = new Fixture(peer: 99);
        await fixture.ConnectAsync();
        await Assert.ThrowsAsync<IOException>(async () => await fixture.Receive.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ANewAccountInvalidatesTheLocalSession()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectAsync();
        await fixture.WriteAsync("""{"kind":"trace","type":"recv","uin":10000,"cmd":"other"}""");
        await fixture.Login.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.WriteAsync("""{"kind":"trace","type":"recv","uin":20000,"cmd":"other"}""");
        await Assert.ThrowsAsync<PmhqAccountChangedException>(async () => await fixture.Receive.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-an-account")]
    public async Task UninitializedNativeAccountsAreNotTreatedAsLoggedIn(string value)
    {
        await using var fixture = new Fixture(currentAccount: () => value);
        await fixture.ConnectAsync();
        Assert.False((await fixture.Client.GetHealthAsync(default)).LoggedIn);
        Assert.Throws<IOException>(() => fixture.Client.Send(42, "must.not.send", [0]));
    }

    [Theory]
    [InlineData("20000")]
    [InlineData("")]
    public async Task NativeAccountPreflightRejectsOldRepliesEvenWithoutANewPush(string next)
    {
        var account = "10000";
        await using var fixture = new Fixture(currentAccount: () => account);
        await fixture.ConnectAsync();
        Assert.True((await fixture.Client.GetHealthAsync(default)).LoggedIn);
        account = next;
        await Assert.ThrowsAsync<PmhqAccountChangedException>(async () => await fixture.Client.GetHealthAsync(default));
        Assert.Throws<PmhqAccountChangedException>(() => fixture.Client.Send(42, "must.not.send", [0]));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new(TimeSpan.FromSeconds(15));
        private readonly string _name = "p" + Guid.NewGuid().ToString("N")[..12];
        private readonly Action<PmhqLocalClient, CapturedMessage>? _receive;
        public PmhqLocalClient Client { get; }
        public NamedPipeClientStream Native { get; private set; } = null!;
        public StreamReader Reader { get; private set; } = null!;
        public Task Receive { get; private set; } = null!;
        public TaskCompletionSource Login { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Fixture(Action<PmhqLocalClient, CapturedMessage>? receive = null, int peer = 42, Func<string>? currentAccount = null)
        {
            _receive = receive;
            Client = new PmhqLocalClient(42, () => true,
                () => new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), _ => peer, currentAccount);
            OpenConnection();
        }
        private void OpenConnection()
        {
            Native = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
            Reader = new StreamReader(Native, Encoding.UTF8, leaveOpen: true);
            Receive = Client.ReceiveAsync(message => _receive?.Invoke(Client, message), health => { if (health.LoggedIn) Login.TrySetResult(); }, _cancel.Token);
        }
        public async Task ConnectAsync()
        {
            await Native.ConnectAsync(_cancel.Token);
            while (!Client.IsConnected && !Receive.IsCompleted) await Task.Delay(1, _cancel.Token);
        }
        public async Task ReconnectAsync()
        {
            Native.Dispose();
            await Receive.WaitAsync(TimeSpan.FromSeconds(5));
            Reader.Dispose();
            OpenConnection();
            await ConnectAsync();
        }
        public async Task WriteAsync(string value) { await Native.WriteAsync(Encoding.UTF8.GetBytes(value + "\n"), _cancel.Token); await Native.FlushAsync(_cancel.Token); }
        public async ValueTask DisposeAsync()
        {
            _cancel.Cancel();Client.Dispose();Native.Dispose();Reader.Dispose();
            try { await Receive; } catch (Exception) { }
            _cancel.Dispose();
        }
    }
}
