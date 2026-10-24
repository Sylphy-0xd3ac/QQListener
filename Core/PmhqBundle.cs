using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace QQListener.Core;

public sealed class PmhqBundle
{
    public const string Version = "9.0.6-local-pipe.1";
    public const string DllHash = "2c667c5bda5e517fd7b97afe21627f23178a68cd18ccc523375ed7935b3ed78b";
    private readonly string _root;
    public PmhqBundle(string nativeDirectory) => _root = Path.Combine(nativeDirectory, "pmhq");
    public string ConfigurationPath => Path.Combine(_root, "pmhq_config.json");
    public string BinaryDirectory => Path.Combine(_root, Version);
    private string ResidentPath => Path.Combine(_root, "qq-session.json");

    public bool HasResidentHook(int pid, long processStartUtcTicks)
    {
        try
        {
            if (!File.Exists(ResidentPath) || new FileInfo(ResidentPath).Length > 1024) return false;
            var marker = JsonSerializer.Deserialize<ResidentProcess>(File.ReadAllText(ResidentPath));
            return marker is not null && marker.Pid == pid && marker.StartUtcTicks == processStartUtcTicks;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    public void RememberResidentHook(int pid, long processStartUtcTicks)
    {
        Directory.CreateDirectory(_root);
        var temporary = ResidentPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ResidentProcess(pid, processStartUtcTicks)));
            File.Move(temporary, ResidentPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ResidentProcess(int Pid, long StartUtcTicks);

    public void Prepare()
    {
        Directory.CreateDirectory(BinaryDirectory);
        Extract("pmhq.dll", DllHash);
    }

    private void Extract(string name, string hash)
    {
        var path = Path.Combine(BinaryDirectory, name);
        if (File.Exists(path) && Hash(path) == hash) return;
        using var resource = typeof(PmhqBundle).Assembly.GetManifestResourceStream("QQListener.PMHQ." + name)
            ?? throw new IOException("插件包缺少 PMHQ 内置文件，请重新安装插件");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) resource.CopyTo(file);
            if (Hash(temporary) != hash) throw new IOException("PMHQ 内置文件校验失败");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }
}
