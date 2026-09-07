using System.Net;
using System.Text;
using KemonoDownloader.Core;

namespace KemonoDownloader.Tests;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
{
    private int _callCount;
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        Requests.Add(clone);
        return Task.FromResult(responder(request, Interlocked.Increment(ref _callCount)));
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed class AsyncStubHttpMessageHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    private int _callCount;
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        Requests.Add(clone);
        return responder(request, Interlocked.Increment(ref _callCount), cancellationToken);
    }
}

internal sealed class StubNetworkClientProvider(HttpClient client) : INetworkClientProvider
{
    public HttpClient Client { get; } = client;
    public void Rebuild() { }
    public void Dispose() => Client.Dispose();
}

internal sealed class StubSettingsStore(AppSettings? settings = null) : ISettingsStore
{
    public AppSettings Current { get; private set; } = settings ?? new AppSettings { PageRequestDelayMs = 500 };
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) { Current = value; return Task.CompletedTask; }
    public Task ResetAsync(CancellationToken cancellationToken = default) { Current = new AppSettings(); return Task.CompletedTask; }
}

internal sealed class StubApiClient : IKemonoApiClient
{
    public event Action? RateLimited;
    public event Action? RequestSucceeded;
    public KemonoProfile Profile { get; init; } = new() { PublicId = "test-author" };
    public IReadOnlyList<KemonoPost> Posts { get; init; } = [];
    public KemonoPostDetail PostDetail { get; init; } = new();
    public List<string> RequestedDomains { get; } = [];

    public Task<bool> VisitHomepageAsync(string domain, string userId, string service, int offset, CancellationToken cancellationToken)
    {
        RequestedDomains.Add(domain);
        return Task.FromResult(true);
    }
    public Task<ApiResult<KemonoProfile>> GetProfileAsync(string domain, string userId, string service, CancellationToken cancellationToken)
    {
        RequestedDomains.Add(domain);
        RequestSucceeded?.Invoke();
        return Task.FromResult(new ApiResult<KemonoProfile>(200, Profile));
    }
    public Task<ApiResult<IReadOnlyList<KemonoPost>>> GetPostsPageAsync(string domain, string userId, string service, int offset, CancellationToken cancellationToken)
    {
        RequestedDomains.Add(domain);
        RequestSucceeded?.Invoke();
        return Task.FromResult(new ApiResult<IReadOnlyList<KemonoPost>>(200, offset == 0 ? Posts : []));
    }
    public Task<ApiResult<KemonoPostDetail>> GetPostDetailAsync(string domain, string service, string userId, string postId, CancellationToken cancellationToken)
    {
        RequestedDomains.Add(domain);
        return Task.FromResult(new ApiResult<KemonoPostDetail>(200, PostDetail));
    }

    public void EmitRateLimit() => RateLimited?.Invoke();
}

internal sealed class StubFileDownloader : IFileDownloader
{
    public event Action? RateLimited;
    public event Action? RequestSucceeded;
    public event Action<FileRetryEvent>? Retrying;
    public List<(Uri Uri, string Domain, string Path, bool Overwrite)> Downloads { get; } = [];

    public async Task<DownloadResult> DownloadAsync(Uri uri, string domain, string destinationPath, TimeSpan timeout, bool overwrite, CancellationToken cancellationToken)
    {
        Downloads.Add((uri, domain, destinationPath, overwrite));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(destinationPath, "downloaded", cancellationToken);
        RequestSucceeded?.Invoke();
        return new DownloadResult(true, false, 10);
    }

    public void EmitRateLimit() => RateLimited?.Invoke();
    public void EmitSuccess() => RequestSucceeded?.Invoke();
    public void EmitRetry(FileRetryEvent value) => Retrying?.Invoke(value);
}

internal sealed class ControlledFileDownloader : IFileDownloader
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskCompletionSource<DownloadResult>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _started = [];
    private int _maxActive;

    public event Action? RateLimited;
    public event Action? RequestSucceeded;
    public event Action<FileRetryEvent>? Retrying;

    public IReadOnlyList<string> Started
    {
        get { lock (_lock) return _started.ToArray(); }
    }
    public int ActiveCount { get { lock (_lock) return _pending.Count; } }
    public int MaxActive { get { lock (_lock) return _maxActive; } }

    public async Task<DownloadResult> DownloadAsync(Uri uri, string domain, string destinationPath, TimeSpan timeout, bool overwrite, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _started.Add(destinationPath);
            _pending[destinationPath] = completion;
            _maxActive = Math.Max(_maxActive, _pending.Count);
        }
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    public void Complete(string title, bool success = true)
    {
        TaskCompletionSource<DownloadResult> completion;
        lock (_lock)
        {
            var pair = _pending.Single(item => item.Key.Contains(title, StringComparison.OrdinalIgnoreCase));
            completion = pair.Value;
            _pending.Remove(pair.Key);
        }
        if (success) RequestSucceeded?.Invoke();
        completion.TrySetResult(new DownloadResult(success, false, success ? 10 : 0));
    }

    public void EmitRateLimit() => RateLimited?.Invoke();
    public void EmitSuccess() => RequestSucceeded?.Invoke();
    public void EmitRetry(FileRetryEvent value) => Retrying?.Invoke(value);
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KemonoDownloader.Tests-{Guid.NewGuid():N}");
    public string Path { get; }
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, true);
    }
}

internal sealed class ThrowAfterBytesStream(byte[] data, int throwAfterBytes) : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= throwAfterBytes) throw new IOException("simulated EOF");
        var length = Math.Min(Math.Min(count, 2), throwAfterBytes - _position);
        Array.Copy(data, _position, buffer, offset, length);
        _position += length;
        return length;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var temporary = new byte[buffer.Length];
        var read = Read(temporary, 0, temporary.Length);
        temporary.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class DelayedChunkStream(byte[] data, int chunkSize, TimeSpan delay) : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, cancellationToken);
        if (_position >= data.Length) return 0;
        var length = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
        data.AsMemory(_position, length).CopyTo(buffer);
        _position += length;
        return length;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class StallAfterBytesStream(byte[] initialData) : Stream
{
    private bool _returnedInitialData;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => initialData.Length + 1;
    public override long Position { get; set; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_returnedInitialData)
        {
            _returnedInitialData = true;
            var length = Math.Min(initialData.Length, buffer.Length);
            initialData.AsMemory(0, length).CopyTo(buffer);
            Position += length;
            return length;
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
