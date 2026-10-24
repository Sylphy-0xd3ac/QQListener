using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QQListener.Models;

public partial class QqListenerSettings : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _saveLock = new();
    private readonly Action<string, string, string?, bool> _replaceFile;
    private volatile int _suppressAutoSaveCount;
    private bool _preserveBackupOnNextSave;

    public QqListenerSettings() : this(File.Replace)
    {
    }

    internal QqListenerSettings(Action<string, string, string?, bool> replaceFile)
    {
        ArgumentNullException.ThrowIfNull(replaceFile);
        _replaceFile = replaceFile;
    }

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private ListenerEngine _engine = ListenerEngine.SnowLuma;

    [ObservableProperty]
    private string _downloadsDirectory = GetDefaultDownloadsDirectory();

    [ObservableProperty]
    private bool _autoDownloadFiles;

    [ObservableProperty]
    private bool _replyOnConfirmEnabled;

    [ObservableProperty]
    private bool _replyWithOkReaction;

    [ObservableProperty]
    private bool _someoneAtMe = true;

    [ObservableProperty]
    private bool _callingEnabled = true;

    [ObservableProperty]
    private string _callingKeyword = "呼叫";

    [ObservableProperty]
    private int _cooldownSeconds = 3;

    [ObservableProperty]
    private int _normalDurationSeconds = 5;

    [ObservableProperty]
    private int _importantDurationSeconds = 10;

    [ObservableProperty]
    private int _callingDurationSeconds = 600;

    [ObservableProperty]
    private double _rollingSpeed = 1.0;

    /// <summary>暂停监听期间的消息积压上限（条）；超过后丢最旧的一条。之前这个数字
    /// 硬编码在 PendingQueue 的默认参数里，用户完全没法调整。</summary>
    [ObservableProperty]
    private int _pendingQueueLimit = 50;

    [ObservableProperty]
    private string _importantPersonsText = "";

    [ObservableProperty]
    private string _importantKeywordsText = "作业\n通知\n考试";

    [ObservableProperty]
    private string _blacklistText = "";

    [ObservableProperty]
    private string _allowedSourcesText = "";

    /// <summary>会话黑名单，一行一条：群号、QQ 号，或群名/昵称里的一段文字。命中即整条丢弃。</summary>
    [ObservableProperty]
    private string _conversationBlockListText = "";

    /// <summary>会话白名单，一行一条，格式同黑名单。非空时只有命中的会话才会提醒。</summary>
    [ObservableProperty]
    private string _conversationAllowListText = "";

    [ObservableProperty]
    private bool _ruleTimeWindowEnabled;

    [ObservableProperty]
    private string _ruleTimeWindowStartText = "00:00";

    [ObservableProperty]
    private string _ruleTimeWindowEndText = "00:00";

    /// <summary>上课期间继续捕获并积压消息，下课后按配置延迟释放。默认开启。</summary>
    [ObservableProperty]
    private bool _disableDuringClass = true;

    /// <summary>下课后继续积压的秒数；0 表示立即释放。</summary>
    [ObservableProperty]
    private int _afterClassDelaySeconds;

    [ObservableProperty]
    private bool _excludeSelfMessages = true;

    [ObservableProperty]
    private string _defaultReplyText = "收到";

    /// <summary>把消息念出来。默认开——课堂上人多半没在看屏幕，念一遍才是这个插件
    /// 真正起作用的方式。走 ClassIsland 自己的语音队列，不跟课程表播报抢音频通道。</summary>
    [ObservableProperty]
    private bool _speechEnabled = true;

    /// <summary>弹提醒时先"叮"一声。默认关——已经有语音播报了，再加一声提示音在
    /// 教室里只是多一次打断。</summary>
    [ObservableProperty]
    private bool _notificationSoundEnabled;

    /// <summary>性能模式：关闭模糊背板和出入场动画。</summary>
    [ObservableProperty]
    private bool _performanceMode;

    [ObservableProperty]
    private bool _normalTopmost;

    [ObservableProperty]
    private bool _importantTopmost;

    [ObservableProperty]
    private bool _callingTopmost = true;

    /// <summary>是否已在设置页里读过并同意 SnowLuma 的 EULA/LICENSE/隐私说明。
    /// 只有 Windows 上会用到；未同意前内核安装向导拒绝下载。</summary>
    [ObservableProperty]
    private bool _kernelEulaAccepted;

    /// <summary>下载 SnowLuma 官方 release 时用的加速代理前缀，用户自己填；
    /// 留空直接访问官方地址，程序不内置、不自建镜像。</summary>
    [ObservableProperty]
    private string _kernelDownloadProxy = "";

    [JsonIgnore]
    public IReadOnlyList<string> ImportantPersons => SplitLines(ImportantPersonsText);

    [JsonIgnore]
    public IReadOnlyList<string> ImportantKeywords => SplitLines(ImportantKeywordsText);

    [JsonIgnore]
    public IReadOnlyList<string> Blacklist => SplitLines(BlacklistText);

    [JsonIgnore]
    public IReadOnlyList<string> AllowedSources => SplitLines(AllowedSourcesText);

    [JsonIgnore]
    public IReadOnlyList<string> ConversationBlockList => SplitLines(ConversationBlockListText);

    [JsonIgnore]
    public IReadOnlyList<string> ConversationAllowList => SplitLines(ConversationAllowListText);

    [JsonIgnore]
    public bool SuppressAutoSave => _suppressAutoSaveCount > 0;

    [JsonIgnore]
    public string SettingsDirectory { get; set; } = "";

    [JsonIgnore]
    public string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    [JsonIgnore]
    public string BackupFilePath => SettingsFilePath + ".bak";

    // 之前是普通属性（get; private set;），SaveCoreAtomic 成功后清空它们时不会发
    // PropertyChanged——目前没有 UI 直接绑定这两个属性所以还没暴露，但只要有人
    // 想在设置页显示"设置已从备份恢复"，绑定就是死的。改成 ObservableProperty。
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _hasLoadWarning;

    [ObservableProperty]
    [property: JsonIgnore]
    private string _loadWarning = "";

    public static QqListenerSettings Load(string directory)
    {
        var primaryPath = Path.Combine(directory, "settings.json");
        var backupPath = primaryPath + ".bak";

        if (TryLoadFile(primaryPath, directory, out var primary))
        {
            return primary;
        }

        if (TryLoadFile(backupPath, directory, out var backup))
        {
            backup.IsEnabled = false;
            backup.HasLoadWarning = true;
            backup.LoadWarning = File.Exists(primaryPath)
                ? "主设置文件无法读取，已从备份恢复并关闭监听。"
                : "主设置文件缺失，已从备份恢复并关闭监听。";
            backup._preserveBackupOnNextSave = true;
            return backup;
        }

        // 两个文件都读不出来：只要其中任意一个文件确实存在（读取失败可能只是权限
        // 问题、被同步盘/杀软临时占用，不代表内容真的坏了），接下来第一次保存就绝
        // 不能顺手把这份还没读出来的历史数据连同备份一起覆盖成默认值——那样用户
        // 配置会彻底不可恢复。只有两个文件都真的不存在（全新安装）时才没什么可保护的。
        var eitherFileExists = File.Exists(primaryPath) || File.Exists(backupPath);
        var fallback = new QqListenerSettings
        {
            SettingsDirectory = directory,
            IsEnabled = false,
            HasLoadWarning = true,
            LoadWarning = eitherFileExists
                ? "设置文件及备份均无法读取，已使用关闭监听的默认设置。"
                : "未找到设置文件或备份，监听保持关闭。"
        };
        fallback._preserveBackupOnNextSave = eitherFileExists;
        return fallback;
    }

    public void Save()
    {
        lock (_saveLock)
        {
            SaveCoreAtomic();
        }
    }

    /// <summary>
    /// 原子地修改设置并保存。在修改期间抑制自动保存。
    /// </summary>
    public void ModifyAndSave(Action<QqListenerSettings> modifier)
    {
        lock (_saveLock)
        {
            Interlocked.Increment(ref _suppressAutoSaveCount);
            try
            {
                modifier(this);
            }
            finally
            {
                Interlocked.Decrement(ref _suppressAutoSaveCount);
            }
            SaveCoreAtomic();
        }
    }

    private static string GetDefaultDownloadsDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop")
            : desktop;
    }

    public void Normalize()
    {
        if (!Enum.IsDefined(Engine)) Engine = ListenerEngine.SnowLuma;
        DownloadsDirectory = string.IsNullOrWhiteSpace(DownloadsDirectory)
            ? GetDefaultDownloadsDirectory() : DownloadsDirectory.Trim();
        CooldownSeconds = Math.Clamp(CooldownSeconds, 1, 3600);
        NormalDurationSeconds = Math.Clamp(NormalDurationSeconds, 1, 3600);
        ImportantDurationSeconds = Math.Clamp(ImportantDurationSeconds, 1, 3600);
        CallingDurationSeconds = Math.Clamp(CallingDurationSeconds, 1, 86400);
        RollingSpeed = Math.Clamp(RollingSpeed, 0.0, 4.0);
        PendingQueueLimit = Math.Clamp(PendingQueueLimit, 1, 500);
        AfterClassDelaySeconds = Math.Clamp(AfterClassDelaySeconds, 0, 3600);
        DefaultReplyText = string.IsNullOrWhiteSpace(DefaultReplyText) ? "收到" : DefaultReplyText.Trim();
        CallingKeyword = (CallingKeyword ?? "").Trim();

        // 两端分别校验、分别回退——之前任何一端解析失败就把两端一起清零，用户只是
        // 把结束时间打错（比如 "25:00"），正确的开始时间也会被跟着悄悄清空。
        RuleTimeWindowStartText = TimeOnly.TryParseExact(
            RuleTimeWindowStartText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? start.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "00:00";
        RuleTimeWindowEndText = TimeOnly.TryParseExact(
            RuleTimeWindowEndText, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            ? end.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "00:00";
    }

    private static IReadOnlyList<string> SplitLines(string value)
    {
        return (value ?? "")
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
    }

    private void SaveCoreAtomic()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = $"{SettingsFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, this, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(SettingsFilePath))
            {
                File.Move(temporaryPath, SettingsFilePath);
            }
            else if (_preserveBackupOnNextSave)
            {
                _replaceFile(temporaryPath, SettingsFilePath, null, true);
            }
            else
            {
                _replaceFile(
                    temporaryPath,
                    SettingsFilePath,
                    BackupFilePath,
                    true);
            }

            _preserveBackupOnNextSave = false;
            HasLoadWarning = false;
            LoadWarning = "";
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool TryLoadFile(
        string path,
        string directory,
        out QqListenerSettings settings)
    {
        settings = null!;
        if (!File.Exists(path)) return false;

        try
        {
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<QqListenerSettings>(json)
                       ?? throw new JsonException("设置内容为空。");
            settings.SettingsDirectory = directory;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
