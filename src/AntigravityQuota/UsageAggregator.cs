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

    public class ConversationContextItem
    {
        public string CascadeId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Model { get; set; } = "";
        public string ModelLabel { get; set; } = "";
        public DateTime LastActive { get; set; }
        public int StepIndex { get; set; }
        public int TotalSteps { get; set; }
        public long CurrentContextTokens { get; set; }
        public long LatestInputTokens { get; set; }
        public long LatestCacheTokens { get; set; }
        public long LatestOutputTokens { get; set; }
        public long LatestThinkingTokens { get; set; }
        public long TotalSessionTokens { get; set; }
        public long ContextWindowLimit { get; set; }
        public double ContextPercentage { get; set; }
        public bool IsActive { get; set; }
    }

    public static class ConversationTitleHelper
    {
        private static readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
        private static readonly object _lock = new();

        public static string GetTitle(string cascadeId)
        {
            if (string.IsNullOrEmpty(cascadeId)) return "未命名会话";
            lock (_lock)
            {
                if (_cache.TryGetValue(cascadeId, out var cached)) return cached;
            }

            string title = "";
            try
            {
                string brainDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gemini", "antigravity", "brain", cascadeId);
                string transcriptFile = System.IO.Path.Combine(brainDir, ".system_generated", "logs", "transcript.jsonl");
                if (System.IO.File.Exists(transcriptFile))
                {
                    using var fs = new System.IO.FileStream(transcriptFile, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                    using var reader = new System.IO.StreamReader(fs, System.Text.Encoding.UTF8);
                    string? line;
                    int count = 0;
                    while ((line = reader.ReadLine()) != null && count++ < 15)
                    {
                        if (line.Contains("\"type\":\"USER_INPUT\""))
                        {
                            try
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(line);
                                if (doc.RootElement.TryGetProperty("content", out var contentProp))
                                {
                                    string content = contentProp.GetString() ?? "";
                                    var m = System.Text.RegularExpressions.Regex.Match(
                                        content, @"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>",
                                        System.Text.RegularExpressions.RegexOptions.Singleline);
                                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                                    {
                                        title = m.Groups[1].Value.Trim();
                                        int nl = title.IndexOfAny(new[] { '\r', '\n' });
                                        if (nl > 0) title = title.Substring(0, nl).Trim();
                                        if (title.Length > 50) title = title.Substring(0, 50) + "...";
                                        break;
                                    }
                                    string plain = content.Trim();
                                    int nlPlain = plain.IndexOfAny(new[] { '\r', '\n' });
                                    if (nlPlain > 0) plain = plain.Substring(0, nlPlain).Trim();
                                    if (!plain.StartsWith("<") && !string.IsNullOrWhiteSpace(plain))
                                    {
                                        title = plain.Length > 50 ? plain.Substring(0, 50) + "..." : plain;
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = cascadeId.Length > 8 ? ("会话 " + cascadeId.Substring(0, 8)) : cascadeId;
            }

            lock (_lock)
            {
                _cache[cascadeId] = title;
            }
            return title;
        }

        public static long GetContextLimit(string modelLabel)
        {
            string s = modelLabel.ToLowerInvariant();
            if (s.Contains("claude") || s.Contains("sonnet") || s.Contains("opus")) return 200_000;
            if (s.Contains("gpt-4") || s.Contains("gpt-oss")) return 128_000;
            return 2_000_000;
        }
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

        /// <summary>按最后活跃时间排序的会话列表及其实时上下文</summary>
        public List<ConversationContextItem> Conversations { get; set; } = new();

        /// <summary>当前正在交互的最活跃会话</summary>
        public ConversationContextItem? ActiveConversation { get; set; }

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

            // 汇总各会话及其最新上下文
            var byCascade = records.Where(r => !string.IsNullOrEmpty(r.CascadeId)).GroupBy(r => r.CascadeId);
            var convList = new List<ConversationContextItem>();

            foreach (var g in byCascade)
            {
                var latest = g.OrderByDescending(r => r.StepIndex).ThenByDescending(r => r.LocalTime).FirstOrDefault();
                if (latest == null) continue;

                string rawModel = latest.Model;
                string label = modelLabeler(rawModel);
                long limit = ConversationTitleHelper.GetContextLimit(label);
                long currentCtx = latest.Input + latest.Cache;
                double pct = limit > 0 ? (double)currentCtx / limit * 100.0 : 0.0;
                long totalSession = g.Sum(r => r.Total);

                convList.Add(new ConversationContextItem
                {
                    CascadeId = g.Key,
                    Title = ConversationTitleHelper.GetTitle(g.Key),
                    Model = rawModel,
                    ModelLabel = label,
                    LastActive = latest.LocalTime,
                    StepIndex = latest.StepIndex,
                    TotalSteps = g.Count(),
                    CurrentContextTokens = currentCtx,
                    LatestInputTokens = latest.Input,
                    LatestCacheTokens = latest.Cache,
                    LatestOutputTokens = latest.Output,
                    LatestThinkingTokens = latest.Thinking,
                    TotalSessionTokens = totalSession,
                    ContextWindowLimit = limit,
                    ContextPercentage = Math.Min(100.0, Math.Round(pct, 2))
                });
            }

            convList = convList.OrderByDescending(c => c.LastActive).ToList();
            if (convList.Count > 0)
            {
                convList[0].IsActive = true;
                summary.ActiveConversation = convList[0];
            }
            summary.Conversations = convList.Take(25).ToList();

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
