using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace QQListener.Native;

/// <summary>
/// 一次 OpenProcess 会话及其远程调用辅助。
///
/// 直接调用 kernel32/ntdll，不经任何第三方注入库——这是手动映射本身要做的事：
/// 分配、写入、保护、注册异常表、远程调用 DllMain，全部走公开文档化的 Win32 API。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcess : IRemoteMemory, IDisposable
{
    private const uint ProcessAccess = 0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const uint InfiniteTimeoutMs = 30_000;
    private const uint Th32csSnapModule = 0x00000008;
    private const uint Th32csSnapModule32 = 0x00000010;
    private const uint GetModuleHandleExFlagFromAddress = 0x00000004;
    private const uint GetModuleHandleExFlagUnchangedRefCount = 0x00000002;

    private readonly int _pid;
    private IntPtr _handle;
    private readonly List<IntPtr> _localModules = [];

    public WindowsProcess(int pid)
    {
        if (IntPtr.Size != 8)
            throw new InjectionError("注入 x64 进程需要 64 位进程");

        _pid = pid;
        _handle = NativeMethods.OpenProcess(ProcessAccess, false, (uint)pid);
        if (_handle == IntPtr.Zero)
            ThrowLastError($"OpenProcess({pid})");
        try
        {
            ValidateTargetArchitecture();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ValidateTargetArchitecture()
    {
        try
        {
            if (!NativeMethods.IsWow64Process2(_handle, out var processMachine, out var nativeMachine))
                ThrowLastError("IsWow64Process2");
            var machine = processMachine != 0 ? processMachine : nativeMachine;
            if (machine != Injector.ImageFileMachineAmd64)
                throw new InjectionError($"目标进程不是 x64 (machine=0x{machine:x4})");
        }
        catch (EntryPointNotFoundException)
        {
            // 老系统没有 IsWow64Process2；退化为信任调用方已经过滤过架构。
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        foreach (var module in _localModules)
            NativeMethods.FreeLibrary(module);
        _localModules.Clear();
    }

    private void ThrowLastError(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new InjectionError($"{operation}: {new Win32Exception(error).Message} (0x{error:x})");
    }

    public ulong Alloc(uint size, ulong address = 0, uint protection = Injector.PageReadWrite)
    {
        var result = NativeMethods.VirtualAllocEx(
            _handle, address != 0 ? (nuint)address : nuint.Zero, size, MemCommit | MemReserve, protection);
        return (ulong)result;
    }

    public void Free(ulong address)
    {
        if (!NativeMethods.VirtualFreeEx(_handle, (nuint)address, 0, MemRelease))
            ThrowLastError("VirtualFreeEx");
    }

    public void Write(ulong address, byte[] data)
    {
        if (data.Length == 0) return;
        if (!NativeMethods.WriteProcessMemory(_handle, (nuint)address, data, (nuint)data.Length, out var written))
            ThrowLastError("WriteProcessMemory");
        if ((int)written != data.Length)
            throw new InjectionError($"WriteProcessMemory 短写: {written}/{data.Length}");
    }

    public byte[] Read(ulong address, int size)
    {
        var buffer = new byte[size];
        if (!NativeMethods.ReadProcessMemory(_handle, (nuint)address, buffer, (nuint)size, out var read))
            ThrowLastError("ReadProcessMemory");
        if ((int)read != size)
            throw new InjectionError($"ReadProcessMemory 短读: {read}/{size}");
        return buffer;
    }

    public void Protect(ulong address, uint size, uint protection)
    {
        if (!NativeMethods.VirtualProtectEx(_handle, (nuint)address, size, protection, out _))
            ThrowLastError("VirtualProtectEx");
    }

    public void Flush(ulong address, uint size)
    {
        if (!NativeMethods.FlushInstructionCache(_handle, (nuint)address, size))
            ThrowLastError("FlushInstructionCache");
    }

    /// <summary>
    /// 在目标进程里跑一段最小 trampoline：把最多 4 个整型参数装进
    /// rcx/rdx/r8/r9（x64 调用约定），call 目标函数，把 rax 写回一个约定地址再读出来。
    /// </summary>
    public ulong RemoteCall(ulong function, params ulong[] args)
    {
        if (function == 0) throw new InjectionError("远程调用地址为空");
        if (args.Length > 4) throw new ArgumentException("x64 远程调用最多支持四个寄存器参数");
        var padded = new ulong[4];
        args.CopyTo(padded, 0);

        var allocation = Alloc(0x1000, protection: Injector.PageExecuteReadWrite);
        if (allocation == 0) ThrowLastError("VirtualAllocEx(remote call)");
        var resultAddress = allocation + 0x200;

        ReadOnlySpan<byte[]> registerLoads =
        [
            [0x48, 0xB9], // mov rcx, imm64
            [0x48, 0xBA], // mov rdx, imm64
            [0x49, 0xB8], // mov r8,  imm64
            [0x49, 0xB9], // mov r9,  imm64
        ];

        var code = new List<byte> { 0x48, 0x83, 0xEC, 0x28 }; // sub rsp, 0x28 (shadow space + 对齐)
        for (var i = 0; i < 4; i++)
        {
            code.AddRange(registerLoads[i]);
            code.AddRange(BitConverter.GetBytes(padded[i]));
        }
        code.AddRange([0x48, 0xB8]);                       // mov rax, function
        code.AddRange(BitConverter.GetBytes(function));
        code.AddRange([0xFF, 0xD0]);                        // call rax
        code.AddRange([0x49, 0xBB]);                        // mov r11, result_address
        code.AddRange(BitConverter.GetBytes(resultAddress));
        code.AddRange([0x49, 0x89, 0x03]);                  // mov [r11], rax
        code.AddRange([0x48, 0x83, 0xC4, 0x28, 0xC3]);       // add rsp, 0x28; ret

        IntPtr thread = IntPtr.Zero;
        var completed = false;
        try
        {
            Write(allocation, [.. code]);
            Flush(allocation, (uint)code.Count);

            thread = NativeMethods.CreateRemoteThread(
                _handle, IntPtr.Zero, UIntPtr.Zero, (nuint)allocation, IntPtr.Zero, 0, out _);
            if (thread == IntPtr.Zero) ThrowLastError("CreateRemoteThread");

            var waitResult = NativeMethods.WaitForSingleObject(thread, InfiniteTimeoutMs);
            if (waitResult == WaitTimeout)
                throw new RemoteCallTimeoutException("远程调用 30 秒未返回；保留调用页以避免目标进程崩溃");
            if (waitResult != WaitObject0)
                ThrowLastError("WaitForSingleObject");

            completed = true;
            return BitConverter.ToUInt64(Read(resultAddress, 8));
        }
        finally
        {
            if (thread != IntPtr.Zero) NativeMethods.CloseHandle(thread);
            if (completed || thread == IntPtr.Zero)
                Free(allocation);
        }
    }

    private Dictionary<string, (ulong Base, string Path)> ModuleEntries()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            Th32csSnapModule | Th32csSnapModule32, (uint)_pid);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            ThrowLastError("CreateToolhelp32Snapshot");
        try
        {
            var entry = new NativeMethods.MODULEENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.MODULEENTRY32W>() };
            if (!NativeMethods.Module32FirstW(snapshot, ref entry))
                ThrowLastError("Module32FirstW");

            var modules = new Dictionary<string, (ulong, string)>();
            do
            {
                modules[entry.szModule.ToLowerInvariant()] = ((ulong)entry.modBaseAddr, entry.szExePath);
            } while (NativeMethods.Module32NextW(snapshot, ref entry));
            return modules;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    private (ulong Base, string Path) LocalOwner(ulong address)
    {
        const uint flags = GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefCount;
        if (!NativeMethods.GetModuleHandleExW(flags, (nuint)address, out var module))
            ThrowLastError("GetModuleHandleExW");

        var buffer = new StringBuilder(32768);
        var length = NativeMethods.GetModuleFileNameW(module, buffer, (uint)buffer.Capacity);
        if (length == 0) ThrowLastError("GetModuleFileNameW");
        return ((ulong)module, buffer.ToString());
    }

    private ulong RemoteEquivalent(ulong localAddress)
    {
        var (localBase, ownerPath) = LocalOwner(localAddress);
        var ownerName = Path.GetFileName(ownerPath).ToLowerInvariant();
        if (!ModuleEntries().TryGetValue(ownerName, out var remote))
            throw new InjectionError($"目标进程未加载解析导入所需模块: {ownerName}");
        return remote.Base + (localAddress - localBase);
    }

    private ulong LoadLibraryAddress()
    {
        var kernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
        if (kernel32 == IntPtr.Zero) ThrowLastError("GetModuleHandleW(kernel32.dll)");
        var localAddress = NativeMethods.GetProcAddress(kernel32, "LoadLibraryW");
        if (localAddress == IntPtr.Zero) ThrowLastError("GetProcAddress(LoadLibraryW)");
        return RemoteEquivalent((ulong)localAddress);
    }

    public ulong EnsureModule(string moduleName)
    {
        var key = Path.GetFileName(moduleName).ToLowerInvariant();
        if (ModuleEntries().TryGetValue(key, out var existing)) return existing.Base;

        var encoded = Encoding.Unicode.GetBytes(moduleName + "\0");
        var remoteString = Alloc((uint)encoded.Length);
        if (remoteString == 0) ThrowLastError("VirtualAllocEx(import name)");
        var safeToFree = true;
        ulong loaded;
        try
        {
            Write(remoteString, encoded);
            loaded = RemoteCall(LoadLibraryAddress(), remoteString);
        }
        catch (RemoteCallTimeoutException)
        {
            // remoteString 正是这次超时的远程调用（LoadLibraryW）的参数指针——
            // 目标线程可能还没跑完、还在读它。跟本类其余地方对同一个异常的处理
            // 原则一致：宁可泄漏这一小块内存，也不要在目标可能还在用的时候释放它。
            safeToFree = false;
            throw;
        }
        finally
        {
            if (safeToFree) Free(remoteString);
        }
        if (loaded == 0)
            throw new InjectionError($"目标进程 LoadLibraryW 失败: {moduleName}");

        // API-set 名称会被加载器重定向，不一定作为同名模块出现在快照里；LoadLibraryW
        // 的返回值仍是目标进程里的有效模块句柄。后续实际函数地址按 owner 模块换算。
        return ModuleEntries().TryGetValue(key, out var refreshed) ? refreshed.Base : loaded;
    }

    public ulong ResolveImport(string module, byte[]? name, int? ordinal)
    {
        EnsureModule(module);
        var localModule = NativeMethods.LoadLibraryW(module);
        if (localModule == IntPtr.Zero) ThrowLastError($"LoadLibraryW({module})");
        _localModules.Add(localModule);

        IntPtr localAddress;
        string symbolForError;
        if (name is not null)
        {
            var symbol = Encoding.ASCII.GetString(name);
            localAddress = NativeMethods.GetProcAddress(localModule, symbol);
            symbolForError = symbol;
        }
        else if (ordinal is { } value)
        {
            localAddress = NativeMethods.GetProcAddress(localModule, (IntPtr)value);
            symbolForError = $"#{value}";
        }
        else
        {
            throw new InjectionError($"导入项缺少名称和序号: {module}");
        }

        if (localAddress == IntPtr.Zero)
            throw new InjectionError($"GetProcAddress 失败: {module}!{symbolForError}");
        return RemoteEquivalent((ulong)localAddress);
    }

    public ulong TargetApi(string module, string symbol) =>
        ResolveImport(module, Encoding.ASCII.GetBytes(symbol), null);
}

/// <summary>薄封装：把 Win32 错误码转成带消息的异常，避免到处手写 FormatMessage。</summary>
internal sealed class Win32Exception(int errorCode) : Exception(new System.ComponentModel.Win32Exception(errorCode).Message);

[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint VirtualAllocEx(IntPtr hProcess, nuint lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, nuint lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualProtectEx(IntPtr hProcess, nuint lpAddress, uint dwSize, uint flNewProtect, out uint lpflOldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, nuint lpBaseAddress, [In] byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, nuint lpBaseAddress, [Out] byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlushInstructionCache(IntPtr hProcess, nuint lpBaseAddress, uint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, nuint dwStackSize, nuint lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeLibrary(IntPtr hLibModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, IntPtr ordinal);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetModuleHandleExW(uint dwFlags, nuint lpModuleName, out IntPtr phModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetModuleFileNameW(IntPtr hModule, StringBuilder lpFilename, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WaitNamedPipeW(string lpNamedPipeName, uint nTimeOut);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Module32FirstW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Module32NextW(IntPtr hSnapshot, ref MODULEENTRY32W lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process2(IntPtr hProcess, out ushort processMachine, out ushort nativeMachine);

    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr LocalFree(IntPtr hMem);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MODULEENTRY32W
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public nuint modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }
}
