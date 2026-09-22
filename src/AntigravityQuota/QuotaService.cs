using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    public class QuotaBucket
    {
        public string BucketId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Window { get; set; } = ""; // "5h" or "weekly"
        public double Percentage { get; set; } = 100.0;
        public int ResetSeconds { get; set; } = 0;
        public string ResetTimeStr { get; set; } = "";
    }

    public class QuotaGroup
    {
        public string DisplayName { get; set; } = "";
        public string Description { get; set; } = "";
        public QuotaBucket? FiveHourBucket { get; set; }
        public QuotaBucket? WeeklyBucket { get; set; }
    }

    public class ModelConfigItem
    {
        public string ModelId { get; set; } = "";
        public string Label { get; set; } = "";
        public string TagTitle { get; set; } = "";
        public string TagDescription { get; set; } = "";
        public string Category { get; set; } = "Gemini";
        public double Version { get; set; } = 0.0;
        public bool IsLatest { get; set; } = false;
        public bool IsRecommended { get; set; }
        public bool SupportsImages { get; set; }
    }

    public class QuotaStatus
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string UserName { get; set; } = "User";
        public string Email { get; set; } = "";
        public string PlanName { get; set; } = "Pro";
        public List<QuotaGroup> Groups { get; set; } = new();
        public List<ModelConfigItem> AvailableModels { get; set; } = new();
        public QuotaGroup? GeminiGroup => Groups.FirstOrDefault(g => g.DisplayName.Contains("Gemini"));
        public QuotaGroup? ClaudeGptGroup => Groups.FirstOrDefault(g => g.DisplayName.Contains("Claude") || g.DisplayName.Contains("GPT"));
    }

    public class QuotaService
    {
        // 进程发现、端口枚举与 Http 客户端统一由 RpcDiscovery 提供（与 TokenUsageService 共用）
        private static HttpClient HttpClient => RpcDiscovery.Http;

        public async Task<QuotaStatus> FetchQuotaAsync()
        {
            if (RpcDiscovery.TryGetCachedEndpoint(out int cachedPort, out string cachedToken))
            {
                var fastRes = await TryRequestAsync(cachedPort, cachedToken);
                if (fastRes != null) return fastRes;
                RpcDiscovery.Invalidate();
            }

            if (!RpcDiscovery.TryFindProcess(out int pid, out string token))
            {
                return new QuotaStatus { Success = false, Error = "未检测到 Antigravity 运行中" };
            }

            var ports = RpcDiscovery.GetListeningPortsForPid(pid);
            if (ports.Count == 0)
            {
                return new QuotaStatus { Success = false, Error = "未能获取本地 RPC 监听端口" };
            }

            foreach (var port in ports)
            {
                var res = await TryRequestAsync(port, token);
                if (res != null)
                {
                    RpcDiscovery.CacheEndpoint(pid, port, token);
                    return res;
                }
            }

            return new QuotaStatus { Success = false, Error = "无法连接本地 LanguageServer RPC" };
        }

        private async Task<QuotaStatus?> TryRequestAsync(int port, string token)
        {
            try
            {
                string payloadJson = $"{{\"metadata\":{{\"csrf_token\":\"{token}\",\"ide_name\":\"antigravity\"}}}}";
                
                // 1. 请求完整的五小时 + 本周配额概览接口 (RetrieveUserQuotaSummary)
                string quotaUrl = RpcDiscovery.BuildUrl(port, "RetrieveUserQuotaSummary");
                using var quotaReq = new HttpRequestMessage(HttpMethod.Post, quotaUrl);
                quotaReq.Headers.Add("Connect-Protocol-Version", "1");
                quotaReq.Headers.Add("x-codeium-csrf-token", token);
                quotaReq.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                using var quotaResp = await HttpClient.SendAsync(quotaReq);
                if (!quotaResp.IsSuccessStatusCode) return null;

                string quotaJson = await quotaResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(quotaJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("response", out var respObj)) return null;

                DateTime nowUtc = DateTime.UtcNow;
                var groupList = new List<QuotaGroup>();

                if (respObj.TryGetProperty("groups", out var groupsArr))
                {
                    foreach (var g in groupsArr.EnumerateArray())
                    {
                        string gName = g.TryGetProperty("displayName", out var gn) ? gn.GetString() ?? "" : "";
                        string gDesc = g.TryGetProperty("description", out var gd) ? gd.GetString() ?? "" : "";

                        var group = new QuotaGroup
                        {
                            DisplayName = gName,
                            Description = gDesc
                        };

                        if (g.TryGetProperty("buckets", out var bucketsArr))
                        {
                            foreach (var b in bucketsArr.EnumerateArray())
                            {
                                string bId = b.TryGetProperty("bucketId", out var bid) ? bid.GetString() ?? "" : "";
                                string bName = b.TryGetProperty("displayName", out var bn) ? bn.GetString() ?? "" : "";
                                string window = b.TryGetProperty("window", out var win) ? win.GetString() ?? "" : "";
                                double fraction = b.TryGetProperty("remainingFraction", out var frac) ? frac.GetDouble() : 1.0;
                                double pct = Math.Round(fraction * 100.0, 1);
                                string resetTimeStr = b.TryGetProperty("resetTime", out var rts) ? rts.GetString() ?? "" : "";

                                int resetSecs = 0;
                                if (!string.IsNullOrEmpty(resetTimeStr) && DateTime.TryParse(resetTimeStr, out var dt))
                                {
                                    resetSecs = Math.Max(0, (int)(dt.ToUniversalTime() - nowUtc).TotalSeconds);
                                }

                                var bucket = new QuotaBucket
                                {
                                    BucketId = bId,
                                    DisplayName = bName,
                                    Window = window,
                                    Percentage = pct,
                                    ResetSeconds = resetSecs,
                                    ResetTimeStr = resetTimeStr
                                };

                                if (window == "5h") group.FiveHourBucket = bucket;
                                else if (window == "weekly") group.WeeklyBucket = bucket;
                            }
                        }

                        groupList.Add(group);
                    }
                }

                // 2. 获取用户基础信息 (GetUserStatus)
                string userUrl = RpcDiscovery.BuildUrl(port, "GetUserStatus");
                using var userReq = new HttpRequestMessage(HttpMethod.Post, userUrl);
                userReq.Headers.Add("Connect-Protocol-Version", "1");
                userReq.Headers.Add("x-codeium-csrf-token", token);
                userReq.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                string userName = "User";
                string email = "";
                string planName = "Pro";

                try
                {
                    using var userResp = await HttpClient.SendAsync(userReq);
                    if (userResp.IsSuccessStatusCode)
                    {
                        string userJson = await userResp.Content.ReadAsStringAsync();
                        using var uDoc = JsonDocument.Parse(userJson);
                        if (uDoc.RootElement.TryGetProperty("userStatus", out var uStatus))
                        {
                            userName = uStatus.TryGetProperty("name", out var n) ? n.GetString() ?? "User" : "User";
                            email = uStatus.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
                            if (uStatus.TryGetProperty("planStatus", out var pStatus) &&
                                pStatus.TryGetProperty("planInfo", out var pInfo) &&
                                pInfo.TryGetProperty("planName", out var pn))
                            {
                                planName = pn.GetString() ?? "Pro";
                            }
                        }
                    }
                }
                catch { }

                // 3. 获取可用模型列表 (GetCascadeModelConfigData)
                var modelList = new List<ModelConfigItem>();
                try
                {
                    string modelUrl = RpcDiscovery.BuildUrl(port, "GetCascadeModelConfigData");
                    using var modelReq = new HttpRequestMessage(HttpMethod.Post, modelUrl);
                    modelReq.Headers.Add("Connect-Protocol-Version", "1");
                    modelReq.Headers.Add("x-codeium-csrf-token", token);
                    modelReq.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

                    using var modelResp = await HttpClient.SendAsync(modelReq);
                    if (modelResp.IsSuccessStatusCode)
                    {
                        string modelJson = await modelResp.Content.ReadAsStringAsync();
                        using var mDoc = JsonDocument.Parse(modelJson);
                        if (mDoc.RootElement.TryGetProperty("clientModelConfigs", out var configsArr))
                        {
                            foreach (var item in configsArr.EnumerateArray())
                            {
                                string label = item.TryGetProperty("label", out var lbl) ? lbl.GetString() ?? "" : "";
                                string modelId = item.TryGetProperty("modelId", out var mid) ? mid.GetString() ?? "" : "";
                                string tagTitle = item.TryGetProperty("tagTitle", out var tt) ? tt.GetString() ?? "" : "";
                                string tagDesc = item.TryGetProperty("tagDescription", out var td) ? td.GetString() ?? "" : "";
                                bool isRec = item.TryGetProperty("isRecommended", out var ir) && ir.GetBoolean();
                                bool supImg = item.TryGetProperty("supportsImages", out var si) && si.GetBoolean();

                                if (string.IsNullOrEmpty(label)) continue;

                                string cat = (modelId.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                                              modelId.Contains("gpt", StringComparison.OrdinalIgnoreCase) ||
                                              label.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
                                              label.Contains("GPT", StringComparison.OrdinalIgnoreCase))
                                              ? "Claude & GPT" : "Gemini";

                                double ver = ParseModelVersion(label, modelId);

                                modelList.Add(new ModelConfigItem
                                {
                                    ModelId = modelId,
                                    Label = label,
                                    TagTitle = tagTitle,
                                    TagDescription = tagDesc,
                                    Category = cat,
                                    Version = ver,
                                    IsRecommended = isRec,
                                    SupportsImages = supImg
                                });
                            }

                            // 动态识别当前各系列下的最新版本模型 (例如 Gemini 最高为 3.8，Claude 最高为 4.6)
                            double maxGeminiVer = modelList.Where(m => m.Category == "Gemini").Select(m => m.Version).DefaultIfEmpty(0.0).Max();
                            double maxClaudeVer = modelList.Where(m => m.Category != "Gemini").Select(m => m.Version).DefaultIfEmpty(0.0).Max();

                            foreach (var m in modelList)
                            {
                                if (m.Category == "Gemini" && m.Version > 0 && Math.Abs(m.Version - maxGeminiVer) < 0.001)
                                {
                                    m.IsLatest = true;
                                }
                                else if (m.Category != "Gemini" && m.Version > 0 && Math.Abs(m.Version - maxClaudeVer) < 0.001)
                                {
                                    m.IsLatest = true;
                                }
                            }
                        }
                    }
                }
                catch { }

                return new QuotaStatus
                {
                    Success = true,
                    UserName = userName,
                    Email = email,
                    PlanName = planName,
                    Groups = groupList,
                    AvailableModels = modelList
                };
            }
            catch
            {
                return null;
            }
        }

        private static double ParseModelVersion(string label, string modelId)
        {
            var m = Regex.Match(label, @"\b(\d+\.\d+)\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
            {
                return v;
            }
            m = Regex.Match(modelId, @"\b(\d+\.\d+)\b");
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                return v;
            }
            return 0.0;
        }
    }
}
