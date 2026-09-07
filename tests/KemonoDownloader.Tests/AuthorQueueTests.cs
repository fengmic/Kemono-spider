using KemonoDownloader.Core;
using KemonoDownloader.Infrastructure;
using KemonoDownloader.Wpf.Services;
using KemonoDownloader.Wpf.ViewModels;

namespace KemonoDownloader.Tests;

public sealed class AuthorQueueTests
{
    [Theory]
    [InlineData(DownloadMode.Author)]
    [InlineData(DownloadMode.SinglePost)]
    public async Task Resume_OverridesStoredSavePathWithSelectedAuthorDirectory(DownloadMode mode)
    {
        using var directory = new TemporaryDirectory();
        var selectedAuthorDirectory = Path.Combine(directory.Path, "A");
        var staleSavePath = Path.Combine(directory.Path, "B");
        Directory.CreateDirectory(selectedAuthorDirectory);
        var progressStore = new ProgressStore();
        await progressStore.SaveAsync(Path.Combine(selectedAuthorDirectory, "download_progress.json"), new ProgressDocument
        {
            Config = new DownloadConfig
            {
                Domain = "kemono.cr",
                Mode = mode,
                Service = "fanbox",
                Username = "42",
                PostUrl = mode == DownloadMode.SinglePost ? "https://kemono.cr/fanbox/user/42/post/10" : string.Empty,
                SavePath = staleSavePath,
                Concurrent = 2
            }
        });
        using var clientProvider = new StubNetworkClientProvider(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage())));
        var scraper = new ControlledScraperService();
        var viewModel = new MainViewModel(
            scraper,
            new NoopRepairService(),
            progressStore,
            new StubSettingsStore(new AppSettings { Domain = "kemono.cr" }),
            clientProvider,
            new FixedFolderPicker(selectedAuthorDirectory),
            new NoopDialogService());
        viewModel.SelectedTabIndex = 2;

        await viewModel.SelectResumePathCommand.ExecuteAsync(null);
        var startTask = viewModel.StartCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => scraper.Started.Count == 1);

        var started = scraper.Started[0];
        Assert.Equal(directory.Path, started.SavePath);
        Assert.Equal(Path.GetFullPath(selectedAuthorDirectory), started.AuthorDirectoryOverride);
        Assert.NotEqual(staleSavePath, started.SavePath);
        scraper.CompleteCurrent();
        await startTask;
    }

    [Fact]
    public async Task Queue_StartsNextAuthorOnlyAfterCurrentCompletesAndKeepsSnapshot()
    {
        using var clientProvider = new StubNetworkClientProvider(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage())));
        var scraper = new ControlledScraperService();
        var settings = new StubSettingsStore(new AppSettings { Domain = "kemono.cr", Domains = ["kemono.cr", "pawchive.pw"] });
        var viewModel = new MainViewModel(
            scraper,
            new NoopRepairService(),
            new ProgressStore(),
            settings,
            clientProvider,
            new NoopFolderPicker(),
            new NoopDialogService());
        viewModel.SelectedTabIndex = 0;
        viewModel.Author.SavePath = "D:\\downloads";
        viewModel.Author.Domain = "kemono.cr";
        viewModel.Author.Username = "first";

        viewModel.AddAuthorToQueueCommand.Execute(null);
        await WaitUntilAsync(() => scraper.Started.Count == 1);
        viewModel.Author.Domain = "pawchive.pw";
        viewModel.Author.Username = "second";
        viewModel.AddAuthorToQueueCommand.Execute(null);

        await Task.Delay(100);
        Assert.Single(scraper.Started);
        Assert.Equal("kemono.cr", scraper.Started[0].Domain);
        Assert.Equal("first", scraper.Started[0].Username);
        await WaitUntilAsync(() => viewModel.AuthorQueue.All(item => !item.AuthorName.StartsWith("正在", StringComparison.Ordinal)));
        Assert.Equal(["Author first", "Author second"], viewModel.AuthorQueue.Select(item => item.AuthorName).ToArray());

        scraper.CompleteCurrent();
        await WaitUntilAsync(() => scraper.Started.Count == 2);
        Assert.Equal("pawchive.pw", scraper.Started[1].Domain);
        Assert.Equal("second", scraper.Started[1].Username);
        scraper.CompleteCurrent();
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal(["已完成", "已完成"], viewModel.AuthorQueue.Select(item => item.StatusText).ToArray());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class ControlledScraperService : IScraperService
    {
        private TaskCompletionSource<ScrapeResult>? _current;
        public bool IsRunning { get; private set; }
        public List<DownloadConfig> Started { get; } = [];

        public Task<string?> ResolveAuthorNameAsync(string domain, string userId, string service, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>($"Author {userId}");

        public async Task<ScrapeResult> StartAsync(DownloadConfig config, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.Add(config);
            IsRunning = true;
            progress?.Report(ScrapeEvent.Author($"Author {config.Username}"));
            _current = new TaskCompletionSource<ScrapeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => _current.TrySetCanceled(cancellationToken));
            try { return await _current.Task; }
            finally { IsRunning = false; }
        }

        public Task StopAsync()
        {
            _current?.TrySetResult(new ScrapeResult(DownloadTaskStatus.Stopped, 0, 0, 0));
            return Task.CompletedTask;
        }

        public void CompleteCurrent() => _current?.TrySetResult(new ScrapeResult(DownloadTaskStatus.Completed, 1, 0, 1));
    }

    private sealed class NoopRepairService : IRepairService
    {
        public Task<RepairScanResult> ScanAsync(string basePath, int fileSizeLimitKb, RepairScanMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RepairScanResult> ScanBatchAsync(string parentPath, int fileSizeLimitKb, RepairScanMode mode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RepairResult> RepairAsync(string basePath, int fileSizeLimitKb, int concurrent, RepairScanMode mode, bool useThumbnail, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RepairResult> RepairBatchAsync(string parentPath, int fileSizeLimitKb, int concurrent, RepairScanMode mode, bool useThumbnail, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopFolderPicker : IFolderPickerService
    {
        public string? PickFolder(string description, string? initialPath = null) => null;
    }

    private sealed class FixedFolderPicker(string path) : IFolderPickerService
    {
        public string? PickFolder(string description, string? initialPath = null) => path;
    }

    private sealed class NoopDialogService : IUserDialogService
    {
        public bool Confirm(string message, string title) => true;
        public void ShowMissingFiles(RepairScanResult scan) { }
    }
}
