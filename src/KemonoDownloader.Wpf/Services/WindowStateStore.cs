using System.Text.Json;
using System.IO;
using System.Windows;
using FormsScreen = System.Windows.Forms.Screen;

namespace KemonoDownloader.Wpf.Services;

public sealed class WindowStateStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KemonoDownloader", "window-state.json");

    public void Restore(Window window)
    {
        try
        {
            if (!File.Exists(_path)) return;
            var state = JsonSerializer.Deserialize<SavedWindowState>(File.ReadAllText(_path));
            if (state is null) return;

            // Screen.WorkingArea 是物理像素，窗口尺寸是 DIP；按当前显示器缩放比换算后再比较。
            var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var screen = FindScreen(state.Left * scale, state.Top * scale) ?? FormsScreen.PrimaryScreen;
            if (screen is null) return;

            var workLeft = screen.WorkingArea.Left / scale;
            var workTop = screen.WorkingArea.Top / scale;
            var workWidth = screen.WorkingArea.Width / scale;
            var workHeight = screen.WorkingArea.Height / scale;

            // 工作区比最小尺寸还小时以工作区为准：装得进屏幕优先于可读。
            window.MinWidth = Math.Min(window.MinWidth, workWidth);
            window.MinHeight = Math.Min(window.MinHeight, workHeight);
            var width = Math.Clamp(state.Width, window.MinWidth, workWidth);
            var height = Math.Clamp(state.Height, window.MinHeight, workHeight);

            // 尺寸变化会触发一次布局，把 Left/Top 回写成改动前的值，因此先让布局落定再定位。
            window.Width = width;
            window.Height = height;
            window.UpdateLayout();
            window.Left = Math.Clamp(state.Left, workLeft, workLeft + workWidth - width);
            window.Top = Math.Clamp(state.Top, workTop, workTop + workHeight - height);

            if (state.IsMaximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private static FormsScreen? FindScreen(double left, double top)
    {
        foreach (var screen in FormsScreen.AllScreens)
        {
            var area = screen.WorkingArea;
            if (left >= area.Left && left < area.Right && top >= area.Top && top < area.Bottom) return screen;
        }
        return null;
    }

    public void Save(Window window)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bounds = window.RestoreBounds;
            var state = new SavedWindowState(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
            File.WriteAllText(_path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record SavedWindowState(double Left, double Top, double Width, double Height, bool IsMaximized);
}
