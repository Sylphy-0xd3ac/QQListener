using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using QQListener.Native;
using QQListener.Pipes;

namespace QQListener.Core;

/// <summary>Loads the verified local DLL reconstructed from the pinned upstream release.</summary>
public sealed class PmhqHost : IPmhqHost
{
    private readonly PmhqBundle _bundle;
    private readonly object _gate = new();
    private Process? _qq;
    private WindowsProcess? _memory;
    private ulong _moduleBase;
    public IPmhqClient? Client { get; private set; }
    public bool HasExited
    {
        get { lock (_gate) return _qq?.HasExited != false; }
    }
    /// <summary>Native initialization flag; it does not confirm a signing call or delivery.</summary>
    public bool NativeInitialized
    {
        get
        {
            lock (_gate)
            {
                if (!OperatingSystem.IsWindows() || _memory is null || _moduleBase == 0 || _qq?.HasExited != false) return false;
                try { return _memory.Read(_moduleBase + 0xf56f1, 1)[0] != 0; }
                catch (InjectionError) { return false; }
            }
        }
    }

    public PmhqHost(PmhqBundle bundle) => _bundle = bundle;
    public Task PrepareAsync(string? proxy, CancellationToken ct) => _bundle.PrepareAsync(proxy, ct);

    private string CurrentAccount()
    {
        lock (_gate)
        {
            if (!OperatingSystem.IsWindows() || _memory is null || _moduleBase == 0 || _qq?.HasExited != false) return "";
            // v9.0.6 LazyLock<Mutex<String>>: the DLL's last observed UIN, not a live login query.
            // Read stable snapshots; never trust a pointer while the native writer owns its lock.
            var address = _moduleBase + 0xf31d8;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var before = _memory.Read(address, 48);
                if (BinaryPrimitives.ReadUInt64LittleEndian(before) != 2) return "";
                if (before[16] != 0) { Thread.Yield(); continue; }
                var pointer = BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(32));
                var length = BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(40));
                if (length == 0) return "";
                if (pointer == 0 || length > 20) throw new IOException("PMHQ QQ 账号状态无效");
                var text = _memory.Read(pointer, (int)length);
                if (!_memory.Read(address, 48).AsSpan().SequenceEqual(before)) continue;
                if (text.Any(value => value is < (byte)'0' or > (byte)'9')) throw new IOException("PMHQ QQ 账号状态无效");
                return Encoding.ASCII.GetString(text);
            }
            throw new IOException("PMHQ QQ 账号正在变化，请稍后重试");
        }
    }

    public void Start(int qqPid)
    {
        if (_qq is not null) throw new InvalidOperationException("PMHQ 已启动");
        if (!OperatingSystem.IsWindows() || !WindowsQqProcessSource.IsStillMainQqProcess(qqPid))
            throw new IOException("所选 QQ 主进程已退出或发生变化，请重新检查");
        _bundle.VerifyInstalled();
        var external = Process.GetProcessesByName("pmhq-win-x64");
        try
        {
            if (external.Length != 0) throw new IOException("发现其他 PMHQ 实例，请先关闭；插件不会接管外部 PMHQ");
        }
        finally { foreach (var process in external) process.Dispose(); }
        try
        {
            _qq = Process.GetProcessById(qqPid);
            var started = _qq.StartTime.ToUniversalTime().Ticks;
            if (_bundle.HasResidentHook(qqPid, started) || HookPipeProbe.Exists("pmhq_sign")
                || HookPipeProbe.Exists("pmhq_cmd_" + qqPid) || HookPipeProbe.Exists(PipeNames.Recv(qqPid)))
                throw new IOException("QQ 中已有消息钩子，请退出并重启 QQ 后重新连接 PMHQ");
            Client = new PmhqLocalClient(qqPid, () => NativeInitialized, currentAccount: CurrentAccount);
            _bundle.RememberResidentHook(qqPid, started);
            // Recheck directly before injection; resource validation can take time.
            if (!WindowsQqProcessSource.IsStillMainQqProcess(qqPid) || _qq.HasExited
                || _qq.StartTime.ToUniversalTime().Ticks != started)
                throw new IOException("QQ 进程已变化，请重新检查");
            _memory = new WindowsProcess(qqPid);
            // PMHQ uses Rust static TLS. Only the system loader may initialize this module.
            _moduleBase = _memory.LoadLibraryFile(Path.Combine(_bundle.BinaryDirectory, "pmhq.dll"));
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        Client?.Dispose();
        lock (_gate)
        {
            if (OperatingSystem.IsWindows()) _memory?.Dispose();
            _memory = null;
            _qq?.Dispose(); _qq = null;
            // Live callbacks remain until QQ exits. EngineSwitchGuard requires that restart.
            // Do not FreeLibrary: native callbacks remain registered until QQ exits.
            _moduleBase = 0;
        }
    }
}
