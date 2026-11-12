using System;
using System.Windows;
using System.IO;
using System.Windows.Markup;
using KemonoDownloader.Infrastructure;
using KemonoDownloader.Wpf.Services;
using KemonoDownloader.Wpf.ViewModels;

namespace KemonoDownloader.Wpf;

public partial class App : System.Windows.Application
{
    private NetworkClientProvider? _networkClientProvider;

    /// <summary>
    /// 模板里的 Storyboard 用 DynamicResource 引用，在控件应用模板时才解析；
    /// 因此把动效字典挂进主题字典自己的 MergedDictionaries，运行期换整份字典也能生效。
    /// 系统关闭「动画效果」时用空的 Animations.Reduced.xaml 顶替，键齐全、数值不动。
    /// </summary>
    private void ApplyTheme()
    {
        var motion = Helpers.Motion.AnimationsEnabled ? "Animations" : "Animations.Reduced";
        var theme = new ResourceDictionary { Source = new Uri("Themes/AnimeTheme.xaml", UriKind.Relative) };
        theme.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"Themes/{motion}.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(theme);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        ApplyTheme();
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
