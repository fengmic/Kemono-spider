using System.Windows;
using KemonoDownloader.Core;

namespace KemonoDownloader.Wpf.Services;

public interface IUserDialogService
{
    bool Confirm(string message, string title);
    void ShowMissingFiles(RepairScanResult scan);
}

public sealed class UserDialogService : IUserDialogService
{
    public bool Confirm(string message, string title) =>
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowMissingFiles(RepairScanResult scan)
    {
        var window = new MissingFilesWindow(scan)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        window.ShowDialog();
    }
}
