using KemonoDownloader.Core;
using KemonoDownloader.Infrastructure;
using System.Text.Json;

namespace KemonoDownloader.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task CustomDomains_AreNormalizedAndPersisted()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var store = new SettingsStore(directory.Path);
        await store.SaveAsync(new AppSettings
        {
            Domain = "https://mirror.example.com/",
            Domains = ["kemono.cr", "kemono.su", "https://mirror.example.com/"],
            Workspace = new WorkspaceSettings
            {
                SelectedTab = 1,
                AuthorDomain = "kemono.su",
                AuthorService = "patreon",
                AuthorId = "42",
                AuthorSavePath = "D:\\downloads\\authors",
                SinglePostUrl = "https://pawchive.pw/fanbox/user/1/post/2",
                SingleSavePath = "D:\\downloads\\single",
                RepairSizeLimitKb = 256,
                RepairBatchMode = true
            }
        });

        var reloaded = new SettingsStore(directory.Path);
        await reloaded.InitializeAsync();

        Assert.Equal("mirror.example.com", reloaded.Current.Domain);
        Assert.Contains("kemono.cr", reloaded.Current.Domains);
        Assert.Contains("kemono.su", reloaded.Current.Domains);
        Assert.Contains("mirror.example.com", reloaded.Current.Domains);
        Assert.Equal(1, reloaded.Current.Workspace.SelectedTab);
        Assert.Equal("kemono.su", reloaded.Current.Workspace.AuthorDomain);
        Assert.Equal("patreon", reloaded.Current.Workspace.AuthorService);
        Assert.Equal("42", reloaded.Current.Workspace.AuthorId);
        Assert.Equal("D:\\downloads\\authors", reloaded.Current.Workspace.AuthorSavePath);
        Assert.Equal("D:\\downloads\\single", reloaded.Current.Workspace.SingleSavePath);
        Assert.Equal(256, reloaded.Current.Workspace.RepairSizeLimitKb);
        Assert.True(reloaded.Current.Workspace.RepairBatchMode);
    }

    [Fact]
    public async Task LegacyDefaults_AreMigratedAndRewritten()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "domain": "kemono.cr",
              "domains": ["kemono.cr", "kemono.su"],
              "retries": 5,
              "image_timeout_seconds": 60,
              "video_timeout_seconds": 1200
            }
            """);

        var store = new SettingsStore(directory.Path);
        await store.InitializeAsync();

        Assert.Equal(AppSettings.CurrentSettingsVersion, store.Current.SettingsVersion);
        Assert.Equal(3, store.Current.Retries);
        Assert.Equal(120, store.Current.VideoTimeoutSeconds);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
        Assert.Equal(AppSettings.CurrentSettingsVersion, document.RootElement.GetProperty("settings_version").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("retries").GetInt32());
        Assert.Equal(120, document.RootElement.GetProperty("video_timeout_seconds").GetInt32());
    }

    [Fact]
    public async Task CurrentVersion_ExplicitRetryValueIsPreserved()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), $$"""
            {
              "settings_version": {{AppSettings.CurrentSettingsVersion}},
              "domain": "kemono.cr",
              "domains": ["kemono.cr", "kemono.su"],
              "retries": 5,
              "video_timeout_seconds": 1200
            }
            """);

        var store = new SettingsStore(directory.Path);
        await store.InitializeAsync();

        Assert.Equal(5, store.Current.Retries);
        Assert.Equal(1200, store.Current.VideoTimeoutSeconds);
    }

    [Fact]
    public async Task AdvancedDownloadTimeoutSettings_ArePersisted()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var store = new SettingsStore(directory.Path);
        await store.SaveAsync(new AppSettings
        {
            DownloadHeaderTimeoutSeconds = 45,
            DownloadMinimumSpeedKibPerSecond = 12,
            DownloadLowSpeedWindowSeconds = 420,
            DownloadMaxTotalHours = 18
        });

        var reloaded = new SettingsStore(directory.Path);
        await reloaded.InitializeAsync();

        Assert.Equal(45, reloaded.Current.DownloadHeaderTimeoutSeconds);
        Assert.Equal(12, reloaded.Current.DownloadMinimumSpeedKibPerSecond);
        Assert.Equal(420, reloaded.Current.DownloadLowSpeedWindowSeconds);
        Assert.Equal(18, reloaded.Current.DownloadMaxTotalHours);
    }
}
