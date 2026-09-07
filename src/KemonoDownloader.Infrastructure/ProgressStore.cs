using System.Text.Json;
using KemonoDownloader.Core;

namespace KemonoDownloader.Infrastructure;

public sealed class ProgressStore : IProgressStore
{
    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(path));

    public async Task<ProgressDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("schema_version", out var schemaElement))
            {
                throw new LegacyProgressFormatException("该进度文件来自旧 Electron 版本，WPF 版本不支持原地续传。请创建新任务。");
            }

            var schemaVersion = schemaElement.GetInt32();
            if (schemaVersion != ProgressDocument.CurrentSchemaVersion)
            {
                throw new InvalidProgressFormatException($"不支持的进度文件版本：{schemaVersion}。");
            }

            var result = document.RootElement.Deserialize<ProgressDocument>(JsonDefaults.Options);
            return result ?? throw new InvalidProgressFormatException("进度文件内容为空。");
        }
        catch (JsonException error)
        {
            throw new InvalidProgressFormatException("进度文件不是有效的 JSON。", error);
        }
    }

    public async Task SaveAsync(string path, ProgressDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonDefaults.Options, cancellationToken);
        }
        File.Move(temporaryPath, path, true);
    }
}
