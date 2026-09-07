using System.Text.RegularExpressions;

namespace KemonoDownloader.Core;

public static partial class KemonoRules
{
    public const string DefaultDomain = "kemono.cr";
    public const int PageSize = 50;
    public const int MaxConsecutivePageErrors = 3;
    public const int PostsPerJsonFile = 50;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".bmp", ".tiff", ".svg"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".avi", ".mov", ".mkv", ".flv", ".wmv", ".m4v"
    };

    [GeneratedRegex("[<>:\"/\\\\|?*\\x00-\\x1F]", RegexOptions.CultureInvariant)]
    private static partial Regex IllegalFilenameCharacters();

    public static ParsedPostUrl ParsePostUrl(string input, IEnumerable<string>? allowedDomains = null, string? canonicalDomain = null)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !IsAllowedDomain(uri.Host, allowedDomains ?? AppSettings.DefaultDomains))
        {
            throw new ArgumentException("请输入有效的 Kemono 作品链接。", nameof(input));
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 || !string.Equals(segments[1], "user", StringComparison.OrdinalIgnoreCase) || !string.Equals(segments[3], "post", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("作品链接格式必须为 /<service>/user/<userId>/post/<postId>。", nameof(input));
        }

        var canonical = new Uri($"{GetBaseUrl(canonicalDomain ?? NormalizeDomain(uri.Host))}/{segments[0]}/user/{segments[2]}/post/{segments[4]}");
        return new ParsedPostUrl(segments[0], segments[2], segments[4], canonical);
    }

    public static string SanitizeFilename(string? value)
    {
        var sanitized = IllegalFilenameCharacters().Replace(value ?? string.Empty, "_").Trim().TrimEnd('.');
        return sanitized.Length > 150 ? sanitized[..150].TrimEnd() : sanitized;
    }

    public static PostMetadata GetPostMetadata(KemonoPost post)
    {
        var postId = string.IsNullOrWhiteSpace(post.Id) ? "unknown" : post.Id;
        var postTitle = string.IsNullOrWhiteSpace(post.Title) ? $"post_{postId}" : post.Title.Trim();
        var postDate = string.IsNullOrWhiteSpace(post.Published) ? "unknown" : post.Published.Split('T', 2)[0];
        var cleanTitle = SanitizeFilename(postTitle);
        if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = $"post_{postId}";
        return new PostMetadata(postId, postTitle, postDate, cleanTitle, $"{postDate} {cleanTitle}", $"{postDate}_{cleanTitle}_{postId}");
    }

    public static IReadOnlyList<(KemonoAttachment Attachment, int Index)> GetDownloadTasks(KemonoPost post)
    {
        var result = new List<(KemonoAttachment, int)>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (IsValid(post.File) && names.Add(post.File!.Name)) result.Add((post.File, 0));

        foreach (var attachment in post.Attachments.Where(IsValid))
        {
            if (names.Add(attachment.Name)) result.Add((attachment, result.Count));
        }

        return result;
    }

    public static string GetSequentialFilename(KemonoAttachment attachment, int index) => $"{index + 1}{Path.GetExtension(attachment.Name)}";

    public static TimeSpan GetDownloadTimeout(string filename, AppSettings settings)
    {
        var extension = Path.GetExtension(filename);
        if (ImageExtensions.Contains(extension)) return TimeSpan.FromSeconds(settings.ImageTimeoutSeconds);
        if (VideoExtensions.Contains(extension)) return TimeSpan.FromSeconds(settings.VideoTimeoutSeconds);
        return TimeSpan.FromSeconds(settings.ImageTimeoutSeconds);
    }

    public static bool IsImageFile(string filename) => ImageExtensions.Contains(Path.GetExtension(filename));
    public static bool IsVideoFile(string filename) => VideoExtensions.Contains(Path.GetExtension(filename));

    public static Uri BuildDownloadUri(KemonoAttachment attachment, string domain = DefaultDomain)
    {
        var raw = attachment.Path.StartsWith('/') ? $"{GetFileBaseUrl(domain)}{attachment.Path}" : attachment.Path;
        var builder = new UriBuilder(raw);
        if (!builder.Query.Contains("f=", StringComparison.Ordinal))
        {
            var query = builder.Query.TrimStart('?');
            var encodedName = Uri.EscapeDataString(attachment.Name);
            builder.Query = string.IsNullOrEmpty(query) ? $"f={encodedName}" : $"{query}&f={encodedName}";
        }
        return builder.Uri;
    }

    public static Uri BuildThumbnailUri(KemonoAttachment attachment, string domain = DefaultDomain)
    {
        var normalizedPath = attachment.Path.StartsWith('/') ? attachment.Path : $"/{attachment.Path}";
        return new Uri($"{GetThumbnailBaseUrl(domain)}/thumbnail/data{normalizedPath}");
    }

    public static void Validate(DownloadConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.SavePath)) throw new ArgumentException("请选择保存路径。", nameof(config));
        if (!IsValidDomain(config.Domain)) throw new ArgumentException("服务器域名无效。", nameof(config));
        if (config.Concurrent is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(config), "并发数必须在 1 到 20 之间。");
        if (config.Limit < 0) throw new ArgumentOutOfRangeException(nameof(config), "限制数量不能小于 0。");
        if (config.BatchDelayMs is < 0 or > 5000) throw new ArgumentOutOfRangeException(nameof(config), "作品入队间隔必须在 0 到 5000 毫秒之间。");
        if (config.Mode == DownloadMode.Author && (string.IsNullOrWhiteSpace(config.Service) || string.IsNullOrWhiteSpace(config.Username))) throw new ArgumentException("作者模式需要服务平台和作者 ID。", nameof(config));
        if (config.Mode == DownloadMode.SinglePost) _ = ParsePostUrl(config.PostUrl, AppSettings.DefaultDomains.Append(config.Domain), config.Domain);
    }

    public static void Validate(AppSettings settings)
    {
        var domains = NormalizeDomains(settings.Domains);
        if (!domains.Contains(NormalizeDomain(settings.Domain), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("当前服务器域名不在可选列表中。", nameof(settings));
        if (settings.Retries is < 1 or > 20 ||
            settings.ImageTimeoutSeconds is < 10 or > 3600 ||
            settings.VideoTimeoutSeconds is < 30 or > 7200 ||
            settings.DownloadHeaderTimeoutSeconds is < 5 or > 300 ||
            settings.DownloadMinimumSpeedKibPerSecond is < 1 or > 1024 ||
            settings.DownloadLowSpeedWindowSeconds is < 30 or > 3600 ||
            settings.DownloadMaxTotalHours is < 1 or > 24 ||
            settings.PageRequestDelayMs is < 500 or > 5000 ||
            settings.DefaultConcurrent is < 1 or > 20 ||
            settings.BatchDelayMs is < 0 or > 5000)
        {
            throw new ArgumentException("设置参数超出允许范围。", nameof(settings));
        }

        if (settings.Proxy.Type != ProxyType.None && (string.IsNullOrWhiteSpace(settings.Proxy.Host) || settings.Proxy.Port is < 1 or > 65535))
        {
            throw new ArgumentException("代理主机或端口无效。", nameof(settings));
        }
    }

    public static string NormalizeDomain(string? domain)
    {
        var value = (domain ?? string.Empty).Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) value = value[8..];
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) value = value[7..];
        value = value.Trim().TrimEnd('/');
        if (value.Contains('/') || value.Contains(':') || value.Contains('?') || value.Contains('#') || value.Contains(' ')) return string.Empty;
        return value.Equals("www.kemono.cr", StringComparison.OrdinalIgnoreCase) || value.Equals("www.kemono.su", StringComparison.OrdinalIgnoreCase)
            ? value[4..]
            : value;
    }

    public static string GetBaseUrl(string domain) => $"https://{NormalizeDomain(domain)}";

    public static string GetFileBaseUrl(string domain) => NormalizeDomain(domain).Equals("pawchive.pw", StringComparison.OrdinalIgnoreCase)
        ? "https://file.pawchive.pw/data"
        : GetBaseUrl(domain);

    public static string GetThumbnailBaseUrl(string domain) => NormalizeDomain(domain).Equals("pawchive.pw", StringComparison.OrdinalIgnoreCase)
        ? "https://img.pawchive.pw"
        : GetBaseUrl(domain);

    public static string ResolveTaskDomain(string inputDomain, string selectedDomain)
    {
        var input = NormalizeDomain(inputDomain);
        return input.Equals("pawchive.pw", StringComparison.OrdinalIgnoreCase) ? input : NormalizeDomain(selectedDomain);
    }

    public static IReadOnlyList<string> NormalizeDomains(IEnumerable<string>? domains)
    {
        var values = (domains ?? []).Select(NormalizeDomain).Where(IsValidDomain).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var defaultDomain in AppSettings.DefaultDomains.Reverse())
        {
            if (!values.Contains(defaultDomain, StringComparer.OrdinalIgnoreCase)) values.Insert(0, defaultDomain);
        }
        return values;
    }

    public static bool IsValidDomain(string? domain)
    {
        var normalized = NormalizeDomain(domain);
        return normalized.Length > 3 && normalized.Contains('.') && Uri.CheckHostName(normalized) == UriHostNameType.Dns;
    }

    private static bool IsAllowedDomain(string host, IEnumerable<string> allowedDomains) =>
        NormalizeDomains(allowedDomains).Contains(NormalizeDomain(host), StringComparer.OrdinalIgnoreCase);

    private static bool IsValid(KemonoAttachment? attachment) => attachment is not null && !string.IsNullOrWhiteSpace(attachment.Name) && !string.IsNullOrWhiteSpace(attachment.Path);
}
