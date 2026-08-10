using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeepseekPlanbarTray;

// 托盘右键菜单：WPF 自绘，与悬浮窗/设置窗同一视觉语言
public partial class TrayMenuWindow : Window
{
    public TrayMenuWindow()
    {
        InitializeComponent();
        // Close 期间 Deactivated 会同步重入；延迟到当前关闭流程结束后再判断，
        // 此时若已在关闭（IsVisible=false）则为 no-op，彻底避免重入崩溃
        Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible) Close();
        }));
    }

    public void ShowAtCursor()
    {
        // Cursor.Position 返回物理像素，WPF 坐标是 DIP（系统 DPI 感知），
        // 高 DPI 缩放下必须换算，否则菜单会偏出屏幕（表现为"没弹出来"）。
        // 多屏异 DPI 时必须用光标所在屏的缩放：隐藏状态的菜单窗口停在主屏，
        // GetDpiForWindow 会拿错系数
        var pos = System.Windows.Forms.Cursor.Position;
        Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        double scale = GetDpiForPoint(pos.X, pos.Y) / 96.0;
        if (scale <= 0) scale = 1;
        double cx = pos.X / scale, cy = pos.Y / scale;
        var wa = SystemParameters.WorkArea;
        double h = ActualHeight, w = ActualWidth;
        // Root Border margin 24 is shadow fade room, not visual inset.
        // Position the visual card (window minus both margins) so it lands
        // exactly where the old 5px-margin menu did.
        const double m = 24;
        double cardW = w - 2 * m, cardH = h - 2 * m;
        // Card left trails the cursor by 5 (the old margin); the right clamp
        // keeps the card 13px inside the work area (old clamp 8 + old margin 5).
        double cardX = Math.Min(cx + 5, wa.Right - cardW - 13);
        // 光标靠近屏幕底部时菜单向上弹出，否则向下。
        // Threshold matches the old window-edge test: old height = cardH + 10,
        // plus the same 24px lookahead.
        double cardY = cy + cardH + 34 > wa.Bottom ? cy - cardH - 13 : cy + 13;
        Left = cardX - m;
        Top = cardY - m;
        Activate();
        // 托盘点击的输入归 explorer，后台进程必须显式抢前台，
        // 否则菜单拿不到焦点会立刻被 Deactivated 关闭（表现为"没弹出来"）
        SetForegroundWindow(hwnd);
    }

    private void OpenClick(object sender, RoutedEventArgs e)
    {
        Close();
        App.Tray.TogglePopup();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        Close();
        await App.Deepseek.SafeRefresh();
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        Close();
        App.Tray.ShowSettings();
    }

    private void ExitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // Effective DPI of the monitor containing the given physical pixel point.
    private static double GetDpiForPoint(int x, int y)
    {
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const int MDT_EFFECTIVE_DPI = 0;
        var mon = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (mon != IntPtr.Zero &&
            GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
        {
            return dx;
        }
        return 96.0;
    }
}
