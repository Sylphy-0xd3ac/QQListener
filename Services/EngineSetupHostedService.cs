using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using QQListener.Core;
using QQListener.Models;
using QQListener.Views.SettingsPages;
using QQListener.Views.Windows;

namespace QQListener.Services;

public sealed class EngineSetupHostedService(QqListenerSettings settings, EngineInstallationService installations,
    IHostApplicationLifetime lifetime, QqListenerDiagnosticsState diagnostics) : IHostedService, IDisposable
{
    private CancellationTokenRegistration _started;
    private KernelSetupWizardWindow? _window;
    public Task StartAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() && settings.KernelSetupCompleted && settings.AutoUpdateEngines)
            _started = lifetime.ApplicationStarted.Register(() => _ = UpdateAsync());
        if (OperatingSystem.IsWindows() && !settings.KernelSetupPrompted && !settings.KernelSetupCompleted)
            _started = lifetime.ApplicationStarted.Register(() => Dispatcher.UIThread.Post(() =>
            {
                if (lifetime.ApplicationStopping.IsCancellationRequested) return;
                try
                {
                    _window = new(new KernelSetupViewModel(settings, KernelLocator.ResolveNativeDirectory(settings.SettingsDirectory), installations));
                    _window.Show();
                    settings.ModifyAndSave(s => s.KernelSetupPrompted = true);
                }
                catch (Exception e) { diagnostics.RecordError(e, "打开安装向导失败"); }
            }));
        return Task.CompletedTask;
    }
    private async Task UpdateAsync()
    {
        try
        {
            await installations.CheckAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
            if (settings.KernelEulaAccepted)
                await installations.InstallBothAsync(lifetime.ApplicationStopping).ConfigureAwait(false);
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("QQListener engine update: " + e.Message); }
    }
    public Task StopAsync(CancellationToken ct)
    {
        _started.Dispose();
        installations.Cancel();
        Dispatcher.UIThread.Post(() => _window?.Close());
        return Task.CompletedTask;
    }
    public void Dispose() => _started.Dispose();
}
