using QQListener.Pipes;
using QQListener.Protocol;

namespace QQListener.Native;

/// <summary>
/// 把某个 pid 的 recv 管道接起来，解码出 <see cref="CapturedMessage"/> 往外抛。
/// </summary>
public sealed class RecvCapture(
    int pid,
    Action<CapturedMessage> onMessage,
    Func<string, IPipeTransport> transportFactory,
    Action? onConnected = null,
    Action? onDisconnected = null,
    Action<Exception>? onError = null)
{
    private readonly object _gate = new();
    private volatile bool _stopRequested;
    private RecvClient? _client;

    /// <summary>
    /// 请求停止。不保证返回时捕获线程已经退出——它可能仍在解开阻塞的管道读、
    /// 跑最后一段 <c>onDisconnected</c> 回调；调用方如果需要"确认线程已死"，
    /// 得自己 join 跑 <see cref="Run"/> 的那个线程。
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            _client?.Stop();
        }
    }

    /// <summary>
    /// 阻塞运行，直到管道关闭或 <see cref="Stop"/>；调用方负责放到后台线程里跑。
    ///
    /// 这个方法从头到尾都在一个异常边界里：连管道（可能超时/失败）、协议解析
    /// （可能遇到损坏/意外的字节）、上层回调，任何一环出问题都不能让异常冒出
    /// <see cref="Run"/> 本身——调用方是裸 <see cref="Thread"/>，.NET 对裸线程上的
    /// 未处理异常会直接终止宿主进程，因此错误边界必须覆盖整个接收循环。
    ///
    /// _stopRequested 和 _client 的赋值/读取用同一把锁：早前只给 _client 标
    /// volatile 只解决了可见性，没堵住"Stop() 已经调用过，Run() 还是照样去连一次
    /// 管道"的时序——已经 Stop() 过的 capture 不该再花时间真连一次命名管道
    /// （最长等 3 秒）。
    /// </summary>
    public void Run()
    {
        RecvClient? client = null;
        try
        {
            if (_stopRequested) return; // 还没开始连就已经被 Stop 过，不用再去连一次

            var transport = transportFactory(PipeNames.Recv(pid));
            lock (_gate)
            {
                if (_stopRequested)
                {
                    transport.Dispose();
                    return;
                }
                client = new RecvClient(transport);
                _client = client;
            }

            onConnected?.Invoke();
            string loginUin = "";
            client.Run(packet =>
            {
                try
                {
                    var message = SsoDecode.DecodeMessagePush(string.IsNullOrWhiteSpace(packet.Uin)
                        ? packet with { Uin = loginUin } : packet);
                    if (message is null) return;
                    message.SourcePid = pid;
                    onMessage(message);
                }
                catch (Exception ex)
                {
                    // 一个包解析失败（协议数据损坏、意外的字段编码）不该打断整条 recv
                    // 会话——不接住的话异常会冒出 client.Run，被外层 catch 当成"连接
                    // 失败"处理，导致一次不必要的断线重连；重连后大概率还会收到同一条
                    // 坏消息，形成反复断连。
                    onError?.Invoke(ex);
                }
            }, login => loginUin = login.LoggedIn ? login.Uin : "");
        }
        catch (Exception) when (_stopRequested)
        {
            // 预期的关闭路径：Stop() 靠 dispose 传输打断阻塞中的 Read，这必然会
            // 从 Read 抛出异常（ObjectDisposedException/IOException 等，具体类型
            // 看传输实现）。这不是错误。
        }
        catch (Exception ex)
        {
            // 非预期异常——连接失败、协议解析遇到损坏数据、onMessage 回调本身抛出。
            // 不能吞掉也不能让它逃出线程：报告给调用方（通常用来更新诊断状态），
            // 然后让这次捕获尝试优雅结束；下一轮 Reconcile 会重新尝试连接。
            onError?.Invoke(ex);
        }
        finally
        {
            // 本方法自称"从头到尾都在一个异常边界里"，finally 却在这个边界之外——
            // client.Stop()（→ transport.Dispose()）或 onDisconnected 任何一处抛出，
            // 都会逃出裸 Thread。分别兜住，不让收尾阶段本身破坏这个方法的核心承诺。
            try { client?.Stop(); }
            catch (Exception ex) { onError?.Invoke(ex); }

            try { onDisconnected?.Invoke(); }
            catch (Exception ex) { onError?.Invoke(ex); }
        }
    }
}
