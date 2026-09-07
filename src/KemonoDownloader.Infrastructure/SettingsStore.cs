using System.Text.Json;
using System.Text.Json.Serialization;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class SettingsStore : ISettingsStore
{
    private readonly string _settingsPath;

    public SettingsStore(string? appDataPath = null)
    {
        var directory = appDataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KemonoDownloader");
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public AppSettings Current { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            PersistedSettings? persisted;
            await using (var stream = File.OpenRead(_settingsPath))
            {
                persisted = await JsonSerializer.DeserializeAsync<PersistedSettings>(stream, JsonDefaults.Options, cancellationToken);
            }
            Current = persisted?.ToSettings() ?? new AppSettings();
            KemonoRules.Validate(Current);
            if (persisted is not null && persisted.SettingsVersion < AppSettings.CurrentSettingsVersion)
            {
                await SaveAsync(Current, cancellationToken);
            }
        }
        catch (Exception error) when (error is JsonException or IOException or ArgumentException or InvalidOperationException)
        {
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var domain = KemonoRules.NormalizeDomain(settings.Domain);
        settings = settings with
        {
            Domain = domain,
            Domains = KemonoRules.NormalizeDomains(settings.Domains.Append(domain))
        };
        KemonoRules.Validate(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = $"{_settingsPath}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, PersistedSettings.FromSettings(settings), JsonDefaults.Options, cancellationToken);
        }
        File.Move(temporaryPath, _settingsPath, true);
        Current = settings;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default) => await SaveAsync(new AppSettings(), cancellationToken);

    private sealed record PersistedSettings
    {
        [JsonPropertyName("settings_version")]
        public int SettingsVersion { get; init; }
        [JsonPropertyName("domain")]
        public string Domain { get; init; } = "kemono.cr";
        [JsonPropertyName("domains")]
        public IReadOnlyList<string> Domains { get; init; } = AppSettings.DefaultDomains;
        [JsonPropertyName("retries")]
        public int Retries { get; init; } = 5;
        [JsonPropertyName("image_timeout_seconds")]
        public int ImageTimeoutSeconds { get; init; } = 60;
        [JsonPropertyName("video_timeout_seconds")]
        public int VideoTimeoutSeconds { get; init; } = 1200;
        [JsonPropertyName("request_timeout_seconds")]
        public int RequestTimeoutSeconds { get; init; } = 30;
        [JsonPropertyName("download_header_timeout_seconds")]
        public int DownloadHeaderTimeoutSeconds { get; init; } = 30;
        [JsonPropertyName("download_min_speed_kib_per_second")]
        public int DownloadMinimumSpeedKibPerSecond { get; init; } = 8;
        [JsonPropertyName("download_low_speed_window_seconds")]
        public int DownloadLowSpeedWindowSeconds { get; init; } = 300;
        [JsonPropertyName("download_max_total_hours")]
        public int DownloadMaxTotalHours { get; init; } = 24;
        [JsonPropertyName("page_request_delay_ms")]
        public int PageRequestDelayMs { get; init; } = 1500;
        [JsonPropertyName("default_concurrent")]
        public int DefaultConcurrent { get; init; } = 5;
        [JsonPropertyName("batch_delay_ms")]
        public int BatchDelayMs { get; init; } = 500;
        [JsonPropertyName("skip_existing_default")]
        public bool SkipExistingDefault { get; init; } = true;
        [JsonPropertyName("proxy")]
        public PersistedProxy Proxy { get; init; } = new();
        [JsonPropertyName("workspace")]
        public WorkspaceSettings Workspace { get; init; } = new();

        public AppSettings ToSettings()
        {
            var domain = KemonoRules.NormalizeDomain(Domain) is { Length: > 0 } value ? value : KemonoRules.DefaultDomain;
            var legacy = SettingsVersion < AppSettings.CurrentSettingsVersion;
            return new AppSettings
            {
                SettingsVersion = AppSettings.CurrentSettingsVersion,
                Domain = domain,
                Domains = KemonoRules.NormalizeDomains(Domains.Append(domain)),
                Retries = legacy && Retries == 5 ? 3 : Retries,
                ImageTimeoutSeconds = ImageTimeoutSeconds,
                VideoTimeoutSeconds = legacy && VideoTimeoutSeconds == 1200 ? 120 : VideoTimeoutSeconds,
                RequestTimeoutSeconds = RequestTimeoutSeconds,
                DownloadHeaderTimeoutSeconds = DownloadHeaderTimeoutSeconds,
                DownloadMinimumSpeedKibPerSecond = DownloadMinimumSpeedKibPerSecond,
                DownloadLowSpeedWindowSeconds = DownloadLowSpeedWindowSeconds,
                DownloadMaxTotalHours = DownloadMaxTotalHours,
                PageRequestDelayMs = PageRequestDelayMs,
                DefaultConcurrent = DefaultConcurrent,
                BatchDelayMs = BatchDelayMs,
                SkipExistingDefault = SkipExistingDefault,
                Proxy = Proxy.ToSettings(),
                Workspace = Workspace
            };
        }

        public static PersistedSettings FromSettings(AppSettings settings) => new()
        {
            SettingsVersion = AppSettings.CurrentSettingsVersion,
            Domain = KemonoRules.NormalizeDomain(settings.Domain),
            Domains = KemonoRules.NormalizeDomains(settings.Domains),
            Retries = settings.Retries,
            ImageTimeoutSeconds = settings.ImageTimeoutSeconds,
            VideoTimeoutSeconds = settings.VideoTimeoutSeconds,
            RequestTimeoutSeconds = settings.RequestTimeoutSeconds,
            DownloadHeaderTimeoutSeconds = settings.DownloadHeaderTimeoutSeconds,
            DownloadMinimumSpeedKibPerSecond = settings.DownloadMinimumSpeedKibPerSecond,
            DownloadLowSpeedWindowSeconds = settings.DownloadLowSpeedWindowSeconds,
            DownloadMaxTotalHours = settings.DownloadMaxTotalHours,
            PageRequestDelayMs = settings.PageRequestDelayMs,
            DefaultConcurrent = settings.DefaultConcurrent,
            BatchDelayMs = settings.BatchDelayMs,
            SkipExistingDefault = settings.SkipExistingDefault,
            Proxy = PersistedProxy.FromSettings(settings.Proxy),
            Workspace = settings.Workspace
        };
    }

    private sealed record PersistedProxy
    {
        [JsonPropertyName("type")]
        public ProxyType Type { get; init; }
        [JsonPropertyName("host")]
        public string Host { get; init; } = string.Empty;
        [JsonPropertyName("port")]
        public int Port { get; init; } = 1080;
        [JsonPropertyName("username")]
        public string Username { get; init; } = string.Empty;
        [JsonPropertyName("protected_password")]
        public string ProtectedPassword { get; init; } = string.Empty;

        public ProxySettings ToSettings() => new() { Type = Type, Host = Host, Port = Port, Username = Username, Password = WindowsDpapi.Unprotect(ProtectedPassword) };
        public static PersistedProxy FromSettings(ProxySettings settings) => new() { Type = settings.Type, Host = settings.Host, Port = settings.Port, Username = settings.Username, ProtectedPassword = WindowsDpapi.Protect(settings.Password) };
    }
}
