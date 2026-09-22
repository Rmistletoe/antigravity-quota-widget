using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace AntigravityQuota
{
    /// <summary>
    /// 极简本地 HTTP 服务（基于 TcpListener，不用 http.sys，因此**不需要管理员权限、
    /// 也不需要 netsh urlacl 预留**）。只监听 127.0.0.1，只服务三类请求：
    ///   GET /             → web/index.html
    ///   GET /api/data     → 当前配额 + 用量 JSON 快照
    ///   GET /api/refresh  → 触发一次立即刷新
    /// </summary>
    internal sealed class HttpServer
    {
        private readonly AppState _state;
        private readonly string _webRoot;
        private TcpListener? _listener;

        public int Port { get; private set; }

        public HttpServer(AppState state)
        {
            _state = state;
            _webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web");
        }

        /// <summary>从 basePort 起逐个尝试绑定，全部占用则返回 false</summary>
        public bool Start(int basePort, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                int port = basePort + i;
                try
                {
                    var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    _ = Task.Run(AcceptLoopAsync);
                    return true;
                }
                catch
                {
                    // 端口被占用，试下一个
                }
            }
            return false;
        }

        private async Task AcceptLoopAsync()
        {
            while (_listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleAsync(client));
                }
                catch
                {
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);

                    string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine)) return;

                    // 吃掉剩余请求头（本服务只处理无 body 的请求）
                    string? headerLine;
                    int guard = 0;
                    while (guard++ < 200)
                    {
                        headerLine = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(headerLine)) break;
                    }

                    string[] parts = requestLine.Split(' ');
                    string rawPath = parts.Length > 1 ? parts[1] : "/";

                    int q = rawPath.IndexOf('?');
                    string path = q >= 0 ? rawPath.Substring(0, q) : rawPath;
                    try { path = Uri.UnescapeDataString(path); } catch { }

                    switch (path)
                    {
                        case "/favicon.ico":
                            await WriteAsync(stream, 204, "image/x-icon", "").ConfigureAwait(false);
                            return;

                        case "/api/data":
                            await WriteAsync(stream, 200, "application/json; charset=utf-8",
                                _state.BuildPayloadJson()).ConfigureAwait(false);
                            return;

                        case "/api/refresh":
                            _state.RequestRefresh();
                            await WriteAsync(stream, 200, "application/json; charset=utf-8",
                                "{\"ok\":true}").ConfigureAwait(false);
                            return;

                        case "/api/export.csv":
                            await WriteAsync(stream, 200, "text/csv; charset=utf-8", _state.BuildCsv(),
                                $"attachment; filename=\"antigravity-token-{DateTime.Now:yyyyMMdd-HHmm}.csv\"")
                                .ConfigureAwait(false);
                            return;

                        case "/":
                        case "/index.html":
                            string file = Path.Combine(_webRoot, "index.html");
                            if (!File.Exists(file))
                            {
                                await WriteAsync(stream, 500, "text/plain; charset=utf-8",
                                    "找不到 web/index.html，请重新执行构建。").ConfigureAwait(false);
                                return;
                            }
                            string html = await File.ReadAllTextAsync(file, Encoding.UTF8).ConfigureAwait(false);
                            await WriteAsync(stream, 200, "text/html; charset=utf-8", html).ConfigureAwait(false);
                            return;

                        default:
                            await WriteAsync(stream, 404, "text/plain; charset=utf-8", "404 Not Found")
                                .ConfigureAwait(false);
                            return;
                    }
                }
            }
            catch
            {
                // 客户端断开等情况直接忽略
            }
        }

        private static async Task WriteAsync(
            NetworkStream stream, int code, string contentType, string body, string? contentDisposition = null)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);

            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(code).Append(' ').Append(ReasonPhrase(code)).Append("\r\n");
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            if (!string.IsNullOrEmpty(contentDisposition))
                head.Append("Content-Disposition: ").Append(contentDisposition).Append("\r\n");
            head.Append("Cache-Control: no-store, no-cache, must-revalidate\r\n");
            head.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
            if (payload.Length > 0)
                await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string ReasonPhrase(int code) => code switch
        {
            200 => "OK",
            204 => "No Content",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };
    }
}
