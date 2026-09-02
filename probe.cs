using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Probe
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    static async Task Main()
    {
        int pid = 0; string token = "";
        using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%language_server.exe%'"))
        {
            foreach (ManagementObject obj in searcher.Get())
            {
                pid = Convert.ToInt32(obj["ProcessId"]);
                string cmdline = obj["CommandLine"]?.ToString() ?? "";
                var m = Regex.Match(cmdline, @"--csrf_token\s+([a-f0-9\-]+)");
                if (m.Success) { token = m.Groups[1].Value; break; }
            }
        }
        if (pid == 0) { Console.WriteLine("LanguageServer not found"); return; }

        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, true, 2, 5, 0);
        IntPtr pTable = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (GetExtendedTcpTable(pTable, ref bufferSize, true, 2, 5, 0) == 0)
            {
                int numEntries = Marshal.ReadInt32(pTable);
                IntPtr rowPtr = IntPtr.Add(pTable, 4);
                var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (s, c, ch, e) => true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };

                for (int i = 0; i < numEntries; i++)
                {
                    int state = Marshal.ReadInt32(rowPtr, 0);
                    int localPortRaw = Marshal.ReadInt32(rowPtr, 8);
                    int owningPid = Marshal.ReadInt32(rowPtr, 20);

                    if (state == 2 && owningPid == pid)
                    {
                        int port = ((localPortRaw & 0xFF) << 8) | ((localPortRaw >> 8) & 0xFF);
                        try
                        {
                            string url = $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetUserStatus";
                            var req = new HttpRequestMessage(HttpMethod.Post, url);
                            req.Headers.Add("Connect-Protocol-Version", "1");
                            req.Headers.Add("x-codeium-csrf-token", token);
                            req.Content = new StringContent($"{{\"metadata\":{{\"csrf_token\":\"{token}\",\"ide_name\":\"antigravity\"}}}}", Encoding.UTF8, "application/json");
                            var resp = await client.SendAsync(req);
                            if (resp.IsSuccessStatusCode)
                            {
                                string body = await resp.Content.ReadAsStringAsync();
                                File.WriteAllText("user_status_dump.json", body);
                                Console.WriteLine("SUCCESS_DUMPED_TO_FILE");
                                return;
                            }
                        }
                        catch { }
                    }
                    rowPtr = IntPtr.Add(rowPtr, 24);
                }
            }
        }
        finally { Marshal.FreeHGlobal(pTable); }
    }
}
