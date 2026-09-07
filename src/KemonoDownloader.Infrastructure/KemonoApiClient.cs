using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class KemonoApiClient(INetworkClientProvider clientProvider, ISettingsStore settingsStore) : IKemonoApiClient
{
    public event Action? RateLimited;
    public event Action? RequestSucceeded;

    public async Task<bool> VisitHomepageAsync(string domain, string userId, string service, int offset, CancellationToken cancellationToken)
    {
        var suffix = offset == 0 ? string.Empty : $"?o={offset}";
        var response = await SendAsync<object>($"{KemonoRules.GetBaseUrl(domain)}/{service}/user/{userId}{suffix}", null, deserialize: false, cancellationToken);
        return response.StatusCode == (int)HttpStatusCode.OK;
    }

    public Task<ApiResult<KemonoProfile>> GetProfileAsync(string domain, string userId, string service, CancellationToken cancellationToken) =>
        SendAsync<KemonoProfile>($"{KemonoRules.GetBaseUrl(domain)}/api/v1/{service}/user/{userId}/profile", $"{KemonoRules.GetBaseUrl(domain)}/{service}/user/{userId}", true, cancellationToken);

    public Task<ApiResult<IReadOnlyList<KemonoPost>>> GetPostsPageAsync(string domain, string userId, string service, int offset, CancellationToken cancellationToken)
    {
        var suffix = offset == 0 ? string.Empty : $"?o={offset}";
        return SendAsync<IReadOnlyList<KemonoPost>>($"{KemonoRules.GetBaseUrl(domain)}/api/v1/{service}/user/{userId}/posts{suffix}", $"{KemonoRules.GetBaseUrl(domain)}/{service}/user/{userId}{suffix}", true, cancellationToken);
    }

    public Task<ApiResult<KemonoPostDetail>> GetPostDetailAsync(string domain, string service, string userId, string postId, CancellationToken cancellationToken) =>
        SendAsync<KemonoPostDetail>($"{KemonoRules.GetBaseUrl(domain)}/api/v1/{service}/user/{userId}/post/{postId}", $"{KemonoRules.GetBaseUrl(domain)}/{service}/user/{userId}/post/{postId}", true, cancellationToken);

    private async Task<ApiResult<T>> SendAsync<T>(string url, string? referer, bool deserialize, CancellationToken cancellationToken)
    {
        var retries = settingsStore.Current.Retries;
        for (var attempt = 1; attempt <= retries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (!string.IsNullOrEmpty(referer)) request.Headers.Referrer = new Uri(referer);
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(settingsStore.Current.RequestTimeoutSeconds));
                using var response = await clientProvider.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
                var statusCode = (int)response.StatusCode;
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    RateLimited?.Invoke();
                    if (attempt < retries) await Task.Delay(GetBackoff(attempt + 1), cancellationToken);
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    RequestSucceeded?.Invoke();
                    if (!deserialize) return new ApiResult<T>(statusCode, default);
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    var result = await JsonSerializer.DeserializeAsync<T>(stream, JsonDefaults.Options, cancellationToken);
                    return new ApiResult<T>(statusCode, result);
                }
                return new ApiResult<T>(statusCode, default);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < retries)
            {
                await Task.Delay(GetBackoff(attempt), cancellationToken);
            }
            catch (HttpRequestException) when (attempt < retries)
            {
                await Task.Delay(GetBackoff(attempt), cancellationToken);
            }
        }
        return new ApiResult<T>(0, default);
    }

    internal static TimeSpan GetBackoff(int attempt, double randomFactor = -1)
    {
        if (randomFactor < 0) randomFactor = 0.5 + Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt - 1) * randomFactor);
    }
}
