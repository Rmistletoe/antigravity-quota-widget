using System;
using System.Collections.Generic;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    /// <summary>
    /// Antigravity LanguageServer 本地 RPC 端点发现与调用工具。
    /// 由 QuotaService（配额）与 TokenUsageService（用量）共用，避免重复扫描进程。
    /// </summary>
    internal static class RpcDiscovery
    {
        /// <summary>Connect-RPC 服务路径前缀</summary>
        public const string ServicePath = "/exa.language_server_pb.LanguageServerService/";

        /// <summary>轻量接口（配额等）用，超时 3 秒</summary>
        public static readonly HttpClient Http;

        /// <summary>批量接口（轨迹 steps，单次可能数十 MB）用，超时 90 秒</summary>
        public static readonly HttpClient BulkHttp;

        /// <summary>反序列化选项：字段名大小写不敏感</summary>
        public static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private static int _cachedPid;
        private static int _cachedPort;
        private static string _cachedToken = "";

        static RpcDiscovery()
        {
            Http = CreateClient(TimeSpan.FromSeconds(3));
            BulkHttp = CreateClient(TimeSpan.FromSeconds(90));
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler
            {
                // Antigravity 本地 RPC 使用自签证书
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };
            return new HttpClient(handler) { Timeout = timeout };
        }

        // ---------------- 端点缓存 ----------------

        public static bool TryGetCachedEndpoint(out int port, out string token)
        {
            port = _cachedPort;
            token = _cachedToken;
            return _cachedPid > 0 && _cachedPort > 0 && !string.IsNullOrEmpty(_cachedToken);
        }

        public static void CacheEndpoint(int pid, int port, string token)
        {
            _cachedPid = pid;
            _cachedPort = port;
            _cachedToken = token;
        }

        public static void Invalidate()
        {
            _cachedPid = 0;
            _cachedPort = 0;
            _cachedToken = "";
        }

        // ---------------- 进程与端口发现 ----------------

        /// <summary>通过 WMI 定位 language_server.exe 并抠出 csrf_token</summary>
        public static bool TryFindProcess(out int pid, out string token)
        {
            pid = 0;
            token = "";
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server.exe%'");
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

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

        /// <summary>枚举指定进程处于 LISTENING 状态的本地端口</summary>
        public static List<int> GetListeningPortsForPid(int targetPid)
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

        // ---------------- 请求构造与发送 ----------------

        public static string BuildUrl(int port, string method)
            => $"https://127.0.0.1:{port}{ServicePath}{method}";

        /// <summary>构造带 metadata 的请求体前缀（已验证的格式，字段名为 snake_case）</summary>
        public static string BuildMetadataJson(string token)
            => $"{{\"metadata\":{{\"csrf_token\":\"{token}\",\"ide_name\":\"antigravity\"}}}}";

        /// <summary>
        /// POST 一个 JSON 请求并把响应流式反序列化为 T。
        /// 失败（非 2xx / 解析异常 / 连接失败）统一返回 null，由调用方决定是否换端口重试。
        /// </summary>
        public static async Task<T?> PostAsync<T>(
            HttpClient http, int port, string token, string method, string jsonBody,
            CancellationToken ct = default) where T : class
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, BuildUrl(port, method));
                req.Headers.Add("Connect-Protocol-Version", "1");
                req.Headers.Add("x-codeium-csrf-token", token);
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;

                // 流式反序列化：避免几十 MB 响应先在内存里变成 string
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }
}
