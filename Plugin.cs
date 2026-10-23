using System.Reflection;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Models.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QQListener.Automations.Actions;
using QQListener.Automations.Triggers;
using QQListener.Components;
using QQListener.Models;
using QQListener.Services;
using QQListener.Views.ActionSettingsControls;
using QQListener.Views.SettingsPages;

namespace QQListener;

[PluginEntrance]
public class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        Views.ItemsRepeaterAutomationCompatibility.Install();
        var settings = LoadStartupSettings(PluginConfigFolder);

        var diagnostics = new QqListenerDiagnosticsState();
        if (settings.HasLoadWarning)
        {
            diagnostics.RecordSettingsWarning(settings.LoadWarning);
        }

        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        diagnostics.RecordRuntimeInfo(
            Info.Manifest.Version ?? "",
            Info.PluginFolderPath,
            settings.SettingsFilePath,
            assemblyPath,
            File.Exists(assemblyPath) ? File.GetLastWriteTime(assemblyPath) : null);

        var pluginInfo = QqListenerPluginInfo.FromManifest(
            Info.Manifest.Version ?? "",
            Info.Manifest.Author ?? "",
            Info.Manifest.Url);

        RegisterRuntimeServices(services, settings, diagnostics);
        services.AddSingleton(pluginInfo);

        services.AddNotificationProvider<QqNotificationProvider>();
        services.AddComponent<LatestQqMessageComponent, LatestQqMessageComponentSettingsControl>();
        RegisterAutomations(services);
        RegisterActionMenu();

        services.AddSettingsPage<QqListenerSettingsPage>();
    }

    internal static QqListenerSettings LoadStartupSettings(string configFolder)
    {
        var settings = QqListenerSettings.Load(configFolder);
        settings.Normalize();
        // Normalize 仅在内存里把越界值夹紧；若不回写，磁盘上的非法值每次启动都会被
        // 重新读出并再次夹紧，用户从外部编辑文件时也会感到设置"被回退"。
        if (!settings.HasLoadWarning)
        {
            TrySaveSettings(settings);
        }

        return settings;
    }


    internal static void RegisterRuntimeServices(
        IServiceCollection services,
        QqListenerSettings settings,
        QqListenerDiagnosticsState diagnostics)
    {
        services.AddSingleton(settings);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ILessonStateProvider, ClassIslandLessonStateProvider>();
        services.AddSingleton<MessageRuleEvaluator>();
        services.AddSingleton<MessageEventPipeline>();
        // Native services use runtime platform checks, including portable net8.0 packages on Windows.
        if (!OperatingSystem.IsWindows())
        {
            services.AddSingleton<IQqNotificationPoller, NoOpNotificationPoller>();
            QQListener.Core.CoreRuntime.Set(
                QQListener.Core.CoreRuntimeState.Unsupported, "原生捕获只能在 Windows x64 上运行");
        }
        else
        {
            // 接管数据源：不再轮询 Windows 通知中心的弹窗文本，改用 SnowLuma 注入 +
            // HQP1 管道直接解析 QQ 的私有 SSO 报文。IQqNotificationPoller 这层抽象与
            // QqListenerWorker 的调用方式都不变，只是背后的实现换了。
            services.AddSingleton(sp =>
            {
                // 目录计算必须跟设置页安装向导（QqListenerSettingsPage 构造 KernelSetupViewModel
                // 时）用的是同一个方法——两边算出不一样的路径，会出现"向导显示安装成功，
                // 核心却永远找不到文件"的问题，之前这里就是这么坏掉的：CoreService 用的是
                // 默认参数（插件安装目录），向导装的是 SettingsDirectory/native。
                var settings = sp.GetRequiredService<QqListenerSettings>();
                var nativeDir = QQListener.Core.KernelLocator.ResolveNativeDirectory(settings.SettingsDirectory);
                return new QQListener.Core.CoreService(
                    findBinary: () => QQListener.Core.KernelLocator.FindHookBinary([nativeDir]));
            });
            services.AddHostedService<NativeCoreLifecycleService>();
            services.AddSingleton<NativeMessageEnrichment>(_ => NativeMessageEnrichment.ForCoreService());
            services.AddSingleton<IQqNotificationPoller>(sp =>
                NativeQqMessageSource.ForCoreService(
                    sp.GetRequiredService<QQListener.Core.CoreService>(),
                    sp.GetRequiredService<MessageEventPipeline>(),
                    sp.GetRequiredService<NativeMessageEnrichment>(),
                    sp.GetRequiredService<QqListenerSettings>(),
                    sp.GetRequiredService<ILessonStateProvider>(),
                    sp.GetRequiredService<TimeProvider>()));
        }

        services.AddSingleton<IQqListenerDelay, SystemQqListenerDelay>();
        services.AddSingleton<ClassIslandMessageNotifier>();
        services.AddSingleton<LatestMessageState>();
        services.AddSingleton<MessageAutomationEventHub>();
        services.AddSingleton<QqListenerWorker>();
        services.AddSingleton(diagnostics);
    }
    private static void TrySaveSettings(QqListenerSettings settings)
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            // 启动期间没有 logger，吞掉异常避免拖累整个宿主启动。
            System.Diagnostics.Debug.WriteLine(
                $"[QQListener] {QqListenerDiagnosticsState.FormatError(ex, "回写设置失败")}");
        }
    }

    private static void RegisterBaseActions(IServiceCollection services)
    {
        services.AddAction<SetListenerEnabledAction, SetListenerEnabledActionSettingsControl>();
        services.AddAction<ShowLatestMessageAction>();
        services.AddAction<ShowTestMessageAction>();
        services.AddAction<ClearLatestMessageAction>();
    }

    internal static void RegisterAutomations(IServiceCollection services)
    {
        RegisterBaseActions(services);
        services.AddTrigger<NormalMessageReceivedTrigger>();
        services.AddTrigger<ImportantMessageReceivedTrigger>();
        services.AddTrigger<CallingMessageReceivedTrigger>();
    }

    internal static IReadOnlyList<AutomationActionMenuContract> CreateActionMenuContracts() =>
    [
        new(
            typeof(SetListenerEnabledAction),
            SetListenerEnabledAction.AutomationId,
            "设置监听状态",
            "\uE73C"),
        new(
            typeof(ShowLatestMessageAction),
            ShowLatestMessageAction.AutomationId,
            "显示最后一条 QQ 消息",
            "\uE02B"),
        new(
            typeof(ClearLatestMessageAction),
            ClearLatestMessageAction.AutomationId,
            "清空最后一条 QQ 消息",
            "\uE74D")
    ];

    private static void RegisterActionMenu()
    {
        const string groupName = "QQListener 行动";
        if (!IActionService.ActionMenuTree.Contains(groupName))
        {
            IActionService.ActionMenuTree.Add(new ActionMenuTreeGroup(groupName, "\uE02B"));
        }

        var children = IActionService.ActionMenuTree[groupName];
        foreach (var contract in CreateActionMenuContracts())
        {
            if (children.Contains(contract.Name)) continue;

            if (contract.ActionType == typeof(SetListenerEnabledAction))
            {
                children.Add(new ActionMenuTreeItem<SetListenerEnabledActionSettings>(
                    contract.ActionId,
                    contract.Name,
                    contract.IconGlyph,
                    _ => { }));
            }
            else
            {
                children.Add(new ActionMenuTreeItem(
                    contract.ActionId,
                    contract.Name,
                    contract.IconGlyph));
            }
        }
    }
}

internal sealed record AutomationActionMenuContract(
    Type ActionType,
    string ActionId,
    string Name,
    string IconGlyph);
