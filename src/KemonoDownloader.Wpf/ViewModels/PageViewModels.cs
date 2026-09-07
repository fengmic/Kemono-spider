using CommunityToolkit.Mvvm.ComponentModel;
using KemonoDownloader.Core;
using System.Collections.ObjectModel;

namespace KemonoDownloader.Wpf.ViewModels;

public partial class AuthorDownloadViewModel : ObservableObject
{
    public IReadOnlyList<string> Services { get; } = ["fanbox", "patreon", "fantia", "subscribestar", "gumroad", "discord"];
    [ObservableProperty] private string _domain = KemonoRules.DefaultDomain;
    [ObservableProperty] private string _service = "fanbox";
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _savePath = string.Empty;
    [ObservableProperty] private int _limit;
    [ObservableProperty] private int _concurrent = 5;
    [ObservableProperty] private bool _skipExisting = true;
    [ObservableProperty] private bool _useThumbnail;
    [ObservableProperty] private bool _forceFresh;
}

public partial class AuthorQueueItemViewModel : ObservableObject
{
    public AuthorQueueItemViewModel(DownloadConfig config) => Config = config;

    public Guid Id { get; } = Guid.NewGuid();
    public DownloadConfig Config { get; }
    public string Title => $"{Config.Service} / {Config.Username}";
    public string IdentityDetail => $"{Config.Service} / {Config.Username} · {Config.Domain} · 并发 {Config.Concurrent} · {(Config.Limit > 0 ? $"限制 {Config.Limit}" : "全部作品")}";
    [ObservableProperty] private string _authorName = "正在获取作者名称...";
    [ObservableProperty] private string _statusText = "等待中";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _currentItem = "等待前序任务";
    public bool IsPending => StatusText == "等待中";

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(IsPending));
}

public partial class SinglePostViewModel : ObservableObject
{
    [ObservableProperty] private string _postUrl = string.Empty;
    [ObservableProperty] private string _savePath = string.Empty;
    [ObservableProperty] private int _concurrent = 5;
    [ObservableProperty] private bool _skipExisting = true;
    [ObservableProperty] private bool _useThumbnail;
    [ObservableProperty] private bool _forceFresh;
}

public partial class ResumeViewModel : ObservableObject
{
    [ObservableProperty] private string _basePath = string.Empty;
    [ObservableProperty] private string _taskInfo = "尚未加载进度文件";
    [ObservableProperty] private bool _resetProgress;
    [ObservableProperty] private DownloadConfig? _config;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _totalCount;
}

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string _selectedDomain = KemonoRules.DefaultDomain;
    [ObservableProperty] private string _newDomain = string.Empty;
    [ObservableProperty] private int _retries;
    [ObservableProperty] private int _imageTimeoutSeconds;
    [ObservableProperty] private int _videoTimeoutSeconds;
    [ObservableProperty] private int _downloadHeaderTimeoutSeconds;
    [ObservableProperty] private int _downloadMinimumSpeedKibPerSecond;
    [ObservableProperty] private int _downloadLowSpeedWindowSeconds;
    [ObservableProperty] private int _downloadMaxTotalHours;
    [ObservableProperty] private int _pageRequestDelayMs;
    [ObservableProperty] private int _defaultConcurrent;
    [ObservableProperty] private int _batchDelayMs;
    [ObservableProperty] private bool _skipExistingDefault;
    [ObservableProperty] private ProxyType _proxyType;
    [ObservableProperty] private string _proxyHost = string.Empty;
    [ObservableProperty] private int _proxyPort;
    [ObservableProperty] private string _proxyUsername = string.Empty;
    [ObservableProperty] private string _proxyPassword = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public IReadOnlyList<ProxyType> ProxyTypes { get; } = Enum.GetValues<ProxyType>();
    public ObservableCollection<string> Domains { get; } = [];
    public bool CanRemoveSelectedDomain => !AppSettings.DefaultDomains.Contains(SelectedDomain, StringComparer.OrdinalIgnoreCase);

    partial void OnSelectedDomainChanged(string value) => OnPropertyChanged(nameof(CanRemoveSelectedDomain));

    public void Load(AppSettings settings)
    {
        Domains.Clear();
        foreach (var domain in KemonoRules.NormalizeDomains(settings.Domains)) Domains.Add(domain);
        SelectedDomain = Domains.Contains(settings.Domain, StringComparer.OrdinalIgnoreCase) ? settings.Domain : Domains[0];
        NewDomain = string.Empty;
        Retries = settings.Retries;
        ImageTimeoutSeconds = settings.ImageTimeoutSeconds;
        VideoTimeoutSeconds = settings.VideoTimeoutSeconds;
        DownloadHeaderTimeoutSeconds = settings.DownloadHeaderTimeoutSeconds;
        DownloadMinimumSpeedKibPerSecond = settings.DownloadMinimumSpeedKibPerSecond;
        DownloadLowSpeedWindowSeconds = settings.DownloadLowSpeedWindowSeconds;
        DownloadMaxTotalHours = settings.DownloadMaxTotalHours;
        PageRequestDelayMs = settings.PageRequestDelayMs;
        DefaultConcurrent = settings.DefaultConcurrent;
        BatchDelayMs = settings.BatchDelayMs;
        SkipExistingDefault = settings.SkipExistingDefault;
        ProxyType = settings.Proxy.Type;
        ProxyHost = settings.Proxy.Host;
        ProxyPort = settings.Proxy.Port;
        ProxyUsername = settings.Proxy.Username;
        ProxyPassword = settings.Proxy.Password;
    }

    public AppSettings Build() => new()
    {
        Domain = SelectedDomain,
        Domains = Domains.ToArray(),
        Retries = Retries,
        ImageTimeoutSeconds = ImageTimeoutSeconds,
        VideoTimeoutSeconds = VideoTimeoutSeconds,
        DownloadHeaderTimeoutSeconds = DownloadHeaderTimeoutSeconds,
        DownloadMinimumSpeedKibPerSecond = DownloadMinimumSpeedKibPerSecond,
        DownloadLowSpeedWindowSeconds = DownloadLowSpeedWindowSeconds,
        DownloadMaxTotalHours = DownloadMaxTotalHours,
        PageRequestDelayMs = PageRequestDelayMs,
        DefaultConcurrent = DefaultConcurrent,
        BatchDelayMs = BatchDelayMs,
        SkipExistingDefault = SkipExistingDefault,
        Proxy = new ProxySettings { Type = ProxyType, Host = ProxyHost.Trim(), Port = ProxyPort, Username = ProxyUsername.Trim(), Password = ProxyPassword }
    };
}

public partial class RepairViewModel : ObservableObject
{
    public IReadOnlyList<RepairModeOption> Modes { get; } =
    [
        new(RepairScanMode.Missing, "缺失补足"),
        new(RepairScanMode.Corrupted, "损坏修复")
    ];

    [ObservableProperty] private string _basePath = string.Empty;
    [ObservableProperty] private int _fileSizeLimitKb = 100;
    [ObservableProperty] private int _concurrent = 5;
    [ObservableProperty] private RepairModeOption _selectedMode;
    [ObservableProperty] private bool _useThumbnail;
    [ObservableProperty] private bool _isBatchMode;
    [ObservableProperty] private string _resultText = "选择作者合集目录后扫描缺失文件";
    [ObservableProperty] private bool _canRepair;
    [ObservableProperty] private bool _canViewMissingFiles;
    [ObservableProperty] private int _corruptedCount;

    public RepairViewModel() => _selectedMode = Modes[0];
    public bool IsMissingMode => SelectedMode.Mode == RepairScanMode.Missing;
    public string ScanButtonText => $"{(IsBatchMode ? "批量" : string.Empty)}扫描{(IsMissingMode ? "缺失" : "损坏")}文件";
    public string RepairButtonText => IsBatchMode
        ? (IsMissingMode ? "批量补足" : "批量修复")
        : (IsMissingMode ? "开始补足" : "开始修复");
    public string PathLabel => IsBatchMode ? "作者合集上一级目录" : "作者合集目录";

    partial void OnSelectedModeChanged(RepairModeOption value)
    {
        CanRepair = false;
        CanViewMissingFiles = false;
        ResultText = IsMissingMode ? "选择作者合集目录后扫描缺失文件" : "选择作者合集目录后扫描损坏文件";
        OnPropertyChanged(nameof(IsMissingMode));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(RepairButtonText));
    }

    partial void OnIsBatchModeChanged(bool value)
    {
        CanRepair = false;
        CanViewMissingFiles = false;
        ResultText = value ? "选择包含多个作者目录的上一级文件夹" : (IsMissingMode ? "选择作者合集目录后扫描缺失文件" : "选择作者合集目录后扫描损坏文件");
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(RepairButtonText));
        OnPropertyChanged(nameof(PathLabel));
    }
}

public sealed record RepairModeOption(RepairScanMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public partial class TaskViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "暂无活动任务";
    [ObservableProperty] private string _statusText = "空闲";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _currentItem = "等待任务";
    [ObservableProperty] private int _downloadedFiles;
    [ObservableProperty] private bool _isIncremental;
    [ObservableProperty] private string _incrementalText = string.Empty;
}

public sealed record LogEntryViewModel(DateTimeOffset Timestamp, string Message, LogLevel Level)
{
    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string DisplayText => $"[{Time}] {Message}";
}
