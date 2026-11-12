using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Path = System.IO.Path;

internal static class UiProbe
{
    [STAThread]
    private static void Main()
    {
        var report = new List<string>();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var motion = Environment.GetEnvironmentVariable("MOTION") ?? "Animations";
        var theme = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/KemonoDownloader;component/Themes/AnimeTheme.xaml")
        };
        theme.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/KemonoDownloader;component/Themes/{motion}.xaml")
        });
        app.Resources.MergedDictionaries.Add(theme);
        report.Add($"motion={motion}");

        try
        {
            var progress = new ProgressBar { Width = 300, Height = 6, IsIndeterminate = true };
            var nav = new ListBox { Width = 180 };
            for (var i = 0; i < 3; i++) nav.Items.Add(new ListBoxItem { Content = "item" + i, Style = (Style)app.Resources["NavigationItemStyle"] });
            nav.SelectedIndex = 0;

            var panel = new StackPanel();
            panel.Children.Add(progress);
            panel.Children.Add(nav);
            var window = new Window { Width = 460, Height = 400, Content = panel };
            window.Show();
            window.UpdateLayout();
            Pump(300);

            var sweep = (Border)progress.Template.FindName("GlowSweep", progress);
            var shift = (TranslateTransform)progress.Template.FindName("GlowSweepShift", progress);
            report.Add($"indeterminate: visible={sweep.Visibility}");
            var a = shift.X;
            Pump(350);
            var b = shift.X;
            report.Add($"glow moving={Math.Abs(a - b) > 1} ({a:F0} -> {b:F0})");

            progress.IsIndeterminate = false;
            Pump(250);
            var c = shift.X;
            Pump(350);
            var d = shift.X;
            report.Add($"after determinate: visible={sweep.Visibility} frozen={Math.Abs(c - d) < 0.001} ({c:F0} -> {d:F0})");

            // 导航项切换：色条 0→3，卡片 1.02
            var first = (ListBoxItem)nav.Items[0];
            var bar = (FrameworkElement)first.Template.FindName("SelectionBar", first);
            var scale = (ScaleTransform)first.Template.FindName("NavScale", first);
            report.Add($"selected nav: barWidth={bar.Width:F1} scale={scale.ScaleX:F2}");
            nav.SelectedIndex = 1;
            Pump(450);
            var second = (ListBoxItem)nav.Items[1];
            var bar2 = (FrameworkElement)second.Template.FindName("SelectionBar", second);
            report.Add($"new selected: barWidth={bar2.Width:F1} old selected barWidth={bar.Width:F1}");

            window.Close();
        }
        catch (Exception error)
        {
            report.Add("EXCEPTION: " + error);
        }

        foreach (var line in report) Console.WriteLine(line);
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
