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
                try { return _memory.Read(_moduleBase + _bundle.Layout.InitializedRva, 1)[0] != 0; }
                catch (InjectionError) { return false; }
            }
        }
    }

    private readonly Func<PmhqBundle, CancellationToken, Task>? _prepare;
    public PmhqHost(PmhqBundle bundle, Func<PmhqBundle, CancellationToken, Task>? prepare = null)
    { _bundle = bundle; _prepare = prepare; }
    public Task PrepareAsync(string? proxy, CancellationToken ct) =>
        _prepare is null ? _bundle.PrepareAsync(proxy, ct) : _prepare(_bundle, ct);

    public static bool HasResidentHook(PmhqBundle bundle, int pid, long startUtcTicks)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var process = new WindowsProcess(pid);
        return bundle.HasBlockingResidentHook(pid, startUtcTicks, HasNativeEvidence(process, pid, includeOtherEngines: false));
    }

    private static bool HasNativeEvidence(WindowsProcess process, int pid, bool includeOtherEngines = true) => OperatingSystem.IsWindows()
        && (process.HasModule("pmhq.dll") || HookPipeProbe.Exists("pmhq_cmd_" + pid)
            || includeOtherEngines && HookPipeProbe.Exists(PipeNames.Recv(pid)));

    private string CurrentAccount()
    {
        lock (_gate)
        {
            if (!OperatingSystem.IsWindows() || _memory is null || _moduleBase == 0 || _qq?.HasExited != false) return "";
            // v9.0.6 LazyLock<Mutex<String>>: the DLL's last observed UIN, not a live login query.
            // Read stable snapshots; never trust a pointer while the native writer owns its lock.
            var address = _moduleBase + _bundle.Layout.AccountRva;
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
        try
        {
            _qq = Process.GetProcessById(qqPid);
            var started = _qq.StartTime.ToUniversalTime().Ticks;
            _memory = new WindowsProcess(qqPid);
            if (_bundle.HasBlockingResidentHook(qqPid, started, HasNativeEvidence(_memory, qqPid)))
                throw new IOException("QQ 中已有消息钩子，请退出并重启 QQ 后重新连接 PMHQ");
            Client = new PmhqLocalClient(qqPid, () => NativeInitialized, currentAccount: CurrentAccount);
            // Recheck directly before injection; resource validation can take time.
            if (!WindowsQqProcessSource.IsStillMainQqProcess(qqPid) || _qq.HasExited
                || _qq.StartTime.ToUniversalTime().Ticks != started)
                throw new IOException("QQ 进程已变化，请重新检查");
            // PMHQ uses Rust static TLS. Only the system loader may initialize this module.
            try
            {
                _moduleBase = _memory.LoadLibraryFile(Path.Combine(_bundle.BinaryDirectory, "pmhq.dll"));
                _bundle.RememberResidentHook(qqPid, started);
            }
            catch (RemoteCallTimeoutException)
            {
                _bundle.RememberResidentHook(qqPid, started, uncertain: true);
                throw;
            }
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
