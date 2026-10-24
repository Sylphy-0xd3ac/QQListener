using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace QQListener.Core;

public sealed class PmhqBundle
{
    public const string Version = "9.0.6-local-pipe.1";
    public const string DllHash = "2c667c5bda5e517fd7b97afe21627f23178a68cd18ccc523375ed7935b3ed78b";
    public const string OriginalHash = "345a8fc083f08968a73c237441c6b56010757f613857bc96cec7449623e46ae4";
    public const string DownloadUrl = "https://github.com/linyuchen/PMHQ/releases/download/v9.0.6/pmhq-win-x64.zip";
    public const string ArchiveHash = "64da6159976aee0eb1b67bc0c82ca3c540d9bfd4a41cc17d5a308e24dbfd7587";
    public const string PatchHash = "6eea307e7f5495093bff6ab920de12afd627d287581b662f99d497ea5a522995";
    private const int MaxDownloadBytes = 32 * 1024 * 1024;
    private static readonly HttpClient SharedHttp = new();
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    internal sealed record Distribution(string ArchiveHash, string OriginalHash, int OriginalLength,
        string DllHash, int DllLength, string PatchHash);
    private readonly Distribution _distribution;
    private readonly HttpClient _http;
    private readonly Func<byte[]> _readPatch;
    private readonly string _root;
    public PmhqBundle(string nativeDirectory, HttpClient? httpClient = null)
        : this(nativeDirectory, httpClient ?? SharedHttp,
            new(ArchiveHash, OriginalHash, 1_034_752, DllHash, 1_076_224, PatchHash), ReadEmbeddedPatch) { }

    internal PmhqBundle(string nativeDirectory, HttpClient httpClient, Distribution distribution, Func<byte[]> readPatch)
    {
        _root = Path.Combine(nativeDirectory, "pmhq");
        _http = httpClient;
        _distribution = distribution;
        _readPatch = readPatch;
    }
    public string ConfigurationPath => Path.Combine(_root, "pmhq_config.json");
    public string BinaryDirectory => Path.Combine(_root, Version);
    internal string OriginalPath => Path.Combine(_root, "upstream-v9.0.6.dll");
    public string BinaryPath => Path.Combine(BinaryDirectory, "pmhq.dll");
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

    public void VerifyInstalled()
    {
        if (!Matches(BinaryPath, _distribution.DllHash, _distribution.DllLength))
            throw new IOException("PMHQ 内核未准备好或校验失败，请重新检查并下载");
    }

    public async Task PrepareAsync(string? proxy = null, CancellationToken ct = default)
    {
        await InstallGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // Keep an existing reviewed DLL usable offline, including upgrades from beta.1.
            if (Matches(BinaryPath, _distribution.DllHash, _distribution.DllLength)) return;
            Directory.CreateDirectory(BinaryDirectory);
            byte[] original;
            if (Matches(OriginalPath, _distribution.OriginalHash, _distribution.OriginalLength))
                original = await File.ReadAllBytesAsync(OriginalPath, ct).ConfigureAwait(false);
            else
            {
                original = await DownloadOriginalAsync(proxy, ct).ConfigureAwait(false);
                await WriteAtomicAsync(OriginalPath, original, ct).ConfigureAwait(false);
            }
            // Recheck bytes after reading: another process may have modified the cache.
            RequireHash(original, _distribution.OriginalHash, "原始 DLL");
            var patch = _readPatch();
            RequireHash(patch, _distribution.PatchHash, "内置补丁");
            var patched = BinaryDelta.Apply(original, patch);
            if (patched.Length != _distribution.DllLength) throw new IOException("PMHQ 补丁输出长度不匹配");
            RequireHash(patched, _distribution.DllHash, "补丁结果");
            await WriteAtomicAsync(BinaryPath, patched, ct).ConfigureAwait(false);
        }
        finally { InstallGate.Release(); }
    }

    private async Task<byte[]> DownloadOriginalAsync(string? proxy, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get,
            KernelInstaller.ApplyProxy(DownloadUrl, string.IsNullOrWhiteSpace(proxy) ? null : proxy.Trim()));
        request.Headers.UserAgent.ParseAdd("QQListener-ClassIsland-Plugin/2.3");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            throw new IOException("PMHQ 下载内容超过 32 MiB 上限");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + read > MaxDownloadBytes) throw new IOException("PMHQ 下载内容超过 32 MiB 上限");
            bytes.Write(buffer, 0, read);
        }
        RequireHash(bytes.ToArray(), _distribution.ArchiveHash, "官方下载包");
        bytes.Position = 0;
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => e.FullName == "pmhq.dll").ToArray();
        if (entries.Length != 1 || entries[0].Length != _distribution.OriginalLength)
            throw new IOException("PMHQ 下载包内的 DLL 无效");
        // Read only the pinned root DLL. Never extract or execute the upstream launcher.
        using var entry = entries[0].Open();
        var original = new byte[_distribution.OriginalLength];
        await entry.ReadExactlyAsync(original, token).ConfigureAwait(false);
        if (entry.ReadByte() != -1) throw new IOException("PMHQ DLL 超过预期长度");
        RequireHash(original, _distribution.OriginalHash, "原始 DLL");
        return original;
    }

    private static byte[] ReadEmbeddedPatch()
    {
        using var resource = typeof(PmhqBundle).Assembly.GetManifestResourceStream("QQListener.PMHQ.local-pipe.patch")
            ?? throw new IOException("插件包缺少 PMHQ 补丁，请重新安装插件");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequireHash(byte[] bytes, string expected, string label)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"PMHQ {label} SHA-256 校验失败；拒绝安装");
    }

    private static bool Matches(string path, string hash, int length)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != length) return false;
        using var file = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(file)), hash, StringComparison.OrdinalIgnoreCase);
    }
}
