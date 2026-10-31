using System.Text.Json;
using QQListener.Core;
using QQListener.Models;
using QQListener.Updates;

namespace QQListener.Services;

public sealed record EngineInstallStatus(string Name, string Installed, string Latest = "尚未检查",
    string Detail = "", double Progress = 0, bool Busy = false);

/// <summary>One download queue and status source for startup, settings, and the setup wizard.</summary>
public sealed class EngineInstallationService
{
    private readonly QqListenerSettings _settings;
    private readonly string _native;
    private readonly KernelInstaller _snow;
    private readonly DistributionClient _client;
    private readonly HttpClient? _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineInstallStatus _snowStatus = new("SnowLuma", "未安装");
    private EngineInstallStatus _pmhqStatus = new("PMHQ", "未安装");
    private PmhqRelease? _candidate;
    private CancellationTokenSource? _operation;
    public EngineInstallStatus Snow => Volatile.Read(ref _snowStatus);
    public EngineInstallStatus Pmhq => Volatile.Read(ref _pmhqStatus);
    public bool Busy => Snow.Busy || Pmhq.Busy;
    public bool AllInstalled => Snow.Installed != "未安装" && Pmhq.Installed != "未安装";

    public EngineInstallationService(QqListenerSettings settings, string? nativeDirectory = null, HttpClient? http = null,
        DistributionClient? distributionClient = null)
    {
        _settings = settings;
        _native = nativeDirectory ?? KernelLocator.ResolveNativeDirectory(settings.SettingsDirectory);
        _snow = new KernelInstaller(_native, http);
        _http = http;
        _client = distributionClient ?? new DistributionClient(http);
        RefreshInstalled();
    }

    public void RefreshInstalled()
    {
        _snowStatus = Snow with { Installed = _snow.IsCoreInstalled() ? _snow.CurrentVersion() ?? "已安装" : "未安装" };
        try
        {
            var bundle = new PmhqBundle(_native);
            _pmhqStatus = Pmhq with { Installed = bundle.IsInstalled ? bundle.RuntimeVersion : "未安装" };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        { _pmhqStatus = Pmhq with { Installed = "未安装", Detail = e.Message }; }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        await RunAsync(async token =>
        {
            _candidate = null;
            _snowStatus = Snow with { Busy = true, Detail = "检查更新" };
            _pmhqStatus = Pmhq with { Busy = true, Detail = "检查更新" };
            var snow = await _snow.CheckUpdateAsync(proxy: _settings.KernelDownloadProxy, ct: token).ConfigureAwait(false);
            _snowStatus = Snow with { Latest = snow.LatestVersion ?? "未知", Detail = snow.Error, Busy = false };
            try
            {
                var loader = LoaderSettings.Read(_settings.SettingsDirectory);
                var catalog = await _client.CatalogAsync(loader.CatalogUrl, _settings.KernelDownloadProxy, token,
                    Path.Combine(_settings.SettingsDirectory, "runtime")).ConfigureAwait(false);
                var json = await _client.GetAsync("https://api.github.com/repos/linyuchen/PMHQ/releases/latest",
                    _settings.KernelDownloadProxy, 1024 * 1024, token).ConfigureAwait(false);
                using var release = JsonDocument.Parse(json);
                var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
                _candidate = catalog.ForPmhq(tag);
                if (_candidate is not null && Pmhq.Installed != "未安装"
                    && ReleaseVersionComparer.Instance.Compare(_candidate.RuntimeVersion, Pmhq.Installed) < 0) _candidate = null;
                // First installs may use the latest supported version; updates never jump to an unadapted release.
                if (_candidate is null && Pmhq.Installed == "未安装") _candidate = catalog.LatestCompatiblePmhq;
                _pmhqStatus = Pmhq with { Latest = tag, Detail = catalog.ForPmhq(tag) is null
                    ? "新版尚未适配，保留可用版本" : "可用版本已确认", Busy = false };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or HttpRequestException or JsonException)
            { _pmhqStatus = Pmhq with { Detail = "检查失败：" + e.Message, Busy = false }; }
        }, ct).ConfigureAwait(false);
    }

    public async Task InstallBothAsync(CancellationToken ct = default)
    {
        if (!_settings.KernelEulaAccepted) throw new IOException("请先阅读并同意 SnowLuma 的协议");
        await RunAsync(async token =>
        {
            var errors = new List<Exception>();
            try
            {
                _snowStatus = Snow with { Busy = true, Detail = "下载 SnowLuma", Progress = 0 };
                if (!_snow.IsCoreInstalled() || Snow.Latest is not ("未知" or "尚未检查")
                    && ReleaseVersionComparer.Instance.Compare(Snow.Latest, Snow.Installed) > 0)
                    await _snow.DownloadAndInstallAsync(_settings.KernelDownloadProxy, ct: token,
                        progress: new InlineProgress(p => _snowStatus = Snow with { Detail = p.Stage, Progress = p.Percent })).ConfigureAwait(false);
                _snowStatus = Snow with { Detail = "安装完成", Progress = 100 };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { _snowStatus = Snow with { Detail = e.Message }; errors.Add(e); }
            finally { _snowStatus = Snow with { Busy = false }; }
            try
            {
                var bundle = _candidate is null ? new PmhqBundle(_native, _http) : new PmhqBundle(_native, _candidate, _http);
                await PreparePmhqCoreAsync(bundle, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { errors.Add(e); }
            RefreshInstalled();
            if (errors.Count > 0) throw new IOException(string.Join("；", errors.Select(e => e.Message)));
        }, ct).ConfigureAwait(false);
    }

    public Task PreparePmhqAsync(PmhqBundle bundle, CancellationToken ct) =>
        RunAsync(token => PreparePmhqCoreAsync(bundle, token), ct);

    private async Task PreparePmhqCoreAsync(PmhqBundle bundle, CancellationToken token)
    {
        _pmhqStatus = Pmhq with { Busy = true, Detail = "准备 PMHQ", Progress = 0 };
        try
        {
            await bundle.PrepareAsync(_settings.KernelDownloadProxy, token,
                new InlineProgress(p => _pmhqStatus = Pmhq with { Detail = p.Stage, Progress = p.Percent })).ConfigureAwait(false);
            _pmhqStatus = Pmhq with { Detail = "安装完成", Installed = bundle.RuntimeVersion, Progress = 100 };
        }
        catch (Exception e) { _pmhqStatus = Pmhq with { Detail = e.Message }; throw; }
        finally { _pmhqStatus = Pmhq with { Busy = false }; }
    }

    public void Cancel() { try { _operation?.Cancel(); } catch (ObjectDisposedException) { } }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _operation = source;
        try { await action(source.Token).ConfigureAwait(false); }
        finally
        {
            _operation = null;
            _snowStatus = Snow with { Busy = false };
            _pmhqStatus = Pmhq with { Busy = false };
            try { RefreshInstalled(); }
            finally { _gate.Release(); }
        }
    }

    private sealed class InlineProgress(Action<DownloadProgress> update) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => update(value); }
}
