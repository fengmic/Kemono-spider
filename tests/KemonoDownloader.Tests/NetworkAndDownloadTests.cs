using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KemonoDownloader.Core;
using KemonoDownloader.Infrastructure;

namespace KemonoDownloader.Tests;

public sealed class NetworkAndDownloadTests
{
    [Fact]
    public async Task ApiClient_RetriesRateLimitAndPreservesReferer()
    {
        var handler = new StubHttpMessageHandler((request, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : StubHttpMessageHandler.Json(HttpStatusCode.OK, "{\"public_id\":\"artist\"}"));
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var settings = new StubSettingsStore(new AppSettings { Retries = 2, RequestTimeoutSeconds = 5, PageRequestDelayMs = 500 });
        var client = new KemonoApiClient(provider, settings);
        var limited = 0;
        client.RateLimited += () => limited++;
        var result = await client.GetProfileAsync("kemono.su", "12", "fanbox", CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("artist", result.Value!.PublicId);
        Assert.Equal(1, limited);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://kemono.su/fanbox/user/12", handler.Requests[1].Headers.Referrer!.ToString().TrimEnd('/'));
        Assert.Equal("kemono.su", handler.Requests[1].RequestUri!.Host);
    }

    [Fact]
    public async Task ApiClient_DeserializesPawchiveNameWhenPublicIdIsNull()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"id\":\"33970936\",\"name\":\"MildT\",\"public_id\":null}"));
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var client = new KemonoApiClient(provider, new StubSettingsStore(new AppSettings { Retries = 1 }));

        var result = await client.GetProfileAsync("pawchive.pw", "33970936", "fanbox", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("MildT", result.Value!.Name);
        Assert.Null(result.Value.PublicId);
    }

    [Fact]
    public async Task FileDownloader_WritesAtomicallyAndValidatesLength()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var bytes = Encoding.UTF8.GetBytes("complete-file");
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var destination = Path.Combine(directory.Path, "file.bin");
        var result = await new FileDownloader(provider, new StubSettingsStore()).DownloadAsync(new Uri("https://kemono.su/data.bin"), "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists($"{destination}.part"));
        Assert.Equal("https://kemono.su/", handler.Requests[0].Headers.Referrer!.ToString());
    }

    [Fact]
    public async Task FileDownloader_RetriesTransientServerError()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var downloader = new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 3 }), _ => TimeSpan.Zero);
        var retries = new List<FileRetryEvent>();
        downloader.Retrying += retries.Add;

        var result = await downloader.DownloadAsync(new Uri("https://kemono.su/data.bin"), "kemono.su", Path.Combine(directory.Path, "file.bin"), TimeSpan.FromSeconds(5), false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, calls);
        Assert.Single(retries);
    }

    [Fact]
    public async Task FileDownloader_DoesNotRetryPermanentClientError()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) => { calls++; return new HttpResponseMessage(HttpStatusCode.NotFound); });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var downloader = new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 5 }), _ => TimeSpan.Zero);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(new Uri("https://kemono.su/missing.bin"), "kemono.su", Path.Combine(directory.Path, "file.bin"), TimeSpan.FromSeconds(5), false, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FileDownloader_ContinuousChunksCanExceedIdleTimeout()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var bytes = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var handler = new StubHttpMessageHandler((_, _) => ResponseWithStream(
            HttpStatusCode.OK,
            new DelayedChunkStream(bytes, 4, TimeSpan.FromMilliseconds(30)),
            bytes.Length));
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        var result = await new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1 }))
            .DownloadAsync(new Uri("https://kemono.su/slow.bin"), "kemono.su", Path.Combine(directory.Path, "slow.bin"), TimeSpan.FromMilliseconds(60), false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "slow.bin")));
    }

    [Fact]
    public async Task FileDownloader_StopsWhenNoDataArrivesWithinIdleTimeout()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var handler = new StubHttpMessageHandler((_, _) => ResponseWithStream(
            HttpStatusCode.OK,
            new StallAfterBytesStream([1]),
            2));
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var destination = Path.Combine(directory.Path, "idle.bin");

        await Assert.ThrowsAsync<DownloadIdleTimeoutException>(() =>
            new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1 }))
                .DownloadAsync(new Uri("https://kemono.su/idle.bin"), "kemono.su", destination, TimeSpan.FromMilliseconds(80), false, CancellationToken.None));

        Assert.Equal([1], await File.ReadAllBytesAsync($"{destination}.part"));
        Assert.True(File.Exists($"{destination}.part.meta.json"));
    }

    [Fact]
    public async Task FileDownloader_StopsWhenResponseHeadersTimeout()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var handler = new AsyncStubHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable response.");
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<DownloadHeaderTimeoutException>(() =>
            new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1, DownloadHeaderTimeoutSeconds = 0 }))
                .DownloadAsync(new Uri("https://kemono.su/headers.bin"), "kemono.su", Path.Combine(directory.Path, "headers.bin"), TimeSpan.FromSeconds(5), false, CancellationToken.None));
    }

    [Fact]
    public async Task FileDownloader_RetriesFromPartialOffsetAndAppends()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var bytes = Encoding.UTF8.GetBytes("0123456789");
        var handler = new StubHttpMessageHandler((request, call) =>
        {
            if (call == 1)
            {
                return ResponseWithStream(HttpStatusCode.OK, new ThrowAfterBytesStream(bytes, 4), bytes.Length, "\"v1\"");
            }

            Assert.Equal(4, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.IfRange?.EntityTag?.ToString());
            return PartialResponse(bytes[4..], 4, bytes.Length, "\"v1\"");
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var destination = Path.Combine(directory.Path, "resumed.bin");

        var result = await new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 3 }), _ => TimeSpan.Zero)
            .DownloadAsync(new Uri("https://kemono.su/resumed.bin"), "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists($"{destination}.part.meta.json"));
    }

    [Fact]
    public async Task FileDownloader_ResumesPartialFileCreatedByEarlierProcess()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var bytes = Encoding.UTF8.GetBytes("restart-resume");
        var destination = Path.Combine(directory.Path, "restart.bin");
        await WritePartialStateAsync(destination, new Uri("https://kemono.su/restart.bin"), bytes[..7], bytes.Length, "\"stable\"");
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(7, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"stable\"", request.Headers.IfRange?.EntityTag?.ToString());
            return PartialResponse(bytes[7..], 7, bytes.Length, "\"stable\"");
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        var result = await new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1 }))
            .DownloadAsync(new Uri("https://kemono.su/restart.bin"), "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task FileDownloader_RestartsCleanlyWhenServerIgnoresRange()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var bytes = Encoding.UTF8.GetBytes("complete-content");
        var uri = new Uri("https://kemono.su/no-range.bin");
        var destination = Path.Combine(directory.Path, "no-range.bin");
        await WritePartialStateAsync(destination, uri, bytes[..5], bytes.Length, "\"v1\"");
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(5, request.Headers.Range?.Ranges.Single().From);
            return ResponseWithStream(HttpStatusCode.OK, new MemoryStream(bytes), bytes.Length, "\"v1\"");
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        await new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1 }))
            .DownloadAsync(uri, "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task FileDownloader_RestartsWhenResumeValidatorChanges()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var oldBytes = Encoding.UTF8.GetBytes("old-content");
        var newBytes = Encoding.UTF8.GetBytes("new-complete-content");
        var uri = new Uri("https://kemono.su/changed.bin");
        var destination = Path.Combine(directory.Path, "changed.bin");
        await WritePartialStateAsync(destination, uri, oldBytes[..4], oldBytes.Length, "\"old\"");
        var handler = new StubHttpMessageHandler((request, call) =>
        {
            if (call == 1)
            {
                Assert.Equal(4, request.Headers.Range?.Ranges.Single().From);
                return PartialResponse(newBytes[4..], 4, newBytes.Length, "\"new\"");
            }

            Assert.Null(request.Headers.Range);
            return ResponseWithStream(HttpStatusCode.OK, new MemoryStream(newBytes), newBytes.Length, "\"new\"");
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        await new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1 }))
            .DownloadAsync(uri, "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task FileDownloader_PromotesCompletePartWhenServerReturns416()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var bytes = Encoding.UTF8.GetBytes("already-complete");
        var uri = new Uri("https://kemono.su/complete.bin");
        var destination = Path.Combine(directory.Path, "complete.bin");
        await WritePartialStateAsync(destination, uri, bytes, bytes.Length, "\"v1\"");
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(bytes.Length);
            return response;
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        var result = await new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 1 }))
            .DownloadAsync(uri, "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists($"{destination}.part"));
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    public async Task FileDownloader_RejectsErrorDocumentsForImagesWithoutRetry(string mediaType)
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("error") };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return response;
        });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var destination = Path.Combine(directory.Path, "image.jpg");

        await Assert.ThrowsAsync<InvalidDownloadContentException>(() =>
            new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 3 }), _ => TimeSpan.Zero)
                .DownloadAsync(new Uri("https://kemono.su/image.jpg"), "kemono.su", destination, TimeSpan.FromSeconds(5), false, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.False(File.Exists($"{destination}.part"));
    }

    [Fact]
    public async Task FileDownloader_RejectsZeroLengthResponse()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDownloadContentException>(() =>
            new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 3 }), _ => TimeSpan.Zero)
                .DownloadAsync(new Uri("https://kemono.su/empty.bin"), "kemono.su", Path.Combine(directory.Path, "empty.bin"), TimeSpan.FromSeconds(5), false, CancellationToken.None));
    }

    [Fact]
    public async Task FileDownloader_StopsAfterTwoAttemptsWithoutProgress()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var calls = 0;
        var handler = new StubHttpMessageHandler((_, _) => { calls++; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); });
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));

        await Assert.ThrowsAsync<DownloadNoProgressException>(() =>
            new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 10 }), _ => TimeSpan.Zero)
                .DownloadAsync(new Uri("https://kemono.su/unavailable.bin"), "kemono.su", Path.Combine(directory.Path, "unavailable.bin"), TimeSpan.FromSeconds(5), false, CancellationToken.None));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FileDownloader_CancellationPreservesNonEmptyPartialState()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var handler = new StubHttpMessageHandler((_, _) => ResponseWithStream(
            HttpStatusCode.OK,
            new StallAfterBytesStream([1, 2, 3, 4]),
            5,
            "\"v1\""));
        using var provider = new StubNetworkClientProvider(new HttpClient(handler));
        var destination = Path.Combine(directory.Path, "cancel.bin");
        using var cancellation = new CancellationTokenSource();
        var task = new FileDownloader(provider, new StubSettingsStore(new AppSettings { Retries = 3 }))
            .DownloadAsync(new Uri("https://kemono.su/cancel.bin"), "kemono.su", destination, TimeSpan.FromSeconds(10), false, cancellation.Token);

        await WaitUntilAsync(() => File.Exists($"{destination}.part.meta.json"));
        await Task.Delay(50);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync($"{destination}.part"));
        Assert.True(File.Exists($"{destination}.part.meta.json"));
    }

    [Fact]
    public void DownloadWatchdog_RejectsSustainedLowSpeedAfterGraceAndWindow()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var watchdog = new DownloadWatchdog(started, TimeSpan.FromHours(2), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), 8 * 1024);
        watchdog.RecordProgress(started + TimeSpan.FromSeconds(60), 0);
        watchdog.RecordProgress(started + TimeSpan.FromSeconds(360), 1024);

        Assert.Throws<DownloadLowSpeedException>(() => watchdog.ThrowIfLimitExceeded(started + TimeSpan.FromSeconds(360)));
    }

    [Fact]
    public void DownloadWatchdog_IgnoresShortLowSpeedDip()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var watchdog = new DownloadWatchdog(started, TimeSpan.FromHours(2), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(300), 8 * 1024);
        watchdog.RecordProgress(started + TimeSpan.FromSeconds(60), 0);
        watchdog.RecordProgress(started + TimeSpan.FromSeconds(350), 3 * 1024 * 1024);
        watchdog.RecordProgress(started + TimeSpan.FromSeconds(360), 3 * 1024 * 1024);

        watchdog.ThrowIfLimitExceeded(started + TimeSpan.FromSeconds(360));
    }

    [Fact]
    public void DownloadWatchdog_CalculatesTypeMinimumsAndGlobalCap()
    {
        var settings = new AppSettings { DownloadMinimumSpeedKibPerSecond = 8, DownloadMaxTotalHours = 24 };

        Assert.Equal(TimeSpan.FromHours(2), DownloadWatchdog.CalculateHardTimeout("image.jpg", null, settings));
        Assert.Equal(TimeSpan.FromHours(12), DownloadWatchdog.CalculateHardTimeout("movie.mp4", null, settings));
        Assert.Equal(TimeSpan.FromHours(4), DownloadWatchdog.CalculateHardTimeout("archive.zip", null, settings));
        Assert.Equal(TimeSpan.FromHours(24), DownloadWatchdog.CalculateHardTimeout("huge.bin", 10L * 1024 * 1024 * 1024, settings));
        Assert.Equal(TimeSpan.FromHours(12), DownloadWatchdog.CalculateHardTimeout("movie.mp4", null, settings with { DownloadMaxTotalHours = 1 }));
    }

    private static HttpResponseMessage ResponseWithStream(HttpStatusCode status, Stream stream, long contentLength, string? etag = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = contentLength;
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static HttpResponseMessage PartialResponse(byte[] bytes, long from, long totalLength, string? etag = null)
    {
        var response = ResponseWithStream(HttpStatusCode.PartialContent, new MemoryStream(bytes), bytes.Length, etag);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, totalLength - 1, totalLength);
        return response;
    }

    private static async Task WritePartialStateAsync(string destination, Uri uri, byte[] bytes, long totalLength, string? etag)
    {
        await File.WriteAllBytesAsync($"{destination}.part", bytes);
        await File.WriteAllTextAsync($"{destination}.part.meta.json", JsonSerializer.Serialize(new
        {
            Url = uri.AbsoluteUri,
            TotalLength = totalLength,
            ETag = etag,
            DownloadedLength = bytes.LongLength,
            UpdatedAt = DateTimeOffset.UtcNow
        }));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
