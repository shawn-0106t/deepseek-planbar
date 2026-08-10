using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace DeepseekPlanbarTray;

public class BalanceInfo
{
    public bool IsAvailable { get; set; }
    public string Currency { get; set; } = "CNY";
    public decimal Total { get; set; }
    public decimal Granted { get; set; }
    public decimal ToppedUp { get; set; }
}

public class ModelUsage
{
    public string Model { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public long MonthTokens { get; set; }
    public long MonthRequests { get; set; }
    public decimal MonthCost { get; set; }
    public double? CacheHitRate { get; set; }   // 0-100；无输入 token 时为 null
    public long TodayTokens { get; set; }
    public decimal TodayCost { get; set; }
}

public class UsageInfo
{
    public long MonthTokens { get; set; }
    public long MonthRequests { get; set; }
    public decimal MonthCost { get; set; }
    public double? CacheHitRate { get; set; }
    public long TodayTokens { get; set; }
    public decimal TodayCost { get; set; }
    public List<ModelUsage> ByModel { get; set; } = new();
}

public class DeepseekResult
{
    public BalanceInfo? Balance { get; set; }
    public UsageInfo? Usage { get; set; }
    // 两类错误独立：余额接口挂了不影响用量展示，反之亦然
    public string? BalanceError { get; set; }
    public string? UsageError { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public bool HasError => BalanceError != null || UsageError != null;
}

// 数据源：余额走官方 API（API Key），用量走平台内部接口（网页 userToken）。
// 接口结构核实自 DeepSeekMonitorWindows（MIT）源码，见 HANDOFF.md。
public class DeepseekService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string BalanceUrl = "https://api.deepseek.com/user/balance";
    private const string UsageBase = "https://platform.deepseek.com/api/v0/usage";
    // 伪装网页端的固定头（上游若收紧校验，只需改这两个常量）
    private const string AppVersion = "1.0.0";
    private const string BrowserUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";

    // 凭证与缓存目录：%USERPROFILE%/.deepseek-planbar/（用户 profile 默认仅本人可访问）
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deepseek-planbar");
    public static string CredentialsPath => Path.Combine(DataDir, "credentials.json");
    public static string CachePath => Path.Combine(DataDir, "cache.json");

    private readonly Timer _timer;
    private int _periodMs;
    private int _refreshing; // SafeRefresh 重入保护（timer/hover/按钮三路并发触发）
    public DeepseekResult? Last { get; private set; }
    public event Action? Updated;

    public DeepseekService()
    {
        _timer = new Timer(async _ => await SafeRefresh(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void StartAutoRefresh()
    {
        LoadCache();
        Reschedule();
    }

    public void Reschedule()
    {
        _periodMs = (int)TimeSpan.FromMinutes(Math.Max(1, App.Settings.Data.RefreshMinutes)).TotalMilliseconds;
        _timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(_periodMs));
    }

    public async Task SafeRefresh()
    {
        // 已有刷新在飞则直接丢弃本次触发，避免旧响应后到达覆盖新数据
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var r = await FetchAsync();
            if (r != null)
            {
                // 失败时保留上一次成功的对应部分，界面不清空（仅状态行提示更新失败）
                if (Last != null)
                {
                    r.Balance ??= r.BalanceError != null ? Last.Balance : r.Balance;
                    r.Usage ??= r.UsageError != null ? Last.Usage : r.Usage;
                }
                Last = r;
                if (r.Balance != null || r.Usage != null) SaveCache(r);
                // 失败后 30 秒快速重试，成功则回到正常周期
                // （退出阶段 Timer 可能已 Dispose，吞掉竞态异常）
                try
                {
                    _timer.Change(TimeSpan.FromMilliseconds(r.HasError ? 30_000 : _periodMs),
                                  TimeSpan.FromMilliseconds(_periodMs));
                }
                catch (ObjectDisposedException) { }
            }
            try { Application.Current?.Dispatcher.Invoke(() => Updated?.Invoke()); } catch { }
        }
        catch { /* 取数层已分类错误，这里兜底防 async void Timer 回调崩进程 */ }
        finally { _refreshing = 0; }
    }

    // 截图/测试模式注入数据
    internal void Inject(DeepseekResult? r) => Last = r;

    public async Task<DeepseekResult?> FetchAsync()
    {
        var r = new DeepseekResult { FetchedAt = DateTimeOffset.Now };
        var (apiKey, userToken) = LoadCredentials();

        if (string.IsNullOrEmpty(apiKey)) r.BalanceError = "no-api-key";
        else (r.Balance, r.BalanceError) = await FetchBalance(apiKey).ConfigureAwait(false);

        if (string.IsNullOrEmpty(userToken)) r.UsageError = "no-user-token";
        else (r.Usage, r.UsageError) = await FetchUsage(userToken).ConfigureAwait(false);

        return r;
    }

    // ---------- 凭证 ----------

    // 读取顺序：环境变量 DEEPSEEK_API_KEY / DEEPSEEK_USER_TOKEN → credentials.json
    public static (string? apiKey, string? userToken) LoadCredentials()
    {
        string? apiKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        string? userToken = Environment.GetEnvironmentVariable("DEEPSEEK_USER_TOKEN");
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(userToken))
            return (apiKey, userToken);
        try
        {
            if (File.Exists(CredentialsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
                var root = doc.RootElement;
                if (string.IsNullOrEmpty(apiKey))
                    apiKey = root.GetPropertyOrDefault("api_key")?.GetString();
                if (string.IsNullOrEmpty(userToken))
                    userToken = root.GetPropertyOrDefault("user_token")?.GetString();
            }
        }
        catch { }
        return (apiKey, userToken);
    }

    // 仅读 credentials.json（不看环境变量）：保存凭证时合并用，
    // 避免把用户临时设置的环境变量凭证意外落盘
    private static (string? apiKey, string? userToken) LoadCredentialsFromFile()
    {
        try
        {
            if (File.Exists(CredentialsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
                var root = doc.RootElement;
                return (root.GetPropertyOrDefault("api_key")?.GetString(),
                        root.GetPropertyOrDefault("user_token")?.GetString());
            }
        }
        catch { }
        return (null, null);
    }

    // 非空字段才覆盖写入；为 null 的字段保留文件里的已有值
    public static void SaveCredentials(string? apiKey, string? userToken)
    {
        var (oldKey, oldToken) = LoadCredentialsFromFile();
        var payload = new Dictionary<string, string>
        {
            ["api_key"] = apiKey ?? oldKey ?? "",
            ["user_token"] = userToken ?? oldToken ?? "",
        };
        Directory.CreateDirectory(DataDir);
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(CredentialsPath, json);
    }

    public static void ClearCredentials()
    {
        try { if (File.Exists(CredentialsPath)) File.Delete(CredentialsPath); } catch { }
    }

    // ---------- 验证（设置窗"验证并保存"用）：返回 null 表示有效 ----------

    public static async Task<string?> VerifyApiKey(string apiKey)
    {
        var (_, err) = await FetchBalance(apiKey).ConfigureAwait(false);
        return err;
    }

    // 登录过程有多个中间 token，必须实调一次 amount 接口返回 200 才算有效
    public static async Task<string?> VerifyUserToken(string userToken)
    {
        var now = DateTime.Now;
        using var req = NewUsageRequest($"{UsageBase}/amount?month={now.Month}&year={now.Year}", userToken);
        try
        {
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return MapUsageError(resp.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "network-error";
        }
    }

    // ---------- 取数 ----------

    private static async Task<(BalanceInfo?, string?)> FetchBalance(string apiKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, MapBalanceError(resp.StatusCode));
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (ParseBalance(json), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "network-error");
        }
        catch (Exception)
        {
            return (null, "parse-error");
        }
    }

    private static async Task<(UsageInfo?, string?)> FetchUsage(string userToken)
    {
        var now = DateTime.Now;
        var amountJson = await FetchUsageJson("amount", now.Month, now.Year, userToken).ConfigureAwait(false);
        if (amountJson.Error != null) return (null, amountJson.Error);
        var costJson = await FetchUsageJson("cost", now.Month, now.Year, userToken).ConfigureAwait(false);
        if (costJson.Error != null) return (null, costJson.Error);
        try
        {
            return (ParseUsage(amountJson.Json!, costJson.Json!), null);
        }
        catch (Exception)
        {
            return (null, "parse-error");
        }
    }

    private static async Task<(string? Json, string? Error)> FetchUsageJson(
        string kind, int month, int year, string userToken)
    {
        try
        {
            using var req = NewUsageRequest($"{UsageBase}/{kind}?month={month}&year={year}", userToken);
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, MapUsageError(resp.StatusCode));
            return (await resp.Content.ReadAsStringAsync().ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "network-error");
        }
    }

    private static HttpRequestMessage NewUsageRequest(string url, string userToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {userToken}");
        req.Headers.TryAddWithoutValidation("x-app-version", AppVersion);
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUA);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        return req;
    }

    private static string MapBalanceError(System.Net.HttpStatusCode code) => code switch
    {
        System.Net.HttpStatusCode.Unauthorized => "key-invalid",
        (System.Net.HttpStatusCode)429 => "rate-limited",
        >= (System.Net.HttpStatusCode)500 => "server-error",
        _ => $"http-{(int)code}",
    };

    private static string MapUsageError(System.Net.HttpStatusCode code) => code switch
    {
        System.Net.HttpStatusCode.Unauthorized => "token-expired",
        (System.Net.HttpStatusCode)429 => "rate-limited",
        >= (System.Net.HttpStatusCode)500 => "server-error",
        _ => $"http-{(int)code}",
    };

    // ---------- 解析（纯静态，fixture 可测；缺字段给默认值不崩溃） ----------
    // 注意：token 数量取整为 long，但 cost 金额（元，含小数）必须保留 double 精度，
    // 因此条目统一按 double 解析，由调用方决定取整还是保留。

    // {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"100.00",...}]}
    public static BalanceInfo ParseBalance(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var info = new BalanceInfo();
        if (root.TryGetProperty("is_available", out var av) && av.ValueKind == JsonValueKind.True)
            info.IsAvailable = true;
        if (root.GetPropertyOrDefault("balance_infos") is { ValueKind: JsonValueKind.Array } arr
            && arr.GetArrayLength() > 0)
        {
            var b = arr[0];
            info.Currency = b.GetPropertyOrDefault("currency")?.GetString() ?? "CNY";
            info.Total = ParseMoney(b.GetPropertyOrDefault("total_balance"));
            info.Granted = ParseMoney(b.GetPropertyOrDefault("granted_balance"));
            info.ToppedUp = ParseMoney(b.GetPropertyOrDefault("topped_up_balance"));
        }
        return info;
    }

    // amount: data.biz_data 是对象 {total[], days[]}
    // cost:   data.biz_data 是数组（取 [0]），内部同为 {total[], days[]}；金额单位为元
    public static UsageInfo ParseUsage(string amountJson, string costJson)
    {
        using var amountDoc = JsonDocument.Parse(amountJson);
        using var costDoc = JsonDocument.Parse(costJson);
        var amountBiz = GetBiz(amountDoc.RootElement);
        var costBiz = GetBiz(costDoc.RootElement);

        var models = new Dictionary<string, ModelUsage>();
        ModelUsage For(string model)
        {
            if (!models.TryGetValue(model, out var m))
            {
                m = new ModelUsage { Model = model, DisplayName = FriendlyName(model) };
                models[model] = m;
            }
            return m;
        }

        // 月度 token：total[] 按模型聚合；REQUEST 计请求数不计 token
        long hitAll = 0, missAll = 0;
        foreach (var (model, entries) in EnumerateModelEntries(amountBiz, "total"))
        {
            long hit = 0, miss = 0;
            foreach (var (type, amount) in entries)
            {
                var m = For(model);
                switch (type)
                {
                    case "REQUEST": m.MonthRequests += (long)Math.Round(amount); break;
                    case "PROMPT_CACHE_HIT_TOKEN": m.MonthTokens += (long)Math.Round(amount); hit += (long)Math.Round(amount); break;
                    case "PROMPT_CACHE_MISS_TOKEN": m.MonthTokens += (long)Math.Round(amount); miss += (long)Math.Round(amount); break;
                    default: m.MonthTokens += (long)Math.Round(amount); break; // RESPONSE_TOKEN / PROMPT_TOKEN 等
                }
            }
            var mm = For(model);
            mm.CacheHitRate = hit + miss > 0 ? hit * 100.0 / (hit + miss) : null;
            hitAll += hit; missAll += miss;
        }

        // 今日 token：days[] 中 date == 本地今天的条目
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        foreach (var (model, entries) in EnumerateDayEntries(amountBiz, "days", today))
            foreach (var (type, amount) in entries)
                if (type != "REQUEST") For(model).TodayTokens += (long)Math.Round(amount);

        // 消费（元，保留小数值）：cost 条目里也有 REQUEST 项，求和必须排除
        foreach (var (model, entries) in EnumerateModelEntries(costBiz, "total"))
            foreach (var (type, amount) in entries)
                if (type != "REQUEST") For(model).MonthCost += (decimal)amount;
        foreach (var (model, entries) in EnumerateDayEntries(costBiz, "days", today))
            foreach (var (type, amount) in entries)
                if (type != "REQUEST") For(model).TodayCost += (decimal)amount;

        var usage = new UsageInfo { ByModel = models.Values.ToList() };
        foreach (var m in usage.ByModel)
        {
            usage.MonthTokens += m.MonthTokens;
            usage.MonthRequests += m.MonthRequests;
            usage.MonthCost += m.MonthCost;
            usage.TodayTokens += m.TodayTokens;
            usage.TodayCost += m.TodayCost;
        }
        usage.CacheHitRate = hitAll + missAll > 0 ? hitAll * 100.0 / (hitAll + missAll) : null;
        return usage;
    }

    // data.biz_data：amount 为对象，cost 为数组（取 [0]）；防御两种形态互换
    private static JsonElement? GetBiz(JsonElement root)
    {
        if (root.GetPropertyOrDefault("data") is not { ValueKind: JsonValueKind.Object } data)
            return null;
        var biz = data.GetPropertyOrDefault("biz_data");
        if (biz is { ValueKind: JsonValueKind.Array } arr)
            return arr.GetArrayLength() > 0 ? arr[0] : null;
        if (biz is { ValueKind: JsonValueKind.Object })
            return biz;
        return null;
    }

    // 遍历 biz.<section>[]（如 total[]）：每项 {model, usage:[{type, amount}]}
    private static IEnumerable<(string model, List<(string type, double amount)> entries)>
        EnumerateModelEntries(JsonElement? biz, string section)
    {
        if (biz?.GetPropertyOrDefault(section) is not { ValueKind: JsonValueKind.Array } list)
            yield break;
        foreach (var item in list.EnumerateArray())
        {
            var model = item.GetPropertyOrDefault("model")?.GetString() ?? "unknown";
            yield return (model, ParseEntries(item.GetPropertyOrDefault("usage")));
        }
    }

    // 遍历 biz.days[] 中指定日期的条目：{date, data:[{model, usage:[]}]}
    private static IEnumerable<(string model, List<(string type, double amount)> entries)>
        EnumerateDayEntries(JsonElement? biz, string section, string date)
    {
        if (biz?.GetPropertyOrDefault(section) is not { ValueKind: JsonValueKind.Array } days)
            yield break;
        foreach (var day in days.EnumerateArray())
        {
            if (day.GetPropertyOrDefault("date")?.GetString() != date) continue;
            if (day.GetPropertyOrDefault("data") is not { ValueKind: JsonValueKind.Array } list)
                continue;
            foreach (var item in list.EnumerateArray())
            {
                var model = item.GetPropertyOrDefault("model")?.GetString() ?? "unknown";
                yield return (model, ParseEntries(item.GetPropertyOrDefault("usage")));
            }
        }
    }

    // amount 是字符串且可能是浮点串（"123.0"）→ 统一按 double 解析；
    // 接口返回恒为点号小数，必须 InvariantCulture（某些区域把小数点当千分位）
    private static List<(string type, double amount)> ParseEntries(JsonElement? usage)
    {
        var list = new List<(string, double)>();
        if (usage is not { ValueKind: JsonValueKind.Array } arr) return list;
        foreach (var e in arr.EnumerateArray())
        {
            var type = e.GetPropertyOrDefault("type")?.GetString() ?? "";
            double amount = 0;
            var raw = e.GetPropertyOrDefault("amount");
            if (raw is { ValueKind: JsonValueKind.String } s)
                double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out amount);
            else if (raw is { ValueKind: JsonValueKind.Number } n)
                n.TryGetDouble(out amount);
            list.Add((type, amount));
        }
        return list;
    }

    // 金额字符串（"100.00"）→ decimal，容忍数字型；同上 InvariantCulture
    private static decimal ParseMoney(JsonElement? e)
    {
        if (e is { ValueKind: JsonValueKind.String } s
            && decimal.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var dn))
            return dn;
        return 0m;
    }

    public static string FriendlyName(string model) => model switch
    {
        "deepseek-v4-pro" => "V4 Pro",
        "deepseek-v4-flash" => "V4 Flash",
        "deepseek-chat" => "Chat",
        "deepseek-reasoner" => "Reasoner",
        _ => model,
    };

    // ---------- 缓存（原子写入：tmp + rename；失败时界面显示旧数据） ----------

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var r = JsonSerializer.Deserialize<DeepseekResult>(File.ReadAllText(CachePath));
            if (r != null)
            {
                // 缓存里的历史错误状态不带回界面（数据照显示，但不误报"更新失败"）
                r.BalanceError = null;
                r.UsageError = null;
                Last = r;
            }
        }
        catch { }
    }

    private static void SaveCache(DeepseekResult r)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(r));
            File.Move(tmp, CachePath, true);
        }
        catch { }
    }

    public void Dispose() => _timer.Dispose();
}

internal static class JsonExt
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;
}
