using System.Net;
using System.Net.Http.Headers;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class NetworkClientProvider(ISettingsStore settingsStore) : INetworkClientProvider
{
    private HttpClient? _client;

    public HttpClient Client => _client ??= CreateClient();

    public void Rebuild()
    {
        var oldClient = _client;
        _client = CreateClient();
        oldClient?.Dispose();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }

    private HttpClient CreateClient()
    {
        var proxy = settingsStore.Current.Proxy;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        if (proxy.Type is ProxyType.Http or ProxyType.Https)
        {
            handler.Proxy = new WebProxy(proxy.ToUri()!)
            {
                Credentials = string.IsNullOrEmpty(proxy.Username) ? null : new NetworkCredential(proxy.Username, proxy.Password)
            };
            handler.UseProxy = true;
        }
        else if (proxy.Type == ProxyType.Socks5)
        {
            var socks = new Socks5Proxy(proxy);
            handler.UseProxy = false;
            handler.ConnectCallback = socks.ConnectAsync;
        }

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh-CN"));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("zh", 0.9));
        client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.8));
        return client;
    }
}
