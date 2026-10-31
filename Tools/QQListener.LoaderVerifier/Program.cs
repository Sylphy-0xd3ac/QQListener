using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQListener.Bootstrap;
using QQListener.Updates;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var package = Path.GetFullPath(args.Single());
        var root = Path.Combine(Path.GetTempPath(), "qql-loader-smoke-" + Guid.NewGuid().ToString("N"));
        var plugin = Path.Combine(root, "plugin");
        var config = Path.Combine(root, "config");
        Directory.CreateDirectory(plugin);
        try
        {
            ZipFile.ExtractToDirectory(package, plugin);
            if (File.Exists(Path.Combine(plugin, "QQListener.dll"))) throw new IOException("The business DLL must be in the core payload.");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
            var store = new RuntimeStore(config);
            using var http = new HttpClient(new Offline());
            var release = new RuntimeBootstrap(store, new DistributionClient(http)).PrepareAsync(plugin,
                new(DistributionCatalog.DefaultUrl, "", false)).GetAwaiter().GetResult();
            var entry = Path.Combine(store.DirectoryFor(release), "QQListener.dll");
            var context = new CoreLoadContext(entry, AssemblyLoadContext.Default);
            var core = context.LoadFromAssemblyPath(entry);
            AppBuilder.Configure<PreviewApp>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var settingsType = core.GetType("QQListener.Models.QqListenerSettings", true)!;
            var settings = Activator.CreateInstance(settingsType)!;
            settingsType.GetProperty("SettingsDirectory")!.SetValue(settings, config);
            var diagnostics = Activator.CreateInstance(core.GetType("QQListener.Models.QqListenerDiagnosticsState", true)!)!;
            using var host = Host.CreateDefaultBuilder().Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(host.Services.GetRequiredService<IHostApplicationLifetime>());
            core.GetType("QQListener.Plugin", true)!.GetMethod("RegisterRuntimeServices", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [services, settings, diagnostics]);
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
            _ = provider.GetRequiredService(core.GetType("QQListener.Services.QqListenerWorker", true)!);
            var control = (Control)Activator.CreateInstance(core.GetType("QQListener.Views.SettingsPages.QqListenerSettingsPage", true)!)!;
            var window = new Window { Width = 680, Height = 800, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var icon = AssetLoader.Open(new Uri("avares://QQListener/icon.png"));
            if (icon.ReadByte() != 0x89) throw new IOException("Core Avalonia resources are not resolved.");
            window.Close();
            store.Activate(release);
            if (File.ReadAllText(Path.Combine(root, "keep.txt")) != "keep") throw new IOException("Configuration was changed.");
            Console.WriteLine($"Loader bootstrap, isolated DI, settings UI and resources passed: {release.Version}, build {release.BuildNumber}");
            return 0;
        }
        finally
        {
            // The noncollectible core remains mapped until exit on Windows.
            try { Directory.Delete(root, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class PreviewApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentAvaloniaTheme());
    }
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
