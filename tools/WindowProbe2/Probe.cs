using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KemonoDownloader.Core;
using KemonoDownloader.Wpf.ViewModels;

internal static class WindowProbe2
{
    [STAThread]
    private static void Main()
    {
        var report = new List<string>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/KemonoDownloader;component/Themes/AnimeTheme.xaml")
        };
        theme.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/KemonoDownloader;component/Themes/Animations.xaml")
        });
        app.Resources.MergedDictionaries.Add(theme);
        report.Add("theme loaded with animations");

        try
        {
            var files = new List<RepairFile>
            {
                new("1.jpg", @"C:\out\abc\posts\2024-01-01\1.jpg", 0, true, "abc123", "9001", "示例作品甲", "2024-01-01", "2024-01-01", "original-a.jpg", "作者甲", @"C:\out\abc"),
                new("2.png", @"C:\out\abc\posts\2024-01-01\2.png", 0, true, "abc123", "9001", "示例作品甲", "2024-01-01", "2024-01-01", "original-b.png", "作者甲", @"C:\out\abc"),
                new("3.jpg", @"C:\out\abc\posts\2024-02-02\3.jpg", 0, true, "def456", "9002", "示例作品乙", "2024-02-02", "2024-02-02", "original-c.jpg", "作者甲", @"C:\out\abc")
            };
            var scan = new RepairScanResult(@"C:\out\abc", files, files.Count, 0);
            var vm = new MissingFilesViewModel(scan);
            var window = new KemonoDownloader.Wpf.MissingFilesWindow(scan);
            window.Show();
            window.UpdateLayout();
            Pump(300);
            report.Add($"MissingFilesWindow shown; groups={vm.Groups.Count}");

            var expanders = new List<Expander>();
            Collect(window, expanders);
            report.Add($"expanders found={expanders.Count} (expect 2 groups)");

            if (expanders.Count > 0)
            {
                var first = expanders[0];
                var contentBorder = (Border)first.Template.FindName("ContentBorder", first);
                report.Add($"before expand: MaxHeight={contentBorder.MaxHeight:F0} opacity={contentBorder.Opacity:F2} visible={contentBorder.IsVisible}");
                first.IsExpanded = true;
                Pump(500);
                report.Add($"after expand: MaxHeight={contentBorder.MaxHeight:F0} opacity={contentBorder.Opacity:F2} height={contentBorder.ActualHeight:F1}");

                var rows = 0;
                Collect<TextBlock>(first, _ => rows++);
                report.Add($"header/body text blocks rendered under expander={rows}");

                first.IsExpanded = false;
                Pump(500);
                Pump(600);
                report.Add($"after collapse: MaxHeight={contentBorder.MaxHeight:F0} opacity={contentBorder.Opacity:F2} height={contentBorder.ActualHeight:F1} expanderHeight={first.ActualHeight:F1} contentDesired={contentBorder.DesiredSize.Height:F1}");
            }

            window.Close();
        }
        catch (Exception error)
        {
            report.Add("EXCEPTION: " + error);
        }

        foreach (var line in report) Console.WriteLine(line);
    }

    private static void Collect<T>(DependencyObject root, List<T> sink) where T : DependencyObject => Collect<T>(root, sink.Add);

    private static void Collect<T>(DependencyObject root, Action<T> visit) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) visit(match);
            Collect(child, visit);
        }
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
