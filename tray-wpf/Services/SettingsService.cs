using System.Text.Json;

namespace DeepseekPlanbarTray;

public class SettingsData
{
    public string Theme { get; set; } = "system"; // system | light | dark
    public int RefreshMinutes { get; set; } = 5;
    public bool AutoStart { get; set; }
}

public class SettingsService
{
    public SettingsData Data { get; private set; } = new();

    // 便携模式：exe 同目录存在 portable.dat 时配置写 exe 旁边，否则写 %APPDATA%
    public static string ConfigDir =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.dat"))
            ? AppContext.BaseDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepseekPlanbarTray");

    private static string FilePath => Path.Combine(ConfigDir, "settings.json");

    public static SettingsService Load()
    {
        var s = new SettingsService();
        try
        {
            if (File.Exists(FilePath))
                s.Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath)) ?? new SettingsData();
        }
        catch { /* 配置损坏时回落默认值 */ }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // 开机自启走 HKCU Run 键，per-user 不触发 UAC
    public void ApplyAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (Data.AutoStart)
                key.SetValue("DeepseekPlanbarTray", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("DeepseekPlanbarTray", false);
        }
        catch { }
    }
}
