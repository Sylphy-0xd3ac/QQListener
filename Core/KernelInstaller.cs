using System.IO.Compression;
using System.Text.Json;

namespace QQListener.Core;

public sealed record ReleaseInfo(string Tag, string AssetName, string AssetUrl);

public sealed record UpdateStatus(
    bool Installed,
    string? CurrentVersion,
    string? LatestVersion = null,
    bool HasUpdate = false,
    string Error = "",
    IReadOnlyList<string>? Binaries = null);

/// <summary>
/// SnowLuma 核心（专有二进制）的获取/更新。
///
/// 合规边界（见仓库 LICENSE 合规说明）：只从 SnowLuma **官方 release** 下载；
/// 加速代理由用户自己填，程序不内置、不自建镜像；不随本插件分发二进制。
/// 二进制装到 <see cref="NativeDir"/>，版本号记在 <c>NativeDir/version.txt</c>。
/// </summary>
public sealed class KernelInstaller(string nativeDir, HttpClient? httpClient = null)
{
    public const string CoreRepo = "SnowLuma/SnowLuma";
    public const string DefaultPlatform = "win-x64";
    private const string RawBase = $"https://raw.githubusercontent.com/{CoreRepo}/main";

    /// <summary>运行时从官方仓库拉取，不随本插件打包 SnowLuma 的法律文本。</summary>
    public static readonly IReadOnlyDictionary<string, (string Title, string Path)> LegalDocs =
        new Dictionary<string, (string, string)>
        {
            ["eula"] = ("SnowLuma EULA（最终用户许可协议）", "EULA.md"),
            ["license"] = ("SnowLuma LICENSE（许可证）", "LICENSE"),
            ["privacy"] = ("SnowLuma 隐私说明", "PRIVACY.md"),
        };

    // 手动映射装载器只需要钩子 DLL，不下载 SnowLuma 的 Node 原生装载器。
    public static readonly IReadOnlyList<string> CoreBinaryNames = ["snowluma-win32-x64.dll"];

    private const string UserAgent = "QQListener-ClassIsland-Plugin/1.0 (+https://github.com/Sylphy-0xd3ac/QQListener)";

    // 内核压缩包正常是几 MB 到几十 MB；200 MiB 已经远超正常用量，只是给一个
    // 损坏/恶意响应的内存占用划一条上限，不让宿主进程被一个巨大的响应体吃穿。
    private const long MaxDownloadBytes = 200 * 1024 * 1024;

    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <summary>
    /// GitHub 的 REST API 对没有 User-Agent 的请求一律 403。之前把 UA 设在
    /// <see cref="HttpClient.DefaultRequestHeaders"/> 上——如果调用方传进来的是一个
    /// 共享/池化的 HttpClient 实例，这是在悄悄修改别人的对象；<see cref="System.Net.Http.Headers.HttpHeaders"/>
    /// 本身也不是线程安全的，两个 Installer 共用一个 client 并发构造会撞内部字典。
    /// 改成每次请求挂到 <see cref="HttpRequestMessage"/> 自己身上。
    /// </summary>
    private static HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    public string NativeDir { get; } = nativeDir;
    public string VersionFilePath => Path.Combine(NativeDir, "version.txt");

    public string? CurrentVersion()
    {
        if (!File.Exists(VersionFilePath)) return null;
        try
        {
            var text = File.ReadAllText(VersionFilePath).Trim();
            return text.Length > 0 ? text : null;
        }
        // 这个方法在 KernelSetupViewModel 的构造函数里被调用（见该类型注释）——
        // 权限问题（文件被同步盘/杀软临时锁住）不该让整个设置页打不开，只应该
        // 退化成"当前未安装"。之前只挡了 IOException，UnauthorizedAccessException
        // 会直接逃逸。
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> InstalledBinaries() =>
        [.. CoreBinaryNames.Where(name => File.Exists(Path.Combine(NativeDir, name)))];

    public bool IsCoreInstalled() => InstalledBinaries().Count > 0;

    public static bool NeedsCoreSetup(bool coreSupported, bool eulaAccepted, bool coreInstalled) =>
        coreSupported && (!eulaAccepted || !coreInstalled);

    public async Task<string> FetchLegalTextAsync(string kind, string? proxy = null, CancellationToken ct = default)
    {
        var (_, path) = LegalDocs[kind];
        var url = ApplyProxy($"{RawBase}/{path}", proxy);
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把第三方 GitHub 代理前缀拼到官方下载 URL 前（代理由用户提供）。
    ///
    /// 强制要求 https：明文 http 代理在本地网络上就能被中间人替换下载内容，而
    /// 这里下载的产物最终会被映射进 QQ 执行——这不是一个"能凑合用"的降级选项。
    /// </summary>
    public static string ApplyProxy(string url, string? proxy)
    {
        if (string.IsNullOrEmpty(proxy)) return url;
        var trimmed = proxy.TrimEnd('/');
        if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("加速代理地址必须以 https:// 开头，明文 http 代理可能被中间人替换下载内容", nameof(proxy));
        return $"{trimmed}/{url}";
    }

    public static bool IsNewer(string remote, string? local) =>
        local is null || string.IsNullOrEmpty(local) || CompareVersions(ParseVersion(remote), ParseVersion(local)) > 0;

    private static int CompareVersions(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    internal static IReadOnlyList<int> ParseVersion(string value)
    {
        var digits = value.Trim().TrimStart('v', 'V');
        var parts = new List<int>();
        foreach (var chunk in digits.Split('.'))
        {
            var num = "";
            foreach (var ch in chunk)
            {
                if (!char.IsDigit(ch)) break;
                num += ch;
            }
            parts.Add(num.Length > 0 ? int.Parse(num) : 0);
        }
        return parts.Count > 0 ? parts : [0];
    }

    /// <summary>选完整版（非 lite）的平台压缩包。</summary>
    public static (string Name, string Url)? SelectAsset(
        IEnumerable<(string Name, string Url)> assets, string platform = DefaultPlatform)
    {
        foreach (var asset in assets)
        {
            if (asset.Name.Contains(platform) && asset.Name.EndsWith(".zip")
                && !asset.Name.Contains("lite", StringComparison.OrdinalIgnoreCase))
                return asset;
        }
        return null;
    }

    public async Task<ReleaseInfo> LatestReleaseAsync(
        string platform = DefaultPlatform, string? proxy = null, CancellationToken ct = default)
    {
        const string directApi = $"https://api.github.com/repos/{CoreRepo}/releases/latest";
        if (string.IsNullOrEmpty(proxy))
            return await FetchLatestReleaseAsync(directApi, platform, ct).ConfigureAwait(false);

        try
        {
            return await FetchLatestReleaseAsync(ApplyProxy(directApi, proxy), platform, ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            // 大多数 GitHub 加速前缀只代理 release 资产/raw 内容，不代理
            // api.github.com 本身——填了代理反而会让查版本这一步先失败，用户会
            // 以为代理本身坏了。直连再试一次，两边都失败才把这次的失败报出去。
            return await FetchLatestReleaseAsync(directApi, platform, ct).ConfigureAwait(false);
        }
    }

    private async Task<ReleaseInfo> FetchLatestReleaseAsync(string api, string platform, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, api);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";

        var assets = new List<(string Name, string Url)>();
        if (root.TryGetProperty("assets", out var assetsProp))
        {
            foreach (var item in assetsProp.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = item.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                assets.Add((name, url));
            }
        }

        var selected = SelectAsset(assets, platform)
            ?? throw new InvalidOperationException($"未在 release {tag} 找到 {platform} 完整包");
        return new ReleaseInfo(tag, selected.Name, selected.Url);
    }

    public async Task<UpdateStatus> CheckUpdateAsync(
        string platform = DefaultPlatform, string? proxy = null, CancellationToken ct = default)
    {
        var installed = IsCoreInstalled();
        var currentVersion = CurrentVersion();
        var binaries = InstalledBinaries();
        try
        {
            var release = await LatestReleaseAsync(platform, proxy, ct).ConfigureAwait(false);
            return new UpdateStatus(installed, currentVersion, release.Tag,
                IsNewer(release.Tag, currentVersion), "", binaries);
        }
        catch (Exception exc) // 网络/解析失败：给出可读错误，不静默
        {
            return new UpdateStatus(installed, currentVersion, Error: exc.Message, Binaries: binaries);
        }
    }

    /// <summary>从官方 release 下载并安装核心二进制，返回安装的版本号。</summary>
    public async Task<string> DownloadAndInstallAsync(
        string? proxy = null, string platform = DefaultPlatform, CancellationToken ct = default)
    {
        var release = await LatestReleaseAsync(platform, proxy, ct).ConfigureAwait(false);
        var downloadUrl = ApplyProxy(release.AssetUrl, proxy);
        var zipBytes = await DownloadCappedAsync(downloadUrl, ct).ConfigureAwait(false);

        var installed = ExtractCoreBinaries(zipBytes, NativeDir);
        if (installed.Count == 0)
            throw new InvalidOperationException("压缩包内未找到核心二进制");

        await WriteVersionFileAtomicAsync(release.Tag, ct).ConfigureAwait(false);
        return release.Tag;
    }

    /// <summary>流式下载并按 <see cref="MaxDownloadBytes"/> 限流——之前用
    /// <c>GetByteArrayAsync</c> 把整个响应体读进内存，一个损坏/恶意的代理返回
    /// 一个巨大的 body 就能把宿主进程 OOM，没有任何上限。</summary>
    private async Task<byte[]> DownloadCappedAsync(string url, CancellationToken ct)
    {
        // ResponseHeadersRead 下 HttpClient.Timeout 不覆盖后续流读取，慢速代理
        // 下载几十 MB 的包很容易超过默认的 100 秒；用独立超时兜住整个读取阶段。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;

        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > MaxDownloadBytes)
            throw new IOException($"下载内容超过大小上限（{MaxDownloadBytes / 1024 / 1024} MiB）");

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream(
            contentLength is { } len && len < MaxDownloadBytes ? (int)len : 81920);
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new IOException($"下载内容超过大小上限（{MaxDownloadBytes / 1024 / 1024} MiB）");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// version.txt 用临时文件 + <see cref="File.Move(string, string, bool)"/> 原子替换。
    ///
    /// 之前直接 <c>WriteAllTextAsync</c> 到正式路径：断电/进程被杀在写到一半时，
    /// 要么留下一个内容为空的 version.txt（<see cref="CurrentVersion"/> 会返回
    /// null，界面显示"未安装"而 DLL 其实已经装好了），要么（更常见）DLL 已经
    /// 换成新版本、这一步还没跑完，version.txt 停留在旧版本号，<see cref="IsNewer"/>
    /// 从此永远认为"有更新"，反复触发不必要的重新下载。
    /// </summary>
    private async Task WriteVersionFileAtomicAsync(string tag, CancellationToken ct)
    {
        var tempPath = $"{VersionFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, tag, ct).ConfigureAwait(false);
            File.Move(tempPath, VersionFilePath, overwrite: true);
        }
        finally
        {
            Suppress(() => { if (File.Exists(tempPath)) File.Delete(tempPath); });
        }
    }

    internal static IReadOnlyList<string> ExtractCoreBinaries(byte[] zipBytes, string destDir)
    {
        Directory.CreateDirectory(destDir);
        // 每个文件各自的临时名用 Guid 而不是固定的 finalPath + ".download"：两次
        // 安装并发跑（比如用户连点两次"检查更新"）会写同一个临时文件，后者截断
        // 前者，前者的 File.Move 就会把一个残缺的 DLL 搬到正式位置——而这个 DLL
        // 会被映射进 QQ 执行。全部解压成功之后才逐个替换正式文件：即便以后
        // CoreBinaryNames 变成多个，也不会出现"文件 A 已经是新版本、文件 B 还没
        // 换、进程刚好在这时候崩了"的混版本状态（注意：这两次 File.Move 之间仍
        // 不是单一文件系统事务，极端情况下第二个 Move 失败依然会留下半套新文件，
        // 这是当前只有一个核心二进制、暂时可以接受的已知局限）。
        var staged = new List<(string BaseName, string TempPath)>();
        try
        {
            using var stream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var baseName = Path.GetFileName(entry.FullName);
                if (baseName.Length == 0 || !CoreBinaryNames.Contains(baseName)) continue;

                var tempPath = Path.Combine(destDir, $"{baseName}.{Guid.NewGuid():N}.download");
                using (var entryStream = entry.Open())
                using (var output = new FileStream(tempPath, FileMode.CreateNew))
                    entryStream.CopyTo(output);
                staged.Add((baseName, tempPath));
            }

            foreach (var (baseName, tempPath) in staged)
                File.Move(tempPath, Path.Combine(destDir, baseName), overwrite: true);

            return [.. staged.Select(s => s.BaseName)];
        }
        finally
        {
            foreach (var (_, tempPath) in staged)
                Suppress(() => { if (File.Exists(tempPath)) File.Delete(tempPath); });
        }
    }

    private static void Suppress(Action action)
    {
        try { action(); }
        catch { /* 收尾清理本身失败不能掩盖原始异常 */ }
    }
}
