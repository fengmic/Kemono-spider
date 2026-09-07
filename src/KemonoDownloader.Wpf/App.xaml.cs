using System.Windows;
using System.IO;
using KemonoDownloader.Infrastructure;
using KemonoDownloader.Wpf.Services;
using KemonoDownloader.Wpf.ViewModels;

namespace KemonoDownloader.Wpf;

public partial class App : System.Windows.Application
{
    private NetworkClientProvider? _networkClientProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var settingsStore = new SettingsStore();
            await settingsStore.InitializeAsync();
            _networkClientProvider = new NetworkClientProvider(settingsStore);
            var progressStore = new ProgressStore();
            var fileDownloader = new FileDownloader(_networkClientProvider, settingsStore);
            var apiClient = new KemonoApiClient(_networkClientProvider, settingsStore);
            var scraperService = new ScraperService(apiClient, fileDownloader, progressStore, settingsStore);
            var repairService = new RepairService(progressStore, fileDownloader, apiClient, settingsStore);
            var viewModel = new MainViewModel(scraperService, repairService, progressStore, settingsStore, _networkClientProvider, new FolderPickerService());
            var window = new MainWindow(viewModel, new WindowStateStore());
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "KemonoDownloader-startup-error.log");
            try { File.WriteAllText(logPath, error.ToString()); } catch (IOException) { }
            System.Windows.MessageBox.Show($"应用启动失败：{error.Message}\n\n详细信息：{logPath}", "Kemono 下载器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _networkClientProvider?.Dispose();
        base.OnExit(e);
    }
}
