using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeepseekPlanbarTray;

public partial class MainWindow : Window
{
    private const decimal LowBalanceThreshold = 10m; // 余额低于此值标红
    private bool _suppressDeactivate;

    public MainWindow()
    {
        InitializeComponent();
        App.Deepseek.Updated += Render;
        Deactivated += (_, _) => { if (!_suppressDeactivate) HideAnimated(); };
        Closed += (_, _) => App.Deepseek.Updated -= Render;
    }

    private bool _hiding;

    public void ShowNearTray()
    {
        _hiding = false;
        // 窗口高度随内容自适应（SizeToContent）：必须先 Render 填充模型卡、
        // 再 UpdateLayout 量最终高度、最后定位——先量后填会被撑高压到任务栏。
        // Opacity=0 起步，定位完成前用户看不到窗口，不会闪跳
        Opacity = 0;
        Show();
        Render();
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 12 + 22;
        Top = wa.Bottom - ActualHeight - 12 + 22;
        Activate();
        // 原生风格的滑入 + 淡入（AllowsTransparency 分层窗口上 AnimateWindow 不可靠，
        // 用 WPF 动画保证可见效果；GPU 合成，开销可忽略）
        var tt = new TranslateTransform(0, 16);
        RootBorder.RenderTransform = tt;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void HideAnimated()
    {
        if (_hiding) return;
        _hiding = true;
        var tt = RootBorder.RenderTransform as TranslateTransform ?? new TranslateTransform();
        RootBorder.RenderTransform = tt;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        var slide = new DoubleAnimation(12, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            Hide();
            Opacity = 1;
            App.Tray?.NotifyPopupHidden();
        };
        BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void RefreshView() => Render();

    // 把当前窗口内容渲染为 PNG（截图模式用）
    public void CapturePng(string path)
    {
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)ActualWidth, (int)ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir != null) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ---------- 渲染 ----------

    private void Render()
    {
        var r = App.Deepseek.Last;
        RenderBalance(r);
        RenderUsage(r);
        LastUpdated.Text = r == null ? ""
            : r.HasError ? "更新失败"
            : $"更新于 {r.FetchedAt.LocalDateTime:HH:mm}";
    }

    private void RenderBalance(DeepseekResult? r)
    {
        if (r?.Balance == null)
        {
            BalanceTotal.Text = "--";
            BalanceTotal.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            BalanceDetail.Text = "";
            BalanceStatus.Text = r?.BalanceError switch
            {
                null => "",
                "no-api-key" => "未配置 API Key（在设置中填写）",
                "key-invalid" => "API Key 无效或已过期",
                _ => "更新失败",
            };
            BalanceStatus.SetResourceReference(ForegroundProperty,
                r?.BalanceError == null ? "TextSecondaryBrush" : "WarningBrush");
            return;
        }
        var b = r.Balance;
        BalanceTotal.Text = FmtMoney(b.Total, b.Currency);
        // 余额低标红；is_available=false 同样告警
        BalanceTotal.SetResourceReference(ForegroundProperty,
            !b.IsAvailable || b.Total < LowBalanceThreshold ? "DangerBrush" : "TextPrimaryBrush");
        BalanceDetail.Text = $"赠送 {FmtMoney(b.Granted, b.Currency)} · 充值 {FmtMoney(b.ToppedUp, b.Currency)}";
        BalanceStatus.Text = b.IsAvailable ? "可用" : "余额不足";
        BalanceStatus.SetResourceReference(ForegroundProperty,
            b.IsAvailable ? "SuccessBrush" : "DangerBrush");
    }

    private void RenderUsage(DeepseekResult? r)
    {
        ModelCards.Children.Clear();
        if (r?.Usage == null)
        {
            UsageSummary.Text = "";
            var hint = r?.UsageError switch
            {
                null => null,
                "no-user-token" => "未配置用量 Token：打开设置，按指引粘贴浏览器 userToken",
                "token-expired" => "用量 Token 已过期：打开设置，按 F12 指引重新获取",
                _ => "用量更新失败",
            };
            if (hint != null) ModelCards.Children.Add(MakeHintCard(hint));
            return;
        }
        var u = r.Usage;
        // 真实响应会带全零的占位模型条目（如 deepseek-v4-pro、
        // "deepseek-chat & deepseek-reasoner"），全部指标为零时不渲染卡片
        var models = u.ByModel
            .Where(m => m.MonthTokens > 0 || m.MonthRequests > 0 || m.MonthCost > 0
                     || m.TodayTokens > 0 || m.TodayCost > 0)
            .OrderByDescending(m => m.MonthCost).Take(4).ToList();
        long maxTokens = Math.Max(1, models.Count > 0 ? models.Max(m => m.MonthTokens) : 1);
        foreach (var m in models)
            ModelCards.Children.Add(MakeModelCard(m, maxTokens));
        if (models.Count == 0)
            ModelCards.Children.Add(MakeHintCard("本月暂无用量数据"));
        UsageSummary.Text =
            $"合计：本月 {FmtMoney(u.MonthCost, "CNY")} / {FmtTokens(u.MonthTokens)} tokens" +
            $" · 今日 {FmtMoney(u.TodayCost, "CNY")} / {FmtTokens(u.TodayTokens)} tokens";
    }

    // 单模型卡：名称+本月消费 / 本月 token+请求数 / 相对进度条 / 命中率+今日消耗
    private Border MakeModelCard(ModelUsage m, long maxTokens)
    {
        var title = new TextBlock
        {
            Text = m.DisplayName,
            FontSize = 13,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var cost = new TextBlock
        {
            Text = FmtMoney(m.MonthCost, "CNY"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        cost.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        var head = new DockPanel();
        DockPanel.SetDock(cost, Dock.Right);
        head.Children.Add(cost);
        head.Children.Add(title);

        var tokens = new TextBlock
        {
            Text = $"本月 {FmtTokens(m.MonthTokens)} tokens · 请求 {FmtTokens(m.MonthRequests)} 次",
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 10),
        };
        tokens.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

        // 相对进度条：宽度 = 本模型 tokens / max(各模型)，复用骨架的双列星宽画法
        double p = Math.Clamp(m.MonthTokens * 100.0 / maxTokens, 0, 100);
        var bar = new Grid { Height = 6 };
        var colFill = new ColumnDefinition { Width = new GridLength(p, GridUnitType.Star) };
        var colRest = new ColumnDefinition { Width = new GridLength(100 - p, GridUnitType.Star) };
        bar.ColumnDefinitions.Add(colFill);
        bar.ColumnDefinitions.Add(colRest);
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "ProgressTrackBrush");
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        bar.Children.Add(track);
        bar.Children.Add(fill);

        var hit = new TextBlock { FontSize = 11 };
        if (m.CacheHitRate.HasValue)
        {
            hit.Text = $"命中率 {m.CacheHitRate.Value:0.#}%";
            hit.SetResourceReference(TextBlock.ForegroundProperty, HitRateBrush(m.CacheHitRate.Value));
        }
        else
        {
            hit.Text = "命中率 --";
            hit.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        }
        var today = new TextBlock
        {
            Text = $"今日 {FmtMoney(m.TodayCost, "CNY")} / {FmtTokens(m.TodayTokens)}",
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        today.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(today, Dock.Right);
        foot.Children.Add(today);
        foot.Children.Add(hit);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(tokens);
        stack.Children.Add(bar);
        stack.Children.Add(foot);
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        return card;
    }

    // 无凭证/错误提示卡（纯文字）
    private static Border MakeHintCard(string text)
    {
        var tb = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 12),
            Child = tb,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        return card;
    }

    // 命中率：≥85 绿 / 60–84 黄 / <60 红（命中率低=缓存浪费，反向标红）
    private static string HitRateBrush(double rate) =>
        rate >= 85 ? "SuccessBrush" : rate >= 60 ? "WarningBrush" : "DangerBrush";

    // ---------- 格式化 ----------

    // 金额：整元省略小数（WPF 全程 UTF-16，¥ 无编码问题）
    public static string FmtMoney(decimal v, string currency)
    {
        var symbol = currency == "USD" ? "$" : "¥";
        return symbol + (v == decimal.Floor(v) ? ((long)v).ToString() : v.ToString("0.00"));
    }

    public static string FmtTokens(long v)
    {
        if (v >= 1_000_000) return $"{v / 1_000_000.0:0.##}M";
        if (v >= 1_000) return $"{v / 1_000.0:0.#}K";
        return v.ToString();
    }

    // ---------- 按钮 ----------

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        await App.Deepseek.SafeRefresh();
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivate = true;
        App.Tray.ShowSettings();
        _suppressDeactivate = false;
    }

    private void ExitClick(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
