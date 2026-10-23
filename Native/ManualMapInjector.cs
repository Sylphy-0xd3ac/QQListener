using System.Runtime.Versioning;

namespace QQListener.Native;

/// <summary>
/// 手动映射的编排层：把 <see cref="Injector"/> 的纯算法和 <see cref="WindowsProcess"/> 的
/// Win32 调用接起来。
///
/// 只使用 LoadLibraryW 确保目标进程具备 DLL 所依赖的系统模块，装载流程本身
/// 不把目标 DLL 交给 LoadLibrary——这就是"手动映射"要做的事。
/// </summary>
[SupportedOSPlatform("windows")]
public static class ManualMapInjector
{
    private static readonly object CacheGate = new();
    private static (string Path, DateTime WriteTimeUtc, byte[] Raw, PortablePeImage Pe)? _cache;

    /// <summary>
    /// 读盘、解析、校验 PE 头——同一个 dllPath 在多个 pid（多开 QQ）或者多轮 Reconcile
    /// 之间产出完全相同的结果，缓存住可以避免重复读盘解析，顺带让"紧邻 OpenProcess
    /// 之前复核 pid 身份"（<see cref="Core.CoreService.Reconcile"/>）真正贴近 OpenProcess——
    /// 复核和 OpenProcess 之间不会再夹着一次读盘+解析的耗时。用写入时间而不是内容哈希
    /// 做失效判断：内核升级会改写文件的 mtime，旧缓存自动失效，不会注入错版本。
    /// </summary>
    private static (byte[] Raw, PortablePeImage Pe) LoadValidatedDll(string dllPath)
    {
        var writeTimeUtc = File.GetLastWriteTimeUtc(dllPath);
        lock (CacheGate)
        {
            if (_cache is { } cached && cached.Path == dllPath && cached.WriteTimeUtc == writeTimeUtc)
                return (cached.Raw, cached.Pe);
        }

        var raw = File.ReadAllBytes(dllPath);
        var pe = PortablePeImage.Parse(raw);
        Injector.ValidatePe(pe);

        lock (CacheGate) _cache = (dllPath, writeTimeUtc, raw, pe);
        return (raw, pe);
    }

    /// <summary>把 x64 DLL 手动映射进指定进程并返回可卸载句柄。</summary>
    public static MapHandle Inject(int pid, string dllPath)
    {
        RequireWindows();
        if (pid <= 0) throw new ArgumentException("pid 必须是正整数", nameof(pid));
        if (!File.Exists(dllPath)) throw new FileNotFoundException(dllPath);

        var (raw, pe) = LoadValidatedDll(dllPath);

        var imageSize = pe.SizeOfImage;
        var preferredBase = pe.ImageBase;

        using var process = new WindowsProcess(pid);
        var baseAddress = process.Alloc(imageSize, preferredBase);
        if (baseAddress == 0) baseAddress = process.Alloc(imageSize);
        if (baseAddress == 0) throw new InjectionError("VirtualAllocEx(image) 失败");

        var exceptionRegistered = false;
        var entryAttached = false;
        var attachedTlsCallbacks = new List<ulong>();
        PreparedImage? prepared = null;
        try
        {
            prepared = Injector.PrepareImage(pe, raw, baseAddress, process.ResolveImport);
            process.Write(baseAddress, prepared.Data);
            process.Protect(baseAddress, pe.SizeOfHeaders, Injector.PageReadOnly);
            foreach (var section in prepared.Sections)
                process.Protect(section.Address, section.Size, section.Protection);
            process.Flush(baseAddress, imageSize);

            if (prepared.ExceptionTable != 0)
            {
                var addFunctionTable = process.TargetApi("ntdll.dll", "RtlAddFunctionTable");
                var result = process.RemoteCall(
                    addFunctionTable, prepared.ExceptionTable, (ulong)prepared.ExceptionCount, baseAddress);
                if (result == 0) throw new InjectionError("RtlAddFunctionTable 返回失败");
                exceptionRegistered = true;
            }

            foreach (var callback in prepared.TlsCallbacks)
            {
                process.RemoteCall(callback, baseAddress, Injector.DllProcessAttach, 0);
                attachedTlsCallbacks.Add(callback);
            }

            if (prepared.Entry != 0)
            {
                if (process.RemoteCall(prepared.Entry, baseAddress, Injector.DllProcessAttach, 0) == 0)
                    throw new InjectionError("DllMain(DLL_PROCESS_ATTACH) 返回失败");
                entryAttached = true;
            }

            return new MapHandle(baseAddress, prepared.Entry, prepared.ExceptionTable, imageSize);
        }
        catch (RemoteCallTimeoutException)
        {
            // 目标线程仍可能位于 TLS/DllMain/ntdll 中。此时释放映像会造成确定性崩溃，
            // 因而宁可保留目标内存并把错误交给上层提示重启 QQ。
            throw;
        }
        catch
        {
            // 只回滚已确认完成的阶段；远程调用超时会由 RemoteCall 保留 trampoline。
            if (entryAttached && prepared is { Entry: not 0 })
                Suppress(() => process.RemoteCall(prepared.Entry, baseAddress, Injector.DllProcessDetach, 0));
            for (var i = attachedTlsCallbacks.Count - 1; i >= 0; i--)
            {
                var callback = attachedTlsCallbacks[i];
                Suppress(() => process.RemoteCall(callback, baseAddress, Injector.DllProcessDetach, 0));
            }
            if (exceptionRegistered && prepared is not null)
                Suppress(() =>
                {
                    var deleteFunctionTable = process.TargetApi("ntdll.dll", "RtlDeleteFunctionTable");
                    process.RemoteCall(deleteFunctionTable, prepared.ExceptionTable);
                });
            Suppress(() => process.Free(baseAddress));
            throw;
        }
    }

    /// <summary>反向调用入口、注销 x64 异常表并释放手动映射映像。</summary>
    public static void Unload(int pid, MapHandle handle)
    {
        RequireWindows();
        if (pid <= 0) throw new ArgumentException("pid 必须是正整数", nameof(pid));
        if (handle.Base == 0 || handle.Size == 0) throw new ArgumentException("无效的手动映射句柄", nameof(handle));

        using var process = new WindowsProcess(pid);
        var tlsCallbacks = Injector.RemoteTlsCallbacks(process, handle);
        if (handle.Entry != 0)
            process.RemoteCall(handle.Entry, handle.Base, Injector.DllProcessDetach, 0);
        for (var i = tlsCallbacks.Count - 1; i >= 0; i--)
            process.RemoteCall(tlsCallbacks[i], handle.Base, Injector.DllProcessDetach, 0);

        if (handle.ExceptionTable != 0)
        {
            var deleteFunctionTable = process.TargetApi("ntdll.dll", "RtlDeleteFunctionTable");
            if (process.RemoteCall(deleteFunctionTable, handle.ExceptionTable) == 0)
                throw new InjectionError("RtlDeleteFunctionTable 返回失败；为安全起见未释放映像");
        }
        process.Free(handle.Base);
    }

    private static void Suppress(Action action)
    {
        try { action(); } catch { /* 回滚阶段本身失败也不能掩盖原始异常 */ }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("手动映射装载器仅支持 Windows x64");
    }
}
