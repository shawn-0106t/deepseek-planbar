using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DeepseekPlanbarTray;

// 托盘管理：鲸鱼 logo 图标、左键悬浮窗、右键菜单
public class TrayManager : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private MainWindow? _popup;
    private SettingsWindow? _settings;
    private DateTime _lastHide = DateTime.MinValue;
    private DateTime _lastHoverRefresh = DateTime.MinValue;
    private TrayMenuWindow? _menu;

    public TrayManager()
    {
        _notify = new Forms.NotifyIcon
        {
            Text = "DeepSeek Planbar",
            Visible = true,
            Icon = LoadLogoIcon()
        };
        // 用 MouseUp 而非 MouseClick：右键在无 ContextMenuStrip 时更可靠
        _notify.MouseUp += OnMouseUp;
        // 悬停预热：10 秒节流，用户看 tooltip/点开窗口时数据总是新的（节流后无额外稳态开销）
        _notify.MouseMove += OnHover;
        // 凭证失效的气泡提醒：点击直接打开设置
        _notify.BalloonTipClicked += (_, _) => ShowSettings();

        App.Deepseek.Updated += OnDataUpdated;
    }

    private string? _lastNotified; // 已通知过的失效状态，避免 30s 重试轮重复弹

    private void OnHover(object? sender, Forms.MouseEventArgs e)
    {
        if ((DateTime.Now - _lastHoverRefresh).TotalSeconds < 10) return;
        _lastHoverRefresh = DateTime.Now;
        _ = App.Deepseek.SafeRefresh();
    }

    // 图标静态不变，刷新只更新 tooltip 里的数字（失败时也展示保留数据并标注）
    private void OnDataUpdated()
    {
        var l = App.Deepseek.Last;
        if (l == null) { _notify.Text = "DeepSeek Planbar"; return; }
        var bal = l.Balance != null
            ? $"Balance {MainWindow.FmtMoney(l.Balance.Total, l.Balance.Currency)}" : "Balance ?";
        var cost = l.Usage != null
            ? $"This month {MainWindow.FmtMoney(l.Usage.MonthCost, "CNY")}" : "This month ?";
        _notify.Text = $"DeepSeek {bal} · {cost}"
                       + (l.HasError ? " (update failed)" : "");
        NotifyCredentialError(l);
    }

    // token 过期 / Key 失效时气泡提醒（仅状态翻转时弹一次；恢复正常后重置）
    private void NotifyCredentialError(DeepseekResult l)
    {
        var err = l.UsageError == "token-expired" ? "Usage token expired"
                : l.BalanceError == "key-invalid" ? "API Key invalid or expired" : null;
        if (err == null) { _lastNotified = null; return; }
        if (err == _lastNotified) return;
        _lastNotified = err;
        _notify.BalloonTipTitle = "DeepSeek Planbar";
        _notify.BalloonTipText = err + " — click to open Settings to reconfigure";
        _notify.ShowBalloonTip(5000);
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Right) { ShowMenu(); return; }
        if (e.Button != Forms.MouseButtons.Left) return;
        // 悬浮窗刚因失焦自动隐藏时，同一次托盘点击不要再把它弹出来
        if ((DateTime.Now - _lastHide).TotalMilliseconds < 300) return;
        TogglePopup();
    }

    private void ShowMenu()
    {
        _menu?.Close();
        _menu = new TrayMenuWindow();
        _menu.ShowAtCursor();
    }

    public void NotifyPopupHidden() => _lastHide = DateTime.Now;

    public void TogglePopup()
    {
        if (_popup is { IsVisible: true }) { _popup.HideAnimated(); return; }
        _popup ??= new MainWindow();
        _popup.ShowNearTray();
    }

    public void ShowSettings()
    {
        if (_settings == null || !_settings.IsLoaded) _settings = new SettingsWindow();
        _settings.Show();
        _settings.Activate();
    }

    // 托盘图标：优先使用内嵌的鲸鱼 logo（PNG 包装为 ICO，Vista+ 原生支持且保留 alpha），
    // 资源缺失时回退为蓝色圆球
    private static Icon LoadLogoIcon()
    {
        try
        {
            var s = Application.GetResourceStream(
                new Uri("pack://application:,,,/deepseek-logo.png"))?.Stream;
            if (s != null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                s.Dispose();
                var png = ms.ToArray();
                using var ico = new MemoryStream();
                var w = new BinaryWriter(ico);
                w.Write((short)0);          // reserved
                w.Write((short)1);          // type: icon
                w.Write((short)1);          // image count
                w.Write((byte)0);           // width: 0 = 256
                w.Write((byte)0);           // height: 0 = 256
                w.Write((byte)0);           // palette colors
                w.Write((byte)0);           // reserved
                w.Write((short)1);          // color planes
                w.Write((short)32);         // bits per pixel
                w.Write(png.Length);        // payload size
                w.Write(6 + 16);            // payload offset
                w.Write(png);
                ico.Position = 0;
                return new Icon(ico);
            }
        }
        catch { }
        return RenderFallbackIcon();
    }

    private static Icon RenderFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var ball = new SolidBrush(Color.FromArgb(255, 0x1A, 0x88, 0xFF));
            g.FillEllipse(ball, 1, 1, 30, 30);
            using var shine = new SolidBrush(Color.FromArgb(90, 255, 255, 255));
            g.FillEllipse(shine, 7, 5, 10, 7);
        }
        IntPtr hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        App.Deepseek.Updated -= OnDataUpdated;
        _notify.Visible = false;
        _notify.Icon?.Dispose();
        _notify.Dispose();
    }
}
