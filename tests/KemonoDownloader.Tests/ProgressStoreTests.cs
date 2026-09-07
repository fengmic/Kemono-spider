using System.Text.Json;
using KemonoDownloader.Core;
using KemonoDownloader.Infrastructure;

namespace KemonoDownloader.Tests;

public sealed class ProgressStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSchemaVersionTwo()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "download_progress.json");
        var store = new ProgressStore();
        var input = new ProgressDocument
        {
            Config = new DownloadConfig { Mode = DownloadMode.Author, Service = "fanbox", Username = "42", SavePath = directory.Path },
            Records = { ["key"] = new ProgressRecord { PostId = "1", AttachmentsCount = 2, DownloadedAttachments = 2, CompletedTime = DateTimeOffset.UtcNow } }
        };
        await store.SaveAsync(path, input);
        var output = await store.LoadAsync(path);
        Assert.Equal(ProgressDocument.CurrentSchemaVersion, output.SchemaVersion);
        Assert.Equal("42", output.Config.Username);
        Assert.True(output.Records.ContainsKey("key"));
    }

    [Fact]
    public async Task Load_RejectsLegacyElectronShape()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "download_progress.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { config = new { mode = "author" } }));
        var error = await Assert.ThrowsAsync<LegacyProgressFormatException>(() => new ProgressStore().LoadAsync(path));
        Assert.Contains("旧 Electron", error.Message);
    }
}
