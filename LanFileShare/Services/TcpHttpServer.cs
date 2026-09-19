using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanFileShare.Models;

namespace LanFileShare.Services;

/// <summary>
/// 极简 HTTP 服务器：完全自己实现，跳过 HttpListener / http.sys。
///
/// 为什么不用 HttpListener？
///   它内部走 http.sys，某些 Windows 机器上 HttpListener.Start() 对占用的端口会"静默成功"
///   （log 里看是绑定成功，但实际上 http.sys 内部路由把请求转给了某个 NT Kernel 系统进程），
///   结果客户端连不上。使用 raw TcpListener 直接在用户态 socket 上服务，完全避开这种坑。
///
/// 支持：
///   - GET /             → 返回 mobile.html
///   - GET /test, /info, /health → 调试端点
///   - POST /upload      → 接收 multipart/form-data 文件
///   - 任何其他路径都 404
/// </summary>
public class TcpHttpServer
{
    private readonly int _port;
    private readonly SettingsStore _settingsStore;
    private readonly Action<IReadOnlyList<FileReceiver.SavedFile>> _onUploadCompleted;
    private readonly FileReceiver _receiver;
    private TcpListener _listener = null!;
    private CancellationTokenSource _cts = null!;
    private readonly ConcurrentDictionary<TcpClient, byte> _activeClients = new();

    /// <summary>实际 listen 的端口</summary>
    public int Port => _port;

    /// <summary>绑定的端口上 listen 0.0.0.0 的主 URL（用于二维码展示）</summary>
    public string PrimaryUrl { get; private set; } = "";

    private TcpHttpServer(int port, SettingsStore settingsStore, Action<IReadOnlyList<FileReceiver.SavedFile>> onUploadCompleted)
    {
        _port = port;
        _settingsStore = settingsStore;
        _onUploadCompleted = onUploadCompleted;
        _receiver = new FileReceiver(settingsStore.Current.SavePath);
    }

    /// <summary>
    /// 真在 0.0.0.0:port 上 listen。成功返回实例，失败返回 null。
    /// 不像 HttpListener 这里 "失败" 就是真的失败，不会骗我们。
    /// </summary>
    /// <param name="primaryIp">二维码里用的主 IP（调用方已筛选过网卡）；null 则回退到 SettingsStore 的默认探测</param>
    public static TcpHttpServer? TryStart(int port, SettingsStore settingsStore, Action<IReadOnlyList<FileReceiver.SavedFile>> onUploadCompleted, string? primaryIp = null)
    {
        var server = new TcpHttpServer(port, settingsStore, onUploadCompleted);

        // 第一道闸：raw socket 试绑 0.0.0.0。System 占着就连不上，跟它正面对抗。
        if (!PortProbe.IsFree(port))
        {
            Logger.Warn($"端口 {port} 被占用（raw socket bind 失败）");
            return null;
        }

        try
        {
            server._listener = new TcpListener(IPAddress.Any, port);
            server._listener.Start();
            Logger.Info($"端口 {port} 绑定成功：0.0.0.0:{port}/");
            server.PrimaryUrl = $"http://{primaryIp ?? settingsStore.GetOrComputePrimaryIp()}:{port}/";
            server._cts = new CancellationTokenSource();
        }
        catch (Exception ex)
        {
            Logger.Error($"TcpListener.Start 失败 {port}", ex);
            return null;
        }

        // 启动 accept loop 后台任务
        _ = Task.Run(() => server.AcceptLoopAsync(server._cts.Token));
        Logger.Info($"AcceptLoopAsync 已起：0.0.0.0:{port}");
        return server;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        Logger.Info($"accept loop entered on port {_port}");
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Logger.Info($"accept loop cancelled");
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error($"Accept 异常：{ex.Message}");
                continue;
            }
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            Logger.Info($"Accepted connection from {remote}");
            _activeClients.TryAdd(client, 0);
            _ = Task.Run(async () =>
            {
                try
                {
                    await SafeHandle(client);
                }
                finally
                {
                    _activeClients.TryRemove(client, out _);
                }
            });
        }
    }

    private async Task SafeHandle(TcpClient client)
    {
        try
        {
            await HandleClientAsync(client);
        }
        catch (Exception ex)
        {
            Logger.Error("HandleClient failed", ex);
            try { client.Close(); } catch { /* ignore */ }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            // 1) 读取直到拿到 header 结束 (\r\n\r\n)，限制 64KB 防 DoS
            var headerBuf = new List<byte>();
            int headerEnd = -1;
            var temp = new byte[1];
            int totalRead = 0;
            const int MAX_HEADER = 64 * 1024;

            while (totalRead < MAX_HEADER)
            {
                int n = await stream.ReadAsync(temp, 0, 1);
                if (n == 0) break; // remote closed
                headerBuf.Add(temp[0]);
                totalRead++;
                if (headerBuf.Count >= 4 &&
                    headerBuf[^4] == 0x0D && headerBuf[^3] == 0x0A &&
                    headerBuf[^2] == 0x0D && headerBuf[^1] == 0x0A)
                {
                    headerEnd = headerBuf.Count;
                    break;
                }
            }

            if (headerEnd <= 0)
            {
                await WriteStatusAsync(stream, 400, "Bad Request", "");
                return;
            }

            var headerBytes = headerBuf.ToArray();
            // UTF-8 解码：multipart part 头里的 filename 可能含中文（浏览器原样放 UTF-8），
            // ASCII 解码会把非 ASCII 变 '?'，导致中文文件名落盘成下划线。
            var headerText = Encoding.UTF8.GetString(headerBytes, 0, headerEnd);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0];
            var headerLines = lines.Skip(1).Where(l => !string.IsNullOrEmpty(l)).ToList();

            // 解析 request line: "GET /path HTTP/1.1"
            var parts = requestLine.Split(' ');
            if (parts.Length < 3)
            {
                await WriteStatusAsync(stream, 400, "Bad Request", "");
                return;
            }
            var method = parts[0];
            var path = parts[1];
            // 剥离 query string：二维码工具可能给 URL 追加参数（如 /?from=qr），
            // 不剥离会把带参数的路径落到 404。
            int qIdx = path.IndexOf('?');
            if (qIdx >= 0) path = path[..qIdx];
            var contentLength = GetHeaderInt(headerLines, "Content-Length", 0);
            var deviceId = GetHeader(headerLines, "X-Device-Id", "");
            var contentType = GetHeader(headerLines, "Content-Type", "");

Logger.Info($"[{method} {path}] device={deviceId}, contentLength={contentLength}, contentType={contentType}");

            // 路由
            try
            {
                switch (path)
                {
                    case "/":
                    case "/index.html":
                    case "/index.htm":
                        await HandleGetHomeAsync(stream);
                        return;

                    case "/test":
                        var msg = $"Hello from LanFileShare! Port={_port}, ServerTime={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
                        await WriteStatusAsync(stream, 200, "OK", msg, "text/plain; charset=utf-8");
                        return;

                    case "/info":
                        var html = MobileHtml.Content;
                        var first80 = html.Substring(0, Math.Min(80, html.Length));
                        var info = $"HTML size: {html.Length} chars. First 80 chars:\n{first80}\n";
                        await WriteStatusAsync(stream, 200, "OK", info, "text/plain; charset=utf-8");
                        return;

                    case "/health":
                        var hb = $"{{\"success\":true,\"port\":{_port}}}\n";
                        await WriteStatusAsync(stream, 200, "OK", hb, "application/json; charset=utf-8");
                        return;

                    case "/upload":
                        if (method != "POST")
                        {
                            await WriteStatusAsync(stream, 405, "Method Not Allowed", "");
                            return;
                        }
                        await HandleUploadAsync(stream, contentLength, deviceId, contentType);
                        return;

                    case "/favicon.ico":
                        await WriteStatusAsync(stream, 204, "No Content", "");
                        return;

                    default:
                        await WriteStatusAsync(stream, 404, "Not Found", "Not Found\n");
                        return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[ROUTE] unhandled exception for [{method} {path}]", ex);
                try
                {
                    await WriteStatusAsync(stream, 500, "Internal Server Error",
                        $"{{\"success\":false,\"error\":\"server exception: {JsonEscape(ex.GetType().Name + ": " + ex.Message)}\"}}\n",
                        "application/json; charset=utf-8");
                }
                catch (Exception writeEx)
                {
                    Logger.Error("[ROUTE] cannot write error response", writeEx);
                }
            }
        }
    }

    private async Task HandleGetHomeAsync(NetworkStream stream)
    {
        var html = MobileHtml.Content;
        var data = Encoding.UTF8.GetBytes(html);
        await WriteResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", data);
        Logger.Info($"Served home page: {data.Length} bytes");
    }

    private async Task HandleUploadAsync(NetworkStream stream, int contentLength, string deviceId, string contentType)
    {
        Logger.Info($"[POST /upload] enter: contentLength={contentLength}, raw-deviceId={deviceId}, contentType={contentType}");
        try
        {
            // X-Device-Id 是 percent-encoded（UTF-8 → ASCII 安全），
            // 这里反解回真实字符串（兼容中文 deviceName 例如 "用户1"）。
            string decodedDeviceId;
            try { decodedDeviceId = Uri.UnescapeDataString(deviceId); }
            catch { decodedDeviceId = deviceId; /* 兼容老版本（未来可直接传 ASCII 设备名） */ }

            Logger.Info($"[POST /upload] decoded-deviceId={decodedDeviceId}, calling receiver");
            var result = await _receiver.HandleUploadAsync(stream, contentLength, decodedDeviceId, contentType);
            var count = result.Files.Count;
            var error = result.Errors.FirstOrDefault() ?? "";
            Logger.Info($"[POST /upload] receiver returned: count={count}, error={error}");
            // 关键：字段名必须和 mobile.html 对得上 —— 用 "success" 和 "error"，别用 "ok"。
            var msg = count > 0
                ? $"{{\"success\":true,\"saved\":{count}}}\n"
                : $"{{\"success\":false,\"error\":\"{JsonEscape(error)}\"}}\n";
            var status = count > 0 ? "OK" : "Bad Request";
            await WriteStatusAsync(stream, count > 0 ? 200 : 400, status, msg, "application/json; charset=utf-8");
            if (count > 0)
            {
                _onUploadCompleted?.Invoke(result.Files);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("upload 处理异常", ex);
            await WriteStatusAsync(stream, 500, "Internal Server Error", $"{{\"success\":false,\"error\":\"{JsonEscape(ex.Message)}\"}}\n", "application/json; charset=utf-8");
        }
    }

    public void UpdateSavePath(string savePath)
    {
        _receiver.UpdateSaveRoot(savePath);
    }

    /// <summary>把任意字符串安全嵌入 JSON 字符串字面量（转义反斜杠/引号/控制字符）。</summary>
    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static int GetHeaderInt(List<string> headers, string name, int defaultValue)
    {
        var v = GetHeader(headers, name, "");
        return int.TryParse(v, out var i) ? i : defaultValue;
    }

    private static string GetHeader(List<string> headers, string name, string defaultValue)
    {
        foreach (var h in headers)
        {
            var idx = h.IndexOf(':');
            if (idx > 0 && string.Equals(h.Substring(0, idx).Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return h.Substring(idx + 1).Trim();
            }
        }
        return defaultValue;
    }

    private async Task WriteStatusAsync(NetworkStream stream, int code, string status, string body, string contentType = "text/plain; charset=utf-8")
    {
        await WriteResponseAsync(stream, code, status, contentType, Encoding.UTF8.GetBytes(body));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int code, string status, string contentType, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"Connection: close\r\n" +
            $"Cache-Control: no-store\r\n" +
            "\r\n");
        await stream.WriteAsync(head, 0, head.Length);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, 0, body.Length);
        }
        await stream.FlushAsync();
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        foreach (var client in _activeClients.Keys)
        {
            try { client.Close(); } catch { /* ignore */ }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _listener?.Server?.Close(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}
