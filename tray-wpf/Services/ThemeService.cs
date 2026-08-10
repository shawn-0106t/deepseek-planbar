using System.Windows;
using Microsoft.Win32;

namespace DeepseekPlanbarTray;

// 主题服务：月之亮面 / 月之暗面 / 跟随系统
public class ThemeService
{
    public event Action<bool>? Changed; // bool = isDark
    public bool IsDark { get; private set; }

    // 共享样式字典只加载一次
    private static readonly ResourceDictionary Shared = new()
    {
        Source = new Uri("Themes/Shared.xaml", UriKind.Relative)
    };

    public void Apply(string mode)
    {
        bool dark = mode switch
        {
            "light" => false,
            "dark" => true,
            _ => SystemIsDark(),
        };
        IsDark = dark;
        var dict = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(Shared);
        merged.Add(dict);
        Changed?.Invoke(dark);
    }

    public static bool SystemIsDark()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    // 系统亮暗切换时实时跟随（仅当用户选了"跟随系统"）
    public void HookSystemEvents()
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (App.Settings.Data.Theme == "system")
                Application.Current.Dispatcher.Invoke(() => Apply("system"));
        };
    }
}
