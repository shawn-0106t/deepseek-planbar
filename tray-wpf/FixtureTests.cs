namespace DeepseekPlanbarTray;

// --test-fixtures <dir>：解析 tests/fixtures 下的样本响应并逐项断言，全程无需真实凭证。
// fixture 中的 @@TODAY@@ 会在解析前替换为本地今天日期（用于验证"今日"聚合）。
public static class FixtureTests
{
    public static int Run(string dir)
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok) failures++;
        }
        string Today = DateTime.Now.ToString("yyyy-MM-dd");
        string? Load(string file)
        {
            var p = Path.Combine(dir, file);
            if (!File.Exists(p))
            {
                Check($"fixture-exists:{file}", false);
                return null;
            }
            return File.ReadAllText(p).Replace("@@TODAY@@", Today);
        }
        bool Close(double a, double b) => Math.Abs(a - b) < 1e-6;

        // --- 余额 ---
        var balanceJson = Load("balance_ok.json");
        if (balanceJson != null)
        {
            var b = DeepseekService.ParseBalance(balanceJson);
            Check("balance.is_available", b.IsAvailable);
            Check("balance.currency", b.Currency == "CNY");
            Check("balance.total", b.Total == 100.00m);
            Check("balance.granted", b.Granted == 50.00m);
            Check("balance.topped_up", b.ToppedUp == 50.00m);
        }

        // --- 用量：双模型 + 当日聚合 + cost 数组形态 + REQUEST 排除 ---
        var amountJson = Load("amount_ok.json");
        var costJson = Load("cost_ok.json");
        if (amountJson != null && costJson != null)
        {
            var u = DeepseekService.ParseUsage(amountJson, costJson);
            var pro = u.ByModel.FirstOrDefault(m => m.Model == "deepseek-v4-pro");
            var flash = u.ByModel.FirstOrDefault(m => m.Model == "deepseek-v4-flash");
            Check("usage.model-count", u.ByModel.Count == 2);
            Check("usage.pro-exists", pro != null);
            Check("usage.flash-exists", flash != null);
            if (pro != null)
            {
                Check("usage.pro.month-tokens", pro.MonthTokens == 1330000);
                Check("usage.pro.month-requests", pro.MonthRequests == 1200);
                Check("usage.pro.hit-rate", pro.CacheHitRate.HasValue && Close(pro.CacheHitRate.Value, 80.0));
                Check("usage.pro.today-tokens", pro.TodayTokens == 17000);
                Check("usage.pro.month-cost-excludes-request", pro.MonthCost == 2.50m);
                Check("usage.pro.today-cost", pro.TodayCost == 0.30m);
                Check("usage.pro.display-name", pro.DisplayName == "V4 Pro");
            }
            if (flash != null)
            {
                Check("usage.flash.month-tokens", flash.MonthTokens == 1120000);
                Check("usage.flash.hit-rate", flash.CacheHitRate.HasValue && Close(flash.CacheHitRate.Value, 50.0));
                Check("usage.flash.today-tokens", flash.TodayTokens == 9500);
                Check("usage.flash.month-cost", flash.MonthCost == 0.50m);
                Check("usage.flash.display-name", flash.DisplayName == "V4 Flash");
            }
            Check("usage.total.month-tokens", u.MonthTokens == 2450000);
            Check("usage.total.month-requests", u.MonthRequests == 4200);
            Check("usage.total.month-cost", u.MonthCost == 3.00m);
            Check("usage.total.today-tokens", u.TodayTokens == 26500);
            Check("usage.total.today-cost", u.TodayCost == 0.30m);
            Check("usage.total.hit-rate", u.CacheHitRate.HasValue && Close(u.CacheHitRate.Value, 200.0 / 3.0));
        }

        // --- 防御：缺字段不崩溃，给默认值 ---
        var sparseJson = Load("amount_sparse.json");
        if (sparseJson != null && costJson != null)
        {
            try
            {
                var u = DeepseekService.ParseUsage(sparseJson, costJson);
                var pro = u.ByModel.FirstOrDefault(m => m.Model == "deepseek-v4-pro");
                var flash = u.ByModel.FirstOrDefault(m => m.Model == "deepseek-v4-flash");
                Check("sparse.no-crash", true);
                Check("sparse.pro-hit-rate-null", pro is { CacheHitRate: null });
                Check("sparse.flash-missing-type-tokens", flash is { MonthTokens: 42 });
                Check("sparse.today-zero", u.TodayTokens == 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  sparse exception: " + ex.Message);
                Check("sparse.no-crash", false);
            }
        }

        // --- 防御：cost 的 biz_data 退化为对象形态也能解析 ---
        if (amountJson != null)
        {
            try
            {
                var u = DeepseekService.ParseUsage(amountJson, amountJson);
                Check("cost-object-shape.no-crash", u.ByModel.Count == 2);
            }
            catch
            {
                Check("cost-object-shape.no-crash", false);
            }
        }

        // --- 模型显示名 ---
        Check("friendly-name.unknown-passthrough", DeepseekService.FriendlyName("some-other-model") == "some-other-model");

        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
