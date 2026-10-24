using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using QQListener.Protocol;

namespace QQListener.Core;

public sealed class PmhqClient : IPmhqClient
{
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<WebSocket>> _connect;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _expectedPid;
    private PmhqHealth? _identity;
    private WebSocket? _socket;
    private readonly Func<bool> _isLocalSignerReady;
    private int _disposed;
    public bool IsConnected => Volatile.Read(ref _socket)?.State == WebSocketState.Open;

    public PmhqClient(Uri endpoint, string apiToken, int expectedPid,
        HttpMessageHandler? handler = null, Func<CancellationToken, Task<WebSocket>>? connect = null,
        Func<bool>? isLocalSignerReady = null)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != "http" || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length > 0 || endpoint.UserInfo.Length > 0 || expectedPid <= 0)
            throw new ArgumentException("PMHQ 仅允许连接本机已选定的 QQ 进程");
        _expectedPid = expectedPid;
        _isLocalSignerReady = isLocalSignerReady ?? (() => true);
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            : new HttpClient(handler);
        _http.BaseAddress = endpoint;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        _connect = connect ?? (async token =>
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiToken);
            try
            {
                await socket.ConnectAsync(new UriBuilder(endpoint) { Scheme = "ws", Path = "/ws" }.Uri, token);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        });
    }

    public async Task<PmhqHealth> GetHealthAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync("health", HttpCompletionOption.ResponseHeadersRead, linked.Token);
        if (!response.IsSuccessStatusCode) throw new IOException($"PMHQ 状态查询失败（HTTP {(int)response.StatusCode}）");
        var health = PmhqProtocol.ReadHealth(await ReadLimitedAsync(response.Content, linked.Token));
        if (health.Pid != 0 && health.Pid != _expectedPid) throw new IOException("PMHQ 连接的 QQ 进程与所选进程不一致");
        var previous = Volatile.Read(ref _identity);
        if (previous is not null && previous != health)
            throw new PmhqAccountChangedException();
        if (health.LoggedIn && Interlocked.CompareExchange(ref _identity, health, null) is { } bound && bound != health)
            throw new PmhqAccountChangedException();
        return health;
    }

    public byte[] Send(int pid, string command, byte[] body) => SendAsync(pid, command, body).GetAwaiter().GetResult();

    private async Task<byte[]> SendAsync(int pid, string command, byte[] body)
    {
        if (pid != _expectedPid) throw new IOException("消息不属于当前 PMHQ 进程");
        if (!_isLocalSignerReady()) throw new IOException("PMHQ 本地签名补丁尚未确认生效，暂不发送请求");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        if (!(await GetHealthAsync(timeout.Token)).LoggedIn) throw new IOException("PMHQ 对应的 QQ 尚未登录");
        var echo = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = JsonContent.Create(new { type = "send", data = new { cmd = command, pb = Convert.ToHexString(body), echo } })
        };
        // HTTP is the documented LLBot fallback. Never retry a possibly delivered send.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new IOException($"PMHQ 请求失败（HTTP {(int)response.StatusCode}）");
        return PmhqProtocol.ReadReply(await ReadLimitedAsync(response.Content, timeout.Token), echo, command);
    }

    public async Task ReceiveAsync(Action<CapturedMessage> receive, Action<PmhqHealth> onHealth,
        CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        WebSocket connected;
        using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token))
        {
            connectDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            connected = await _connect(connectDeadline.Token);
        }
        using var socket = connected;
        Volatile.Write(ref _socket, socket);
        using var abort = linked.Token.Register(socket.Abort);
        try { await ReadMessagesAsync(socket, receive, onHealth, linked.Token); }
        finally { Interlocked.CompareExchange(ref _socket, null, socket); }
    }

    private async Task ReadMessagesAsync(WebSocket socket, Action<CapturedMessage> receive,
        Action<PmhqHealth> onHealth, CancellationToken token)
    {
        var health = await GetHealthAsync(token);
        onHealth(health);
        var buffer = new byte[64 * 1024];
        while (!token.IsCancellationRequested)
        {
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text || frame.Length + result.Count > PmhqProtocol.MaxJsonBytes)
                    throw new IOException("PMHQ WebSocket 帧类型或大小无效");
                frame.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            health = Volatile.Read(ref _identity) ?? health;
            var packet = PmhqProtocol.ReadPush(frame.ToArray(), health.Uin);
            if (packet is null) continue;
            // Verify account identity at the message boundary as well as on the heartbeat.
            health = await GetHealthAsync(token);
            if (!health.LoggedIn) continue;
            onHealth(health);
            var message = SsoDecode.DecodeMessagePush(packet with { Uin = health.Uin });
            if (message is null) continue;
            message.SourcePid = _expectedPid;
            if (!message.IsGroup && message.AccountUid.Length == 0) message.AccountUid = health.Uid;
            receive(message);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > PmhqProtocol.MaxJsonBytes) throw new IOException("PMHQ 响应超过大小限制");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + read > PmhqProtocol.MaxJsonBytes) throw new IOException("PMHQ 响应超过大小限制");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { Volatile.Read(ref _socket)?.Abort(); }
        catch (ObjectDisposedException) { }
        _http.Dispose();
        // Callers may still be unwinding I/O callbacks; do not dispose their token source underneath them.
    }
}

public sealed class PmhqAccountChangedException() : IOException("PMHQ 账号或登录状态已变化，请重启 QQ 后重新连接");
