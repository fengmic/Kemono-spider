using System.Text.Json;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class RepairService(
    IProgressStore progressStore,
    IFileDownloader downloader,
    IKemonoApiClient apiClient,
    ISettingsStore settingsStore) : IRepairService
{
    public async Task<RepairScanResult> ScanAsync(string basePath, int fileSizeLimitKb, RepairScanMode mode, CancellationToken cancellationToken = default)
    {
        ValidatePath(basePath);
        _ = await progressStore.LoadAsync(Path.Combine(basePath, "download_progress.json"), cancellationToken);
        var targets = await ReadAttachmentTargetsAsync(basePath, cancellationToken);
        var files = new List<RepairFile>();
        long totalSize = 0;
        var threshold = fileSizeLimitKb * 1024L;
        var authorName = Path.GetFileName(Path.TrimEndingDirectorySeparator(basePath));

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = File.Exists(target.DestinationPath);
            var size = exists ? new FileInfo(target.DestinationPath).Length : 0;
            totalSize += size;
            var selected = mode == RepairScanMode.Missing ? !exists || size == 0 : exists && size < threshold;
            if (selected)
            {
                files.Add(new RepairFile(
                    target.Filename,
                    target.DestinationPath,
                    size,
                    !exists,
                    target.Metadata.Key,
                    target.Metadata.PostId,
                    target.Metadata.PostTitle,
                    target.Metadata.PostDate,
                    target.Metadata.FolderName,
                    target.Attachment.Name,
                    authorName,
                    Path.GetFullPath(basePath)));
            }
        }

        return new RepairScanResult(basePath, files, targets.Count, totalSize);
    }

    public async Task<RepairScanResult> ScanBatchAsync(string parentPath, int fileSizeLimitKb, RepairScanMode mode, CancellationToken cancellationToken = default)
    {
        var authorDirectories = GetAuthorDirectories(parentPath);
        var files = new List<RepairFile>();
        var totalCount = 0;
        long totalSize = 0;
        var scannedAuthors = 0;

        foreach (var authorDirectory in authorDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var scan = await ScanAsync(authorDirectory, fileSizeLimitKb, mode, cancellationToken);
                files.AddRange(scan.Files);
                totalCount += scan.TotalCount;
                totalSize += scan.TotalSize;
                scannedAuthors++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Invalid author directories are skipped so one stale task cannot block a batch scan.
            }
        }

        if (scannedAuthors == 0) throw new InvalidOperationException("没有可读取的作者合集目录。");
        return new RepairScanResult(parentPath, files, totalCount, totalSize, scannedAuthors);
    }

    public async Task<RepairResult> RepairAsync(
        string basePath,
        int fileSizeLimitKb,
        int concurrent,
        RepairScanMode mode,
        bool useThumbnail,
        IProgress<ScrapeEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (concurrent is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(concurrent));
        ValidatePath(basePath);
        var progressPath = Path.Combine(basePath, "download_progress.json");
        var progressDocument = await progressStore.LoadAsync(progressPath, cancellationToken);
        var domain = string.IsNullOrWhiteSpace(progressDocument.Config.Domain) ? settingsStore.Current.Domain : progressDocument.Config.Domain;
        var scan = await ScanAsync(basePath, fileSizeLimitKb, mode, cancellationToken);
        var selectedPaths = scan.Files.Select(file => Path.GetFullPath(file.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = (await ReadAttachmentTargetsAsync(basePath, cancellationToken))
            .Where(target => selectedPaths.Contains(Path.GetFullPath(target.DestinationPath)))
            .ToArray();
        progress?.Report(ScrapeEvent.Log($"匹配到 {targets.Length} 个{(mode == RepairScanMode.Missing ? "缺失" : "损坏")}文件。"));

        var thumbnailMaps = useThumbnail
            ? await BuildThumbnailMapsAsync(targets, domain, progress, cancellationToken)
            : new Dictionary<string, IReadOnlyDictionary<string, Uri>>(StringComparer.Ordinal);
        var repaired = 0;
        var failed = 0;
        var completed = 0;
        using var semaphore = new SemaphoreSlim(concurrent, concurrent);
        void HandleRetry(FileRetryEvent retry) =>
            progress?.Report(ScrapeEvent.Log($"文件 {retry.FileName} 补足失败（{retry.Reason}），{retry.Delay.TotalMilliseconds:F0}ms 后进行第 {retry.FailedAttempt + 1}/{retry.MaxAttempts} 次尝试。", LogLevel.Warning));
        downloader.Retrying += HandleRetry;

        try
        {
            await Task.WhenAll(targets.Select(async target =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    if (mode == RepairScanMode.Corrupted && File.Exists(target.DestinationPath)) File.Delete(target.DestinationPath);
                    var uri = ResolveUri(target, domain, useThumbnail, thumbnailMaps);
                    if (uri is null)
                    {
                        Interlocked.Increment(ref failed);
                        progress?.Report(ScrapeEvent.Log($"未找到缩略图：{target.Filename}", LogLevel.Warning));
                        return;
                    }

                    var timeout = KemonoRules.GetDownloadTimeout(target.Filename, settingsStore.Current);
                    await downloader.DownloadAsync(uri, domain, target.DestinationPath, timeout, overwrite: true, cancellationToken);
                    Interlocked.Increment(ref repaired);
                    progress?.Report(ScrapeEvent.Log($"补足完成：{target.Filename}", LogLevel.Success));
                }
                catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref failed);
                    progress?.Report(ScrapeEvent.Log($"补足失败 {target.Filename}：{error.Message}", LogLevel.Warning));
                }
                finally
                {
                    var value = Interlocked.Increment(ref completed);
                    progress?.Report(ScrapeEvent.Progress(value, targets.Length, repaired, target.Filename));
                    semaphore.Release();
                }
            }));
        }
        finally
        {
            downloader.Retrying -= HandleRetry;
        }

        await UpdateCompletedProgressAsync(basePath, progressPath, progressDocument, cancellationToken);
        return new RepairResult(targets.Length, repaired, failed);
    }

    public async Task<RepairResult> RepairBatchAsync(
        string parentPath,
        int fileSizeLimitKb,
        int concurrent,
        RepairScanMode mode,
        bool useThumbnail,
        IProgress<ScrapeEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var authorDirectories = GetAuthorDirectories(parentPath);
        var scans = new List<(string Directory, RepairScanResult Scan)>();
        foreach (var authorDirectory in authorDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                scans.Add((authorDirectory, await ScanAsync(authorDirectory, fileSizeLimitKb, mode, cancellationToken)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                progress?.Report(ScrapeEvent.Log($"跳过作者目录 {Path.GetFileName(authorDirectory)}：{error.Message}", LogLevel.Warning));
            }
        }

        if (scans.Count == 0) throw new InvalidOperationException("没有可读取的作者合集目录。");
        var totalMatched = scans.Sum(item => item.Scan.Files.Count);
        var matched = 0;
        var repaired = 0;
        var failed = 0;
        var completedOffset = 0;
        var processedAuthors = 0;

        foreach (var item in scans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorName = Path.GetFileName(item.Directory);
            progress?.Report(ScrapeEvent.Log($"开始处理作者：{authorName}"));
            var authorRepaired = 0;
            var adapter = new InlineProgress<ScrapeEvent>(value =>
            {
                if (value.Kind == ScrapeEventKind.Progress)
                {
                    authorRepaired = value.DownloadedFiles;
                    progress?.Report(ScrapeEvent.Progress(
                        completedOffset + value.Completed,
                        totalMatched,
                        repaired + value.DownloadedFiles,
                        $"{authorName}/{value.CurrentItem}"));
                }
                else if (value.Kind == ScrapeEventKind.Log)
                {
                    progress?.Report(ScrapeEvent.Log($"[{authorName}] {value.Message}", value.Level));
                }
            });

            try
            {
                var result = await RepairAsync(item.Directory, fileSizeLimitKb, concurrent, mode, useThumbnail, adapter, cancellationToken);
                matched += result.MatchedFiles;
                repaired += result.RepairedFiles;
                failed += result.FailedFiles;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or ArgumentException)
            {
                matched += item.Scan.Files.Count;
                repaired += authorRepaired;
                failed += Math.Max(0, item.Scan.Files.Count - authorRepaired);
                progress?.Report(ScrapeEvent.Log($"作者 {authorName} 处理失败：{error.Message}", LogLevel.Warning));
            }
            completedOffset += item.Scan.Files.Count;
            processedAuthors++;
        }

        return new RepairResult(matched, repaired, failed, processedAuthors);
    }

    private async Task<IReadOnlyList<AttachmentTarget>> ReadAttachmentTargetsAsync(string basePath, CancellationToken cancellationToken)
    {
        var jsonDirectory = Path.Combine(basePath, "json");
        var sourceDirectory = Path.Combine(basePath, "src");
        var targets = new Dictionary<string, AttachmentTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (var jsonFile in Directory.EnumerateFiles(jsonDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = File.OpenRead(jsonFile);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var posts = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(element => element.Deserialize<KemonoPost>(JsonDefaults.Options)).Where(post => post is not null).Cast<KemonoPost>().ToArray()
                : [document.RootElement.Deserialize<KemonoPost>(JsonDefaults.Options)!];

            foreach (var post in posts.Where(post => post is not null))
            {
                var metadata = KemonoRules.GetPostMetadata(post);
                foreach (var entry in KemonoRules.GetDownloadTasks(post))
                {
                    var filename = KemonoRules.GetSequentialFilename(entry.Attachment, entry.Index);
                    var destination = Path.GetFullPath(Path.Combine(sourceDirectory, metadata.FolderName, filename));
                    targets[destination] = new AttachmentTarget(post, metadata, entry.Attachment, filename, destination);
                }
            }
        }
        return targets.Values.ToArray();
    }

    private async Task<Dictionary<string, IReadOnlyDictionary<string, Uri>>> BuildThumbnailMapsAsync(
        IReadOnlyList<AttachmentTarget> targets,
        string domain,
        IProgress<ScrapeEvent>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, Uri>>(StringComparer.Ordinal);
        if (domain.Equals("pawchive.pw", StringComparison.OrdinalIgnoreCase)) return result;

        foreach (var target in targets.Where(target => KemonoRules.IsImageFile(target.Filename)).DistinctBy(target => target.Metadata.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await apiClient.GetPostDetailAsync(domain, target.Post.Service, target.Post.User, target.Metadata.PostId, cancellationToken);
                if (!response.IsSuccess) continue;
                result[target.Metadata.Key] = response.Value!.Previews
                    .Where(preview => string.Equals(preview.Type, "thumbnail", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(preview.Name) && !string.IsNullOrWhiteSpace(preview.Path))
                    .GroupBy(preview => preview.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => new Uri($"{KemonoRules.GetThumbnailBaseUrl(domain)}/thumbnail/data{group.First().Path}"), StringComparer.Ordinal);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
            {
                progress?.Report(ScrapeEvent.Log($"获取作品 {target.Metadata.FolderName} 的缩略图数据失败：{error.Message}", LogLevel.Warning));
            }
        }
        return result;
    }

    private static Uri? ResolveUri(
        AttachmentTarget target,
        string domain,
        bool useThumbnail,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Uri>> thumbnailMaps)
    {
        if (!useThumbnail || !KemonoRules.IsImageFile(target.Filename))
        {
            return KemonoRules.BuildDownloadUri(target.Attachment, domain);
        }
        if (domain.Equals("pawchive.pw", StringComparison.OrdinalIgnoreCase))
        {
            return KemonoRules.BuildThumbnailUri(target.Attachment, domain);
        }
        return thumbnailMaps.TryGetValue(target.Metadata.Key, out var map) && map.TryGetValue(target.Attachment.Name, out var uri)
            ? uri
            : null;
    }

    private async Task UpdateCompletedProgressAsync(string basePath, string progressPath, ProgressDocument document, CancellationToken cancellationToken)
    {
        var targets = await ReadAttachmentTargetsAsync(basePath, cancellationToken);
        foreach (var group in targets.GroupBy(target => target.Metadata.Key, StringComparer.Ordinal))
        {
            var entries = group.ToArray();
            var first = entries[0];
            if (!entries.All(entry => File.Exists(entry.DestinationPath) && new FileInfo(entry.DestinationPath).Length > 0))
            {
                document.Records.Remove(first.Metadata.Key);
                continue;
            }
            document.Records[first.Metadata.Key] = new ProgressRecord
            {
                PostId = first.Metadata.PostId,
                PostTitle = first.Metadata.PostTitle,
                PostDate = first.Metadata.PostDate,
                AttachmentsCount = entries.Length,
                DownloadedAttachments = entries.Length,
                CompletedTime = DateTimeOffset.UtcNow
            };
        }
        await progressStore.SaveAsync(progressPath, document, cancellationToken);
    }

    private static void ValidatePath(string basePath)
    {
        if (!Directory.Exists(Path.Combine(basePath, "json")) || !Directory.Exists(Path.Combine(basePath, "src")) || !File.Exists(Path.Combine(basePath, "download_progress.json")))
        {
            throw new DirectoryNotFoundException("所选目录必须包含 json、src 和 download_progress.json。");
        }
    }

    private static IReadOnlyList<string> GetAuthorDirectories(string parentPath)
    {
        if (!Directory.Exists(parentPath)) throw new DirectoryNotFoundException("所选批量目录不存在。");
        var directories = Directory.EnumerateDirectories(parentPath, "*", SearchOption.TopDirectoryOnly)
            .Where(IsAuthorDirectory)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (directories.Length == 0) throw new DirectoryNotFoundException("所选目录下没有作者合集目录。作者目录必须包含 json、src 和 download_progress.json。");
        return directories;
    }

    private static bool IsAuthorDirectory(string path) =>
        Directory.Exists(Path.Combine(path, "json")) &&
        Directory.Exists(Path.Combine(path, "src")) &&
        File.Exists(Path.Combine(path, "download_progress.json"));

    private sealed record AttachmentTarget(
        KemonoPost Post,
        PostMetadata Metadata,
        KemonoAttachment Attachment,
        string Filename,
        string DestinationPath);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
