using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    /// <summary>
    /// 采集服务：后台持续轮询配额 + 扫描 Token 用量，并对外提供一份 JSON 快照。
    /// 完全没有 UI —— 展示交给浏览器里的 web/index.html。
    /// </summary>
    public sealed class AppState
    {
        private const int QuotaIntervalSeconds = 30;

        private readonly TokenLedger _ledger = new();
        private readonly TokenUsageService _usageService = new();
        private readonly QuotaService _quotaService = new();

        private readonly object _lock = new();
        private int _quotaBusy;
        private int _scanBusy;

        public AppConfig Config { get; private set; } = new();
        public QuotaStatus? Status { get; private set; }
        public UsageSummary? Usage { get; private set; }
        public string LastError { get; private set; } = "";
        public DateTime LastQuotaFetchUtc { get; private set; } = DateTime.MinValue;
        public DateTime LastScanUtc { get; private set; } = DateTime.MinValue;
        public bool BackfillCompleted { get; private set; }

        public void Start()
        {
            Config = ConfigManager.Load();
            // 顺手回写一次，把历史遗留的旧配置字段清掉
            ConfigManager.Save();
            BackfillCompleted = Config.UsageBackfillCompleted;

            try
            {
                _usageService.LoadModelMapCache();
                _ledger.Load(Config.UsageRetentionMonths);
                RebuildSummary();
            }
            catch (Exception ex)
            {
                LastError = "载入账本失败: " + ex.Message;
            }

            Task.Run(QuotaLoopAsync);
            Task.Run(UsageLoopAsync);
        }

        // ---------------- 后台循环 ----------------

        private async Task QuotaLoopAsync()
        {
            while (true)
            {
                await FetchQuotaOnceAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(QuotaIntervalSeconds)).ConfigureAwait(false);
            }
        }

        private async Task UsageLoopAsync()
        {
            // 首次启动：全量回填一次历史
            await ScanUsageOnceAsync(forceFull: !BackfillCompleted).ConfigureAwait(false);

            while (true)
            {
                int secs = Math.Max(30, Config.UsageScanIntervalSec);
                await Task.Delay(TimeSpan.FromSeconds(secs)).ConfigureAwait(false);
                await ScanUsageOnceAsync(forceFull: false).ConfigureAwait(false);
            }
        }

        private async Task FetchQuotaOnceAsync()
        {
            if (Interlocked.Exchange(ref _quotaBusy, 1) == 1) return;
            try
            {
                var status = await _quotaService.FetchQuotaAsync().ConfigureAwait(false);
                lock (_lock)
                {
                    Status = status;
                    LastQuotaFetchUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { LastError = "配额获取失败: " + ex.Message; }
            }
            finally { Interlocked.Exchange(ref _quotaBusy, 0); }
        }

        private async Task ScanUsageOnceAsync(bool forceFull)
        {
            if (Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
            try
            {
                int added = await _usageService.ScanAsync(_ledger, forceFull).ConfigureAwait(false);

                if (forceFull && added >= 0)
                {
                    BackfillCompleted = true;
                    Config.UsageBackfillCompleted = true;
                    ConfigManager.Save();
                }

                lock (_lock)
                {
                    LastScanUtc = DateTime.UtcNow;
                    if (added < 0) LastError = _usageService.LastError;
                    else if (!string.IsNullOrEmpty(_usageService.LastError)) LastError = _usageService.LastError;
                }

                RebuildSummary();
            }
            catch (Exception ex)
            {
                lock (_lock) { LastError = "用量扫描失败: " + ex.Message; }
            }
            finally { Interlocked.Exchange(ref _scanBusy, 0); }
        }

        private void RebuildSummary()
        {
            try
            {
                // 面板要展示完整分模型明细，取 Top 12
                var summary = UsageAggregator.Build(
                    _ledger.Snapshot(), _usageService.GetModelLabel, DateTime.Now, 12);
                lock (_lock) { Usage = summary; }
            }
            catch { }
        }

        /// <summary>前端点「刷新」时调用：立即拉一次配额 + 扫一次用量</summary>
        public void RequestRefresh()
        {
            Task.Run(async () =>
            {
                await FetchQuotaOnceAsync().ConfigureAwait(false);
                await ScanUsageOnceAsync(forceFull: false).ConfigureAwait(false);
            });
        }

        /// <summary>导出账本全量明细为 CSV（含 UTF-8 BOM，Excel 直接打开不乱码）</summary>
        public string BuildCsv()
        {
            try
            {
                return "\uFEFF" + _ledger.BuildCsv(_usageService.GetModelLabel);
            }
            catch (Exception ex)
            {
                return "导出失败: " + ex.Message;
            }
        }

        // ---------------- JSON 快照 ----------------

        private static readonly JsonSerializerOptions PayloadJsonOptions = new() { WriteIndented = false };

        public string BuildPayloadJson()
        {
            Dictionary<string, object?> payload;
            lock (_lock)
            {
                var summary = Usage;
                var status = Status;

                payload = new Dictionary<string, object?>
                {
                    ["type"] = "update",
                    ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["user"] = status?.UserName ?? "",
                    ["plan"] = status?.PlanName ?? "",
                    ["connected"] = status?.Success == true,
                    ["error"] = LastError,
                    ["quotaFetchedAt"] = LastQuotaFetchUtc == DateTime.MinValue
                        ? "" : LastQuotaFetchUtc.ToLocalTime().ToString("HH:mm:ss"),
                    ["scanAt"] = LastScanUtc == DateTime.MinValue
                        ? "" : LastScanUtc.ToLocalTime().ToString("HH:mm:ss"),
                    ["backfillCompleted"] = BackfillCompleted,
                    ["groups"] = (status?.Groups ?? new List<QuotaGroup>())
                        .Select(g => new Dictionary<string, object?>
                        {
                            ["name"] = g.DisplayName,
                            ["buckets"] = new[] { g.FiveHourBucket, g.WeeklyBucket }
                                .Where(b => b != null)
                                .Select(b => new Dictionary<string, object?>
                                {
                                    ["label"] = b!.DisplayName,
                                    ["window"] = b!.Window,
                                    ["pct"] = b!.Percentage,
                                    ["resetSeconds"] = b!.ResetSeconds
                                })
                                .ToList()
                        })
                        .ToList(),
                    ["earliest"] = summary?.Earliest?.ToString("yyyy-MM-dd") ?? "",
                    ["recordCount"] = summary?.RecordCount ?? 0,
                    ["retentionMonths"] = Config.UsageRetentionMonths,
                    ["usage"] = summary == null ? null : BuildUsagePayload(summary)
                };
            }

            return JsonSerializer.Serialize(payload, PayloadJsonOptions);
        }

        private static Dictionary<string, object?> BuildUsagePayload(UsageSummary s) => new()
        {
            ["day"] = TotalsToPayload(s.Day.Totals),
            ["week"] = TotalsToPayload(s.Week.Totals),
            ["month"] = TotalsToPayload(s.Month.Totals),
            ["daily"] = s.Daily.Select(d => new Dictionary<string, object?>
            {
                ["date"] = d.Date.ToString("yyyy-MM-dd"),
                ["input"] = d.Totals.Input,
                ["output"] = d.Totals.Output,
                ["thinking"] = d.Totals.Thinking,
                ["cache"] = d.Totals.Cache,
                ["calls"] = d.Totals.Calls,
                ["work"] = d.Totals.Work
            }).ToList(),
            ["models"] = new Dictionary<string, object?>
            {
                ["day"] = ModelsToPayload(s.Day.TopModels),
                ["week"] = ModelsToPayload(s.Week.TopModels),
                ["month"] = ModelsToPayload(s.Month.TopModels)
            }
        };

        private static Dictionary<string, object?> TotalsToPayload(UsageTotals t) => new()
        {
            ["input"] = t.Input,
            ["output"] = t.Output,
            ["thinking"] = t.Thinking,
            ["cache"] = t.Cache,
            ["calls"] = t.Calls,
            ["work"] = t.Work,
            ["total"] = t.Total
        };

        private static List<Dictionary<string, object?>> ModelsToPayload(IEnumerable<ModelUsageItem> items)
            => items.Select(m => new Dictionary<string, object?>
            {
                ["label"] = m.Label,
                ["input"] = m.Input,
                ["output"] = m.Output,
                ["thinking"] = m.Thinking,
                ["cache"] = m.Cache,
                ["calls"] = m.Calls,
                ["work"] = m.Work,
                ["total"] = m.Total
            }).ToList();
    }
}
