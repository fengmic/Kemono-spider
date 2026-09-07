using KemonoDownloader.Core;
using System.IO;

namespace KemonoDownloader.Wpf.ViewModels;

public sealed class MissingFilesViewModel
{
    public MissingFilesViewModel(RepairScanResult scan)
    {
        Groups = scan.Files
            .GroupBy(file => $"{file.AuthorBasePath}|{(string.IsNullOrWhiteSpace(file.PostKey) ? file.PostFolderName : file.PostKey)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new MissingPostGroupViewModel(group.ToArray()))
            .OrderByDescending(group => group.PostDate, StringComparer.Ordinal)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Summary = $"共 {Groups.Count} 个作品，缺失 {scan.Files.Count} 个文件";
        HasMissingFiles = scan.Files.Count > 0;
    }

    public IReadOnlyList<MissingPostGroupViewModel> Groups { get; }
    public string Summary { get; }
    public bool HasMissingFiles { get; }
}

public sealed class MissingPostGroupViewModel
{
    public MissingPostGroupViewModel(IReadOnlyList<RepairFile> files)
    {
        var first = files[0];
        Title = string.IsNullOrWhiteSpace(first.PostTitle) ? "未命名作品" : first.PostTitle;
        PostDate = first.PostDate;
        Subtitle = BuildSubtitle(first);
        Files = files
            .OrderBy(file => GetSequence(file.Name))
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file => new MissingFileViewModel(file))
            .ToArray();
    }

    public string Title { get; }
    public string PostDate { get; }
    public string Subtitle { get; }
    public IReadOnlyList<MissingFileViewModel> Files { get; }
    public string CountText => $"缺失 {Files.Count} 个";

    private static string BuildSubtitle(RepairFile file)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(file.AuthorName)) parts.Add(file.AuthorName);
        if (!string.IsNullOrWhiteSpace(file.PostDate)) parts.Add(file.PostDate);
        if (!string.IsNullOrWhiteSpace(file.PostId)) parts.Add($"作品 ID {file.PostId}");
        return string.Join(" · ", parts);
    }

    private static int GetSequence(string filename) =>
        int.TryParse(Path.GetFileNameWithoutExtension(filename), out var sequence) ? sequence : int.MaxValue;
}

public sealed class MissingFileViewModel
{
    public MissingFileViewModel(RepairFile file)
    {
        Name = file.Name;
        OriginalName = string.IsNullOrWhiteSpace(file.OriginalName) ? "未提供原始文件名" : file.OriginalName;
        Status = file.IsMissing ? "本地文件不存在" : "本地文件为零字节";
        Path = file.Path;
    }

    public string Name { get; }
    public string OriginalName { get; }
    public string Status { get; }
    public string Path { get; }
}
