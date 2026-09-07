using System.Text.Json;
using System.Text.Json.Serialization;

namespace KemonoDownloader.Core;

public enum DownloadMode { Author, SinglePost }
public enum ProxyType { None, Http, Https, Socks5 }
public enum LogLevel { Information, Success, Warning, Error }
public enum ScrapeEventKind { Log, Progress, Status, Incremental, AuthorResolved }
public enum DownloadTaskStatus { Idle, Running, Completed, Stopped, Failed }
public enum RepairScanMode { Corrupted, Missing }

public sealed record DownloadConfig
{
    [JsonPropertyName("domain")]
    public string Domain { get; init; } = "kemono.cr";

    [JsonPropertyName("mode")]
    [JsonConverter(typeof(JsonStringEnumConverter<DownloadMode>))]
    public DownloadMode Mode { get; init; } = DownloadMode.Author;

    [JsonPropertyName("service")]
    public string Service { get; init; } = "fanbox";

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("post_url")]
    public string PostUrl { get; init; } = string.Empty;

    [JsonPropertyName("save_path")]
    public string SavePath { get; init; } = string.Empty;

    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    [JsonPropertyName("concurrent")]
    public int Concurrent { get; init; } = 5;

    [JsonPropertyName("skip_existing")]
    public bool SkipExisting { get; init; } = true;

    [JsonPropertyName("use_thumbnail")]
    public bool UseThumbnail { get; init; }

    [JsonPropertyName("batch_delay_ms")]
    public int BatchDelayMs { get; init; } = 500;

    [JsonIgnore]
    public bool ForceFresh { get; init; }

    [JsonIgnore]
    public string AuthorDirectoryOverride { get; init; } = string.Empty;
}

public sealed record ProxySettings
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter<ProxyType>))]
    public ProxyType Type { get; init; }

    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; } = 1080;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonIgnore]
    public string Password { get; init; } = string.Empty;

    public Uri? ToUri() => Type switch
    {
        ProxyType.None => null,
        ProxyType.Http => new Uri($"http://{Host}:{Port}"),
        ProxyType.Https => new Uri($"https://{Host}:{Port}"),
        ProxyType.Socks5 => new Uri($"socks5://{Host}:{Port}"),
        _ => null
    };
}

public sealed record AppSettings
{
    public const int CurrentSettingsVersion = 2;
    public static readonly IReadOnlyList<string> DefaultDomains = ["kemono.cr", "kemono.su", "pawchive.pw"];

    [JsonPropertyName("settings_version")]
    public int SettingsVersion { get; init; } = CurrentSettingsVersion;

    [JsonPropertyName("domain")]
    public string Domain { get; init; } = "kemono.cr";

    [JsonPropertyName("domains")]
    public IReadOnlyList<string> Domains { get; init; } = DefaultDomains;

    [JsonPropertyName("retries")]
    public int Retries { get; init; } = 3;

    [JsonPropertyName("image_timeout_seconds")]
    public int ImageTimeoutSeconds { get; init; } = 60;

    [JsonPropertyName("video_timeout_seconds")]
    public int VideoTimeoutSeconds { get; init; } = 120;

    [JsonPropertyName("request_timeout_seconds")]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [JsonPropertyName("download_header_timeout_seconds")]
    public int DownloadHeaderTimeoutSeconds { get; init; } = 30;

    [JsonPropertyName("download_min_speed_kib_per_second")]
    public int DownloadMinimumSpeedKibPerSecond { get; init; } = 8;

    [JsonPropertyName("download_low_speed_window_seconds")]
    public int DownloadLowSpeedWindowSeconds { get; init; } = 300;

    [JsonPropertyName("download_max_total_hours")]
    public int DownloadMaxTotalHours { get; init; } = 24;

    [JsonPropertyName("page_request_delay_ms")]
    public int PageRequestDelayMs { get; init; } = 1500;

    [JsonPropertyName("default_concurrent")]
    public int DefaultConcurrent { get; init; } = 5;

    [JsonPropertyName("batch_delay_ms")]
    public int BatchDelayMs { get; init; } = 500;

    [JsonPropertyName("skip_existing_default")]
    public bool SkipExistingDefault { get; init; } = true;

    [JsonPropertyName("proxy")]
    public ProxySettings Proxy { get; init; } = new();

    [JsonPropertyName("workspace")]
    public WorkspaceSettings Workspace { get; init; } = new();
}

public sealed record WorkspaceSettings
{
    [JsonPropertyName("selected_tab")]
    public int SelectedTab { get; init; }
    [JsonPropertyName("author_service")]
    public string AuthorService { get; init; } = "fanbox";
    [JsonPropertyName("author_domain")]
    public string AuthorDomain { get; init; } = KemonoRules.DefaultDomain;
    [JsonPropertyName("author_id")]
    public string AuthorId { get; init; } = string.Empty;
    [JsonPropertyName("author_save_path")]
    public string AuthorSavePath { get; init; } = string.Empty;
    [JsonPropertyName("author_limit")]
    public int AuthorLimit { get; init; }
    [JsonPropertyName("author_concurrent")]
    public int AuthorConcurrent { get; init; } = 5;
    [JsonPropertyName("author_skip_existing")]
    public bool AuthorSkipExisting { get; init; } = true;
    [JsonPropertyName("author_use_thumbnail")]
    public bool AuthorUseThumbnail { get; init; }
    [JsonPropertyName("single_post_url")]
    public string SinglePostUrl { get; init; } = string.Empty;
    [JsonPropertyName("single_save_path")]
    public string SingleSavePath { get; init; } = string.Empty;
    [JsonPropertyName("single_concurrent")]
    public int SingleConcurrent { get; init; } = 5;
    [JsonPropertyName("single_skip_existing")]
    public bool SingleSkipExisting { get; init; } = true;
    [JsonPropertyName("single_use_thumbnail")]
    public bool SingleUseThumbnail { get; init; }
    [JsonPropertyName("resume_path")]
    public string ResumePath { get; init; } = string.Empty;
    [JsonPropertyName("repair_path")]
    public string RepairPath { get; init; } = string.Empty;
    [JsonPropertyName("repair_size_limit_kb")]
    public int RepairSizeLimitKb { get; init; } = 100;
    [JsonPropertyName("repair_concurrent")]
    public int RepairConcurrent { get; init; } = 5;
    [JsonPropertyName("repair_scan_mode")]
    public RepairScanMode RepairScanMode { get; init; } = RepairScanMode.Missing;
    [JsonPropertyName("repair_use_thumbnail")]
    public bool RepairUseThumbnail { get; init; }
    [JsonPropertyName("repair_batch_mode")]
    public bool RepairBatchMode { get; init; }
}

public sealed record KemonoAttachment
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

public sealed record KemonoPreview
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;
}

public sealed record KemonoPost
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; init; } = string.Empty;

    [JsonPropertyName("service")]
    public string Service { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("published")]
    public string Published { get; init; } = string.Empty;

    [JsonPropertyName("file")]
    public KemonoAttachment? File { get; init; }

    [JsonPropertyName("attachments")]
    public IReadOnlyList<KemonoAttachment> Attachments { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record KemonoProfile
{
    [JsonPropertyName("public_id")]
    public string? PublicId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record KemonoPostDetail
{
    [JsonPropertyName("previews")]
    public IReadOnlyList<KemonoPreview> Previews { get; init; } = [];
}

public sealed record ParsedPostUrl(string Service, string UserId, string PostId, Uri CanonicalUri);
public sealed record PostMetadata(string PostId, string PostTitle, string PostDate, string CleanTitle, string FolderName, string Key);

public sealed record ProgressRecord
{
    [JsonPropertyName("post_id")]
    public string PostId { get; init; } = string.Empty;

    [JsonPropertyName("post_title")]
    public string PostTitle { get; init; } = string.Empty;

    [JsonPropertyName("post_date")]
    public string PostDate { get; init; } = string.Empty;

    [JsonPropertyName("attachments_count")]
    public int AttachmentsCount { get; init; }

    [JsonPropertyName("downloaded_attachments")]
    public int DownloadedAttachments { get; init; }

    [JsonPropertyName("completed_time")]
    public DateTimeOffset CompletedTime { get; init; }
}

public sealed record ProgressDocument
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("config")]
    public DownloadConfig Config { get; set; } = new();

    [JsonPropertyName("records")]
    public Dictionary<string, ProgressRecord> Records { get; init; } = new(StringComparer.Ordinal);
}

public sealed record ApiResult<T>(int StatusCode, T? Value)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300 && Value is not null;
}

public sealed record ScrapeEvent(
    ScrapeEventKind Kind,
    string Message = "",
    LogLevel Level = LogLevel.Information,
    int Completed = 0,
    int Total = 0,
    int DownloadedFiles = 0,
    string CurrentItem = "",
    DownloadTaskStatus Status = DownloadTaskStatus.Idle,
    int PreviouslyCompleted = 0,
    string AuthorName = "")
{
    public static ScrapeEvent Log(string message, LogLevel level = LogLevel.Information) => new(ScrapeEventKind.Log, message, level);
    public static ScrapeEvent Progress(int completed, int total, int files, string current) => new(ScrapeEventKind.Progress, Completed: completed, Total: total, DownloadedFiles: files, CurrentItem: current);
    public static ScrapeEvent StatusChanged(DownloadTaskStatus status, string message = "") => new(ScrapeEventKind.Status, message, Status: status);
    public static ScrapeEvent Incremental(int completed) => new(ScrapeEventKind.Incremental, PreviouslyCompleted: completed);
    public static ScrapeEvent Author(string authorName) => new(ScrapeEventKind.AuthorResolved, AuthorName: authorName);
}

public sealed record ScrapeResult(DownloadTaskStatus Status, int CompletedPosts, int SkippedPosts, int DownloadedFiles, string? Error = null);
public sealed record DownloadResult(bool Success, bool AlreadyExists, long BytesWritten, int Attempts = 1);
public sealed record FileRetryEvent(string FileName, int FailedAttempt, int MaxAttempts, TimeSpan Delay, string Reason, int? StatusCode = null);
public sealed record CorruptedFile(string Name, string Path, long Size);
public sealed record RepairFile(
    string Name,
    string Path,
    long Size,
    bool IsMissing,
    string PostKey = "",
    string PostId = "",
    string PostTitle = "",
    string PostDate = "",
    string PostFolderName = "",
    string OriginalName = "",
    string AuthorName = "",
    string AuthorBasePath = "");
public sealed record RepairScanResult(string BasePath, IReadOnlyList<RepairFile> Files, int TotalCount, long TotalSize, int ScannedAuthorCount = 1);
public sealed record RepairResult(int MatchedFiles, int RepairedFiles, int FailedFiles, int ProcessedAuthors = 1);
