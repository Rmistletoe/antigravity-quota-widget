using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    public class ModelQuota
    {
        public string Label { get; set; } = "";
        public string DisplayLabel { get; set; } = "";
        public double Percentage { get; set; } = 100.0;
        public int ResetSeconds { get; set; } = 0;
        public string ResetTimeStr { get; set; } = "";
    }

    public class QuotaStatus
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public string UserName { get; set; } = "User";
        public string Email { get; set; } = "";
        public string PlanName { get; set; } = "Pro";
        public ModelQuota? PrimaryModel { get; set; }
        public List<ModelQuota> Models { get; set; } = new();
    }

    public class QuotaService
    {
        private static readonly HttpClient _httpClient;
        private static int _cachedPid = 0;
        private static string _cachedToken = "";
        private static int _cachedPort = 0;

        static QuotaService()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(3)
            };
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        private static List<int> GetListeningPortsForPid(int targetPid)
        {
            var ports = new List<int>();
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, 5, 0);

            IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(pTable, ref bufferSize, true, 2, 5, 0) == 0)
                {
                    int numEntries = Marshal.ReadInt32(pTable);
                    IntPtr rowPtr = IntPtr.Add(pTable, 4);

                    for (int i = 0; i < numEntries; i++)
                    {
                        int state = Marshal.ReadInt32(rowPtr, 0);
                        int localPortRaw = Marshal.ReadInt32(rowPtr, 8);
                        int owningPid = Marshal.ReadInt32(rowPtr, 20);

                        if (state == 2 && owningPid == targetPid)
                        {
                            int port = ((localPortRaw & 0xFF) << 8) | ((localPortRaw >> 8) & 0xFF);
                            if (!ports.Contains(port)) ports.Add(port);
                        }
                        rowPtr = IntPtr.Add(rowPtr, 24);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pTable);
            }
            return ports;
        }

        private static bool FindProcessAndToken(out int pid, out string token)
        {
            pid = 0;
            token = "";
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server.exe%'");
                using var results = searcher.Get();

                foreach (ManagementObject obj in results)
                {
                    pid = Convert.ToInt32(obj["ProcessId"]);
                    string cmdline = obj["CommandLine"]?.ToString() ?? "";
                    var m = Regex.Match(cmdline, @"--csrf_token\s+([a-f0-9\-]+)", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        token = m.Groups[1].Value;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public async Task<QuotaStatus> FetchQuotaAsync()
        {
            if (_cachedPid > 0 && !string.IsNullOrEmpty(_cachedToken) && _cachedPort > 0)
            {
                var fastRes = await TryRequestAsync(_cachedPort, _cachedToken);
                if (fastRes != null) return fastRes;
            }

            if (!FindProcessAndToken(out int pid, out string token))
            {
                return new QuotaStatus { Success = false, Error = "未检测到 Antigravity 运行中" };
            }

            _cachedPid = pid;
            _cachedToken = token;

            var ports = GetListeningPortsForPid(pid);
            if (ports.Count == 0)
            {
                return new QuotaStatus { Success = false, Error = "未能获取本地 RPC 监听端口" };
            }

            foreach (var port in ports)
            {
                var res = await TryRequestAsync(port, token);
                if (res != null)
                {
                    _cachedPort = port;
                    return res;
                }
            }

            return new QuotaStatus { Success = false, Error = "无法连接本地 LanguageServer RPC" };
        }

        private async Task<QuotaStatus?> TryRequestAsync(int port, string token)
        {
            try
            {
                string url = $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus";
                var payload = new { metadata = new { csrf_token = token, ide_name = "antigravity" } };
                string jsonBody = JsonSerializer.Serialize(payload);

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Connect-Protocol-Version", "1");
                req.Headers.Add("x-codeium-csrf-token", token);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var response = await _httpClient.SendAsync(req);
                if (!response.IsSuccessStatusCode) return null;

                string respJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("userStatus", out var userStatus)) return null;

                string userName = userStatus.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "User" : "User";
                string email = userStatus.TryGetProperty("email", out var emailProp) ? emailProp.GetString() ?? "" : "";
                string planName = "Pro";
                if (userStatus.TryGetProperty("planStatus", out var planStatus) &&
                    planStatus.TryGetProperty("planInfo", out var planInfo) &&
                    planInfo.TryGetProperty("planName", out var planNameProp))
                {
                    planName = planNameProp.GetString() ?? "Pro";
                }

                var rawList = new List<ModelQuota>();
                DateTime nowUtc = DateTime.UtcNow;

                if (userStatus.TryGetProperty("cascadeModelConfigData", out var cascadeData) &&
                    cascadeData.TryGetProperty("clientModelConfigs", out var clientConfigs))
                {
                    foreach (var m in clientConfigs.EnumerateArray())
                    {
                        string label = m.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(label)) continue;

                        if (m.TryGetProperty("quotaInfo", out var quotaInfo))
                        {
                            double fraction = quotaInfo.TryGetProperty("remainingFraction", out var frac) ? frac.GetDouble() : 1.0;
                            double pct = Math.Round(fraction * 100.0, 1);
                            string resetTimeStr = quotaInfo.TryGetProperty("resetTime", out var rts) ? rts.GetString() ?? "" : "";

                            int resetSecs = 0;
                            if (!string.IsNullOrEmpty(resetTimeStr))
                            {
                                if (DateTime.TryParse(resetTimeStr, out var dt))
                                {
                                    resetSecs = Math.Max(0, (int)(dt.ToUniversalTime() - nowUtc).TotalSeconds);
                                }
                            }

                            string displayLabel = label;
                            if (displayLabel.Contains("3.7 Flash")) displayLabel = "Gemini 3.7 Flash";
                            else if (displayLabel.Contains("3.1 Pro")) displayLabel = "Gemini 3.1 Pro";
                            else if (displayLabel.Contains("Claude Sonnet")) displayLabel = "Claude Sonnet 4.6";
                            else if (displayLabel.Contains("Claude Opus")) displayLabel = "Claude Opus 4.6";
                            else if (displayLabel.Contains("GPT-OSS")) displayLabel = "GPT-OSS 120B";
                            else if (displayLabel.Contains("3.6 Flash")) displayLabel = "Gemini 3.6 Flash";

                            rawList.Add(new ModelQuota
                            {
                                Label = label,
                                DisplayLabel = displayLabel,
                                Percentage = pct,
                                ResetSeconds = resetSecs,
                                ResetTimeStr = resetTimeStr
                            });
                        }
                    }
                }

                var distinctModels = new List<ModelQuota>();
                var seenLabels = new HashSet<string>();

                string[] preferredOrder = new[] { "Gemini 3.7 Flash", "Gemini 3.1 Pro", "Claude Sonnet 4.6", "GPT-OSS 120B", "Claude Opus 4.6", "Gemini 3.6 Flash" };
                
                foreach (var pref in preferredOrder)
                {
                    var match = rawList.FirstOrDefault(m => m.DisplayLabel == pref && m.Label.Contains("(High)")) 
                             ?? rawList.FirstOrDefault(m => m.DisplayLabel == pref);
                    if (match != null && !seenLabels.Contains(match.DisplayLabel))
                    {
                        seenLabels.Add(match.DisplayLabel);
                        distinctModels.Add(match);
                    }
                }

                foreach (var m in rawList)
                {
                    if (!seenLabels.Contains(m.DisplayLabel))
                    {
                        seenLabels.Add(m.DisplayLabel);
                        distinctModels.Add(m);
                    }
                }

                ModelQuota? primary = distinctModels.FirstOrDefault(m => m.DisplayLabel.Contains("3.7 Flash"))
                                   ?? distinctModels.FirstOrDefault(m => m.DisplayLabel.Contains("3.1 Pro"))
                                   ?? distinctModels.FirstOrDefault();

                return new QuotaStatus
                {
                    Success = true,
                    UserName = userName,
                    Email = email,
                    PlanName = planName,
                    PrimaryModel = primary,
                    Models = distinctModels
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
