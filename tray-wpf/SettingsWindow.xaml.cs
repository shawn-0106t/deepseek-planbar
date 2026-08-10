using System.Windows;
using System.Windows.Input;

namespace DeepseekPlanbarTray;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var d = App.Settings.Data;
        ThemeSystem.IsChecked = d.Theme == "system";
        ThemeLight.IsChecked = d.Theme == "light";
        ThemeDark.IsChecked = d.Theme == "dark";
        AutoStartBox.IsChecked = d.AutoStart;
        foreach (var child in IntervalPanel.Children)
        {
            if (child is RadioButton { Tag: string tag } rb && tag == d.RefreshMinutes.ToString())
            {
                rb.IsChecked = true;
                break;
            }
        }
        ShowCredentialStatus();
    }

    // 已配置凭证的脱敏预览（前 7 + 后 4），不显示完整值
    private void ShowCredentialStatus()
    {
        var (apiKey, userToken) = DeepseekService.LoadCredentials();
        ApiKeyStatus.Text = string.IsNullOrEmpty(apiKey) ? "未配置" : $"已配置 {Mask(apiKey)}（输入新值可替换）";
        TokenStatus.Text = string.IsNullOrEmpty(userToken) ? "未配置" : $"已配置 {Mask(userToken)}（输入新值可替换）";
        ApiKeyStatus.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        TokenStatus.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
    }

    private static string Mask(string s) =>
        s.Length > 12 ? s[..7] + "..." + s[^4..] : "***";

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        var d = App.Settings.Data;
        d.Theme = ThemeLight.IsChecked == true ? "light"
                : ThemeDark.IsChecked == true ? "dark" : "system";
        foreach (var child in IntervalPanel.Children)
        {
            if (child is RadioButton { IsChecked: true, Tag: string tag }
                && int.TryParse(tag, out int mins))
            {
                d.RefreshMinutes = mins;
                break;
            }
        }
        d.AutoStart = AutoStartBox.IsChecked == true;
        App.Settings.Save();
        App.Settings.ApplyAutoStart();
        App.Theme.Apply(d.Theme);
        App.Deepseek.Reschedule();

        // 凭证：只在输入了新值时验证并保存（验证不过不落盘）
        var newKey = ApiKeyBox.Password.Trim();
        var newToken = UserTokenBox.Password.Trim();
        if (newKey.Length > 0 || newToken.Length > 0)
        {
            SaveButton.IsEnabled = false;
            SaveButton.Content = "验证中...";
            try
            {
                if (newKey.Length > 0)
                {
                    var err = await DeepseekService.VerifyApiKey(newKey);
                    if (err == null)
                    {
                        DeepseekService.SaveCredentials(newKey, null);
                        SetStatus(ApiKeyStatus, "已验证并保存", true);
                        ApiKeyBox.Clear();
                    }
                    else SetStatus(ApiKeyStatus, CredErrorText(err, "API Key"), false);
                }
                if (newToken.Length > 0)
                {
                    var err = await DeepseekService.VerifyUserToken(newToken);
                    if (err == null)
                    {
                        DeepseekService.SaveCredentials(null, newToken);
                        SetStatus(TokenStatus, "已验证并保存", true);
                        UserTokenBox.Clear();
                    }
                    else SetStatus(TokenStatus, CredErrorText(err, "Token"), false);
                }
            }
            finally
            {
                SaveButton.IsEnabled = true;
                SaveButton.Content = "保存";
            }
        }
        _ = App.Deepseek.SafeRefresh();
    }

    private static void SetStatus(System.Windows.Controls.TextBlock tb, string text, bool ok)
    {
        tb.Text = text;
        tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            ok ? "SuccessBrush" : "DangerBrush");
    }

    private static string CredErrorText(string err, string what) => err switch
    {
        "key-invalid" or "token-expired" => $"{what} 无效或已过期，未保存",
        "network-error" => "网络错误，请检查网络后重试，未保存",
        "rate-limited" => "请求过于频繁，请稍后重试，未保存",
        "server-error" => "DeepSeek 服务端错误，请稍后重试，未保存",
        _ => $"验证失败（{err}），未保存",
    };

    private void ClearClick(object sender, RoutedEventArgs e)
    {
        DeepseekService.ClearCredentials();
        ApiKeyBox.Clear();
        UserTokenBox.Clear();
        ShowCredentialStatus();
        ApiKeyStatus.Text = "已清除";
        TokenStatus.Text = "已清除";
        _ = App.Deepseek.SafeRefresh();
    }

    // 一键复制 F12 Console 获取命令（手动粘贴兜底路径用）
    private async void CopyCmdClick(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText("JSON.parse(localStorage.userToken).value"); } catch { }
        CopyCmdButton.Content = "已复制，到浏览器 Console 粘贴执行";
        await Task.Delay(1500);
        CopyCmdButton.Content = "复制 Console 命令";
    }

    // 网页登录自动同步：内嵌 WebView2 登录窗，登录成功即捕获+验证+保存用量 Token
    private void SyncClick(object sender, RoutedEventArgs e)
    {
        var w = new LoginWindow();
        w.TokenSynced += token =>
        {
            ShowCredentialStatus();
            SetStatus(TokenStatus, "已通过网页登录同步并保存", true);
            _ = App.Deepseek.SafeRefresh();
        };
        w.Show();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
