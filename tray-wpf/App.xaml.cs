using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace DeepseekPlanbarTray;

public partial class App : Application
{
    private Mutex? _mutex;
    public static SettingsService Settings { get; private set; } = null!;
    public static ThemeService Theme { get; private set; } = null!;
    public static DeepseekService Deepseek { get; private set; } = null!;
    public static TrayManager Tray { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Settings = SettingsService.Load();
        Theme = new ThemeService();
        Deepseek = new DeepseekService();

        // Headless 自检模式：拉取一次数据并打印 JSON 后退出（无凭证时输出结构化错误，
        // 同样验证取数管道）
        // （Task.Run 脱离 UI 线程的 SynchronizationContext，避免死锁；
        //   先于单实例检查，保证有 GUI 实例运行时自检仍可用）
        if (e.Args.Contains("--test-fetch"))
        {
            var r = Task.Run(() => Deepseek.FetchAsync()).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(r,
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Shutdown();
            return;
        }

        // Headless 解析层自检：跑 tests/fixtures 样本断言，无需真实凭证
        if (e.Args.Contains("--test-fixtures"))
        {
            int i = e.Args.ToList().IndexOf("--test-fixtures");
            var dir = i + 1 < e.Args.Length ? e.Args[i + 1] : "tests/fixtures";
            Shutdown(FixtureTests.Run(dir));
            return;
        }

        // Headless UI 自检：构造三个窗口验证资源解析与 XAML 加载
        if (e.Args.Contains("--test-ui"))
        {
            Theme.Apply(Settings.Data.Theme);
            try
            {
                var w = new MainWindow();
                Console.WriteLine("MainWindow OK");
                var s = new SettingsWindow();
                Console.WriteLine("SettingsWindow OK");
                var m = new TrayMenuWindow();
                Console.WriteLine("TrayMenuWindow OK");
                var l = new LoginWindow();
                Console.WriteLine("LoginWindow OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("UI-FAIL: " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.InnerException?.Message);
                Shutdown(1);
                return;
            }
            Shutdown();
            return;
        }

        // 截图模式：真实或 mock 数据 + 指定主题渲染悬浮窗为 PNG（供 README/目检使用）
        // 用法：--screenshot <输出路径> [--dark] [--mock 用模拟数据]
        if (e.Args.Contains("--screenshot"))
        {
            int i = e.Args.ToList().IndexOf("--screenshot");
            var path = i + 1 < e.Args.Length ? e.Args[i + 1] : "screenshot.png";
            Theme.Apply(e.Args.Contains("--dark") ? "dark" : "light");
            DeepseekResult? r;
            if (e.Args.Contains("--mock"))
            {
                r = new DeepseekResult
                {
                    FetchedAt = DateTimeOffset.Now,
                    Balance = new BalanceInfo
                    {
                        IsAvailable = true,
                        Currency = "CNY",
                        Total = 66.50m,
                        Granted = 10m,
                        ToppedUp = 56.50m,
                    },
                    Usage = new UsageInfo
                    {
                        MonthTokens = 2450000,
                        MonthRequests = 4200,
                        MonthCost = 3.00m,
                        CacheHitRate = 66.7,
                        TodayTokens = 26500,
                        TodayCost = 0.30m,
                        ByModel = new System.Collections.Generic.List<ModelUsage>
                        {
                            new() { Model = "deepseek-v4-pro", DisplayName = "V4 Pro",
                                    MonthTokens = 1330000, MonthRequests = 1200, MonthCost = 2.50m,
                                    CacheHitRate = 80.0, TodayTokens = 17000, TodayCost = 0.30m },
                            new() { Model = "deepseek-v4-flash", DisplayName = "V4 Flash",
                                    MonthTokens = 1120000, MonthRequests = 3000, MonthCost = 0.50m,
                                    CacheHitRate = 50.0, TodayTokens = 9500, TodayCost = 0m },
                        },
                    },
                };
            }
            else
            {
                r = Task.Run(() => Deepseek.FetchAsync()).GetAwaiter().GetResult();
            }
            Deepseek.Inject(r);
            var w = new MainWindow();
            w.Show();
            w.RefreshView();
            w.UpdateLayout();
            w.CapturePng(path);
            w.Close();
            Console.WriteLine("saved: " + path);
            Shutdown();
            return;
        }

        // WebView2 运行时自检：真实初始化一次 CoreWebView2（验证单文件发布时
        // WebView2Loader.dll 的自解压可用），30 秒超时，打印 WEBVIEW OK/FAIL 后退出
        if (e.Args.Contains("--test-webview"))
        {
            Theme.Apply(Settings.Data.Theme);
            var w = new LoginWindow();
            w.Show();
            Task.Run(async () =>
            {
                var done = await Task.WhenAny(w.InitDone.Task, Task.Delay(30000));
                var ok = done == w.InitDone.Task && w.InitDone.Task.Result;
                Dispatcher.Invoke(() =>
                {
                    Console.WriteLine(ok ? "WEBVIEW OK" : "WEBVIEW FAIL: " + (w.InitError ?? "timeout"));
                    Shutdown(ok ? 0 : 1);
                });
            });
            return;
        }

        _mutex = new Mutex(true, "DeepseekPlanbarTray.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        Theme.Apply(Settings.Data.Theme);
        Theme.HookSystemEvents();
        Tray = new TrayManager();
        Deepseek.StartAutoRefresh();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        Deepseek?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
