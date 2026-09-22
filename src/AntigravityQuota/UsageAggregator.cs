using System;
using System.Collections.Generic;
using System.Linq;

namespace AntigravityQuota
{
    public enum UsageRange
    {
        Day,
        Week,
        Month
    }

    /// <summary>一个时间范围内的 token 汇总</summary>
    public class UsageTotals
    {
        public long Input;
        public long Output;
        public long Thinking;
        public long Cache;
        public int Calls;

        /// <summary>有效工作量口径：输入 + 输出（输出本身已包含思考，不重复相加）</summary>
        public long Work => Input + Output;

        /// <summary>
        /// 总 Token 口径：输入 + 输出 + 缓存读取。
        /// 缓存读取属于「缓存命中」，也是模型实际处理的 token，计入总量。
        /// 明细仍分列展示，便于单独判断成本。
        /// </summary>
        public long Total => Input + Output + Cache;

        public void Add(TokenUsageRecord r)
        {
            Input += r.Input;
            Output += r.Output;
            Thinking += r.Thinking;
            Cache += r.Cache;
            Calls++;
        }

        public void Add(UsageTotals other)
        {
            Input += other.Input;
            Output += other.Output;
            Thinking += other.Thinking;
            Cache += other.Cache;
            Calls += other.Calls;
        }
    }

    public class ModelUsageItem
    {
        public string Label { get; set; } = "";
        public long Input { get; set; }
        public long Output { get; set; }
        public long Thinking { get; set; }
        public long Cache { get; set; }
        public int Calls { get; set; }

        public long Work => Input + Output;

        /// <summary>输入 + 输出 + 缓存读取（与汇总口径一致）</summary>
        public long Total => Input + Output + Cache;
    }

    /// <summary>某一天的用量（供趋势图使用）</summary>
    public class DailyUsage
    {
        public DateTime Date { get; set; }
        public UsageTotals Totals { get; } = new();
    }

    public class UsageRangeStats
    {
        public UsageTotals Totals { get; } = new();
        public DateTime Start { get; set; }
        public List<ModelUsageItem> TopModels { get; set; } = new();
    }

    public class UsageSummary
    {
        public UsageRangeStats Day { get; } = new();
        public UsageRangeStats Week { get; } = new();
        public UsageRangeStats Month { get; } = new();

        /// <summary>账本中最早一条记录的时间（用于提示"数据自 X 起"）</summary>
        public DateTime? Earliest { get; set; }

        public int RecordCount { get; set; }

        /// <summary>按日期升序的全量每日汇总（供趋势图/热力图）</summary>
        public List<DailyUsage> Daily { get; } = new();

        public UsageRangeStats For(UsageRange range) => range switch
        {
            UsageRange.Week => Week,
            UsageRange.Month => Month,
            _ => Day
        };
    }

    public static class UsageAggregator
    {
        private const int DefaultTopModelCount = 3;

        /// <summary>
        /// 按「今日 / 本周（周一起）/ 本月」三档汇总账本记录，并额外产出全量每日序列。
        /// 三档独立判断区间，因为本周起点可能落在上个月内。
        /// </summary>
        public static UsageSummary Build(
            IReadOnlyList<TokenUsageRecord> records,
            Func<string, string> modelLabeler,
            DateTime now,
            int topModelCount = DefaultTopModelCount)
        {
            var summary = new UsageSummary();

            var dayStart = now.Date;
            var weekStart = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7)); // 周一为一周之始
            var monthStart = new DateTime(dayStart.Year, dayStart.Month, 1);

            summary.Day.Start = dayStart;
            summary.Week.Start = weekStart;
            summary.Month.Start = monthStart;
            summary.RecordCount = records.Count;

            var dayModels = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
            var weekModels = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
            var monthModels = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
            var perDay = new Dictionary<DateTime, UsageTotals>();

            DateTime? earliest = null;

            foreach (var r in records)
            {
                var t = r.LocalTime;
                if (t == DateTime.MinValue) continue;
                if (earliest == null || t < earliest) earliest = t;

                if (t >= monthStart)
                {
                    summary.Month.Totals.Add(r);
                    Accumulate(monthModels, r);
                }
                if (t >= weekStart)
                {
                    summary.Week.Totals.Add(r);
                    Accumulate(weekModels, r);
                }
                if (t >= dayStart)
                {
                    summary.Day.Totals.Add(r);
                    Accumulate(dayModels, r);
                }

                var date = t.Date;
                if (!perDay.TryGetValue(date, out var dayTotals))
                {
                    dayTotals = new UsageTotals();
                    perDay[date] = dayTotals;
                }
                dayTotals.Add(r);
            }

            summary.Earliest = earliest;
            summary.Day.TopModels = BuildTopModels(dayModels, modelLabeler, topModelCount);
            summary.Week.TopModels = BuildTopModels(weekModels, modelLabeler, topModelCount);
            summary.Month.TopModels = BuildTopModels(monthModels, modelLabeler, topModelCount);

            foreach (var kv in perDay.OrderBy(kv => kv.Key))
            {
                var daily = new DailyUsage { Date = kv.Key };
                daily.Totals.Add(kv.Value);
                summary.Daily.Add(daily);
            }

            return summary;
        }

        private static void Accumulate(Dictionary<string, UsageTotals> bag, TokenUsageRecord r)
        {
            string key = string.IsNullOrEmpty(r.Model) ? TokenLedger.UnknownModel : r.Model;
            if (!bag.TryGetValue(key, out var totals))
            {
                totals = new UsageTotals();
                bag[key] = totals;
            }
            totals.Add(r);
        }

        private static List<ModelUsageItem> BuildTopModels(
            Dictionary<string, UsageTotals> bag, Func<string, string> modelLabeler, int topCount)
        {
            // 先按显示名合并（不同占位符理论上可能映射到同一显示名）
            var merged = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);

            foreach (var kv in bag)
            {
                string label = modelLabeler(kv.Key);
                if (!merged.TryGetValue(label, out var totals))
                {
                    totals = new UsageTotals();
                    merged[label] = totals;
                }
                totals.Add(kv.Value);
            }

            return merged
                .OrderByDescending(kv => kv.Value.Total)
                .Take(topCount < 1 ? DefaultTopModelCount : topCount)
                .Select(kv => new ModelUsageItem
                {
                    Label = kv.Key,
                    Input = kv.Value.Input,
                    Output = kv.Value.Output,
                    Thinking = kv.Value.Thinking,
                    Cache = kv.Value.Cache,
                    Calls = kv.Value.Calls
                })
                .ToList();
        }
    }
}
