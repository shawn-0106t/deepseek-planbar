using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace DeepseekPlanbarTray;

// 内嵌 WebView2 登录窗：用户在窗口内正常登录 platform.deepseek.com，
// JS hook 从登录后请求的 Authorization 头捕获用量 token（也可从 localStorage 兜底读取），
// host 侧验证（实调一次 amount 接口返回 200）后才持久化。
// 捕获走 chrome.webview.postMessage 事件推送，登录窗关闭后零开销，无任何轮询。
public partial class LoginWindow : Window
{
    // 登录成功后触发（参数为已保存的 token）
    public event Action<string>? TokenSynced;

    // WebView2 初始化结果（供 --test-webview 自检等待；RunContinuationsAsynchronously 避免同步重入）
    public readonly TaskCompletionSource<bool> InitDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? InitError { get; private set; }

    private bool _accepted;    // 已有 token 通过验证
    private bool _verifying;   // 正在验证某个候选 token（登录过程会有多个中间 token）

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // 用户数据目录放 %LOCALAPPDATA%（缓存体积大，不应漫游；便携模式也不污染 exe 目录）
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder:
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "DeepseekPlanbarTray", "webview2"));
            await WebView.EnsureCoreWebView2Async(env);
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HookJs);
            WebView.CoreWebView2.Navigate("https://platform.deepseek.com/");
            InitDone.TrySetResult(true);
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            InitDone.TrySetResult(false);
            SetStatus("加载失败：" + ex.Message + "（可改用手动粘贴 Token）", false);
        }
    }

    // 拦截 fetch / XHR 的 Authorization 头；兜底读 localStorage.userToken
    private const string HookJs = """
        (function () {
          function deliver(t) {
            if (t && t.length >= 20) {
              window.chrome.webview.postMessage({ kind: 'token', token: t });
            }
          }
          function fromAuth(h) {
            if (!h) return;
            var m = /Bearer\s+(\S+)/i.exec(h);
            if (m) deliver(m[1]);
          }
          var of = window.fetch;
          window.fetch = function (input, init) {
            try {
              var h = (init && init.headers) || (input && input.headers);
              if (h) {
                if (typeof h.get === 'function') fromAuth(h.get('Authorization'));
                else fromAuth(h.Authorization || h.authorization);
              }
            } catch (e) {}
            return of.apply(this, arguments);
          };
          var os = XMLHttpRequest.prototype.setRequestHeader;
          XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
            try { if (String(name).toLowerCase() === 'authorization') fromAuth(value); } catch (e) {}
            return os.apply(this, arguments);
          };
          try {
            var raw = localStorage.getItem('userToken');
            if (raw) deliver(JSON.parse(raw).value);
          } catch (e) {}
        })();
        """;

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? token = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.GetPropertyOrDefault("kind")?.GetString() == "token")
                token = doc.RootElement.GetPropertyOrDefault("token")?.GetString();
        }
        catch { }
        if (token != null) await TryAccept(token);
    }

    // 兜底通道：每次导航完成后直接读 localStorage.userToken
    // （hook 注入时序异常时仍能拿到；已登录老会话刷新页面即可触发）
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_accepted || WebView.CoreWebView2 == null) return;
        try
        {
            var raw = await WebView.CoreWebView2.ExecuteScriptAsync(
                "localStorage.getItem('userToken')");
            if (string.IsNullOrEmpty(raw) || raw == "null") return;
            var inner = JsonSerializer.Deserialize<string>(raw);   // 结果是 JSON 编码的字符串
            if (string.IsNullOrEmpty(inner)) return;
            using var doc = JsonDocument.Parse(inner);
            var token = doc.RootElement.GetPropertyOrDefault("value")?.GetString();
            if (token != null) await TryAccept(token);
        }
        catch { }
    }

    // 登录过程会经过多个中间 token，并非每个都有效：实调一次 amount 接口，200 才接受
    private async Task TryAccept(string token)
    {
        if (_accepted || _verifying || token.Length < 20) return;
        _verifying = true;
        try
        {
            SetStatus("已捕获 Token，正在验证...", true);
            var err = await DeepseekService.VerifyUserToken(token);
            if (_accepted) return;
            if (err == null)
            {
                _accepted = true;
                DeepseekService.SaveCredentials(null, token);
                SetStatus("验证通过，已保存", true);
                TokenSynced?.Invoke(token);
                await Task.Delay(1000);
                Close();
            }
            else
            {
                SetStatus("捕获的 Token 未通过验证，请继续完成登录...", true);
            }
        }
        finally
        {
            _verifying = false;
        }
    }

    private void SetStatus(string text, bool normal)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(ForegroundProperty,
            normal ? "TextSecondaryBrush" : "DangerBrush");
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void TitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
