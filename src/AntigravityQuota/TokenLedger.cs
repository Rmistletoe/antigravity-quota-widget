using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntigravityQuota
{
    /// <summary>
    /// 账本中的一条 token 用量记录（对应一次模型调用）。
    /// 使用短字段名以压缩 3 个月账本体积（约 2 万行 / &lt; 2 MB）。
    /// </summary>
    public class TokenUsageRecord
    {
        /// <summary>本地时间 ISO 字符串（yyyy-MM-ddTHH:mm:ss.fff）</summary>
        [JsonPropertyName("ts")] public string Ts { get; set; } = "";

        /// <summary>所属会话（cascadeId）</summary>
        [JsonPropertyName("cid")] public string CascadeId { get; set; } = "";

        /// <summary>会话内步骤序号，与 cid 组成幂等键</summary>
        [JsonPropertyName("si")] public int StepIndex { get; set; }

        /// <summary>模型占位符，如 MODEL_PLACEHOLDER_M318</summary>
        [JsonPropertyName("mo")] public string Model { get; set; } = "";

        [JsonPropertyName("in")] public long Input { get; set; }
        [JsonPropertyName("out")] public long Output { get; set; }
        [JsonPropertyName("th")] public long Thinking { get; set; }
        [JsonPropertyName("ca")] public long Cache { get; set; }

        private DateTime _localTime = DateTime.MinValue;
        private bool _localTimeResolved;

        /// <summary>
        /// 本地时间。由 Ts 惰性解析，避免调用方忘记赋值（历史 bug：新入账记录
        /// 未设置该字段，导致日/周/月聚合全部归零）。
        /// </summary>
        [JsonIgnore]
        public DateTime LocalTime
        {
            get
            {
                if (!_localTimeResolved)
                {
                    _localTime = TokenLedger.ParseTimestamp(Ts);
                    _localTimeResolved = true;
                }
                return _localTime;
            }
            set
            {
                _localTime = value;
                _localTimeResolved = true;
            }
        }

        [JsonIgnore] public string DedupKey => CascadeId + "#" + StepIndex.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// 所属执行轮次（同一次用户提交内的所有步骤共享一个 executionId）。
        /// 仅用于采集阶段回填缺失的模型名，不落盘。
        /// </summary>
        [JsonIgnore] public string ExecutionId { get; set; } = "";

        /// <summary>有效工作量口径：输入 + 输出（输出已含思考，不重复相加）</summary>
        [JsonIgnore] public long Work => Input + Output;
    }

    /// <summary>
    /// 本地 Token 用量账本：append-only JSONL，按月分文件，滚动保留 N 个月。
    /// 使用 JSONL 而非 SQLite 的理由：无第三方依赖、append 天然崩溃安全、
    /// 数据量极小（3 个月约 2 万行）、删最老月份只需删一个文件。
    /// </summary>
    public class TokenLedger
    {
        private static readonly JsonSerializerOptions LineOpts = new() { WriteIndented = false };
        private const string MonthFormat = "yyyy-MM";

        /// <summary>模型未知时的占位值（系统步骤如 checkpoint 不声明模型）</summary>
        public const string UnknownModel = "unknown";

        private readonly object _lock = new();
        private readonly List<TokenUsageRecord> _records = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _maxStepIndex = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lastModelByCascade = new(StringComparer.Ordinal);

        public string DirectoryPath { get; }

        /// <summary>账本中最早一条记录的时间（用于提示"数据自 X 起"）</summary>
        public DateTime? Earliest { get; private set; }

        public int Count
        {
            get { lock (_lock) return _records.Count; }
        }

        public TokenLedger(string? directory = null)
        {
            DirectoryPath = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AntigravityQuota",
                "usage");
        }

        /// <summary>载入全部账本文件，并清理超出保留期的月份</summary>
        public void Load(int retentionMonths)
        {
            try
            {
                if (!Directory.Exists(DirectoryPath)) Directory.CreateDirectory(DirectoryPath);
                PruneOldMonths(retentionMonths);

                var files = Directory.GetFiles(DirectoryPath, "*.jsonl")
                                     .OrderBy(f => f, StringComparer.Ordinal)
                                     .ToList();

                lock (_lock)
                {
                    _records.Clear();
                    _seen.Clear();
                    _maxStepIndex.Clear();
                    _lastModelByCascade.Clear();

                    foreach (var file in files) LoadFile(file);

                    _records.Sort((a, b) => a.LocalTime.CompareTo(b.LocalTime));
                    Earliest = _records.Count > 0 ? _records[0].LocalTime : null;
                }
            }
            catch { }
        }

        /// <summary>把账本里的本地时间字符串解析回 DateTime，失败返回 MinValue</summary>
        internal static DateTime ParseTimestamp(string? ts)
        {
            if (string.IsNullOrEmpty(ts)) return DateTime.MinValue;
            try
            {
                return DateTime.TryParse(ts, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var t) ? t : DateTime.MinValue;
            }
            catch { return DateTime.MinValue; }
        }

        private void LoadFile(string path)
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    TokenUsageRecord? r;
                    try { r = JsonSerializer.Deserialize<TokenUsageRecord>(line, LineOpts); }
                    catch { continue; }   // 容忍损坏行（例如写入中途断电的最后一行）
                    if (r == null || string.IsNullOrEmpty(r.CascadeId)) continue;

                    if (!_seen.Add(r.DedupKey)) continue;
                    _records.Add(r);
                    if (!_maxStepIndex.TryGetValue(r.CascadeId, out int mx) || r.StepIndex > mx)
                        _maxStepIndex[r.CascadeId] = r.StepIndex;
                    TrackModel(r);
                }
            }
            catch { }
        }

        /// <summary>某会话已入账的最大 stepIndex；未入账过返回 -1</summary>
        public int GetLastStepIndex(string cascadeId)
        {
            lock (_lock)
            {
                return _maxStepIndex.TryGetValue(cascadeId, out int v) ? v : -1;
            }
        }

        /// <summary>
        /// 该会话最近一次已知的模型占位符。用于回填不声明模型的系统步骤
        /// （例如 checkpoint 步骤的 usage 里没有 model 字段）。
        /// </summary>
        public string GetLastKnownModel(string cascadeId)
        {
            lock (_lock)
            {
                return _lastModelByCascade.TryGetValue(cascadeId, out var m) ? m : "";
            }
        }

        private void TrackModel(TokenUsageRecord r)
        {
            if (string.IsNullOrEmpty(r.Model) || r.Model == UnknownModel) return;
            _lastModelByCascade[r.CascadeId] = r.Model;
        }

        /// <summary>去重后追加记录，返回实际新增条数（幂等：重复的 cid+si 会被丢弃）</summary>
        public int Append(IEnumerable<TokenUsageRecord> items)
        {
            var accepted = new List<TokenUsageRecord>();

            lock (_lock)
            {
                foreach (var r in items)
                {
                    if (string.IsNullOrEmpty(r.CascadeId)) continue;
                    if (!_seen.Add(r.DedupKey)) continue;

                    accepted.Add(r);
                    _records.Add(r);

                    if (!_maxStepIndex.TryGetValue(r.CascadeId, out int mx) || r.StepIndex > mx)
                        _maxStepIndex[r.CascadeId] = r.StepIndex;

                    TrackModel(r);

                    if (Earliest == null || (r.LocalTime != DateTime.MinValue && r.LocalTime < Earliest))
                        Earliest = r.LocalTime;
                }
            }

            if (accepted.Count > 0) WriteRecords(accepted);
            return accepted.Count;
        }

        private void WriteRecords(List<TokenUsageRecord> recs)
        {
            try
            {
                // 先按月份分桶。注意必须用 LocalTime（惰性解析 Ts），
                // 否则解析失败会写出 0001-01.jsonl 这种会被清理逻辑误删的文件。
                var byMonth = new Dictionary<string, List<TokenUsageRecord>>(StringComparer.Ordinal);

                foreach (var r in recs)
                {
                    var lt = r.LocalTime;
                    if (lt == DateTime.MinValue) lt = DateTime.Now;   // 兜底：时间戳异常也不写进公元 1 年
                    string key = lt.ToString(MonthFormat, CultureInfo.InvariantCulture);

                    if (!byMonth.TryGetValue(key, out var list))
                    {
                        list = new List<TokenUsageRecord>();
                        byMonth[key] = list;
                    }
                    list.Add(r);
                }

                foreach (var group in byMonth)
                {
                    string path = Path.Combine(DirectoryPath, group.Key + ".jsonl");
                    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var sw = new StreamWriter(fs, new UTF8Encoding(false));
                    foreach (var r in group.Value)
                    {
                        sw.WriteLine(JsonSerializer.Serialize(r, LineOpts));
                    }
                    sw.Flush();
                }
            }
            catch { }
        }

        /// <summary>删除超出保留期的月份文件</summary>
        public void PruneOldMonths(int retentionMonths)
        {
            try
            {
                if (retentionMonths < 1) retentionMonths = 1;
                var thisMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
                var cutoff = thisMonth.AddMonths(-(retentionMonths - 1));

                foreach (var file in Directory.GetFiles(DirectoryPath, "*.jsonl"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!DateTime.TryParseExact(name, MonthFormat, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var month)) continue;

                    // 护栏：年份明显不合理的文件名（例如历史 bug 造成的 0001-01.jsonl）
                    // 一律不删，避免把有效数据整月误删。
                    if (month.Year < 2000) continue;

                    if (month < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>当前记录快照（已按时间升序）</summary>
        public List<TokenUsageRecord> Snapshot()
        {
            lock (_lock) return new List<TokenUsageRecord>(_records);
        }

        /// <summary>导出为 CSV 文本（带 BOM 便于 Excel 直接打开）</summary>
        public string BuildCsv(Func<string, string> modelLabeler)
        {
            var sb = new StringBuilder();
            sb.Append("时间,会话ID,步骤,模型,输入,输出,其中思考,缓存读取\n");
            foreach (var r in Snapshot())
            {
                string cid = r.CascadeId.Length >= 8 ? r.CascadeId.Substring(0, 8) : r.CascadeId;
                sb.Append(r.LocalTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
                  .Append(cid).Append(',')
                  .Append(r.StepIndex.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append('"').Append(modelLabeler(r.Model).Replace('"', '\'')).Append('"').Append(',')
                  .Append(r.Input.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Output.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Thinking.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.Cache.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
