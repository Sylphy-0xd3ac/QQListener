using System.Text;

namespace QQListener.Native;

/// <summary>Owns the LoadLibraryW argument until the remote call is known to have finished.</summary>
internal static class RemoteLibraryLoader
{
    public static ulong Load(string path, Func<uint, ulong> allocate, Action<ulong, byte[]> write,
        Func<ulong, ulong> loadLibrary, Action<ulong> free)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
            throw new ArgumentException("DLL 路径无效", nameof(path));
        var encoded = Encoding.Unicode.GetBytes(path + "\0");
        var argument = allocate(checked((uint)encoded.Length));
        if (argument == 0) throw new InjectionError("VirtualAllocEx(DLL path) 失败");
        var safeToFree = true;
        try
        {
            write(argument, encoded);
            var module = loadLibrary(argument);
            if (module == 0) throw new InjectionError($"目标进程 LoadLibraryW 失败: {path}");
            return module; // HMODULE is 64-bit; a remote thread exit code would truncate it.
        }
        catch (RemoteCallTimeoutException)
        {
            safeToFree = false;
            throw;
        }
        finally
        {
            if (safeToFree) free(argument);
        }
    }
}
