using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

internal static class ThemeProbe
{
    [STAThread]
    private static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var motionFile = Environment.GetEnvironmentVariable("MOTION") ?? "Animations";

        var theme = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/KemonoDownloader;component/Themes/AnimeTheme.xaml")
        };
        theme.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/KemonoDownloader;component/Themes/{motionFile}.xaml")
        });
        app.Resources.MergedDictionaries.Add(theme);
        Console.WriteLine($"{motionFile}: theme parsed ok, merged={theme.MergedDictionaries.Count}");

        try
        {
            var button = new Button
            {
                Style = (Style)app.Resources["PrimaryButtonStyle"],
                Content = "x", Width = 160, Height = 44
            };
            var window = new Window { Content = button, Width = 320, Height = 160, Topmost = true };
            window.Show();
            window.Activate();
            window.UpdateLayout();
            Pump(300);

            var scale = (ScaleTransform)button.Template.FindName("ButtonScale", button)!;
            var lift = (TranslateTransform)button.Template.FindName("ButtonLift", button)!;
            Console.WriteLine($"{motionFile}: idle scale={scale.ScaleX:F2} lift={lift.Y:F2}");

            var center = button.PointToScreen(new Point(80, 22));
            SetCursorPos((int)center.X, (int)center.Y);
            Pump(80);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Pump(350);
            Console.WriteLine($"{motionFile}: hover+pressed scale={scale.ScaleX:F2} lift={lift.Y:F2}");
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Pump(500);
            Console.WriteLine($"{motionFile}: released scale={scale.ScaleX:F2} lift={lift.Y:F2}");
            window.Close();
        }
        catch (Exception error)
        {
            Console.WriteLine($"{motionFile}: FAILED {error.GetType().Name}: {error.Message}");
        }
        Console.WriteLine("THEME-PROBE-DONE");
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
