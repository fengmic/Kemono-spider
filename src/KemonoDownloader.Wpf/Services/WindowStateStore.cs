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
            if (state is null || state.Width < window.MinWidth || state.Height < window.MinHeight) return;
            var bounds = new System.Drawing.Rectangle((int)state.Left, (int)state.Top, (int)state.Width, (int)state.Height);
            if (!FormsScreen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))) return;
            window.Left = state.Left;
            window.Top = state.Top;
            window.Width = state.Width;
            window.Height = state.Height;
            if (state.IsMaximized) window.WindowState = WindowState.Maximized;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
        }
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
