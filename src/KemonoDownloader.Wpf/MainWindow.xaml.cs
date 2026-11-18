using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using KemonoDownloader.Wpf.Services;
using KemonoDownloader.Wpf.ViewModels;

namespace KemonoDownloader.Wpf;

public partial class MainWindow : Window
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly WindowStateStore _windowStateStore;
    private readonly MainViewModel _viewModel;
    private bool _shutdownCompleted;
    private bool _logScrollPending;

    public MainWindow(MainViewModel viewModel, WindowStateStore windowStateStore)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _windowStateStore = windowStateStore;
        StateChanged += (_, _) => UpdateMaximizeButton();
        viewModel.Logs.CollectionChanged += ScrollLogsToEnd;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeButton()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        FitToWorkArea();
        // HWND 已存在但尚未渲染，此时应用上次的尺寸与位置不会闪。
        _windowStateStore.Restore(this);
        source?.AddHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            ApplyMonitorWorkArea(windowHandle, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ApplyMonitorWorkArea(IntPtr windowHandle, IntPtr minMaxInfoPointer)
    {
        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var workWidth = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var workHeight = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        var scale = GetDpiForWindow(windowHandle) / 96.0;
        if (scale <= 0) scale = 1.0;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(minMaxInfoPointer);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = workWidth;
        minMaxInfo.MaxSize.Y = workHeight;
        minMaxInfo.MaxTrackSize.X = workWidth;
        minMaxInfo.MaxTrackSize.Y = workHeight;
        minMaxInfo.MinTrackSize.X = (int)Math.Min(Math.Round(MinWidth * scale), workWidth);
        minMaxInfo.MinTrackSize.Y = (int)Math.Min(Math.Round(MinHeight * scale), workHeight);
        Marshal.StructureToPtr(minMaxInfo, minMaxInfoPointer, false);
    }

    private void FitToWorkArea()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero) return;

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        var scale = GetDpiForWindow(windowHandle) / 96.0;
        if (scale <= 0) scale = 1.0;

        var workLeft = monitorInfo.WorkArea.Left / scale;
        var workTop = monitorInfo.WorkArea.Top / scale;
        var workWidth = (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / scale;
        var workHeight = (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / scale;

        MinWidth = Math.Min(MinWidth, workWidth);
        MinHeight = Math.Min(MinHeight, workHeight);
        Width = Math.Min(Width, workWidth);
        Height = Math.Min(Height, workHeight);
        Left = workLeft + Math.Max(0, (workWidth - Width) / 2);
        Top = workTop + Math.Max(0, (workHeight - Height) / 2);
    }

    private void ScrollLogsToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_logScrollPending) return;
        _logScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _logScrollPending = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_shutdownCompleted)
        {
            e.Cancel = true;
            if (_viewModel.IsBusy) await _viewModel.ShutdownAsync();
            await _viewModel.SaveApplicationStateAsync();
            _shutdownCompleted = true;
            Close();
            return;
        }
        _windowStateStore.Save(this);
        base.OnClosing(e);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
