namespace KemonoDownloader.Core;

public interface IScraperService
{
    bool IsRunning { get; }
    Task<string?> ResolveAuthorNameAsync(string domain, string userId, string service, CancellationToken cancellationToken = default);
    Task<ScrapeResult> StartAsync(DownloadConfig config, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default);
    Task StopAsync();
}

public interface IKemonoApiClient
{
    event Action? RateLimited;
    event Action? RequestSucceeded;
    Task<bool> VisitHomepageAsync(string domain, string userId, string service, int offset, CancellationToken cancellationToken);
    Task<ApiResult<KemonoProfile>> GetProfileAsync(string domain, string userId, string service, CancellationToken cancellationToken);
    Task<ApiResult<IReadOnlyList<KemonoPost>>> GetPostsPageAsync(string domain, string userId, string service, int offset, CancellationToken cancellationToken);
    Task<ApiResult<KemonoPostDetail>> GetPostDetailAsync(string domain, string service, string userId, string postId, CancellationToken cancellationToken);
}

public interface IFileDownloader
{
    event Action? RateLimited;
    event Action? RequestSucceeded;
    event Action<FileRetryEvent>? Retrying;
    Task<DownloadResult> DownloadAsync(Uri uri, string domain, string destinationPath, TimeSpan timeout, bool overwrite, CancellationToken cancellationToken);
}

public interface IProgressStore
{
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
    Task<ProgressDocument> LoadAsync(string path, CancellationToken cancellationToken = default);
    Task SaveAsync(string path, ProgressDocument document, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public interface INetworkClientProvider : IDisposable
{
    HttpClient Client { get; }
    void Rebuild();
}

public interface IRepairService
{
    Task<RepairScanResult> ScanAsync(string basePath, int fileSizeLimitKb, RepairScanMode mode, CancellationToken cancellationToken = default);
    Task<RepairScanResult> ScanBatchAsync(string parentPath, int fileSizeLimitKb, RepairScanMode mode, CancellationToken cancellationToken = default);
    Task<RepairResult> RepairAsync(string basePath, int fileSizeLimitKb, int concurrent, RepairScanMode mode, bool useThumbnail, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<RepairResult> RepairBatchAsync(string parentPath, int fileSizeLimitKb, int concurrent, RepairScanMode mode, bool useThumbnail, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class LegacyProgressFormatException(string message) : IOException(message);
public sealed class InvalidProgressFormatException(string message, Exception? innerException = null) : IOException(message, innerException);
