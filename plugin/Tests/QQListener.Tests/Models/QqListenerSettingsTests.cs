using System.Text.Json;
using QQListener.Models;
using QQListener.Views.SettingsPages;
using Xunit;

namespace QQListener.Tests.Models;

public sealed class QqListenerSettingsTests
{
    [Fact]
    public void OldSettingsGetSelfExclusionAndNewOptionsRoundTripWithBounds()
    {
        var settings = JsonSerializer.Deserialize<QqListenerSettings>("{}")!;
        Assert.True(settings.ExcludeSelfMessages);
        Assert.Equal("收到", settings.DefaultReplyText);
        Assert.Equal(0, settings.AfterClassDelaySeconds);
        settings.AfterClassDelaySeconds = 99999;
        settings.DefaultReplyText = "  好的，已收到  ";
        settings.ExcludeSelfMessages = false;
        settings.Normalize();
        var loaded = JsonSerializer.Deserialize<QqListenerSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(3600, loaded.AfterClassDelaySeconds);
        Assert.Equal("好的，已收到", loaded.DefaultReplyText);
        Assert.False(loaded.ExcludeSelfMessages);
    }

    [Fact]
    public void OldSettingsDefaultToTtsOnlyAndAudioChoicesRoundTrip()
    {
        var old = JsonSerializer.Deserialize<QqListenerSettings>("{}")!;
        Assert.True(old.SpeechEnabled);
        Assert.False(old.NotificationSoundEnabled);
        old.SpeechEnabled = false;
        old.NotificationSoundEnabled = true;
        var loaded = JsonSerializer.Deserialize<QqListenerSettings>(JsonSerializer.Serialize(old))!;
        Assert.False(loaded.SpeechEnabled);
        Assert.True(loaded.NotificationSoundEnabled);
    }

    [Fact]
    public void NewRuleDefaultsAndDerivedAllowedSourcesAreStable()
    {
        var settings = new QqListenerSettings { AllowedSourcesText = " qq.toast \r\n\r\nOTHER.Source\n " };

        Assert.Equal(["qq.toast", "OTHER.Source"], settings.AllowedSources);
        Assert.False(settings.RuleTimeWindowEnabled);
        Assert.Equal("00:00", settings.RuleTimeWindowStartText);
        Assert.Equal("00:00", settings.RuleTimeWindowEndText);
        Assert.False(settings.NormalTopmost);
        Assert.False(settings.ImportantTopmost);
        Assert.True(settings.CallingTopmost);
    }

    [Fact]
    public void NormalizeResetsBothTimeBoundsWhenBothAreInvalid()
    {
        var settings = new QqListenerSettings
        {
            RuleTimeWindowStartText = "invalid",
            RuleTimeWindowEndText = "also-invalid"
        };

        settings.Normalize();

        Assert.Equal("00:00", settings.RuleTimeWindowStartText);
        Assert.Equal("00:00", settings.RuleTimeWindowEndText);
    }

    [Theory]
    [InlineData("08:00", "invalid", "08:00", "00:00")]
    [InlineData("invalid", "18:00", "00:00", "18:00")]
    public void NormalizeOnlyResetsTheInvalidTimeBoundAndKeepsTheValidOne(
        string start, string end, string expectedStart, string expectedEnd)
    {
        // 之前任何一端解析失败就把两端一起清零——用户只是把结束时间打错
        // （比如 "25:00"），正确的开始时间也会被跟着悄悄清空。
        var settings = new QqListenerSettings
        {
            RuleTimeWindowStartText = start,
            RuleTimeWindowEndText = end
        };

        settings.Normalize();

        Assert.Equal(expectedStart, settings.RuleTimeWindowStartText);
        Assert.Equal(expectedEnd, settings.RuleTimeWindowEndText);
    }

    [Fact]
    public void MissingPrimaryAndBackupUsesDisabledDefaults()
    {
        using var directory = new TemporaryDirectory();

        var settings = QqListenerSettings.Load(directory.Path);

        Assert.False(settings.IsEnabled);
        Assert.True(settings.HasLoadWarning);
        Assert.NotEmpty(settings.LoadWarning);
        Assert.False(File.Exists(settings.SettingsFilePath));
    }

    [Fact]
    public void SavingAfterBothFilesFailToLoadDoesNotOverwriteTheExistingBackup()
    {
        // 主文件和备份都因为内容损坏读取失败——这是"临时性问题"的一种简化模拟；
        // 关键在于两个文件都真实存在，代表用户历史上确实有过配置数据。之前这种
        // 情况下第一次保存会把刚生成的默认设置通过 File.Replace 写进主文件，
        // 同时把主文件"旧内容"（也就是这份已经读不出来的坏数据）复制进备份，
        // 连同备份里那份唯一还没被确认损坏的副本一起冲掉。
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.File("settings.json");
        var backupPath = primaryPath + ".bak";
        File.WriteAllText(primaryPath, "{ not valid json");
        File.WriteAllText(backupPath, "{ this looks corrupted too, but it is the user's only remaining copy");
        var backupContentBefore = File.ReadAllBytes(backupPath);

        var settings = QqListenerSettings.Load(directory.Path);
        Assert.True(settings.HasLoadWarning);

        settings.Save();

        Assert.Equal(backupContentBefore, File.ReadAllBytes(backupPath));
        Assert.True(File.Exists(primaryPath));
    }

    [Fact]
    public void SavingAfterFreshInstallWithNoExistingFilesBehavesNormally()
    {
        // 对照组：两个文件都真的从未存在过（全新安装），没有什么需要保护，
        // 第一次保存应该正常在两个位置都留下痕迹——备份保护逻辑不能矫枉过正到
        // 连全新安装都不敢正常写入备份。
        using var directory = new TemporaryDirectory();
        var primaryPath = directory.File("settings.json");
        var backupPath = primaryPath + ".bak";

        var settings = QqListenerSettings.Load(directory.Path);
        settings.Save();
        settings.ModifyAndSave(s => s.IsEnabled = !s.IsEnabled); // 第二次保存才会真正产生备份

        Assert.True(File.Exists(primaryPath));
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public void StartupDecisionKeepsMissingSettingsDisabledWithoutCreatingFiles()
    {
        using var directory = new TemporaryDirectory();

        var settings = Plugin.LoadStartupSettings(directory.Path);

        Assert.True(settings.HasLoadWarning);
        Assert.False(settings.IsEnabled);
        Assert.False(File.Exists(directory.File("settings.json")));
        Assert.False(File.Exists(directory.File("settings.json.bak")));
    }

    [Fact]
    public void StartupDecisionNormalizesAndPersistsValidSettings()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            directory.File("settings.json"),
            """
            {
              "IsEnabled": true,
              "CooldownSeconds": 0,
              "PendingQueueLimit": 9999,
              "CallingKeyword": "  call me  "
            }
            """);

        var settings = Plugin.LoadStartupSettings(directory.Path);

        Assert.False(settings.HasLoadWarning);
        Assert.True(settings.IsEnabled);
        Assert.Equal(500, settings.PendingQueueLimit);
        Assert.Equal(1, settings.CooldownSeconds);
        Assert.Equal("call me", settings.CallingKeyword);
        Assert.Equal(500, ReadInt32(settings.SettingsFilePath, "PendingQueueLimit"));
        Assert.Equal(1, ReadInt32(settings.SettingsFilePath, "CooldownSeconds"));
        Assert.Equal("call me", ReadString(settings.SettingsFilePath, "CallingKeyword"));
        Assert.True(File.Exists(settings.BackupFilePath));
    }

    [Fact]
    public void MissingPrimaryLoadsValidBackupDisabledWithWarning()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            directory.File("settings.json.bak"),
            "{\"IsEnabled\":true,\"BlacklistText\":\"secret\"}");

        var settings = QqListenerSettings.Load(directory.Path);

        Assert.False(settings.IsEnabled);
        Assert.Equal("secret", settings.BlacklistText);
        Assert.True(settings.HasLoadWarning);
        Assert.False(File.Exists(settings.SettingsFilePath));
    }

    [Fact]
    public void CorruptPrimaryLoadsBackupDisabledWithoutOverwritingPrimary()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("settings.json"), "{broken");
        File.WriteAllText(
            directory.File("settings.json.bak"),
            "{\"IsEnabled\":true,\"BlacklistText\":\"secret\"}");

        var settings = QqListenerSettings.Load(directory.Path);

        Assert.False(settings.IsEnabled);
        Assert.Equal("secret", settings.BlacklistText);
        Assert.True(settings.HasLoadWarning);
        Assert.DoesNotContain("secret", settings.LoadWarning);
        Assert.Equal("{broken", File.ReadAllText(directory.File("settings.json")));
    }

    [Fact]
    public void CorruptPrimaryAndBackupUseDisabledDefaultsWithoutOverwritingFiles()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("settings.json"), "bad-primary");
        File.WriteAllText(directory.File("settings.json.bak"), "bad-backup");

        var settings = QqListenerSettings.Load(directory.Path);

        Assert.False(settings.IsEnabled);
        Assert.True(settings.HasLoadWarning);
        Assert.Equal("bad-primary", File.ReadAllText(directory.File("settings.json")));
        Assert.Equal("bad-backup", File.ReadAllText(directory.File("settings.json.bak")));
    }

    [Fact]
    public void SaveCreatesBackupAndLeavesValidJson()
    {
        using var directory = new TemporaryDirectory();
        var settings = new QqListenerSettings
        {
            SettingsDirectory = directory.Path,
            IsEnabled = false,
            BlacklistText = "first"
        };
        settings.Save();
        settings.BlacklistText = "second";

        settings.Save();

        Assert.Equal("second", ReadString(settings.SettingsFilePath, "BlacklistText"));
        Assert.Equal("first", ReadString(settings.BackupFilePath, "BlacklistText"));
    }

    [Fact]
    public void ModifyAndSaveCreatesBackupAndLeavesValidJson()
    {
        using var directory = new TemporaryDirectory();
        var settings = new QqListenerSettings
        {
            SettingsDirectory = directory.Path,
            IsEnabled = false,
            BlacklistText = "first"
        };
        settings.Save();

        settings.ModifyAndSave(value => value.BlacklistText = "second");

        Assert.Equal("second", ReadString(settings.SettingsFilePath, "BlacklistText"));
        Assert.Equal("first", ReadString(settings.BackupFilePath, "BlacklistText"));
    }

    [Fact]
    public void RecoveredBackupFirstSaveKeepsPrimaryAndBackupValid()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.File("settings.json"), "{broken");
        File.WriteAllText(
            directory.File("settings.json.bak"),
            "{\"IsEnabled\":true,\"BlacklistText\":\"backup\"}");
        var settings = QqListenerSettings.Load(directory.Path);
        settings.BlacklistText = "recovered";

        settings.Save();

        Assert.Equal("recovered", ReadString(settings.SettingsFilePath, "BlacklistText"));
        Assert.Equal("backup", ReadString(settings.BackupFilePath, "BlacklistText"));
    }

    [Fact]
    public void OpenReadHandleUsesPlatformReplaceSemantics()
    {
        using var directory = new TemporaryDirectory();
        var settings = new QqListenerSettings
        {
            SettingsDirectory = directory.Path,
            IsEnabled = false,
            BlacklistText = "first"
        };
        settings.Save();
        settings.BlacklistText = "second";
        settings.Save();
        var primaryBefore = File.ReadAllText(settings.SettingsFilePath);
        var backupBefore = File.ReadAllText(settings.BackupFilePath);
        settings.BlacklistText = "third";

        using var primaryLock = new FileStream(
            settings.SettingsFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<IOException>(() => settings.Save());
            Assert.Equal(primaryBefore, File.ReadAllText(settings.SettingsFilePath));
            Assert.Equal(backupBefore, File.ReadAllText(settings.BackupFilePath));
        }
        else
        {
            settings.Save();
            Assert.Equal("third", ReadString(settings.SettingsFilePath, "BlacklistText"));
            Assert.Equal("second", ReadString(settings.BackupFilePath, "BlacklistText"));
        }

        Assert.Empty(Directory.GetFiles(directory.Path, "settings.json.*.tmp"));
    }

    [Fact]
    public void InjectedReplaceFailurePreservesFilesAndDeletesTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var settings = new QqListenerSettings
        {
            SettingsDirectory = directory.Path,
            IsEnabled = false,
            BlacklistText = "first"
        };
        settings.Save();
        settings.BlacklistText = "second";
        settings.Save();
        var primaryBefore = File.ReadAllText(settings.SettingsFilePath);
        var backupBefore = File.ReadAllText(settings.BackupFilePath);

        string? temporaryPath = null;
        var temporaryExistedAtReplace = false;
        var failing = new QqListenerSettings((source, _, _, _) =>
        {
            temporaryPath = source;
            temporaryExistedAtReplace = File.Exists(source);
            throw new IOException("injected replace failure");
        })
        {
            SettingsDirectory = directory.Path,
            IsEnabled = false,
            BlacklistText = "third"
        };

        var exception = Assert.Throws<IOException>(() => failing.Save());

        Assert.Equal("injected replace failure", exception.Message);
        Assert.True(temporaryExistedAtReplace);
        Assert.NotNull(temporaryPath);
        Assert.False(File.Exists(temporaryPath));
        Assert.Equal(primaryBefore, File.ReadAllText(failing.SettingsFilePath));
        Assert.Equal(backupBefore, File.ReadAllText(failing.BackupFilePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "settings.json.*.tmp"));
    }

    [Fact]
    public void PersistenceCoordinatorFlushesOnDetachAndSavesAfterReattach()
    {
        using var directory = new TemporaryDirectory();
        var settings = new QqListenerSettings
        {
            SettingsDirectory = directory.Path,
            IsEnabled = false
        };
        var diagnostics = new QqListenerDiagnosticsState();
        var coordinator = new QqListenerSettingsPersistenceCoordinator(
            settings,
            diagnostics,
            _ => { });

        coordinator.Attach();
        settings.BlacklistText = "first";
        coordinator.Detach();
        Assert.Equal("first", ReadString(settings.SettingsFilePath, "BlacklistText"));

        coordinator.Attach();
        settings.BlacklistText = "second";
        coordinator.Detach();
        Assert.Equal("second", ReadString(settings.SettingsFilePath, "BlacklistText"));
    }

    [Fact]
    public void PersistenceCoordinatorKeepsDirtyStateAfterSaveFailureForRetry()
    {
        using var directory = new TemporaryDirectory();
        var invalidDirectory = directory.File("not-a-directory");
        File.WriteAllText(invalidDirectory, "blocking file");
        var settings = new QqListenerSettings
        {
            SettingsDirectory = invalidDirectory,
            IsEnabled = false
        };
        var diagnostics = new QqListenerDiagnosticsState();
        var coordinator = new QqListenerSettingsPersistenceCoordinator(
            settings,
            diagnostics,
            _ => { });

        coordinator.Attach();
        settings.BlacklistText = "retry";
        coordinator.Detach();

        Assert.True(coordinator.IsDirty);
        Assert.Contains("保存设置失败", diagnostics.LastErrorSummary);

        settings.SettingsDirectory = directory.Path;
        coordinator.Attach();
        coordinator.Detach();

        Assert.False(coordinator.IsDirty);
        Assert.Equal("retry", ReadString(settings.SettingsFilePath, "BlacklistText"));
    }

    private static string ReadString(string path, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(propertyName).GetString() ?? "";
    }

    private static int ReadInt32(string path, string propertyName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty(propertyName).GetInt32();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"QQListener.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
