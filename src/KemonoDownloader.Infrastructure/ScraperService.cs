using System.Text.Json;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class ScraperService : IScraperService
{
    private readonly IKemonoApiClient _apiClient;
    private readonly IFileDownloader _fileDownloader;
    private readonly IProgressStore _progressStore;
    private readonly ISettingsStore _settingsStore;
    private readonly object _stateLock = new();
    private CancellationTokenSource? _runCancellation;
    private IProgress<ScrapeEvent>? _progress;
    private int _adaptiveConcurrent;
    private int _requestedConcurrent;
    private int _successfulRequests;
    private TaskCompletionSource<bool> _concurrencyChanged = CreateConcurrencySignal();

    public ScraperService(IKemonoApiClient apiClient, IFileDownloader fileDownloader, IProgressStore progressStore, ISettingsStore settingsStore)
    {
        _apiClient = apiClient;
        _fileDownloader = fileDownloader;
        _progressStore = progressStore;
        _settingsStore = settingsStore;
        _apiClient.RateLimited += HandleRateLimited;
        _apiClient.RequestSucceeded += HandleRequestSucceeded;
        _fileDownloader.RateLimited += HandleRateLimited;
        _fileDownloader.RequestSucceeded += HandleRequestSucceeded;
        _fileDownloader.Retrying += HandleFileRetrying;
    }

    public bool IsRunning { get; private set; }

    public async Task<string?> ResolveAuthorNameAsync(string domain, string userId, string service, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await _apiClient.VisitHomepageAsync(domain, userId, service, 0, cancellationToken)) return null;
            await Task.Delay(_settingsStore.Current.PageRequestDelayMs, cancellationToken);
            var result = await _apiClient.GetProfileAsync(domain, userId, service, cancellationToken);
            return result.IsSuccess ? GetAuthorName(result.Value!, userId) : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<ScrapeResult> StartAsync(DownloadConfig config, IProgress<ScrapeEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        KemonoRules.Validate(config);
        lock (_stateLock)
        {
            if (IsRunning) throw new InvalidOperationException("已有下载任务正在运行。");
            IsRunning = true;
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        _progress = progress;
        _requestedConcurrent = config.Concurrent > 0 ? config.Concurrent : _settingsStore.Current.DefaultConcurrent;
        _adaptiveConcurrent = _requestedConcurrent;
        _successfulRequests = 0;
        _concurrencyChanged = CreateConcurrencySignal();
        Report(ScrapeEvent.StatusChanged(DownloadTaskStatus.Running));
        Report(ScrapeEvent.Log("========== 开始爬取任务 =========="));

        try
        {
            var result = config.Mode == DownloadMode.SinglePost
                ? await RunSinglePostAsync(config, _runCancellation.Token)
                : await RunAuthorAsync(config, _runCancellation.Token);
            Report(ScrapeEvent.StatusChanged(result.Status));
            return result;
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            Report(ScrapeEvent.Log("任务已停止。", LogLevel.Warning));
            Report(ScrapeEvent.StatusChanged(DownloadTaskStatus.Stopped));
            return new ScrapeResult(DownloadTaskStatus.Stopped, 0, 0, 0);
        }
        catch (Exception error)
        {
            Report(ScrapeEvent.Log($"爬取任务失败：{error.Message}", LogLevel.Error));
            Report(ScrapeEvent.StatusChanged(DownloadTaskStatus.Failed, error.Message));
            return new ScrapeResult(DownloadTaskStatus.Failed, 0, 0, 0, error.Message);
        }
        finally
        {
            lock (_stateLock)
            {
                IsRunning = false;
                _runCancellation?.Dispose();
                _runCancellation = null;
            }
            _progress = null;
        }
    }

    public Task StopAsync()
    {
        lock (_stateLock)
        {
            if (IsRunning)
            {
                Report(ScrapeEvent.Log("正在停止任务；已下载的部分文件将保留用于断点续传...", LogLevel.Warning));
                _runCancellation?.Cancel();
            }
        }
        return Task.CompletedTask;
    }

    private async Task<ScrapeResult> RunAuthorAsync(DownloadConfig config, CancellationToken cancellationToken)
    {
        Report(ScrapeEvent.Log("任务模式：作者集合下载"));
        Report(ScrapeEvent.Log($"服务器：https://{config.Domain}"));
        Report(ScrapeEvent.Log($"服务平台：{config.Service}"));
        Report(ScrapeEvent.Log($"作者 ID：{config.Username}"));
        Report(ScrapeEvent.Log($"保存路径：{config.SavePath}"));

        var profile = await GetProfileAsync(config.Domain, config.Username, config.Service, cancellationToken)
            ?? throw new InvalidOperationException("获取用户资料失败。");
        var authorName = GetAuthorName(profile, config.Username);
        Report(ScrapeEvent.Author(authorName));
        Report(ScrapeEvent.Log($"作者名称：{authorName}", LogLevel.Success));
        var paths = CreateDirectories(config.SavePath, authorName, config.Username, config.AuthorDirectoryOverride);
        var progressDocument = await LoadOrCreateProgressAsync(paths.ProgressFile, config, cancellationToken);

        var posts = await GetPostsAsync(config.Domain, config.Username, config.Service, config.Limit, cancellationToken);
        if (posts.Count == 0)
        {
            Report(ScrapeEvent.Log("未获取到任何作品数据。", LogLevel.Warning));
            return new ScrapeResult(DownloadTaskStatus.Completed, 0, 0, 0);
        }

        await SavePostsPagesAsync(paths.JsonDirectory, posts, cancellationToken);
        return await DownloadPostsAsync(posts, config, paths.SourceDirectory, paths.ProgressFile, progressDocument, cancellationToken);
    }

    private async Task<ScrapeResult> RunSinglePostAsync(DownloadConfig config, CancellationToken cancellationToken)
    {
        var parsed = KemonoRules.ParsePostUrl(config.PostUrl, _settingsStore.Current.Domains.Append(config.Domain), config.Domain);
        Report(ScrapeEvent.Log("任务模式：单作品下载"));
        Report(ScrapeEvent.Log($"服务器：https://{config.Domain}"));
        Report(ScrapeEvent.Log($"作品链接：{parsed.CanonicalUri}"));
        Report(ScrapeEvent.Log($"保存路径：{config.SavePath}"));

        var profile = await GetProfileAsync(config.Domain, parsed.UserId, parsed.Service, cancellationToken)
            ?? throw new InvalidOperationException("获取用户资料失败。");
        var authorName = GetAuthorName(profile, parsed.UserId);
        Report(ScrapeEvent.Author(authorName));
        Report(ScrapeEvent.Log($"作者名称：{authorName}", LogLevel.Success));
        var normalizedConfig = config with { Service = parsed.Service, Username = parsed.UserId, PostUrl = parsed.CanonicalUri.ToString() };
        var paths = CreateDirectories(config.SavePath, authorName, parsed.UserId, config.AuthorDirectoryOverride);
        var progressDocument = await LoadOrCreateProgressAsync(paths.ProgressFile, normalizedConfig, cancellationToken);
        var post = await FindSinglePostAsync(config.Domain, parsed, cancellationToken)
            ?? throw new InvalidOperationException($"未找到 ID 为 {parsed.PostId} 的作品。");

        await SaveJsonAsync(Path.Combine(paths.JsonDirectory, $"post_{parsed.PostId}.json"), post, cancellationToken);
        return await DownloadPostsAsync([post], normalizedConfig, paths.SourceDirectory, paths.ProgressFile, progressDocument, cancellationToken);
    }

    private async Task<KemonoProfile?> GetProfileAsync(string domain, string userId, string service, CancellationToken cancellationToken)
    {
        Report(ScrapeEvent.Log("正在访问用户主页以建立会话..."));
        if (!await _apiClient.VisitHomepageAsync(domain, userId, service, 0, cancellationToken)) return null;
        await Task.Delay(_settingsStore.Current.PageRequestDelayMs, cancellationToken);
        var result = await _apiClient.GetProfileAsync(domain, userId, service, cancellationToken);
        return result.IsSuccess ? result.Value : null;
    }

    private async Task<IReadOnlyList<KemonoPost>> GetPostsAsync(string domain, string userId, string service, int limit, CancellationToken cancellationToken)
    {
        var posts = new List<KemonoPost>();
        var offset = 0;
        var page = 1;
        var consecutiveErrors = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await _apiClient.VisitHomepageAsync(domain, userId, service, offset, cancellationToken))
                {
                    consecutiveErrors++;
                }
                else
                {
                    await Task.Delay(_settingsStore.Current.PageRequestDelayMs, cancellationToken);
                    Report(ScrapeEvent.Log($"正在获取第 {page} 页作品数据..."));
                    var response = await _apiClient.GetPostsPageAsync(domain, userId, service, offset, cancellationToken);
                    if (response.IsSuccess)
                    {
                        var pagePosts = response.Value!;
                        if (pagePosts.Count == 0) break;
                        posts.AddRange(pagePosts);
                        consecutiveErrors = 0;
                        Report(ScrapeEvent.Log($"第 {page} 页获取 {pagePosts.Count} 条，累计 {posts.Count} 条。"));
                        if (limit > 0 && posts.Count >= limit) return posts.Take(limit).ToArray();
                        offset += KemonoRules.PageSize;
                        page++;
                        continue;
                    }
                    consecutiveErrors++;
                }
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException)
            {
                consecutiveErrors++;
                Report(ScrapeEvent.Log($"第 {page} 页获取失败：{error.Message}", LogLevel.Warning));
            }

            if (consecutiveErrors >= KemonoRules.MaxConsecutivePageErrors)
            {
                Report(ScrapeEvent.Log("连续分页错误达到上限，停止继续获取。", LogLevel.Error));
                break;
            }
            offset += KemonoRules.PageSize;
            page++;
        }
        return posts;
    }

    private async Task<KemonoPost?> FindSinglePostAsync(string domain, ParsedPostUrl parsed, CancellationToken cancellationToken)
    {
        var offset = 0;
        var page = 1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _apiClient.VisitHomepageAsync(domain, parsed.UserId, parsed.Service, offset, cancellationToken)) return null;
            await Task.Delay(_settingsStore.Current.PageRequestDelayMs, cancellationToken);
            Report(ScrapeEvent.Log($"正在第 {page} 页查找作品 {parsed.PostId}..."));
            var response = await _apiClient.GetPostsPageAsync(domain, parsed.UserId, parsed.Service, offset, cancellationToken);
            if (!response.IsSuccess) return null;
            var pagePosts = response.Value!;
            var target = pagePosts.FirstOrDefault(post => string.Equals(post.Id, parsed.PostId, StringComparison.Ordinal));
            if (target is not null) return target;
            if (pagePosts.Count < KemonoRules.PageSize) return null;
            offset += KemonoRules.PageSize;
            page++;
        }
    }

    private async Task<ScrapeResult> DownloadPostsAsync(IReadOnlyList<KemonoPost> posts, DownloadConfig config, string sourceDirectory, string progressFile, ProgressDocument progressDocument, CancellationToken cancellationToken)
    {
        var completed = 0;
        var skipped = 0;
        var downloaded = 0;
        var processed = 0;
        var plans = new List<PostDownloadPlan>();
        var immediateAttachmentCount = 0;
        var admissionDelay = TimeSpan.Zero;
        using var thumbnailPreparationGate = new SemaphoreSlim(Math.Min(2, _requestedConcurrent), Math.Min(2, _requestedConcurrent));

        foreach (var post in posts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = KemonoRules.GetPostMetadata(post);
            if (config.SkipExisting && progressDocument.Records.ContainsKey(metadata.Key))
            {
                skipped++;
                processed++;
                Report(ScrapeEvent.Log($"作品 {metadata.FolderName} 已完成，跳过。"));
                Report(ScrapeEvent.Progress(processed, posts.Count, downloaded, metadata.FolderName));
                continue;
            }

            var attachmentTasks = KemonoRules.GetDownloadTasks(post);
            var plan = new PostDownloadPlan(
                post,
                metadata,
                Path.Combine(sourceDirectory, metadata.FolderName),
                attachmentTasks,
                admissionDelay);
            plan.PreparationTask = PreparePlanAsync(plan, config, thumbnailPreparationGate, cancellationToken);
            plans.Add(plan);

            if (admissionDelay == TimeSpan.Zero)
            {
                immediateAttachmentCount += attachmentTasks.Count;
                if (immediateAttachmentCount >= _requestedConcurrent && config.BatchDelayMs > 0)
                {
                    admissionDelay = TimeSpan.FromMilliseconds(config.BatchDelayMs);
                }
            }
            else
            {
                admissionDelay += TimeSpan.FromMilliseconds(config.BatchDelayMs);
            }
        }

        var preparing = plans.Select(plan => plan.PreparationTask).ToList();
        var readyPlans = new Queue<PostDownloadPlan>();
        var running = new List<Task<AttachmentExecutionResult>>();

        while (preparing.Count > 0 || readyPlans.Count > 0 || running.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preparedTasks = preparing.Where(task => task.IsCompleted).ToArray();
            foreach (var preparedTask in preparedTasks)
            {
                var plan = await preparedTask;
                preparing.Remove(preparedTask);
                if (plan.TotalAttachments == 0)
                {
                    await FinalizePlanAsync(plan, progressDocument, progressFile, cancellationToken);
                    completed++;
                    processed++;
                    Report(ScrapeEvent.Progress(processed, posts.Count, downloaded, plan.Metadata.FolderName));
                }
                else
                {
                    readyPlans.Enqueue(plan);
                }
            }

            var concurrencySignal = Volatile.Read(ref _concurrencyChanged).Task;
            var concurrency = Math.Max(1, Volatile.Read(ref _adaptiveConcurrent));
            while (running.Count < concurrency && readyPlans.Count > 0)
            {
                var plan = readyPlans.Dequeue();
                if (!plan.TryTakeNext(out var entry)) continue;
                if (plan.HasPendingAttachments) readyPlans.Enqueue(plan);
                var filename = KemonoRules.GetSequentialFilename(entry.Attachment, entry.Index);
                Report(ScrapeEvent.Progress(processed, posts.Count, downloaded, $"{plan.Metadata.FolderName}/{filename}"));
                running.Add(ExecuteAttachmentAsync(plan, entry, config, cancellationToken));
            }

            if (preparing.Count == 0 && running.Count == 0) continue;

            var waitTasks = new List<Task>(preparing.Count + running.Count + 1);
            waitTasks.AddRange(preparing);
            waitTasks.AddRange(running);
            if (readyPlans.Count > 0) waitTasks.Add(concurrencySignal);
            var finishedTask = await Task.WhenAny(waitTasks);

            var runningIndex = running.FindIndex(task => ReferenceEquals(task, finishedTask));
            if (runningIndex < 0) continue;

            var outcome = await running[runningIndex];
            running.RemoveAt(runningIndex);
            if (outcome.Success) downloaded++;
            outcome.Plan.RecordOutcome(outcome.Success);
            if (!outcome.Plan.IsFinished) continue;

            var planCompleted = await FinalizePlanAsync(outcome.Plan, progressDocument, progressFile, cancellationToken);
            if (planCompleted) completed++;
            processed++;
            Report(ScrapeEvent.Progress(processed, posts.Count, downloaded, outcome.Plan.Metadata.FolderName));
        }

        Report(ScrapeEvent.Progress(posts.Count, posts.Count, downloaded, "已完成"));
        Report(ScrapeEvent.Log($"下载完成：处理 {completed} 个作品，跳过 {skipped} 个作品，成功文件 {downloaded} 个。", LogLevel.Success));
        return new ScrapeResult(DownloadTaskStatus.Completed, completed, skipped, downloaded);
    }

    private async Task<PostDownloadPlan> PreparePlanAsync(PostDownloadPlan plan, DownloadConfig config, SemaphoreSlim preparationGate, CancellationToken cancellationToken)
    {
        if (plan.AdmissionDelay > TimeSpan.Zero) await Task.Delay(plan.AdmissionDelay, cancellationToken);
        if (config.UseThumbnail)
        {
            await preparationGate.WaitAsync(cancellationToken);
            try
            {
                plan.ThumbnailMap = await GetThumbnailMapAsync(config.Domain, plan.Post, plan.Metadata.PostId, cancellationToken);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
            {
                Report(ScrapeEvent.Log($"获取作品 {plan.Metadata.FolderName} 的缩略图数据失败：{error.Message}", LogLevel.Warning));
            }
            finally
            {
                preparationGate.Release();
            }
        }
        return plan;
    }

    private async Task<bool> FinalizePlanAsync(PostDownloadPlan plan, ProgressDocument progressDocument, string progressFile, CancellationToken cancellationToken)
    {
        if (plan.SuccessfulAttachments != plan.TotalAttachments)
        {
            Report(ScrapeEvent.Log($"作品 {plan.Metadata.FolderName} 有 {plan.TotalAttachments - plan.SuccessfulAttachments} 个附件在重试后仍失败，将不会标记为完成。", LogLevel.Warning));
            return false;
        }

        progressDocument.Records[plan.Metadata.Key] = new ProgressRecord
        {
            PostId = plan.Metadata.PostId,
            PostTitle = plan.Metadata.PostTitle,
            PostDate = plan.Metadata.PostDate,
            AttachmentsCount = plan.TotalAttachments,
            DownloadedAttachments = plan.SuccessfulAttachments,
            CompletedTime = DateTimeOffset.UtcNow
        };
        await _progressStore.SaveAsync(progressFile, progressDocument, cancellationToken);
        Report(ScrapeEvent.Log($"作品 {plan.Metadata.FolderName} 下载完成，共 {plan.SuccessfulAttachments} 个附件。", LogLevel.Success));
        return true;
    }

    private async Task<AttachmentExecutionResult> ExecuteAttachmentAsync(PostDownloadPlan plan, (KemonoAttachment Attachment, int Index) entry, DownloadConfig config, CancellationToken cancellationToken)
    {
        var success = await DownloadAttachmentAsync(entry, plan.ThumbnailMap, config, plan.Folder, cancellationToken);
        return new AttachmentExecutionResult(plan, success);
    }

    private async Task<bool> DownloadAttachmentAsync((KemonoAttachment Attachment, int Index) entry, IReadOnlyDictionary<string, Uri>? thumbnailMap, DownloadConfig config, string folder, CancellationToken cancellationToken)
    {
        var filename = KemonoRules.GetSequentialFilename(entry.Attachment, entry.Index);
        Uri uri;
        if (config.UseThumbnail)
        {
            if (thumbnailMap is null || !thumbnailMap.TryGetValue(entry.Attachment.Name, out uri!)) return false;
        }
        else
        {
            uri = KemonoRules.BuildDownloadUri(entry.Attachment, config.Domain);
        }

        try
        {
            var destination = Path.Combine(folder, filename);
            Report(ScrapeEvent.Log($"正在下载：{filename}"));
            var result = await _fileDownloader.DownloadAsync(uri, config.Domain, destination, KemonoRules.GetDownloadTimeout(filename, _settingsStore.Current), overwrite: !config.SkipExisting, cancellationToken);
            Report(ScrapeEvent.Log(result.AlreadyExists ? $"文件已存在：{filename}" : $"下载完成：{filename}", result.AlreadyExists ? LogLevel.Information : LogLevel.Success));
            return result.Success;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Report(ScrapeEvent.Log($"下载超时：{filename}", LogLevel.Warning));
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Report(ScrapeEvent.Log($"下载失败 {filename}：{error.Message}", LogLevel.Warning));
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, Uri>?> GetThumbnailMapAsync(string domain, KemonoPost post, string postId, CancellationToken cancellationToken)
    {
        var service = post.Service;
        var userId = post.User;
        if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(userId)) return null;
        var detail = await _apiClient.GetPostDetailAsync(domain, service, userId, postId, cancellationToken);
        if (!detail.IsSuccess) return null;
        return detail.Value!.Previews
            .Where(preview => string.Equals(preview.Type, "thumbnail", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(preview.Name) && !string.IsNullOrWhiteSpace(preview.Path))
            .GroupBy(preview => preview.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Uri($"{KemonoRules.GetThumbnailBaseUrl(domain)}/thumbnail/data{group.First().Path}"), StringComparer.Ordinal);
    }

    private async Task<ProgressDocument> LoadOrCreateProgressAsync(string progressFile, DownloadConfig config, CancellationToken cancellationToken)
    {
        ProgressDocument document;
        var progressExists = await _progressStore.ExistsAsync(progressFile, cancellationToken);
        if (progressExists)
        {
            var existing = await _progressStore.LoadAsync(progressFile, cancellationToken);
            document = config.ForceFresh ? new ProgressDocument() : existing;
            if (!config.ForceFresh && document.Records.Count > 0)
            {
                Report(ScrapeEvent.Incremental(document.Records.Count));
                Report(ScrapeEvent.Log($"检测到 {document.Records.Count} 个已完成作品，启用增量/续传模式。", LogLevel.Success));
            }
        }
        else
        {
            document = new ProgressDocument();
        }
        if (config.ForceFresh) Report(ScrapeEvent.Log("已创建全新进度记录。"));
        document.Config = config with { ForceFresh = false };
        await _progressStore.SaveAsync(progressFile, document, cancellationToken);
        return document;
    }

    private static string GetAuthorName(KemonoProfile profile, string userId)
    {
        var name = KemonoRules.SanitizeFilename(profile.PublicId);
        if (string.IsNullOrWhiteSpace(name)) name = KemonoRules.SanitizeFilename(profile.Name);
        return string.IsNullOrWhiteSpace(name) ? $"user_{userId}" : name;
    }

    private DownloadPaths CreateDirectories(string savePath, string authorName, string userId, string authorDirectoryOverride)
    {
        var authorDirectory = string.IsNullOrWhiteSpace(authorDirectoryOverride)
            ? Path.Combine(savePath, authorName)
            : Path.GetFullPath(authorDirectoryOverride);
        if (string.IsNullOrWhiteSpace(authorDirectoryOverride))
        {
            var fallbackName = $"user_{userId}";
            var fallbackDirectory = Path.Combine(savePath, fallbackName);
            if (!string.Equals(authorName, fallbackName, StringComparison.OrdinalIgnoreCase) &&
                !Directory.Exists(authorDirectory) && Directory.Exists(fallbackDirectory))
            {
                try
                {
                    Directory.Move(fallbackDirectory, authorDirectory);
                    Report(ScrapeEvent.Log($"已将旧作者目录 {fallbackName} 迁移为 {authorName}。", LogLevel.Success));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    authorDirectory = fallbackDirectory;
                    Report(ScrapeEvent.Log($"作者目录重命名失败，将继续使用 {fallbackName}：{error.Message}", LogLevel.Warning));
                }
            }
        }
        var jsonDirectory = Path.Combine(authorDirectory, "json");
        var sourceDirectory = Path.Combine(authorDirectory, "src");
        Directory.CreateDirectory(jsonDirectory);
        Directory.CreateDirectory(sourceDirectory);
        return new DownloadPaths(authorDirectory, jsonDirectory, sourceDirectory, Path.Combine(authorDirectory, "download_progress.json"));
    }

    private static async Task SavePostsPagesAsync(string jsonDirectory, IReadOnlyList<KemonoPost> posts, CancellationToken cancellationToken)
    {
        var pages = (int)Math.Ceiling(posts.Count / (double)KemonoRules.PostsPerJsonFile);
        for (var page = 0; page < pages; page++)
        {
            await SaveJsonAsync(Path.Combine(jsonDirectory, $"{page + 1}.json"), posts.Skip(page * KemonoRules.PostsPerJsonFile).Take(KemonoRules.PostsPerJsonFile).ToArray(), cancellationToken);
        }
    }

    private static async Task SaveJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken);
    }

    private void HandleRateLimited()
    {
        var current = Volatile.Read(ref _adaptiveConcurrent);
        var reduced = Math.Max(1, current / 2);
        Interlocked.Exchange(ref _adaptiveConcurrent, reduced);
        Interlocked.Exchange(ref _successfulRequests, 0);
        SignalConcurrencyChanged();
        Report(ScrapeEvent.Log($"收到 429 限流，并发已降至 {reduced}。", LogLevel.Warning));
    }

    private void HandleRequestSucceeded()
    {
        if (Interlocked.Increment(ref _successfulRequests) < 2) return;
        Interlocked.Exchange(ref _successfulRequests, 0);
        var current = Volatile.Read(ref _adaptiveConcurrent);
        if (current >= _requestedConcurrent) return;
        var recovered = Math.Min(_requestedConcurrent, current + 1);
        Interlocked.Exchange(ref _adaptiveConcurrent, recovered);
        SignalConcurrencyChanged();
        Report(ScrapeEvent.Log($"请求恢复正常，并发已恢复至 {recovered}。"));
    }

    private void HandleFileRetrying(FileRetryEvent retry) =>
        Report(ScrapeEvent.Log($"文件 {retry.FileName} 下载失败（{retry.Reason}），{retry.Delay.TotalMilliseconds:F0}ms 后进行第 {retry.FailedAttempt + 1}/{retry.MaxAttempts} 次尝试。", LogLevel.Warning));

    private void SignalConcurrencyChanged()
    {
        var next = CreateConcurrencySignal();
        var previous = Interlocked.Exchange(ref _concurrencyChanged, next);
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateConcurrencySignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Report(ScrapeEvent value) => _progress?.Report(value);

    private sealed record DownloadPaths(string AuthorDirectory, string JsonDirectory, string SourceDirectory, string ProgressFile);
    private sealed record AttachmentExecutionResult(PostDownloadPlan Plan, bool Success);

    private sealed class PostDownloadPlan(
        KemonoPost post,
        PostMetadata metadata,
        string folder,
        IReadOnlyList<(KemonoAttachment Attachment, int Index)> attachments,
        TimeSpan admissionDelay)
    {
        private int _cursor;
        private int _finishedAttachments;

        public KemonoPost Post { get; } = post;
        public PostMetadata Metadata { get; } = metadata;
        public string Folder { get; } = folder;
        public IReadOnlyList<(KemonoAttachment Attachment, int Index)> Attachments { get; } = attachments;
        public TimeSpan AdmissionDelay { get; } = admissionDelay;
        public Task<PostDownloadPlan> PreparationTask { get; set; } = null!;
        public IReadOnlyDictionary<string, Uri>? ThumbnailMap { get; set; }
        public int TotalAttachments => Attachments.Count;
        public int SuccessfulAttachments { get; private set; }
        public bool HasPendingAttachments => _cursor < Attachments.Count;
        public bool IsFinished => _finishedAttachments == Attachments.Count;

        public bool TryTakeNext(out (KemonoAttachment Attachment, int Index) entry)
        {
            if (!HasPendingAttachments)
            {
                entry = default;
                return false;
            }
            entry = Attachments[_cursor++];
            return true;
        }

        public void RecordOutcome(bool success)
        {
            _finishedAttachments++;
            if (success) SuccessfulAttachments++;
        }
    }
}
