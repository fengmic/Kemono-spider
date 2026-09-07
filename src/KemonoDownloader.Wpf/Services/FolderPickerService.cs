using System.IO;

namespace KemonoDownloader.Wpf.Services;

public interface IFolderPickerService
{
    string? PickFolder(string description, string? initialDirectory = null);
}

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string description, string? initialDirectory = null)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty
        };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }
}
