using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class FileDownloader(
    INetworkClientProvider clientProvider,
    ISettingsStore settingsStore,
    Func<int, TimeSpan>? backoffFactory = null,
    TimeProvider? timeProvider = null) : IFileDownloader
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public event Action? RateLimited;
    public event Action? RequestSucceeded;
    public event Action<FileRetryEvent>? Retrying;

    public async Task<DownloadResult> DownloadAsync(Uri uri, string domain, string destinationPath, TimeSpan timeout, bool overwrite, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (!overwrite && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
        {
            return new DownloadResult(true, true, 0);
        }

        var partPath = $"{destinationPath}.part";
        var metadataPath = $"{partPath}.meta.json";
        var metadata = await LoadMetadataAsync(uri, partPath, metadataPath, cancellationToken);
        var maxAttempts = Math.Max(1, settingsStore.Current.Retries);
        var consecutiveNoProgress = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var beforeLength = GetLength(partPath);
            try
            {
                var result = await DownloadOnceAsync(uri, domain, destinationPath, partPath, metadataPath, metadata, timeout, cancellationToken);
                RequestSucceeded?.Invoke();
                return result with { Attempts = attempt };
            }
            catch (Exception error)
            {
                var afterLength = GetLength(partPath);
                if (afterLength == 0)
                {
                    ResetPartial(partPath, metadataPath);
                    metadata = PartialDownloadMetadata.Create(uri);
                }
                if (cancellationToken.IsCancellationRequested) throw;

                if (afterLength <= beforeLength) consecutiveNoProgress++;
                else consecutiveNoProgress = 0;

                var statusCode = error is HttpRequestException requestError ? requestError.StatusCode : null;
                if (statusCode == HttpStatusCode.TooManyRequests) RateLimited?.Invoke();
                if (consecutiveNoProgress >= 2)
                {
                    throw new DownloadNoProgressException("连续两次下载尝试没有收到任何新数据。", error);
                }
                if (!IsTransient(error) || attempt >= maxAttempts) throw;

                metadata = await ReconcileMetadataAsync(uri, partPath, metadataPath, metadata, cancellationToken);
                var delay = backoffFactory?.Invoke(attempt) ?? KemonoApiClient.GetBackoff(attempt);
                Retrying?.Invoke(new FileRetryEvent(
                    Path.GetFileName(destinationPath),
                    attempt,
                    maxAttempts,
                    delay,
                    $"{Describe(error)}；已保留 {FormatBytes(afterLength)}",
                    statusCode.HasValue ? (int)statusCode.Value : null));
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException("文件下载重试状态异常。");
    }

    private async Task<DownloadResult> DownloadOnceAsync(
        Uri uri,
        string domain,
        string destinationPath,
        string partPath,
        string metadataPath,
        PartialDownloadMetadata metadata,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken,
        bool allowResume = true)
    {
        var resumeOffset = allowResume ? GetLength(partPath) : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        request.Headers.Referrer = new Uri(KemonoRules.GetBaseUrl(domain));
        if (resumeOffset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeOffset, null);
            if (EntityTagHeaderValue.TryParse(metadata.ETag, out var entityTag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
            }
            else if (metadata.LastModified.HasValue)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(metadata.LastModified.Value);
            }
        }

        using var response = await SendForHeadersAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && resumeOffset > 0)
        {
            var rangeTotalLength = response.Content.Headers.ContentRange?.Length ?? metadata.TotalLength;
            if (rangeTotalLength.HasValue && resumeOffset == rangeTotalLength.Value)
            {
                PromoteCompletedPart(partPath, metadataPath, destinationPath);
                return new DownloadResult(true, false, resumeOffset);
            }

            ResetPartial(partPath, metadataPath);
            return await DownloadOnceAsync(uri, domain, destinationPath, partPath, metadataPath, PartialDownloadMetadata.Create(uri), idleTimeout, cancellationToken, false);
        }

        response.EnsureSuccessStatusCode();
        try
        {
            ValidateContent(response, destinationPath);
        }
        catch (InvalidDownloadContentException)
        {
            ResetPartial(partPath, metadataPath);
            throw;
        }

        var append = resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var validatorChanged = append && ValidatorChanged(metadata, response);
        if (append && (response.Content.Headers.ContentRange?.From != resumeOffset || validatorChanged))
        {
            ResetPartial(partPath, metadataPath);
            return await DownloadOnceAsync(uri, domain, destinationPath, partPath, metadataPath, PartialDownloadMetadata.Create(uri), idleTimeout, cancellationToken, false);
        }
        if (resumeOffset > 0 && !append)
        {
            ResetPartial(partPath, metadataPath);
            resumeOffset = 0;
        }

        var totalLength = response.Content.Headers.ContentRange?.Length ??
            (response.Content.Headers.ContentLength.HasValue ? resumeOffset + response.Content.Headers.ContentLength.Value : metadata.TotalLength);
        metadata = new PartialDownloadMetadata
        {
            Url = uri.AbsoluteUri,
            TotalLength = totalLength,
            ETag = response.Headers.ETag?.ToString(),
            LastModified = response.Content.Headers.LastModified,
            DownloadedLength = resumeOffset,
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        await SaveMetadataAsync(metadataPath, metadata, cancellationToken);

        var settings = settingsStore.Current;
        var hardTimeout = DownloadWatchdog.CalculateHardTimeout(destinationPath, totalLength, settings);
        var watchdog = new DownloadWatchdog(
            _timeProvider.GetUtcNow(),
            hardTimeout,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(settings.DownloadLowSpeedWindowSeconds),
            settings.DownloadMinimumSpeedKibPerSecond * 1024L);
        watchdog.RecordProgress(_timeProvider.GetUtcNow(), resumeOffset);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        var downloaded = resumeOffset;
        var lastMetadataLength = resumeOffset;
        var lastMetadataUpdate = _timeProvider.GetUtcNow();

        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(input, buffer, idleTimeout, cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            var now = _timeProvider.GetUtcNow();
            watchdog.RecordProgress(now, downloaded);
            watchdog.ThrowIfLimitExceeded(now);

            if (downloaded - lastMetadataLength >= 1024 * 1024 || now - lastMetadataUpdate >= TimeSpan.FromSeconds(5))
            {
                metadata = metadata with { DownloadedLength = downloaded, UpdatedAt = now };
                await SaveMetadataAsync(metadataPath, metadata, cancellationToken);
                lastMetadataLength = downloaded;
                lastMetadataUpdate = now;
            }
        }

        await output.FlushAsync(cancellationToken);
        if (totalLength.HasValue && downloaded != totalLength.Value)
        {
            await SaveMetadataAsync(metadataPath, metadata with { DownloadedLength = downloaded, UpdatedAt = _timeProvider.GetUtcNow() }, cancellationToken);
            throw new IOException($"下载不完整：{downloaded}/{totalLength.Value} bytes。");
        }
        if (downloaded == 0) throw new InvalidDownloadContentException("服务器返回了零长度文件。");

        output.Close();
        PromoteCompletedPart(partPath, metadataPath, destinationPath);
        return new DownloadResult(true, false, downloaded);
    }

    private static bool ValidatorChanged(PartialDownloadMetadata metadata, HttpResponseMessage response)
    {
        var responseETag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrWhiteSpace(metadata.ETag) && !string.IsNullOrWhiteSpace(responseETag))
        {
            return !string.Equals(metadata.ETag, responseETag, StringComparison.Ordinal);
        }

        var responseLastModified = response.Content.Headers.LastModified;
        return metadata.LastModified.HasValue && responseLastModified.HasValue &&
            metadata.LastModified.Value != responseLastModified.Value;
    }

    private async Task<HttpResponseMessage> SendForHeadersAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(settingsStore.Current.DownloadHeaderTimeoutSeconds));
        try
        {
            return await clientProvider.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadHeaderTimeoutException("等待服务器响应头超时。", error);
        }
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream input, byte[] buffer, TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCancellation.CancelAfter(idleTimeout);
        try
        {
            return await input.ReadAsync(buffer, idleCancellation.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadIdleTimeoutException($"连续 {idleTimeout.TotalSeconds:F0} 秒未收到数据。", error);
        }
    }

    private static void ValidateContent(HttpResponseMessage response, string destinationPath)
    {
        if (response.Content.Headers.ContentLength == 0) throw new InvalidDownloadContentException("服务器返回了零长度内容。 ");
        if (!KemonoRules.IsImageFile(destinationPath) && !KemonoRules.IsVideoFile(destinationPath)) return;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null) return;
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDownloadContentException($"服务器返回了错误内容类型：{mediaType}。");
        }
    }

    private async Task<PartialDownloadMetadata> LoadMetadataAsync(Uri uri, string partPath, string metadataPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(partPath) && File.Exists(metadataPath)) TryDelete(metadataPath);
        if (!File.Exists(partPath)) return PartialDownloadMetadata.Create(uri);
        if (!File.Exists(metadataPath))
        {
            ResetPartial(partPath, metadataPath);
            return PartialDownloadMetadata.Create(uri);
        }

        try
        {
            await using var stream = File.OpenRead(metadataPath);
            var metadata = await JsonSerializer.DeserializeAsync<PartialDownloadMetadata>(stream, JsonDefaults.Options, cancellationToken);
            var partLength = GetLength(partPath);
            if (metadata is null ||
                !string.Equals(metadata.Url, uri.AbsoluteUri, StringComparison.Ordinal) ||
                metadata.TotalLength < 0 ||
                metadata.TotalLength.HasValue && partLength > metadata.TotalLength.Value ||
                !string.IsNullOrWhiteSpace(metadata.ETag) && !EntityTagHeaderValue.TryParse(metadata.ETag, out _))
            {
                ResetPartial(partPath, metadataPath);
                return PartialDownloadMetadata.Create(uri);
            }
            return metadata with { DownloadedLength = partLength };
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            ResetPartial(partPath, metadataPath);
            return PartialDownloadMetadata.Create(uri);
        }
    }

    private async Task<PartialDownloadMetadata> ReconcileMetadataAsync(Uri uri, string partPath, string metadataPath, PartialDownloadMetadata metadata, CancellationToken cancellationToken)
    {
        var persisted = await TryReadMetadataAsync(metadataPath, cancellationToken) ?? metadata;
        var reconciled = persisted with { Url = uri.AbsoluteUri, DownloadedLength = GetLength(partPath), UpdatedAt = _timeProvider.GetUtcNow() };
        if (reconciled.DownloadedLength > 0) await SaveMetadataAsync(metadataPath, reconciled, cancellationToken);
        return reconciled;
    }

    private static async Task<PartialDownloadMetadata?> TryReadMetadataAsync(string metadataPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(metadataPath)) return null;
        try
        {
            await using var stream = File.OpenRead(metadataPath);
            return await JsonSerializer.DeserializeAsync<PartialDownloadMetadata>(stream, JsonDefaults.Options, cancellationToken);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task SaveMetadataAsync(string metadataPath, PartialDownloadMetadata metadata, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{metadataPath}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonDefaults.Options, cancellationToken);
            }
            File.Move(temporaryPath, metadataPath, true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static void PromoteCompletedPart(string partPath, string metadataPath, string destinationPath)
    {
        File.Move(partPath, destinationPath, true);
        TryDelete(metadataPath);
    }

    private static void ResetPartial(string partPath, string metadataPath)
    {
        TryDelete(partPath);
        TryDelete(metadataPath);
    }

    private static long GetLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsTransient(Exception error) => error switch
    {
        InvalidDownloadContentException or DownloadNoProgressException => false,
        DownloadHeaderTimeoutException or DownloadIdleTimeoutException or DownloadLowSpeedException or DownloadHardTimeoutException => true,
        OperationCanceledException => true,
        IOException => true,
        HttpRequestException requestError when requestError.StatusCode is null => true,
        HttpRequestException requestError when requestError.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => true,
        HttpRequestException requestError when requestError.StatusCode.HasValue => (int)requestError.StatusCode.Value >= 500,
        _ => false
    };

    private static string Describe(Exception error) => error switch
    {
        DownloadHeaderTimeoutException => "响应头超时",
        DownloadIdleTimeoutException => "无数据超时",
        DownloadLowSpeedException => "持续低速",
        DownloadHardTimeoutException => "达到动态总时长上限",
        HttpRequestException requestError when requestError.StatusCode.HasValue => $"HTTP {(int)requestError.StatusCode.Value}",
        IOException => error.Message,
        _ => error.Message
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F2} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F2} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PartialDownloadMetadata
    {
        public string Url { get; init; } = string.Empty;
        public long? TotalLength { get; init; }
        public string? ETag { get; init; }
        public DateTimeOffset? LastModified { get; init; }
        public long DownloadedLength { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public static PartialDownloadMetadata Create(Uri uri) => new() { Url = uri.AbsoluteUri };
    }
}

internal sealed class DownloadWatchdog(
    DateTimeOffset startedAt,
    TimeSpan hardTimeout,
    TimeSpan gracePeriod,
    TimeSpan lowSpeedWindow,
    long minimumBytesPerSecond)
{
    private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();

    public void RecordProgress(DateTimeOffset now, long totalBytes)
    {
        if (_samples.Count > 0 && now - _samples.Last().At < TimeSpan.FromSeconds(1)) return;
        _samples.Enqueue((now, totalBytes));
        while (_samples.Count > 1 && now - _samples.ElementAt(1).At >= lowSpeedWindow) _samples.Dequeue();
    }

    public void ThrowIfLimitExceeded(DateTimeOffset now)
    {
        if (now - startedAt > hardTimeout)
        {
            throw new DownloadHardTimeoutException($"下载超过动态上限 {hardTimeout.TotalHours:F1} 小时。");
        }
        if (now - startedAt < gracePeriod + lowSpeedWindow || _samples.Count < 2) return;

        var first = _samples.Peek();
        var last = _samples.Last();
        var duration = last.At - first.At;
        if (duration < lowSpeedWindow) return;
        var speed = (last.Bytes - first.Bytes) / Math.Max(1, duration.TotalSeconds);
        if (speed < minimumBytesPerSecond)
        {
            throw new DownloadLowSpeedException($"连续 {lowSpeedWindow.TotalSeconds:F0} 秒平均速度仅 {speed / 1024d:F1} KiB/s。");
        }
    }

    public static TimeSpan CalculateHardTimeout(string filename, long? totalLength, AppSettings settings)
    {
        var typeMinimum = KemonoRules.IsVideoFile(filename)
            ? TimeSpan.FromHours(12)
            : KemonoRules.IsImageFile(filename) ? TimeSpan.FromHours(2) : TimeSpan.FromHours(4);
        var lengthBased = totalLength.HasValue
            ? TimeSpan.FromSeconds(totalLength.Value / Math.Max(1d, settings.DownloadMinimumSpeedKibPerSecond * 1024d) * 2)
            : TimeSpan.Zero;
        var desired = typeMinimum > lengthBased ? typeMinimum : lengthBased;
        var maximum = TimeSpan.FromHours(settings.DownloadMaxTotalHours);
        if (maximum < typeMinimum) maximum = typeMinimum;
        return desired < maximum ? desired : maximum;
    }
}

internal sealed class DownloadHeaderTimeoutException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class DownloadIdleTimeoutException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class DownloadLowSpeedException(string message) : IOException(message);
internal sealed class DownloadHardTimeoutException(string message) : IOException(message);
internal sealed class DownloadNoProgressException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class InvalidDownloadContentException(string message) : IOException(message);
