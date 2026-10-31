using System.Reflection;
using System.Runtime.Loader;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQListener.Updates;

namespace QQListener.Bootstrap;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Directory.CreateDirectory(PluginConfigFolder);
        var store = new RuntimeStore(PluginConfigFolder);
        var coordinator = new RuntimeBootstrap(store, new DistributionClient());
        var release = Task.Run(() => coordinator.PrepareAsync(Info.PluginFolderPath,
            LoaderSettings.Read(PluginConfigFolder), Log)).GetAwaiter().GetResult();
        var path = Path.Combine(store.DirectoryFor(release), "QQListener.dll");
        MethodInfo initialize;
        try
        {
            var loadContext = new CoreLoadContext(path, AssemblyLoadContext.GetLoadContext(typeof(Plugin).Assembly)!);
            var assembly = loadContext.LoadFromAssemblyPath(path);
            initialize = assembly.GetType("QQListener.Plugin", throwOnError: true)!
                .GetMethod("InitializeCore", BindingFlags.Public | BindingFlags.Static,
                    [typeof(HostBuilderContext), typeof(IServiceCollection), typeof(string), typeof(string),
                        typeof(string), typeof(string), typeof(string)])
                ?? throw new MissingMethodException("核心不支持此引导器，请更新 QQListener 插件包");
        }
        catch (Exception error)
        {
            store.Reject(release);
            Log(error.ToString());
            throw new IOException("QQListener 核心加载失败；下次启动将回退至上一版本", error);
        }
        try
        {
            initialize.Invoke(null, [context, services, PluginConfigFolder, Info.PluginFolderPath,
                Info.Manifest.Version ?? "", Info.Manifest.Author ?? "", Info.Manifest.Url]);
            store.Activate(release);
            Log($"Loaded {release.Version}, build {release.BuildNumber}, {release.Package.Sha256}");
        }
        catch (Exception error)
        {
            // Registration may already have changed DI/registries. Never run another core in this host.
            store.Reject(release);
            Log(error.ToString());
            throw new IOException("QQListener 核心初始化失败；请重启 ClassIsland 以使用上一版本", error);
        }
    }

    private void Log(string message)
    {
        try
        {
            var path = Path.Combine(PluginConfigFolder, "loader.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
        }
        catch (IOException) { }
    }
}

internal sealed class CoreLoadContext(string entry, AssemblyLoadContext parent) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver = new(entry);
    protected override Assembly? Load(AssemblyName name)
    {
        // The host owns these contracts and their static registries; a second copy breaks DI and UI.
        var id = name.Name ?? "";
        if (id.StartsWith("ClassIsland", StringComparison.Ordinal) || id.StartsWith("Avalonia", StringComparison.Ordinal)
            || id.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) || id.StartsWith("CommunityToolkit.", StringComparison.Ordinal)
            || id.StartsWith("FluentAvalonia", StringComparison.Ordinal)
            || id is "WinRT.Runtime" or "Microsoft.Windows.SDK.NET" or "System.Runtime")
            return parent.LoadFromAssemblyName(name);
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
