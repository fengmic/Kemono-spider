using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KemonoDownloader.Core;
using KemonoDownloader.Wpf.Services;

namespace KemonoDownloader.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IScraperService _scraperService;
    private readonly IRepairService _repairService;
    private readonly IProgressStore _progressStore;
    private readonly ISettingsStore _settingsStore;
    private readonly INetworkClientProvider _networkClientProvider;
    private readonly IFolderPickerService _folderPicker;
    private readonly IUserDialogService _dialogService;
    private RepairScanResult? _lastScan;
    private CancellationTokenSource? _operationCancellation;
    private Task? _activeOperation;
    private bool _authorQueueRunnerActive;

    public MainViewModel(
        IScraperService scraperService,
        IRepairService repairService,
        IProgressStore progressStore,
        ISettingsStore settingsStore,
        INetworkClientProvider networkClientProvider,
        IFolderPickerService folderPicker,
        IUserDialogService? dialogService = null)
    {
        _scraperService = scraperService;
        _repairService = repairService;
        _progressStore = progressStore;
        _settingsStore = settingsStore;
        _networkClientProvider = networkClientProvider;
        _folderPicker = folderPicker;
        _dialogService = dialogService ?? new UserDialogService();

        Author = new AuthorDownloadViewModel();
        SinglePost = new SinglePostViewModel();
        Resume = new ResumeViewModel();
        Settings = new SettingsViewModel();
        Repair = new RepairViewModel();
        Repair.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RepairViewModel.SelectedMode) or nameof(RepairViewModel.IsBatchMode)) _lastScan = null;
            if (args.PropertyName is nameof(RepairViewModel.SelectedMode) or nameof(RepairViewModel.CanRepair)) RepairFilesCommand.NotifyCanExecuteChanged();
            if (args.PropertyName is nameof(RepairViewModel.SelectedMode) or nameof(RepairViewModel.CanViewMissingFiles)) ViewMissingFilesCommand.NotifyCanExecuteChanged();
        };
        Task = new TaskViewModel();
        Settings.Load(settingsStore.Current);
        ApplyDefaults(settingsStore.Current);
        ApplyWorkspace(settingsStore.Current.Workspace);
        AddLog("应用已启动，.NET 10 / WPF 原生模式。", LogLevel.Success);
    }

    public AuthorDownloadViewModel Author { get; }
    public SinglePostViewModel SinglePost { get; }
    public ResumeViewModel Resume { get; }
    public SettingsViewModel Settings { get; }
    public RepairViewModel Repair { get; }
    public TaskViewModel Task { get; }
    public ObservableCollection<LogEntryViewModel> Logs { get; } = [];
    public ObservableCollection<AuthorQueueItemViewModel> AuthorQueue { get; } = [];

    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAuthorQueuePaused;

    [RelayCommand]
    private async Task SelectAuthorPathAsync()
    {
        var path = _folderPicker.PickFolder("选择作者集合下载保存路径", Author.SavePath);
        if (path is not null)
        {
            Author.SavePath = path;
            await SaveApplicationStateAsync();
        }
    }

    [RelayCommand]
    private async Task SelectSinglePostPathAsync()
    {
        var path = _folderPicker.PickFolder("选择单作品下载保存路径", SinglePost.SavePath);
        if (path is not null)
        {
            SinglePost.SavePath = path;
            await SaveApplicationStateAsync();
        }
    }

    [RelayCommand]
    private async Task SelectResumePathAsync()
    {
        var path = _folderPicker.PickFolder("选择包含 download_progress.json 的作者目录", Resume.BasePath);
        if (path is null) return;
        Resume.BasePath = path;
        try
        {
            var document = await _progressStore.LoadAsync(Path.Combine(path, "download_progress.json"));
            Resume.Config = document.Config;
            Resume.CompletedCount = document.Records.Count;
            Resume.TotalCount = document.Records.Count;
            Resume.TaskInfo = document.Config.Mode == DownloadMode.Author
                ? $"作者集合 · {document.Config.Service}/{document.Config.Username} · 已完成 {document.Records.Count} 个作品"
                : $"单作品 · {document.Config.PostUrl} · 已完成 {document.Records.Count} 个作品";
            AddLog("进度文件加载成功。", LogLevel.Success);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Resume.Config = null;
            Resume.TaskInfo = error.Message;
            AddLog($"进度文件加载失败：{error.Message}", LogLevel.Error);
        }
        await SaveApplicationStateAsync();
    }

    [RelayCommand]
    private async Task SelectRepairPathAsync()
    {
        var title = Repair.IsBatchMode
            ? "选择包含多个作者合集目录的上一级文件夹"
            : "选择包含 json、src 和 download_progress.json 的作者目录";
        var path = _folderPicker.PickFolder(title, Repair.BasePath);
        if (path is null) return;
        Repair.BasePath = path;
        Repair.ResultText = Repair.IsMissingMode ? "路径已选择，请扫描缺失文件" : "路径已选择，请扫描损坏文件";
        Repair.CanRepair = false;
        Repair.CanViewMissingFiles = false;
        _lastScan = null;
        await SaveApplicationStateAsync();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedTabIndex == 0)
        {
            if (TryAddAuthorQueueItem()) await SaveApplicationStateAsync();
            return;
        }

        DownloadConfig config;
        try
        {
            config = BuildSelectedConfig();
            KemonoRules.Validate(config);
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or InvalidOperationException)
        {
            AddLog(error.Message, LogLevel.Error);
            return;
        }

        await SaveApplicationStateAsync();

        IsBusy = true;
        Task.Title = config.Mode == DownloadMode.Author ? $"{config.Service} / {config.Username}" : config.PostUrl;
        Task.StatusText = "运行中";
        Task.ProgressPercent = 0;
        Task.DownloadedFiles = 0;
        Task.IsIncremental = false;
        try
        {
            var operation = _scraperService.StartAsync(config, new Progress<ScrapeEvent>(HandleScrapeEvent));
            _activeOperation = operation;
            StopCommand.NotifyCanExecuteChanged();
            var result = await operation;
            Task.StatusText = result.Status switch
            {
                DownloadTaskStatus.Completed => "已完成",
                DownloadTaskStatus.Stopped => "已停止",
                DownloadTaskStatus.Failed => "失败",
                _ => result.Status.ToString()
            };
        }
        finally
        {
            _activeOperation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_authorQueueRunnerActive || AuthorQueue.Any(item => item.IsPending)) IsAuthorQueuePaused = true;
        _operationCancellation?.Cancel();
        await _scraperService.StopAsync();
    }

    [RelayCommand]
    private void AddAuthorToQueue()
    {
        if (TryAddAuthorQueueItem()) _ = SaveApplicationStateAsync();
    }

    [RelayCommand]
    private void ResumeAuthorQueue()
    {
        if (!IsAuthorQueuePaused) return;
        IsAuthorQueuePaused = false;
        AddLog("作者下载队列已继续。", LogLevel.Information);
        _ = RunAuthorQueueAsync();
    }

    [RelayCommand]
    private void RemoveAuthorQueueItem(AuthorQueueItemViewModel? item)
    {
        if (item is null || !item.IsPending) return;
        AuthorQueue.Remove(item);
        AddLog($"已从队列移除：{item.Title}", LogLevel.Information);
    }

    [RelayCommand]
    private void ClearFinishedAuthorQueueItems()
    {
        foreach (var item in AuthorQueue.Where(item => !item.IsPending && item.StatusText != "运行中").ToArray()) AuthorQueue.Remove(item);
    }

    private bool TryAddAuthorQueueItem()
    {
        DownloadConfig config;
        try
        {
            config = BuildAuthorConfig();
            KemonoRules.Validate(config);
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or InvalidOperationException)
        {
            AddLog(error.Message, LogLevel.Error);
            return false;
        }

        var item = new AuthorQueueItemViewModel(config);
        AuthorQueue.Add(item);
        AddLog($"已加入作者队列：{item.Title}（{config.Domain}）", LogLevel.Success);
        if (IsBusy || _authorQueueRunnerActive) _ = ResolveQueueAuthorNameAsync(item);
        else if (!IsAuthorQueuePaused) _ = RunAuthorQueueAsync();
        return true;
    }

    private async Task ResolveQueueAuthorNameAsync(AuthorQueueItemViewModel item)
    {
        var authorName = await _scraperService.ResolveAuthorNameAsync(item.Config.Domain, item.Config.Username, item.Config.Service);
        if (!string.IsNullOrWhiteSpace(authorName)) item.AuthorName = authorName;
        else if (item.AuthorName == "正在获取作者名称...") item.AuthorName = "未获取到作者名称";
    }

    private async Task RunAuthorQueueAsync()
    {
        if (_authorQueueRunnerActive || IsBusy || IsAuthorQueuePaused) return;
        _authorQueueRunnerActive = true;
        IsBusy = true;
        try
        {
            while (!IsAuthorQueuePaused)
            {
                var item = AuthorQueue.FirstOrDefault(candidate => candidate.IsPending);
                if (item is null) break;
                item.StatusText = "运行中";
                item.CurrentItem = "正在准备作者任务";
                Task.Title = item.Title;
                Task.StatusText = "运行中";
                Task.ProgressPercent = 0;
                Task.DownloadedFiles = 0;
                Task.IsIncremental = false;
                AddLog($"开始队列任务：{item.Title}（{item.Config.Domain}）", LogLevel.Information);

                var progress = new Progress<ScrapeEvent>(value =>
                {
                    HandleScrapeEvent(value);
                    if (value.Kind == ScrapeEventKind.Progress)
                    {
                        item.ProgressPercent = value.Total > 0 ? value.Completed * 100d / value.Total : 0;
                        item.CurrentItem = string.IsNullOrWhiteSpace(value.CurrentItem) ? "处理中" : value.CurrentItem;
                    }
                    else if (value.Kind == ScrapeEventKind.AuthorResolved && !string.IsNullOrWhiteSpace(value.AuthorName))
                    {
                        item.AuthorName = value.AuthorName;
                    }
                });
                ScrapeResult result;
                try
                {
                    var operation = _scraperService.StartAsync(item.Config, progress);
                    _activeOperation = operation;
                    StopCommand.NotifyCanExecuteChanged();
                    result = await operation;
                }
                catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    item.StatusText = "失败";
                    item.CurrentItem = error.Message;
                    AddLog($"队列任务失败 {item.Title}：{error.Message}", LogLevel.Error);
                    continue;
                }
                finally
                {
                    _activeOperation = null;
                }
                if (result.Status == DownloadTaskStatus.Stopped)
                {
                    item.StatusText = "等待中";
                    item.CurrentItem = "已停止，等待继续";
                    IsAuthorQueuePaused = true;
                    break;
                }
                item.StatusText = result.Status switch
                {
                    DownloadTaskStatus.Completed => "已完成",
                    DownloadTaskStatus.Failed => "失败",
                    _ => result.Status.ToString()
                };
                item.CurrentItem = result.Error ?? $"下载文件 {result.DownloadedFiles} 个";
            }
        }
        finally
        {
            _activeOperation = null;
            _authorQueueRunnerActive = false;
            IsBusy = false;
            if (!IsAuthorQueuePaused && AuthorQueue.Any(item => item.IsPending)) _ = RunAuthorQueueAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunSettingsOperation))]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var settings = Settings.Build() with { Workspace = BuildWorkspace() };
            await _settingsStore.SaveAsync(settings);
            _networkClientProvider.Rebuild();
            Settings.Load(_settingsStore.Current);
            ApplyDefaults(_settingsStore.Current);
            Settings.StatusMessage = $"设置已保存，当前服务器：https://{_settingsStore.Current.Domain}";
            AddLog($"全局设置已保存，服务器已切换至 {_settingsStore.Current.Domain}。", LogLevel.Success);
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException)
        {
            Settings.StatusMessage = error.Message;
            AddLog($"设置保存失败：{error.Message}", LogLevel.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunSettingsOperation))]
    private void AddDomain()
    {
        var domain = KemonoRules.NormalizeDomain(Settings.NewDomain);
        if (!KemonoRules.IsValidDomain(domain))
        {
            Settings.StatusMessage = "请输入有效域名，例如 kemono.party";
            return;
        }

        if (!Settings.Domains.Contains(domain, StringComparer.OrdinalIgnoreCase)) Settings.Domains.Add(domain);
        Settings.SelectedDomain = domain;
        Settings.NewDomain = string.Empty;
        Settings.StatusMessage = $"已添加 {domain}，保存设置后生效";
    }

    [RelayCommand(CanExecute = nameof(CanRunSettingsOperation))]
    private void RemoveDomain()
    {
        var domain = Settings.SelectedDomain;
        if (AppSettings.DefaultDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
        {
            Settings.StatusMessage = "默认域名不能删除";
            return;
        }

        if (Settings.Domains.Remove(domain)) Settings.SelectedDomain = KemonoRules.DefaultDomain;
        Settings.StatusMessage = $"已删除 {domain}，保存设置后生效";
    }

    [RelayCommand(CanExecute = nameof(CanRunSettingsOperation))]
    private async Task ResetSettingsAsync()
    {
        if (!_dialogService.Confirm("确定恢复全部默认设置吗？", "恢复默认设置")) return;
        await _settingsStore.SaveAsync(new AppSettings { Workspace = BuildWorkspace() });
        Settings.Load(_settingsStore.Current);
        _networkClientProvider.Rebuild();
        ApplyDefaults(_settingsStore.Current);
        Settings.StatusMessage = "已恢复默认设置";
        AddLog("设置已恢复为默认值。", LogLevel.Information);
    }

    [RelayCommand(CanExecute = nameof(CanRunRepairOperation))]
    private async Task ScanRepairAsync()
    {
        if (string.IsNullOrWhiteSpace(Repair.BasePath))
        {
            AddLog("请先选择修复路径。", LogLevel.Error);
            return;
        }
        IsBusy = true;
        _operationCancellation = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        try
        {
            var mode = Repair.SelectedMode.Mode;
            _lastScan = null;
            Repair.CanRepair = false;
            Repair.CanViewMissingFiles = false;
            Repair.ResultText = mode == RepairScanMode.Missing ? "正在扫描缺失文件..." : "正在扫描损坏文件...";
            var operation = Repair.IsBatchMode
                ? _repairService.ScanBatchAsync(Repair.BasePath, Repair.FileSizeLimitKb, mode, _operationCancellation.Token)
                : _repairService.ScanAsync(Repair.BasePath, Repair.FileSizeLimitKb, mode, _operationCancellation.Token);
            _activeOperation = operation;
            _lastScan = await operation;
            Repair.CorruptedCount = _lastScan.Files.Count;
            Repair.CanRepair = _lastScan.Files.Count > 0;
            Repair.CanViewMissingFiles = mode == RepairScanMode.Missing;
            RepairFilesCommand.NotifyCanExecuteChanged();
            ViewMissingFilesCommand.NotifyCanExecuteChanged();
            var label = mode == RepairScanMode.Missing ? "缺失" : "疑似损坏";
            var authorSummary = Repair.IsBatchMode ? $"，作者 {_lastScan.ScannedAuthorCount} 个" : string.Empty;
            Repair.ResultText = $"应有附件 {_lastScan.TotalCount} 个，{label} {_lastScan.Files.Count} 个{authorSummary}，本地大小 {_lastScan.TotalSize / 1024d / 1024d:F2} MB";
            AddLog($"扫描完成，发现 {_lastScan.Files.Count} 个{label}文件{authorSummary}。", _lastScan.Files.Count > 0 ? LogLevel.Warning : LogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            Repair.ResultText = "扫描已停止";
            AddLog("扫描已停止。", LogLevel.Warning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Repair.ResultText = error.Message;
            AddLog($"扫描失败：{error.Message}", LogLevel.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _activeOperation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanViewMissingFiles))]
    private void ViewMissingFiles()
    {
        if (_lastScan is null || !Repair.IsMissingMode) return;
        _dialogService.ShowMissingFiles(_lastScan);
    }

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairFilesAsync()
    {
        if (_lastScan is null || _lastScan.Files.Count == 0) return;
        var mode = Repair.SelectedMode.Mode;
        var actionText = mode == RepairScanMode.Missing ? "补足" : "删除并重新下载";
        if (!_dialogService.Confirm($"将{actionText} {_lastScan.Files.Count} 个文件，是否继续？", Repair.RepairButtonText)) return;
        IsBusy = true;
        _operationCancellation = new CancellationTokenSource();
        StopCommand.NotifyCanExecuteChanged();
        Task.Title = mode == RepairScanMode.Missing ? "缺失文件补足" : "损坏文件修复";
        Task.StatusText = "运行中";
        try
        {
            var operation = Repair.IsBatchMode
                ? _repairService.RepairBatchAsync(Repair.BasePath, Repair.FileSizeLimitKb, Repair.Concurrent, mode, mode == RepairScanMode.Missing && Repair.UseThumbnail, new Progress<ScrapeEvent>(HandleScrapeEvent), _operationCancellation.Token)
                : _repairService.RepairAsync(Repair.BasePath, Repair.FileSizeLimitKb, Repair.Concurrent, mode, mode == RepairScanMode.Missing && Repair.UseThumbnail, new Progress<ScrapeEvent>(HandleScrapeEvent), _operationCancellation.Token);
            _activeOperation = operation;
            var result = await operation;
            if (mode == RepairScanMode.Missing && _lastScan is not null)
            {
                var remainingFiles = _lastScan.Files
                    .Where(file => !File.Exists(file.Path) || new FileInfo(file.Path).Length == 0)
                    .ToArray();
                _lastScan = _lastScan with { Files = remainingFiles };
                Repair.CorruptedCount = remainingFiles.Length;
                Repair.CanRepair = remainingFiles.Length > 0;
                Repair.CanViewMissingFiles = true;
            }
            else
            {
                Repair.CanRepair = false;
                Repair.CanViewMissingFiles = false;
            }
            Repair.ResultText = $"修复完成：成功 {result.RepairedFiles}，失败 {result.FailedFiles}，匹配 {result.MatchedFiles}";
            RepairFilesCommand.NotifyCanExecuteChanged();
            ViewMissingFilesCommand.NotifyCanExecuteChanged();
            Task.StatusText = result.FailedFiles == 0 ? "已完成" : "部分完成";
            AddLog(Repair.ResultText, result.FailedFiles == 0 ? LogLevel.Success : LogLevel.Warning);
        }
        catch (OperationCanceledException)
        {
            Repair.ResultText = "修复已停止";
            Task.StatusText = "已停止";
            AddLog("修复任务已停止。", LogLevel.Warning);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or ArgumentException or InvalidOperationException)
        {
            Repair.ResultText = error.Message;
            Task.StatusText = "失败";
            AddLog($"修复失败：{error.Message}", LogLevel.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _activeOperation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        Logs.Clear();
        AddLog("日志已清空。", LogLevel.Information);
    }

    private DownloadConfig BuildSelectedConfig() => SelectedTabIndex switch
    {
        0 => BuildAuthorConfig(),
        1 => BuildSinglePostConfig(),
        2 when Resume.Config is not null => BuildResumeConfig(Resume.Config),
        2 => throw new InvalidOperationException("请先加载新版进度文件。"),
        _ => throw new InvalidOperationException("当前页面不能启动下载任务。")
    };

    private DownloadConfig BuildAuthorConfig() => new()
    {
        Domain = KemonoRules.NormalizeDomain(Author.Domain),
        Mode = DownloadMode.Author,
        Service = Author.Service,
        Username = Author.Username.Trim(),
        SavePath = Author.SavePath.Trim(),
        Limit = Author.Limit,
        Concurrent = Author.Concurrent,
        SkipExisting = Author.SkipExisting,
        UseThumbnail = Author.UseThumbnail,
        BatchDelayMs = _settingsStore.Current.BatchDelayMs,
        ForceFresh = Author.ForceFresh
    };

    private DownloadConfig BuildSinglePostConfig()
    {
        var parsedInput = KemonoRules.ParsePostUrl(SinglePost.PostUrl.Trim(), Settings.Domains);
        var domain = KemonoRules.ResolveTaskDomain(parsedInput.CanonicalUri.Host, _settingsStore.Current.Domain);
        var canonical = KemonoRules.ParsePostUrl(SinglePost.PostUrl.Trim(), Settings.Domains, domain);
        return new DownloadConfig
        {
            Domain = domain,
            Mode = DownloadMode.SinglePost,
            PostUrl = canonical.CanonicalUri.ToString(),
            SavePath = SinglePost.SavePath.Trim(),
            Concurrent = SinglePost.Concurrent,
            SkipExisting = SinglePost.SkipExisting,
            UseThumbnail = SinglePost.UseThumbnail,
            BatchDelayMs = _settingsStore.Current.BatchDelayMs,
            ForceFresh = SinglePost.ForceFresh
        };
    }

    private DownloadConfig BuildResumeConfig(DownloadConfig savedConfig)
    {
        var domain = _settingsStore.Current.Domain;
        var authorDirectory = Path.GetFullPath(Resume.BasePath.Trim());
        var saveRoot = Directory.GetParent(authorDirectory)?.FullName
            ?? throw new InvalidOperationException("无法确定续传作者目录的上一级保存路径。");
        var postUrl = savedConfig.Mode == DownloadMode.SinglePost
            ? KemonoRules.ParsePostUrl(savedConfig.PostUrl, Settings.Domains.Append(savedConfig.Domain), domain).CanonicalUri.ToString()
            : savedConfig.PostUrl;
        return savedConfig with
        {
            Domain = domain,
            PostUrl = postUrl,
            SavePath = saveRoot,
            AuthorDirectoryOverride = authorDirectory,
            ForceFresh = Resume.ResetProgress
        };
    }

    private void HandleScrapeEvent(ScrapeEvent value)
    {
        switch (value.Kind)
        {
            case ScrapeEventKind.Log:
                AddLog(value.Message, value.Level);
                break;
            case ScrapeEventKind.Progress:
                Task.ProgressPercent = value.Total > 0 ? value.Completed * 100d / value.Total : 0;
                Task.CurrentItem = string.IsNullOrWhiteSpace(value.CurrentItem) ? "处理中" : value.CurrentItem;
                Task.DownloadedFiles = value.DownloadedFiles;
                break;
            case ScrapeEventKind.Status:
                Task.StatusText = value.Status switch
                {
                    DownloadTaskStatus.Running => "运行中",
                    DownloadTaskStatus.Completed => "已完成",
                    DownloadTaskStatus.Stopped => "已停止",
                    DownloadTaskStatus.Failed => "失败",
                    _ => "空闲"
                };
                break;
            case ScrapeEventKind.Incremental:
                Task.IsIncremental = true;
                Task.IncrementalText = $"检测到 {value.PreviouslyCompleted} 个已完成作品，将执行增量下载";
                break;
            case ScrapeEventKind.AuthorResolved:
                if (!string.IsNullOrWhiteSpace(value.AuthorName)) Task.Title = value.AuthorName;
                break;
        }
    }

    private void ApplyDefaults(AppSettings settings)
    {
        Author.Domain = settings.Domains.Contains(settings.Domain, StringComparer.OrdinalIgnoreCase) ? settings.Domain : settings.Domains[0];
        Author.Concurrent = settings.DefaultConcurrent;
        SinglePost.Concurrent = settings.DefaultConcurrent;
        Author.SkipExisting = settings.SkipExistingDefault;
        SinglePost.SkipExisting = settings.SkipExistingDefault;
        Repair.Concurrent = settings.DefaultConcurrent;
    }

    private void ApplyWorkspace(WorkspaceSettings workspace)
    {
        SelectedTabIndex = Math.Clamp(workspace.SelectedTab, 0, 4);
        Author.Service = Author.Services.Contains(workspace.AuthorService) ? workspace.AuthorService : "fanbox";
        Author.Domain = Settings.Domains.Contains(workspace.AuthorDomain, StringComparer.OrdinalIgnoreCase) ? workspace.AuthorDomain : _settingsStore.Current.Domain;
        Author.Username = workspace.AuthorId;
        Author.SavePath = workspace.AuthorSavePath;
        Author.Limit = Math.Max(0, workspace.AuthorLimit);
        Author.Concurrent = Math.Clamp(workspace.AuthorConcurrent, 1, 20);
        Author.SkipExisting = workspace.AuthorSkipExisting;
        Author.UseThumbnail = workspace.AuthorUseThumbnail;
        SinglePost.PostUrl = workspace.SinglePostUrl;
        SinglePost.SavePath = workspace.SingleSavePath;
        SinglePost.Concurrent = Math.Clamp(workspace.SingleConcurrent, 1, 20);
        SinglePost.SkipExisting = workspace.SingleSkipExisting;
        SinglePost.UseThumbnail = workspace.SingleUseThumbnail;
        Resume.BasePath = workspace.ResumePath;
        Repair.BasePath = workspace.RepairPath;
        Repair.FileSizeLimitKb = Math.Clamp(workspace.RepairSizeLimitKb, 1, 10000);
        Repair.Concurrent = Math.Clamp(workspace.RepairConcurrent, 1, 20);
        Repair.SelectedMode = Repair.Modes.First(mode => mode.Mode == workspace.RepairScanMode);
        Repair.UseThumbnail = workspace.RepairUseThumbnail;
        Repair.IsBatchMode = workspace.RepairBatchMode;
    }

    private WorkspaceSettings BuildWorkspace() => new()
    {
        SelectedTab = SelectedTabIndex,
        AuthorService = Author.Service,
        AuthorDomain = Author.Domain,
        AuthorId = Author.Username.Trim(),
        AuthorSavePath = Author.SavePath.Trim(),
        AuthorLimit = Author.Limit,
        AuthorConcurrent = Author.Concurrent,
        AuthorSkipExisting = Author.SkipExisting,
        AuthorUseThumbnail = Author.UseThumbnail,
        SinglePostUrl = SinglePost.PostUrl.Trim(),
        SingleSavePath = SinglePost.SavePath.Trim(),
        SingleConcurrent = SinglePost.Concurrent,
        SingleSkipExisting = SinglePost.SkipExisting,
        SingleUseThumbnail = SinglePost.UseThumbnail,
        ResumePath = Resume.BasePath.Trim(),
        RepairPath = Repair.BasePath.Trim(),
        RepairSizeLimitKb = Repair.FileSizeLimitKb,
        RepairConcurrent = Repair.Concurrent,
        RepairScanMode = Repair.SelectedMode.Mode,
        RepairUseThumbnail = Repair.UseThumbnail,
        RepairBatchMode = Repair.IsBatchMode
    };

    private void AddLog(string message, LogLevel level)
    {
        Logs.Add(new LogEntryViewModel(DateTimeOffset.Now, message, level));
        while (Logs.Count > 500) Logs.RemoveAt(0);
    }

    public async Task ShutdownAsync()
    {
        try
        {
            _operationCancellation?.Cancel();
            await _scraperService.StopAsync();
            if (_activeOperation is not null) await _activeOperation;
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SaveApplicationStateAsync()
    {
        var workspace = BuildWorkspace();
        try
        {
            try
            {
                await _settingsStore.SaveAsync(Settings.Build() with { Workspace = workspace });
            }
            catch (ArgumentException)
            {
                await _settingsStore.SaveAsync(_settingsStore.Current with { Workspace = workspace });
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AddLog($"保存界面配置失败：{error.Message}", LogLevel.Warning);
        }
    }

    private bool CanStart() => !IsBusy && SelectedTabIndex is >= 0 and <= 2;
    private bool CanStop() => IsBusy && (_scraperService.IsRunning || _operationCancellation is not null);
    private bool CanRunRepairOperation() => !IsBusy;
    private bool CanRepair() => !IsBusy && Repair.CanRepair;
    private bool CanViewMissingFiles() => !IsBusy && Repair.IsMissingMode && Repair.CanViewMissingFiles;
    private bool CanRunSettingsOperation() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        ScanRepairCommand.NotifyCanExecuteChanged();
        RepairFilesCommand.NotifyCanExecuteChanged();
        ViewMissingFilesCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        ResetSettingsCommand.NotifyCanExecuteChanged();
        AddDomainCommand.NotifyCanExecuteChanged();
        RemoveDomainCommand.NotifyCanExecuteChanged();
        if (!value && !_authorQueueRunnerActive && !IsAuthorQueuePaused && AuthorQueue.Any(item => item.IsPending)) _ = RunAuthorQueueAsync();
    }

    partial void OnSelectedTabIndexChanged(int value) => StartCommand.NotifyCanExecuteChanged();
}
