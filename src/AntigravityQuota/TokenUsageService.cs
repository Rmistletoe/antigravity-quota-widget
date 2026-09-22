using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    /// <summary>
    /// 从 Antigravity LanguageServer 增量采集 token 用量并写入本地账本。
    /// 数据源：GetCascadeTrajectorySteps（逐步骤 modelUsage）+
    ///         GetAllCascadeTrajectories（枚举会话）+
    ///         GetAvailableModels（占位符 → 模型真名）。
    /// </summary>
    public class TokenUsageService
    {
        private const string MethodAllTrajectories = "GetAllCascadeTrajectories";
        private const string MethodSteps = "GetCascadeTrajectorySteps";
        private const string MethodAvailableModels = "GetAvailableModels";

        /// <summary>
        /// 单个会话单次回填的步骤上限，防止超长会话一次拉取过大响应打爆内存。
        /// 超出时优先回填最近的片段（最近的数据更重要），并在 LastError 里提示。
        /// </summary>
        private const int MaxStepsPerCascade = 25000;

        private const string PlaceholderPrefix = "MODEL_PLACEHOLDER_";
        private const int ModelMapMaxAgeHours = 24;

        /// <summary>
        /// 映射表缓存格式版本。改动名称生成逻辑（如新增 slug 美化）时必须 +1，
        /// 否则旧缓存会在 24 小时内继续生效，面板上就会看到过时的名字。
        /// </summary>
        private const int ModelMapVersion = 2;

        private readonly Dictionary<string, string> _modelLabels = new(StringComparer.Ordinal);
        private readonly object _mapLock = new();
        private readonly string _modelMapPath;
        private DateTime _modelMapFetchedAt = DateTime.MinValue;

        /// <summary>最近一次扫描的提示信息（错误或被截断的说明），无异常时为空</summary>
        public string LastError { get; private set; } = "";

        public TokenUsageService()
        {
            _modelMapPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AntigravityQuota",
                "modelmap.json");
        }

        // ---------------------------------------------------------------
        // 模型真名映射
        // ---------------------------------------------------------------

        public void LoadModelMapCache()
        {
            try
            {
                if (!File.Exists(_modelMapPath)) return;
                var cache = JsonSerializer.Deserialize<ModelMapCache>(
                    File.ReadAllText(_modelMapPath), RpcDiscovery.JsonOpts);
                if (cache?.Map == null || cache.Map.Count == 0) return;

                // 旧格式缓存直接丢弃，等下一次扫描重建（_modelMapFetchedAt 保持 MinValue）
                if (cache.Version != ModelMapVersion) return;

                lock (_mapLock)
                {
                    _modelLabels.Clear();
                    foreach (var kv in cache.Map) _modelLabels[kv.Key] = kv.Value;
                    _modelMapFetchedAt = cache.FetchedAt;
                }
            }
            catch { }
        }

        private void SaveModelMap()
        {
            try
            {
                var cache = new ModelMapCache
                {
                    Version = ModelMapVersion,
                    FetchedAt = _modelMapFetchedAt
                };
                lock (_mapLock)
                {
                    foreach (var kv in _modelLabels) cache.Map[kv.Key] = kv.Value;
                }
                string dir = Path.GetDirectoryName(_modelMapPath) ?? "";
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_modelMapPath,
                    JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>把 MODEL_PLACEHOLDER_Mxxx 翻译成 Gemini 3.8 Flash (High) 这类真名</summary>
        public string GetModelLabel(string placeholder)
        {
            if (string.IsNullOrEmpty(placeholder) || placeholder == TokenLedger.UnknownModel)
                return "未标注模型";

            lock (_mapLock)
            {
                if (_modelLabels.TryGetValue(placeholder, out var label) && !string.IsNullOrEmpty(label))
                    return label;
            }

            // 映射表未命中：去掉占位符前缀，至少比原始 ID 可读
            return placeholder.StartsWith(PlaceholderPrefix, StringComparison.Ordinal)
                ? placeholder.Substring(PlaceholderPrefix.Length)
                : placeholder;
        }

        private async Task RefreshModelMapAsync(CancellationToken ct)
        {
            var resp = await CallAsync<AvailableModelsDto>(
                MethodAvailableModels, BuildMetaBody, RpcDiscovery.Http, ct);
            var models = resp?.Response?.Models;
            if (models == null || models.Count == 0) return;

            lock (_mapLock)
            {
                _modelLabels.Clear();
                foreach (var kv in models)
                {
                    var item = kv.Value;
                    if (string.IsNullOrEmpty(item?.Model)) continue;
                    // 有 displayName 用 displayName；没有（如 gemini-3.8-flash-tiered）
                    // 就把 slug 美化成 "Gemini 3.8 Flash Tiered"，别让面板上出现裸 ID
                    _modelLabels[item.Model!] = !string.IsNullOrEmpty(item.DisplayName)
                        ? item.DisplayName!
                        : PrettifySlug(kv.Key);
                }
                _modelMapFetchedAt = DateTime.Now;
            }
            SaveModelMap();
        }

        /// <summary>把 gemini-3.8-flash-tiered 这类 slug 转成可读名称</summary>
        private static string PrettifySlug(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return "未知模型";

            var parts = slug.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                parts[i] = p.Length <= 2
                    ? p.ToUpperInvariant()
                    : char.ToUpperInvariant(p[0]) + p.Substring(1);
            }
            return string.Join(' ', parts);
        }

        // ---------------------------------------------------------------
        // 扫描
        // ---------------------------------------------------------------

        /// <summary>
        /// 扫描全部会话并增量入账。
        /// </summary>
        /// <param name="fullBackfill">true = 忽略账本进度全量回填（仅首次运行 / 手动重建时使用）</param>
        /// <returns>新增入账条数；完全无法连接时返回 -1</returns>
        public async Task<int> ScanAsync(TokenLedger ledger, bool fullBackfill, CancellationToken ct = default)
        {
            LastError = "";

            var all = await CallAsync<AllTrajectoriesDto>(
                MethodAllTrajectories, BuildMetaBody, RpcDiscovery.Http, ct);

            if (all?.TrajectorySummaries == null)
            {
                LastError = "无法读取会话列表（Antigravity 可能未运行）";
                return -1;
            }

            // 映射表缺失或过期则刷新（失败不影响采集）
            if (_modelLabels.Count == 0 ||
                (DateTime.Now - _modelMapFetchedAt).TotalHours >= ModelMapMaxAgeHours)
            {
                await RefreshModelMapAsync(ct);
            }

            int totalAdded = 0;
            int truncated = 0;

            foreach (var kv in all.TrajectorySummaries)
            {
                ct.ThrowIfCancellationRequested();

                string cascadeId = kv.Key;
                int stepCount = kv.Value.StepCount;
                if (string.IsNullOrEmpty(cascadeId) || stepCount <= 0) continue;

                int offset = fullBackfill ? 0 : ledger.GetLastStepIndex(cascadeId) + 1;
                if (offset >= stepCount) continue;                 // 该会话无新增

                if (stepCount - offset > MaxStepsPerCascade)
                {
                    offset = stepCount - MaxStepsPerCascade;       // 优先保住最近的数据
                    truncated++;
                }

                var resp = await CallAsync<StepsResponseDto>(
                    MethodSteps, t => BuildStepsBody(t, cascadeId, offset), RpcDiscovery.BulkHttp, ct);
                if (resp?.Steps == null) continue;

                var batch = new List<TokenUsageRecord>();
                for (int i = 0; i < resp.Steps.Count; i++)
                {
                    var rec = ExtractRecord(resp.Steps[i], cascadeId, offset + i);
                    if (rec != null) batch.Add(rec);
                }

                ResolveUnknownModels(batch, ledger);

                if (batch.Count > 0) totalAdded += ledger.Append(batch);
            }

            if (truncated > 0)
                LastError = $"有 {truncated} 个超长会话超出单次回填上限，仅记录了最近 {MaxStepsPerCascade} 步";

            return totalAdded;
        }

        /// <summary>从一条 step 中抽取用量记录；无用量返回 null</summary>
        private static TokenUsageRecord? ExtractRecord(StepDto step, string cascadeId, int fallbackIndex)
        {
            var md = step.Metadata;
            if (md == null) return null;

            // 用量通常位于 modelUsage；checkpoint 等类型仅存在于 retryInfos[].usage
            var usage = md.ModelUsage;
            if (!HasTokens(usage) && md.RetryInfos != null)
            {
                usage = md.RetryInfos.Select(r => r.Usage).FirstOrDefault(HasTokens);
            }
            if (!HasTokens(usage)) return null;

            var localTime = ParseLocalTime(md.CreatedAt);
            if (localTime == DateTime.MinValue) return null;

            int stepIndex = md.SourceTrajectoryStepInfo?.StepIndex ?? fallbackIndex;

            string model = usage!.Model ?? "";
            if (string.IsNullOrEmpty(model)) model = md.GeneratorModel ?? "";
            if (string.IsNullOrEmpty(model)) model = TokenLedger.UnknownModel;

            return new TokenUsageRecord
            {
                Ts = localTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
                LocalTime = localTime,
                CascadeId = cascadeId,
                StepIndex = stepIndex,
                Model = model,
                ExecutionId = md.ExecutionId ?? "",
                Input = ParseLong(usage.InputTokens),
                Output = ParseLong(usage.OutputTokens),
                Thinking = ParseLong(usage.ThinkingOutputTokens),
                Cache = ParseLong(usage.CacheReadTokens)
            };
        }

        /// <summary>
        /// 回填不声明模型的系统步骤（如 checkpoint 的 usage 里没有 model 字段）。
        /// 优先用同一 executionId 内其它步骤的模型（同一次用户提交通常同模型），
        /// 其次退回该会话最近一次已知模型。
        /// </summary>
        private static void ResolveUnknownModels(List<TokenUsageRecord> batch, TokenLedger ledger)
        {
            var modelByExecution = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in batch)
            {
                if (IsKnownModel(r.Model) && !string.IsNullOrEmpty(r.ExecutionId))
                    modelByExecution[r.ExecutionId] = r.Model;
            }

            foreach (var r in batch)
            {
                if (IsKnownModel(r.Model)) continue;

                if (!string.IsNullOrEmpty(r.ExecutionId) &&
                    modelByExecution.TryGetValue(r.ExecutionId, out var byExec))
                {
                    r.Model = byExec;
                    continue;
                }

                string hint = ledger.GetLastKnownModel(r.CascadeId);
                if (!string.IsNullOrEmpty(hint)) r.Model = hint;
            }
        }

        private static bool IsKnownModel(string? model)
            => !string.IsNullOrEmpty(model) && model != TokenLedger.UnknownModel;

        private static bool HasTokens(UsageDto? u)
            => u != null && (ParseLong(u.InputTokens) > 0 || ParseLong(u.OutputTokens) > 0);

        private static long ParseLong(string? s)
            => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;

        /// <summary>
        /// 解析 UTC 时间戳并转本地时区。注意服务器给的是**纳秒**精度（9 位小数），
        /// 需先截断为 7 位，否则解析器可能拒绝。
        /// </summary>
        private static DateTime ParseLocalTime(string? iso)
        {
            if (string.IsNullOrEmpty(iso)) return DateTime.MinValue;
            try
            {
                var m = Regex.Match(iso, @"^(.*\.\d{7})\d*(.*)$");
                string normalized = m.Success ? m.Groups[1].Value + m.Groups[2].Value : iso;

                if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                {
                    return utc.ToLocalTime();
                }
            }
            catch { }
            return DateTime.MinValue;
        }

        // ---------------------------------------------------------------
        // 请求构造与端点容错调用
        // ---------------------------------------------------------------

        private static string BuildMetaBody(string token) => RpcDiscovery.BuildMetadataJson(token);

        private static string BuildStepsBody(string token, string cascadeId, int offset)
            => $"{{\"metadata\":{{\"csrf_token\":\"{token}\",\"ide_name\":\"antigravity\"}}," +
               $"\"cascadeId\":\"{cascadeId}\",\"stepOffset\":{offset.ToString(CultureInfo.InvariantCulture)}}}";

        /// <summary>
        /// 带端点自愈的调用：先用缓存端点，失败则重新发现进程并逐个端口重试。
        /// </summary>
        private static async Task<T?> CallAsync<T>(
            string method, Func<string, string> bodyBuilder, HttpClient http,
            CancellationToken ct) where T : class
        {
            if (RpcDiscovery.TryGetCachedEndpoint(out int port, out string token))
            {
                var fast = await RpcDiscovery.PostAsync<T>(
                    http, port, token, method, bodyBuilder(token), ct).ConfigureAwait(false);
                if (fast != null) return fast;
                RpcDiscovery.Invalidate();
            }

            if (!RpcDiscovery.TryFindProcess(out int pid, out string freshToken)) return null;

            foreach (var candidate in RpcDiscovery.GetListeningPortsForPid(pid))
            {
                var result = await RpcDiscovery.PostAsync<T>(
                    http, candidate, freshToken, method, bodyBuilder(freshToken), ct).ConfigureAwait(false);
                if (result != null)
                {
                    RpcDiscovery.CacheEndpoint(pid, candidate, freshToken);
                    return result;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------
        // RPC 响应 DTO（只声明需要的字段，未声明的由解析器跳过，避免为无用负载分配内存）
        // ---------------------------------------------------------------

        private class ModelMapCache
        {
            public int Version { get; set; }
            public DateTime FetchedAt { get; set; } = DateTime.MinValue;
            public Dictionary<string, string> Map { get; set; } = new();
        }

        private class AllTrajectoriesDto
        {
            public Dictionary<string, TrajectorySummaryDto>? TrajectorySummaries { get; set; }
        }

        private class TrajectorySummaryDto
        {
            public int StepCount { get; set; }
            public string? TrajectoryId { get; set; }
        }

        private class StepsResponseDto
        {
            public List<StepDto>? Steps { get; set; }
        }

        private class StepDto
        {
            public StepMetadataDto? Metadata { get; set; }
        }

        private class StepMetadataDto
        {
            public string? CreatedAt { get; set; }
            public string? GeneratorModel { get; set; }
            public string? ExecutionId { get; set; }
            public UsageDto? ModelUsage { get; set; }
            public List<RetryInfoDto>? RetryInfos { get; set; }
            public SourceTrajectoryStepInfoDto? SourceTrajectoryStepInfo { get; set; }
        }

        private class RetryInfoDto
        {
            public UsageDto? Usage { get; set; }
        }

        private class SourceTrajectoryStepInfoDto
        {
            public int? StepIndex { get; set; }
            public string? CascadeId { get; set; }
        }

        private class UsageDto
        {
            public string? Model { get; set; }
            public string? InputTokens { get; set; }
            public string? OutputTokens { get; set; }
            public string? ThinkingOutputTokens { get; set; }
            public string? CacheReadTokens { get; set; }
        }

        private class AvailableModelsDto
        {
            public AvailableModelsResponseDto? Response { get; set; }
        }

        private class AvailableModelsResponseDto
        {
            public Dictionary<string, AvailableModelDto>? Models { get; set; }
        }

        private class AvailableModelDto
        {
            public string? DisplayName { get; set; }
            public string? Model { get; set; }
        }
    }
}
