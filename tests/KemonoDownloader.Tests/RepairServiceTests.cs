using System.Text.Json;
using KemonoDownloader.Core;
using KemonoDownloader.Infrastructure;

namespace KemonoDownloader.Tests;

public sealed class RepairServiceTests
{
    [Fact]
    public async Task MissingScan_FindsAbsentAndZeroLengthFiles()
    {
        using var fixture = await RepairFixture.CreateAsync("pawchive.pw");
        File.WriteAllText(fixture.MainPath, "present");
        File.WriteAllBytes(fixture.AttachmentPath, []);

        var scan = await fixture.Service.ScanAsync(fixture.BasePath, 100, RepairScanMode.Missing);

        Assert.Equal(2, scan.TotalCount);
        Assert.Single(scan.Files);
        Assert.Equal(fixture.AttachmentPath, scan.Files[0].Path);
        Assert.Equal("1", scan.Files[0].PostId);
        Assert.Equal("Repair Post", scan.Files[0].PostTitle);
        Assert.Equal("second.png", scan.Files[0].OriginalName);
        Assert.False(string.IsNullOrWhiteSpace(scan.Files[0].PostKey));
        Assert.False(string.IsNullOrWhiteSpace(scan.Files[0].PostFolderName));
    }

    [Fact]
    public async Task MissingRepair_UsesOriginalUrlAndCompletesProgress()
    {
        using var fixture = await RepairFixture.CreateAsync("pawchive.pw");
        File.WriteAllText(fixture.MainPath, "present");

        var result = await fixture.Service.RepairAsync(fixture.BasePath, 100, 2, RepairScanMode.Missing, false);

        Assert.Equal(1, result.RepairedFiles);
        Assert.True(File.Exists(fixture.AttachmentPath));
        Assert.Equal("file.pawchive.pw", fixture.Downloader.Downloads[0].Uri.Host);
        Assert.StartsWith("/data/", fixture.Downloader.Downloads[0].Uri.AbsolutePath);
        var progress = await fixture.ProgressStore.LoadAsync(fixture.ProgressPath);
        Assert.Single(progress.Records);
    }

    [Fact]
    public async Task MissingRepair_UsesPawchiveThumbnailButKeepsTargetFilename()
    {
        using var fixture = await RepairFixture.CreateAsync("pawchive.pw");
        File.WriteAllText(fixture.MainPath, "present");

        var result = await fixture.Service.RepairAsync(fixture.BasePath, 100, 2, RepairScanMode.Missing, true);

        Assert.Equal(1, result.RepairedFiles);
        var download = Assert.Single(fixture.Downloader.Downloads);
        Assert.Equal("img.pawchive.pw", download.Uri.Host);
        Assert.StartsWith("/thumbnail/data/", download.Uri.AbsolutePath);
        Assert.EndsWith("2.png", download.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingRepair_UsesKemonoPreviewMap()
    {
        var detail = new KemonoPostDetail
        {
            Previews = [new KemonoPreview { Type = "thumbnail", Name = "second.png", Path = "/thumb/second.jpg" }]
        };
        using var fixture = await RepairFixture.CreateAsync("kemono.cr", detail);
        File.WriteAllText(fixture.MainPath, "present");

        var result = await fixture.Service.RepairAsync(fixture.BasePath, 100, 2, RepairScanMode.Missing, true);

        Assert.Equal(1, result.RepairedFiles);
        Assert.Equal("https://kemono.cr/thumbnail/data/thumb/second.jpg", Assert.Single(fixture.Downloader.Downloads).Uri.ToString());
    }

    [Fact]
    public async Task CorruptedMode_OnlySelectsExistingFilesBelowThreshold()
    {
        using var fixture = await RepairFixture.CreateAsync("kemono.cr");
        File.WriteAllText(fixture.MainPath, "tiny");
        File.WriteAllBytes(fixture.AttachmentPath, new byte[150 * 1024]);

        var scan = await fixture.Service.ScanAsync(fixture.BasePath, 100, RepairScanMode.Corrupted);

        Assert.Single(scan.Files);
        Assert.Equal(fixture.MainPath, scan.Files[0].Path);
        Assert.False(scan.Files[0].IsMissing);
    }

    [Fact]
    public async Task BatchScan_AggregatesValidAuthorDirectoriesAndTracksOwnership()
    {
        using var parent = new TemporaryDirectory();
        Directory.CreateDirectory(parent.Path);
        var progressStore = new ProgressStore();
        await CreateBatchAuthorAsync(parent.Path, "Author A", "1", progressStore);
        await CreateBatchAuthorAsync(parent.Path, "Author B", "2", progressStore);
        Directory.CreateDirectory(Path.Combine(parent.Path, "unrelated"));
        var service = new RepairService(
            progressStore,
            new StubFileDownloader(),
            new StubApiClient(),
            new StubSettingsStore(new AppSettings { Domain = "pawchive.pw", Retries = 1 }));

        var scan = await service.ScanBatchAsync(parent.Path, 100, RepairScanMode.Missing);

        Assert.Equal(2, scan.ScannedAuthorCount);
        Assert.Equal(2, scan.TotalCount);
        Assert.Equal(2, scan.Files.Count);
        Assert.Equal(["Author A", "Author B"], scan.Files.Select(file => file.AuthorName).Order().ToArray());
        Assert.All(scan.Files, file => Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(file.Path))), file.AuthorBasePath));
    }

    [Fact]
    public async Task BatchRepair_ProcessesEveryAuthorSequentially()
    {
        using var parent = new TemporaryDirectory();
        Directory.CreateDirectory(parent.Path);
        var progressStore = new ProgressStore();
        await CreateBatchAuthorAsync(parent.Path, "Author A", "1", progressStore);
        await CreateBatchAuthorAsync(parent.Path, "Author B", "2", progressStore);
        var downloader = new StubFileDownloader();
        var service = new RepairService(
            progressStore,
            downloader,
            new StubApiClient(),
            new StubSettingsStore(new AppSettings { Domain = "pawchive.pw", Retries = 1 }));

        var result = await service.RepairBatchAsync(parent.Path, 100, 2, RepairScanMode.Missing, false);

        Assert.Equal(2, result.ProcessedAuthors);
        Assert.Equal(2, result.MatchedFiles);
        Assert.Equal(2, result.RepairedFiles);
        Assert.Equal(2, downloader.Downloads.Count);
        Assert.Contains(downloader.Downloads, download => download.Path.Contains("Author A", StringComparison.Ordinal));
        Assert.Contains(downloader.Downloads, download => download.Path.Contains("Author B", StringComparison.Ordinal));
    }

    private static async Task CreateBatchAuthorAsync(string parentPath, string authorName, string postId, ProgressStore progressStore)
    {
        var basePath = Path.Combine(parentPath, authorName);
        var jsonPath = Path.Combine(basePath, "json");
        var sourcePath = Path.Combine(basePath, "src");
        Directory.CreateDirectory(jsonPath);
        Directory.CreateDirectory(sourcePath);
        var post = new KemonoPost
        {
            Id = postId,
            User = postId,
            Service = "fanbox",
            Title = $"Post {postId}",
            Published = "2026-08-23T00:00:00",
            File = new KemonoAttachment { Name = $"{postId}.jpg", Path = $"/{postId}.jpg" }
        };
        await File.WriteAllTextAsync(Path.Combine(jsonPath, "1.json"), JsonSerializer.Serialize(new[] { post }));
        var metadata = KemonoRules.GetPostMetadata(post);
        Directory.CreateDirectory(Path.Combine(sourcePath, metadata.FolderName));
        await progressStore.SaveAsync(Path.Combine(basePath, "download_progress.json"), new ProgressDocument
        {
            Config = new DownloadConfig { Domain = "pawchive.pw", Mode = DownloadMode.Author, Service = "fanbox", Username = postId, SavePath = parentPath }
        });
    }

    private sealed class RepairFixture : IDisposable
    {
        private readonly TemporaryDirectory _temporaryDirectory;

        private RepairFixture(TemporaryDirectory temporaryDirectory, string basePath, string progressPath, string mainPath, string attachmentPath, ProgressStore progressStore, StubFileDownloader downloader, RepairService service)
        {
            _temporaryDirectory = temporaryDirectory;
            BasePath = basePath;
            ProgressPath = progressPath;
            MainPath = mainPath;
            AttachmentPath = attachmentPath;
            ProgressStore = progressStore;
            Downloader = downloader;
            Service = service;
        }

        public string BasePath { get; }
        public string ProgressPath { get; }
        public string MainPath { get; }
        public string AttachmentPath { get; }
        public ProgressStore ProgressStore { get; }
        public StubFileDownloader Downloader { get; }
        public RepairService Service { get; }

        public static async Task<RepairFixture> CreateAsync(string domain, KemonoPostDetail? detail = null)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var basePath = temporaryDirectory.Path;
            var jsonPath = Path.Combine(basePath, "json");
            var sourcePath = Path.Combine(basePath, "src");
            Directory.CreateDirectory(jsonPath);
            Directory.CreateDirectory(sourcePath);

            var post = new KemonoPost
            {
                Id = "1",
                User = "42",
                Service = "fanbox",
                Title = "Repair Post",
                Published = "2026-08-22T00:00:00",
                File = new KemonoAttachment { Name = "cover.jpg", Path = "/aa/cover.jpg" },
                Attachments = [new KemonoAttachment { Name = "second.png", Path = "/bb/second.png" }]
            };
            await File.WriteAllTextAsync(Path.Combine(jsonPath, "1.json"), JsonSerializer.Serialize(new[] { post }));
            var metadata = KemonoRules.GetPostMetadata(post);
            var postPath = Path.Combine(sourcePath, metadata.FolderName);
            Directory.CreateDirectory(postPath);
            var mainPath = Path.GetFullPath(Path.Combine(postPath, "1.jpg"));
            var attachmentPath = Path.GetFullPath(Path.Combine(postPath, "2.png"));
            var progressPath = Path.Combine(basePath, "download_progress.json");
            var progressStore = new ProgressStore();
            await progressStore.SaveAsync(progressPath, new ProgressDocument
            {
                Config = new DownloadConfig { Domain = domain, Mode = DownloadMode.Author, Service = "fanbox", Username = "42", SavePath = basePath }
            });

            var downloader = new StubFileDownloader();
            var api = new StubApiClient { PostDetail = detail ?? new KemonoPostDetail() };
            var settings = new StubSettingsStore(new AppSettings { Domain = domain, Retries = 1 });
            var service = new RepairService(progressStore, downloader, api, settings);
            return new RepairFixture(temporaryDirectory, basePath, progressPath, mainPath, attachmentPath, progressStore, downloader, service);
        }

        public void Dispose() => _temporaryDirectory.Dispose();
    }
}
