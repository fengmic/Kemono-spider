using System.Windows;
using KemonoDownloader.Core;
using KemonoDownloader.Wpf.ViewModels;

namespace KemonoDownloader.Wpf;

public partial class MissingFilesWindow : Window
{
    public MissingFilesWindow(RepairScanResult scan)
    {
        InitializeComponent();
        DataContext = new MissingFilesViewModel(scan);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
