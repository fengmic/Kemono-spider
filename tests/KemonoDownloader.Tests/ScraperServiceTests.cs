using KemonoDownloader.Core;
using KemonoDownloader.Infrastructure;

namespace KemonoDownloader.Tests;

public sealed class ScraperServiceTests
{
    [Fact]
    public async Task AuthorTask_WritesJsonFilesAttachmentsAndProgress()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var post = new KemonoPost
        {
            Id = "10",
            User = "42",
            Service = "fanbox",
            Title = "Test Post",
            Published = "2026-08-21T12:00:00",
            File = new KemonoAttachment { Name = "cover.jpg", Path = "/cover.jpg" },
            Attachments = [new KemonoAttachment { Name = "extra.png", Path = "/extra.png" }]
        };
        var api = new StubApiClient { Posts = [post] };
        var downloader = new StubFileDownloader();
        var progressStore = new ProgressStore();
        var settings = new StubSettingsStore(new AppSettings { PageRequestDelayMs = 500 });
        var service = new ScraperService(api, downloader, progressStore, settings);
        var config = new DownloadConfig { Domain = "kemono.su", Mode = DownloadMode.Author, Service = "fanbox", Username = "42", SavePath = directory.Path, Concurrent = 2, BatchDelayMs = 0 };

        var result = await service.StartAsync(config);

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.Equal(2, result.DownloadedFiles);
        var authorPath = Path.Combine(directory.Path, "test-author");
        Assert.True(File.Exists(Path.Combine(authorPath, "json", "1.json")));
        Assert.True(File.Exists(Path.Combine(authorPath, "src", "2026-08-21 Test Post", "1.jpg")));
        Assert.True(File.Exists(Path.Combine(authorPath, "src", "2026-08-21 Test Post", "2.png")));
        var progress = await progressStore.LoadAsync(Path.Combine(authorPath, "download_progress.json"));
        Assert.Single(progress.Records);
        Assert.All(api.RequestedDomains, domain => Assert.Equal("kemono.su", domain));
        Assert.All(downloader.Downloads, download => Assert.Equal("kemono.su", download.Domain));
    }

    [Fact]
    public async Task AuthorTask_UsesPawchiveNameWhenPublicIdIsMissing()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var api = new StubApiClient
        {
            Profile = new KemonoProfile { Name = "MildT" }
        };
        var settings = new StubSettingsStore(new AppSettings { PageRequestDelayMs = 500, Retries = 1 });
        var service = new ScraperService(api, new StubFileDownloader(), new ProgressStore(), settings);

        var result = await service.StartAsync(new DownloadConfig
        {
            Domain = "pawchive.pw",
            Mode = DownloadMode.Author,
            Service = "fanbox",
            Username = "33970936",
            SavePath = directory.Path,
            BatchDelayMs = 0
        });

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "MildT", "json")));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "user_33970936")));
    }

    [Fact]
    public async Task AuthorTask_RenamesExistingFallbackDirectoryToResolvedName()
    {
        using var directory = new TemporaryDirectory();
        var fallbackDirectory = Path.Combine(directory.Path, "user_33970936");
        Directory.CreateDirectory(fallbackDirectory);
        await File.WriteAllTextAsync(Path.Combine(fallbackDirectory, "existing.txt"), "preserved");
        var api = new StubApiClient
        {
            Profile = new KemonoProfile { Name = "MildT" }
        };
        var settings = new StubSettingsStore(new AppSettings { PageRequestDelayMs = 500, Retries = 1 });
        var service = new ScraperService(api, new StubFileDownloader(), new ProgressStore(), settings);

        _ = await service.StartAsync(new DownloadConfig
        {
            Domain = "pawchive.pw",
            Mode = DownloadMode.Author,
            Service = "fanbox",
            Username = "33970936",
            SavePath = directory.Path,
            BatchDelayMs = 0
        });

        Assert.False(Directory.Exists(fallbackDirectory));
        Assert.Equal("preserved", await File.ReadAllTextAsync(Path.Combine(directory.Path, "MildT", "existing.txt")));
    }

    [Fact]
    public async Task AuthorTask_UsesExactDirectoryOverrideForResume()
    {
        using var directory = new TemporaryDirectory();
        var selectedAuthorDirectory = Path.Combine(directory.Path, "A");
        var unrelatedSaveRoot = Path.Combine(directory.Path, "B");
        Directory.CreateDirectory(selectedAuthorDirectory);
        var post = CreatePost("10", "Resume Post");
        var service = CreateService([post], new StubFileDownloader());

        var result = await service.StartAsync(new DownloadConfig
        {
            Domain = "kemono.su",
            Mode = DownloadMode.Author,
            Service = "fanbox",
            Username = "42",
            SavePath = unrelatedSaveRoot,
            AuthorDirectoryOverride = selectedAuthorDirectory,
            Concurrent = 1,
            BatchDelayMs = 0
        });

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.True(File.Exists(Path.Combine(selectedAuthorDirectory, "download_progress.json")));
        Assert.True(File.Exists(Path.Combine(selectedAuthorDirectory, "json", "1.json")));
        Assert.False(Directory.Exists(unrelatedSaveRoot));
        Assert.False(Directory.Exists(Path.Combine(selectedAuthorDirectory, "test-author")));
    }

    [Fact]
    public async Task GlobalPool_RefillsFromNextPostWhileSlowDownloadIsRunning()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var posts = new[] { CreatePost("1", "Slow"), CreatePost("2", "Fast"), CreatePost("3", "Next") };
        var downloader = new ControlledFileDownloader();
        var service = CreateService(posts, downloader);
        var operation = service.StartAsync(CreateConfig(directory.Path, 2));

        await WaitUntilAsync(() => downloader.Started.Count == 2);
        downloader.Complete("Fast");
        await WaitUntilAsync(() => downloader.Started.Count == 3);

        Assert.Equal(2, downloader.ActiveCount);
        Assert.Contains(downloader.Started, path => path.Contains("Next", StringComparison.Ordinal));
        downloader.Complete("Slow");
        downloader.Complete("Next");
        var result = await operation;

        Assert.Equal(DownloadTaskStatus.Completed, result.Status);
        Assert.Equal(3, result.CompletedPosts);
        Assert.True(downloader.MaxActive <= 2);
    }

    [Fact]
    public async Task GlobalPool_DoesNotRefillAboveReducedConcurrency()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var posts = Enumerable.Range(1, 5).Select(index => CreatePost(index.ToString(), $"Post{index}")).ToArray();
        var downloader = new ControlledFileDownloader();
        var service = CreateService(posts, downloader);
        var operation = service.StartAsync(CreateConfig(directory.Path, 4));

        await WaitUntilAsync(() => downloader.Started.Count == 4);
        downloader.EmitRateLimit();
        downloader.Complete("Post2", success: false);
        downloader.Complete("Post3", success: false);
        await Task.Delay(100);
        Assert.Equal(4, downloader.Started.Count);

        downloader.Complete("Post4", success: false);
        await WaitUntilAsync(() => downloader.Started.Count == 5);
        Assert.Equal(2, downloader.ActiveCount);
        downloader.Complete("Post1");
        downloader.Complete("Post5");
        _ = await operation;
    }

    [Fact]
    public async Task GlobalPool_CancellationStopsRefill()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var posts = Enumerable.Range(1, 5).Select(index => CreatePost(index.ToString(), $"Cancel{index}")).ToArray();
        var downloader = new ControlledFileDownloader();
        var service = CreateService(posts, downloader);
        using var cancellation = new CancellationTokenSource();
        var operation = service.StartAsync(CreateConfig(directory.Path, 2), cancellationToken: cancellation.Token);

        await WaitUntilAsync(() => downloader.Started.Count == 2);
        cancellation.Cancel();
        var result = await operation;
        var startedAfterCancellation = downloader.Started.Count;
        await Task.Delay(100);

        Assert.Equal(DownloadTaskStatus.Stopped, result.Status);
        Assert.Equal(startedAfterCancellation, downloader.Started.Count);
    }

    private static ScraperService CreateService(IReadOnlyList<KemonoPost> posts, IFileDownloader downloader)
    {
        var settings = new StubSettingsStore(new AppSettings { PageRequestDelayMs = 500, Retries = 1 });
        return new ScraperService(new StubApiClient { Posts = posts }, downloader, new ProgressStore(), settings);
    }

    private static DownloadConfig CreateConfig(string savePath, int concurrent) => new()
    {
        Domain = "kemono.su",
        Mode = DownloadMode.Author,
        Service = "fanbox",
        Username = "42",
        SavePath = savePath,
        Concurrent = concurrent,
        BatchDelayMs = 0
    };

    private static KemonoPost CreatePost(string id, string title) => new()
    {
        Id = id,
        User = "42",
        Service = "fanbox",
        Title = title,
        Published = "2026-08-22T00:00:00",
        File = new KemonoAttachment { Name = $"{id}.jpg", Path = $"/{id}.jpg" }
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the expected scheduler state.");
    }
}
