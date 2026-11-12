using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

internal static class Probe
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/MotionProbe;component/Themes/AnimeTheme.xaml")
        });
        Console.WriteLine("dictionaries loaded: " + app.Resources.MergedDictionaries.Count);
        Console.WriteLine("ExpanderStyle: " + app.Resources.Contains("ExpanderStyle"));
        Console.WriteLine("PressDownStoryboard: " + app.Resources.Contains("PressDownStoryboard"));

        var panel = new StackPanel();
        var button = new Button { Style = (Style)app.Resources["PrimaryButtonStyle"], Content = "x" };
        var nav = new ListBoxItem { Style = (Style)app.Resources["NavigationItemStyle"], Content = "nav" };
        var expander = new Expander { Style = (Style)app.Resources["ExpanderStyle"], Header = "h", Content = new TextBlock { Text = "content" } };
        var progress = new ProgressBar { Style = (Style)app.Resources[typeof(ProgressBar)], IsIndeterminate = true, Width = 100 };
        panel.Children.Add(button);
        panel.Children.Add(nav);
        panel.Children.Add(expander);
        panel.Children.Add(progress);

        var window = new Window { Content = panel, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();
        Pump(60);

        var scale = (ScaleTransform)button.Template.FindName("ButtonScale", button);
        var lift = (TranslateTransform)button.Template.FindName("ButtonLift", button);
        Console.WriteLine($"idle scale={scale.ScaleX:F3} lift={lift.Y:F3}");

        Run(app, button, "PressDownStoryboard");
        Pump(120);
        Console.WriteLine($"after PressDown scale={scale.ScaleX:F3} (expect 0.96)");

        Run(app, button, "PressUpStoryboard");
        Pump(300);
        Console.WriteLine($"after PressUp scale={scale.ScaleX:F3} (expect 1.0)");

        Run(app, button, "HoverLiftStoryboard");
        Pump(300);
        Console.WriteLine($"after HoverLift lift={lift.Y:F3} (expect -1.0)");
        Run(app, button, "HoverRestoreStoryboard");
        Pump(300);
        Console.WriteLine($"after HoverRestore lift={lift.Y:F3} (expect 0.0)");

        // 导航项选中动画：走 ListBoxItem 自身模板，用 SelectionBar 宽度 0→3
        var selectionBar = (FrameworkElement)nav.Template.FindName("SelectionBar", nav);
        Console.WriteLine($"nav SelectionBar width before={selectionBar.Width:F2}");
        nav.IsSelected = true;
        Pump(400);
        Console.WriteLine($"nav SelectionBar width after select={selectionBar.Width:F2} (expect 3)");
        var navScale = (ScaleTransform)nav.Template.FindName("NavScale", nav);
        Console.WriteLine($"nav scale={navScale.ScaleX:F3} (expect 1.02)");
        nav.IsSelected = false;
        Pump(400);
        Console.WriteLine($"nav SelectionBar width after deselect={selectionBar.Width:F2} (expect 0)");

        // Expander：MaxHeight 0 → 6000
        var contentBorder = (Border)expander.Template.FindName("ContentBorder", expander);
        Console.WriteLine($"expander MaxHeight before={contentBorder.MaxHeight:F1} opacity={contentBorder.Opacity:F2}");
        expander.IsExpanded = true;
        Pump(500);
        Console.WriteLine($"expander MaxHeight after expand={contentBorder.MaxHeight:F1} opacity={contentBorder.Opacity:F2}");
        Console.WriteLine($"expander content visible height={(contentBorder.ActualHeight):F1}");
        expander.IsExpanded = false;
        Pump(500);
        Console.WriteLine($"expander MaxHeight after collapse={contentBorder.MaxHeight:F1} opacity={contentBorder.Opacity:F2}");

        // ProgressBar 流光
        var sweep = (Border)progress.Template.FindName("GlowSweep", progress);
        var shift = (TranslateTransform)progress.Template.FindName("GlowSweepShift", progress);
        Console.WriteLine($"glow vis={sweep.Visibility} x={shift.X:F1}");
        Pump(400);
        Console.WriteLine($"glow x after pump={shift.X:F1} (expect moved)");
        progress.IsIndeterminate = false;
        Pump(200);
        Console.WriteLine($"glow vis after determinate={sweep.Visibility} x={shift.X:F1}");

        // 共享 Storyboard 复用：第二个按钮用同一个资源实例
        var button2 = new Button { Style = (Style)app.Resources["PrimaryButtonStyle"], Content = "y" };
        panel.Children.Add(button2);
        window.UpdateLayout();
        var scale2 = (ScaleTransform)button2.Template.FindName("ButtonScale", button2);
        Run(app, button2, "PressDownStoryboard");
        Pump(150);
        Console.WriteLine($"button2 scale after shared storyboard={scale2.ScaleX:F3} (expect 0.96)");
        Console.WriteLine($"button1 scale unaffected={scale.ScaleX:F3} (expect 1.0)");

        window.Close();
        Console.WriteLine("PROBE-OK");
    }

    private static void Run(Application app, FrameworkElement owner, string key)
    {
        var storyboard = (Storyboard)app.Resources[key];
        storyboard.Begin(owner, owner is Control control ? control.Template : null, true);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
