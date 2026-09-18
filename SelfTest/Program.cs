using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using LanFileShare.Models;
using LanFileShare.Services;

namespace SelfTest;

/// <summary>
/// LanFileShare 全面自测（零依赖、自建断言）。
/// 运行：dotnet run --project SelfTest
/// </summary>
internal static class Program
{
    private static int _pass, _fail;
    private static readonly List<string> _failures = new();

    private static void Check(string name, bool cond, string detail = "")
    {
        if (cond) { _pass++; Console.WriteLine($"  [PASS] {name}"); }
        else
        {
            _fail++;
            _failures.Add($"{name}  {detail}");
            Console.WriteLine($"  [FAIL] {name}  {detail}");
        }
    }

    private static async Task<int> Main()
    {
        Console.WriteLine("==== LanFileShare 自测 ====\n");

        // 兜底：任一套件意外崩溃也要报告出来，而不是无汇总直接崩
        try { await E2E_Tests(); }
        catch (Exception ex) { Check("E2E 套件完整跑完", false, "套件异常: " + ex); }
        try { Unit_Tests(); }
        catch (Exception ex) { Check("单元套件完整跑完", false, "套件异常: " + ex); }

        Console.WriteLine("\n==== 结果 ====");
        Console.WriteLine($"PASS: {_pass}  FAIL: {_fail}");
        if (_failures.Count > 0)
        {
            Console.WriteLine("失败项：");
            foreach (var f in _failures) Console.WriteLine("  - " + f);
        }
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------
    // 端到端：真实 TcpHttpServer + 真实 socket
    // ---------------------------------------------------------------
    private static string _root = "";
    private static string _parent = "";

    private static async Task E2E_Tests()
    {
        Console.WriteLine("-- E2E（真实 HTTP 服务）--");
        _root = Path.Combine(Path.GetTempPath(), "lfs-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        _parent = Directory.GetParent(_root)!.FullName;

        var settings = new SettingsStore();
        settings.Current.SavePath = _root;

        int port = GetFreePort();
        var server = TcpHttpServer.TryStart(port, settings, () => { });
        Check("服务启动成功", server != null, $"port={port}");
        if (server == null) return;

        // 1. GET / 返回手机页
        var r = await SendAsync(port, "GET / HTTP/1.1");
        Check("GET / -> 200", r.Status == 200);
        Check("GET / 含手机页标题", r.BodyText.Contains("发送文件到电脑"));

        // 2. 带查询串的首页（二维码工具常追加参数）
        r = await SendAsync(port, "GET /?from=qr HTTP/1.1");
        Check("GET /?from=qr -> 200（query 剥离）", r.Status == 200, "状态码 " + r.Status);

        // 3. /health JSON
        r = await SendAsync(port, "GET /health HTTP/1.1");
        Check("GET /health -> 200 JSON", r.Status == 200 && r.BodyText.Contains("\"success\""));

        // 4. /favicon.ico 204
        r = await SendAsync(port, "GET /favicon.ico HTTP/1.1");
        Check("GET /favicon.ico -> 204", r.Status == 204);

        // 5. 未知路径 404
        r = await SendAsync(port, "GET /nope HTTP/1.1");
        Check("GET /nope -> 404", r.Status == 404);

        // 6. 正常 ASCII 文件上传
        var resp = await UploadAsync(port, "hello.txt", Encoding.UTF8.GetBytes("hi"), "User1");
        Check("上传 hello.txt -> success", resp.Success, resp.Raw);
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        var saved = Path.Combine(_root, "User1", day, "hello.txt");
        Check("hello.txt 落盘到 用户/日期 目录", File.Exists(saved), saved);

        // 7. 中文文件名（浏览器以 UTF-8 原样放进 multipart header）
        resp = await UploadAsync(port, "测试文档.pdf", new byte[] { 1, 2, 3 }, "User1");
        var cnDir = Path.Combine(_root, "User1", day);
        var cnSaved = Directory.Exists(cnDir) ? Directory.GetFiles(cnDir).Select(Path.GetFileName).ToArray() : Array.Empty<string>();
        Check("中文文件名按原名保存", cnSaved.Contains("测试文档.pdf"),
            "实际: [" + string.Join(", ", cnSaved) + "]");

        // 8. 同名冲突 → 自动 (1)
        File.WriteAllText(Path.Combine(_root, "User1", day, "conflict.txt"), "old");
        resp = await UploadAsync(port, "conflict.txt", Encoding.UTF8.GetBytes("new"), "User1");
        var conflictFiles = Directory.GetFiles(Path.Combine(_root, "User1", day), "conflict*").Select(Path.GetFileName).ToArray();
        Check("同名冲突自动重命名 (1)", resp.Success && conflictFiles.Contains("conflict(1).txt"),
            "实际: [" + string.Join(", ", conflictFiles) + "]");

        // 9. 并发上传同名文件（CreateNew 竞态应自动递增序号）
        var t1 = UploadAsync(port, "race.txt", Encoding.UTF8.GetBytes("A"), "User1");
        var t2 = UploadAsync(port, "race.txt", Encoding.UTF8.GetBytes("B"), "User1");
        await Task.WhenAll(t1, t2);
        var raceSaved = Directory.GetFiles(Path.Combine(_root, "User1", day), "race*").Length;
        Check("并发同名两个都成功", raceSaved == 2, $"实际落盘 {raceSaved} 个");

        // 10. 白名单：.exe 拒绝
        resp = await UploadAsync(port, "virus.exe", new byte[] { 1 }, "User1");
        Check(".exe 被白名单拒绝", !resp.Success);

        // 11. 非 multipart（无 boundary）→ 拒绝
        resp = await UploadRawAsync(port, "text/plain", "X-Device-Id", "User1", new byte[] { 1 });
        Check("非 multipart 上传被拒绝", !resp.Success, resp.Raw);

        // 12. 超大 Content-Length（>256MB 上限）→ 明确拒绝（不再 RST）
        var bigLen = 257 * 1024 * 1024;
        var req = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\nContent-Type: multipart/form-data; boundary=B\r\n" +
            $"Content-Length: {bigLen}\r\nX-Device-Id: User1\r\nConnection: close\r\n\r\n");
        string? bigResp = null;
        try { bigResp = await SendRawAsync(port, req, timeout: 5); }
        catch (Exception ex) { bigResp = "EX: " + ex.Message; }
        Check("超 256MB 请求体被拒且收到 400", bigResp != null && bigResp.Contains("400"),
            $"实际响应: {(bigResp is null ? "null" : bigResp[..Math.Min(120, bigResp.Length)])}");

        // 13. 目录穿越：X-Device-Id = ".." → 明确拒绝（不再落盘到保存根之外）
        resp = await UploadAsync(port, "escape.txt", Encoding.UTF8.GetBytes("x"), "..");
        var escapeDir = Path.Combine(_parent, day, "escape.txt");
        Check("设备名 '..' 被拒绝且不落盘", !resp.Success && !File.Exists(escapeDir),
            $"resp={resp.Raw}，存在={File.Exists(escapeDir)}");

        // 14. 流式大文件：8MB（跨多块、多轮窗口滚动）内容完整性校验
        var bigContent = new byte[8 * 1024 * 1024];
        new Random(42).NextBytes(bigContent);
        resp = await UploadAsync(port, "bigfile.bin".Replace(".bin", ".txt"), bigContent, "User1");
        var bigSaved = Path.Combine(_root, "User1", day, "bigfile.txt");
        Check("8MB 文件上传成功", resp.Success, resp.Raw);
        if (File.Exists(bigSaved))
        {
            var savedBytes = await File.ReadAllBytesAsync(bigSaved);
            Check("8MB 文件内容逐字节一致", savedBytes.Length == bigContent.Length && savedBytes.AsSpan().SequenceEqual(bigContent),
                $"落盘 {savedBytes.Length} bytes");
        }
        else Check("8MB 文件内容逐字节一致", false, "文件不存在");

        // 14b. 手机型号设备名（新版手机页会送型号+随机尾缀，如 2210132C-a3f2、Pixel 7 Pro-x2k9）
        resp = await UploadAsync(port, "model.txt", Encoding.UTF8.GetBytes("m"), "Pixel 7 Pro-xy23");
        Check("型号设备名（含空格）落盘到对应目录",
            resp.Success && File.Exists(Path.Combine(_root, "Pixel 7 Pro-xy23", day, "model.txt")), resp.Raw);

        // 14c. 设备名含非法字符（服务端 SanitizeFolder 应替换而非拒绝）
        resp = await UploadAsync(port, "sanitized.txt", Encoding.UTF8.GetBytes("s"), "bad:name");
        var sanitizedDir = Path.Combine(_root, "bad_name", day);
        Check("非法字符设备名被清洗后落盘", resp.Success && File.Exists(Path.Combine(sanitizedDir, "sanitized.txt")), resp.Raw);

        // 15. filename*（RFC 5987，浏览器对非 ASCII 文件名的另一种编码）
        resp = await UploadFilenameStar(port, "star中文.txt", Encoding.UTF8.GetBytes("s"), "User1");
        var starSaved = Path.Combine(_root, "User1", day, "star中文.txt");
        Check("filename* (RFC5987) 中文名解析", resp.Success && File.Exists(starSaved), resp.Raw);

        // 16. 多 part 单请求（3 个文件一次上传）
        resp = await UploadMultiPartAsync(port, new[]
        {
            ("multi-a.txt", Encoding.UTF8.GetBytes("AAA")),
            ("multi-b.txt", Encoding.UTF8.GetBytes("BBB")),
            ("multi-c.txt", Encoding.UTF8.GetBytes("CCC")),
        }, "User1");
        Check("单请求 3 个 part 全部落盘",
            resp.Success &&
            File.Exists(Path.Combine(_root, "User1", day, "multi-a.txt")) &&
            File.Exists(Path.Combine(_root, "User1", day, "multi-b.txt")) &&
            File.Exists(Path.Combine(_root, "User1", day, "multi-c.txt")), resp.Raw);

        // 17. 白名单 part 混在合法 part 中 → 黑名单丢弃，合法的照存
        resp = await UploadMultiPartAsync(port, new[]
        {
            ("ok.txt", Encoding.UTF8.GetBytes("keep")),
            ("bad.exe", Encoding.UTF8.GetBytes("drop")),
        }, "User1");
        Check("混合 part：合法保存/黑名单丢弃",
            File.Exists(Path.Combine(_root, "User1", day, "ok.txt")) &&
            !File.Exists(Path.Combine(_root, "User1", day, "bad.exe")), resp.Raw);

        // 18. 截断请求（声称 1000 字节只发 500）→ 拒绝且无残留文件
        resp = await UploadTruncatedAsync(port, "trunc.txt", 1000, 500, "User1");
        var truncLeftover = Directory.GetFiles(Path.Combine(_root, "User1", day), "trunc*");
        Check("截断请求被拒且不留半截文件", !resp.Success && truncLeftover.Length == 0,
            $"resp={resp.Raw}，残留=[{string.Join(", ", truncLeftover.Select(Path.GetFileName))}]");

        // 19. part 头跨块：48KB 填充文件名前的头部（跨越 64KB 窗口补块间隔，但低于 64KB 头部上限）
        resp = await UploadLongHeaderAsync(port, "longhead.txt", 48 * 1024, "User1");
        Check("part 头跨块（含 48KB 填充）解析成功", resp.Success, resp.Raw);

        // 19b. 超过 64KB 的 part 头是 DoS 向量 → 服务端必须拒绝（不是无限缓冲）
        resp = await UploadLongHeaderAsync(port, "bighdr.txt", 80 * 1024, "User1");
        Check("超 64KB part 头被拒绝", !resp.Success, resp.Raw);

        // 19c. 2MB 填充头部同样必须被拒（服务端不会为畸形头无限缓存）
        resp = await UploadLongHeaderAsync(port, "bighdr2.txt", 2 * 1024 * 1024, "User1");
        Check("超长 part 头（2MB 填充）被拒绝", !resp.Success, resp.Raw);

        server.Stop(); server.Dispose();

        try { Directory.Delete(_root, true); } catch { }
    }

    // ---------------------------------------------------------------
    // 单元级
    // ---------------------------------------------------------------
    private static void Unit_Tests()
    {
        Console.WriteLine("\n-- 单元 --");

        Check("IsAllowed 大小写不敏感", AllowedExtensions.IsAllowed("A.JPG") && AllowedExtensions.IsAllowed("x.Pdf"));
        Check("IsAllowed 拒绝无扩展名", !AllowedExtensions.IsAllowed("noext"));
        Check("IsAllowed 拒绝双扩展伪装", !AllowedExtensions.IsAllowed("evil.jpg.exe"));

        // HTML accept 与服务端白名单是否一致
        var accept = AllowedExtensions.HtmlAccept;
        Check("HtmlAccept 覆盖 .7z", accept.Contains(".7z"));
        var home = ExtractAcceptFromMobileHtml();
        Check("mobile.html 的 accept 含 .md/.csv/.7z（服务端允许但选择器过滤）",
            home.Contains(".md") && home.Contains(".csv") && home.Contains(".7z"),
            "实际 accept: " + home);

        // QR 生成
        try
        {
            var png = QrGenerator.GeneratePng("http://192.168.1.5:50000/", 240);
            Check("二维码 PNG 生成", png.Length > 8 && png[0] == 0x89 && png[1] == (byte)'P');
        }
        catch (Exception ex) { Check("二维码 PNG 生成", false, ex.Message); }

        // 端口占用探测（间接：占住端口后 TryStart 应失败）
        int p = GetFreePort();
        var blocker = new TcpListener(IPAddress.Any, p);
        blocker.Start();
        var s = TcpHttpServer.TryStart(p, new SettingsStore(), () => { });
        Check("端口被占时 TryStart 返回 null", s == null, $"port={p}");
        blocker.Stop();
    }

    /// <summary>从可执行文件位置向上找含 LanFileShare.sln 的目录（仓库根），避免硬编码 bin 深度。</summary>
    internal static string? FindWorkspaceRoot()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "LanFileShare.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }

    private static string ExtractAcceptFromMobileHtml()
    {
        // MobileHtml 是 internal，借 GET / 拿不到（此处直接从 Resources 断言常量来源），
        // 用最小方式：读取仓库内 mobile.html（先按 sln 定位，再退回 cwd）。
        try
        {
            var candidates = new List<string>();
            var ws = FindWorkspaceRoot();
            if (ws != null)
                candidates.Add(Path.Combine(ws, "LanFileShare", "Resources", "mobile.html"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "LanFileShare", "Resources", "mobile.html"))
                ;
            foreach (var c in candidates)
                if (File.Exists(c))
                {
                    var html = File.ReadAllText(c);
                    int i = html.IndexOf("accept=\"", StringComparison.Ordinal);
                    if (i >= 0) { i += 8; int j = html.IndexOf('"', i); return html[i..j]; }
                }
        }
        catch { }
        return "(mobile.html 未找到)";
    }

    // ---------------------------------------------------------------
    // HTTP 辅助
    // ---------------------------------------------------------------
    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private sealed record Resp(int Status, string BodyText, string Raw)
    {
        public bool Success => Status is >= 200 and < 300;
    }

    private static Task<Resp> SendAsync(int port, string requestLine)
        => SendRawRespAsync(port, Encoding.ASCII.GetBytes(requestLine + "\r\nHost: t\r\nConnection: close\r\n\r\n"));

    private static Task<Resp> UploadAsync(int port, string filename, byte[] content, string deviceId)
    {
        var (headers, body) = BuildMultipart(filename, content, deviceId);
        return SendRawRespAsync(port, Concat(headers, body));
    }

    private static Task<Resp> UploadRawAsync(int port, string contentType, string hdrName, string hdrVal, byte[] body)
    {
        var head = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\nContent-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n{hdrName}: {hdrVal}\r\nConnection: close\r\n\r\n");
        return SendRawRespAsync(port, Concat(head, body));
    }

    private static (byte[] head, byte[] body) BuildMultipart(string filename, byte[] content, string deviceId)
    {
        const string b = "----SelfTestBoundary";
        var sb = new StringBuilder();
        sb.Append($"--{b}\r\n");
        sb.Append($"Content-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n\r\n");
        var pre = Encoding.UTF8.GetBytes(sb.ToString());
        var post = Encoding.ASCII.GetBytes($"\r\n--{b}--\r\n");
        var body = Concat(pre, content, post);
        var head = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\n" +
            $"Content-Type: multipart/form-data; boundary={b}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"X-Device-Id: {Uri.EscapeDataString(deviceId)}\r\n" +
            "Connection: close\r\n\r\n");
        return (head, body);
    }

    // ===== 流式/边界用例辅助 =====

    private const string Mb = "----SelfTestBoundary";

    private static byte[] BuildMultiPartBody(IEnumerable<(string name, byte[] data)> files)
    {
        var ms = new MemoryStream();
        foreach (var (name, data) in files)
        {
            var pre = Encoding.UTF8.GetBytes(
                $"--{Mb}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{name}\"\r\n" +
                "Content-Type: application/octet-stream\r\n\r\n");
            ms.Write(pre); ms.Write(data); ms.Write(Encoding.ASCII.GetBytes("\r\n"));
        }
        ms.Write(Encoding.ASCII.GetBytes($"--{Mb}--\r\n"));
        return ms.ToArray();
    }

    private static Task<Resp> UploadMultiPartAsync(int port, IEnumerable<(string name, byte[] data)> files, string deviceId)
    {
        var body = BuildMultiPartBody(files);
        var head = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\nContent-Type: multipart/form-data; boundary={Mb}\r\n" +
            $"Content-Length: {body.Length}\r\nX-Device-Id: {Uri.EscapeDataString(deviceId)}\r\nConnection: close\r\n\r\n");
        return SendRawRespAsync(port, Concat(head, body));
    }

    /// <summary>filename*=UTF-8''... 形式的文件名（浏览器对非 ASCII 的另一编码）。</summary>
    private static Task<Resp> UploadFilenameStar(int port, string filename, byte[] content, string deviceId)
    {
        var encoded = "UTF-8''" + Uri.EscapeDataString(filename);
        var pre = Encoding.ASCII.GetBytes(
            $"--{Mb}\r\nContent-Disposition: form-data; name=\"file\"; filename*=\"{encoded}\"\r\n" +
            "Content-Type: application/octet-stream\r\n\r\n");
        var body = Concat(pre, content, Encoding.ASCII.GetBytes($"\r\n--{Mb}--\r\n"));
        var head = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\nContent-Type: multipart/form-data; boundary={Mb}\r\n" +
            $"Content-Length: {body.Length}\r\nX-Device-Id: {Uri.EscapeDataString(deviceId)}\r\nConnection: close\r\n\r\n");
        return SendRawRespAsync(port, Concat(head, body));
    }

    /// <summary>声称 contentLength 字节、只发 sendLen 字节并半关写端（模拟传输中断）。</summary>
    private static async Task<Resp> UploadTruncatedAsync(int port, string filename, int contentLength, int sendLen, string deviceId)
    {
        var data = new byte[sendLen];
        new Random(7).NextBytes(data);
        var pre = Encoding.UTF8.GetBytes(
            $"--{Mb}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n" +
            "Content-Type: application/octet-stream\r\n\r\n");
        var body = Concat(pre, data); // 无收尾 boundary —— 模拟截断
        var head = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\nContent-Type: multipart/form-data; boundary={Mb}\r\n" +
            $"Content-Length: {contentLength}\r\nX-Device-Id: {Uri.EscapeDataString(deviceId)}\r\nConnection: close\r\n\r\n");

        // 手工发送：写完立即 shutdown Write，让服务端 ReadAsync 返回 0（EOF）而不是永远等剩余字节
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Concat(head, body));
        await stream.FlushAsync();
        client.Client.Shutdown(SocketShutdown.Send);

        var ms = new MemoryStream();
        var buf = new byte[8192];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf);
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
        }
        catch (IOException) { /* RST */ }
        var raw = Encoding.UTF8.GetString(ms.ToArray());
        int sp = raw.IndexOf(' ');
        int sp2 = raw.IndexOf(' ', sp + 1);
        int status = int.Parse(raw.Substring(sp + 1, sp2 - sp - 1));
        int bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var bodyText = bodyStart >= 0 ? raw[(bodyStart + 4)..] : "";
        return new Resp(status, bodyText, raw.Length > 300 ? raw[..300] : raw);
    }

    /// <summary>part 头里塞 paddingBytes 字节填充，把头撑到跨多块（>64KB 触发多次补块）。padding 放在 Content-Disposition 之前，保证头本身合法。</summary>
    private static Task<Resp> UploadLongHeaderAsync(int port, string filename, int paddingBytes, string deviceId)
    {
        var content = Encoding.UTF8.GetBytes("long-header-test");
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes($"--{Mb}\r\n"));
        // 用合法的自定义头行做 padding（协议允许任意头）
        var padLine = Encoding.ASCII.GetBytes($"X-Pad: {new string('A', 1024)}\r\n");
        for (int i = 0; i < paddingBytes / 1024; i++) ms.Write(padLine);
        ms.Write(Encoding.ASCII.GetBytes($"Content-Disposition: form-data; name=\"file\"; filename=\"{filename}\"\r\n"));
        ms.Write(Encoding.ASCII.GetBytes("Content-Type: application/octet-stream\r\n\r\n"));
        ms.Write(content);
        ms.Write(Encoding.ASCII.GetBytes($"\r\n--{Mb}--\r\n"));
        var body = ms.ToArray();
        var head = Encoding.ASCII.GetBytes(
            $"POST /upload HTTP/1.1\r\nHost: t\r\nContent-Type: multipart/form-data; boundary={Mb}\r\n" +
            $"Content-Length: {body.Length}\r\nX-Device-Id: {Uri.EscapeDataString(deviceId)}\r\nConnection: close\r\n\r\n");
        return SendRawRespAsync(port, Concat(head, body));
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    private static async Task<Resp> SendRawRespAsync(int port, byte[] request)
    {
        var raw = await SendRawAsync(port, request, timeout: 10);
        int sp = raw.IndexOf(' ');
        int sp2 = raw.IndexOf(' ', sp + 1);
        if (sp < 0 || sp2 < 0)
            throw new InvalidDataException("无 HTTP 响应（服务端可能断连）: " + raw[..Math.Min(120, raw.Length)]);
        int status = int.Parse(raw.Substring(sp + 1, sp2 - sp - 1));
        int bodyStart = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = bodyStart >= 0 ? raw[(bodyStart + 4)..] : "";
        return new Resp(status, body, raw.Length > 300 ? raw[..300] : raw);
    }

    private static async Task<string> SendRawAsync(int port, byte[] request, int timeout)
    {
        using var client = new TcpClient();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(request, cts.Token);
        await stream.FlushAsync(cts.Token);

        var ms = new MemoryStream();
        var buf = new byte[8192];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf, cts.Token);
                if (n == 0) break;
                ms.Write(buf, 0, n);
            }
        }
        catch (OperationCanceledException) { /* 服务端保持连接时超时截断 */ }
        catch (IOException) { /* RST */ }
        // 响应体可能是 UTF-8（HTML/JSON），按 UTF-8 解码；头部 ASCII 兼容
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
