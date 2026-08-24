using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanFileShare.Helpers;
using LanFileShare.Models;

namespace LanFileShare.Services;

/// <summary>
/// 内置 HTTP 服务：用 HttpListener 提供手机端页面 + 上传端点。
/// 不依赖 ASP.NET Core，整个 EXE 极其轻量。
/// </summary>
public sealed class HttpFileServer : IDisposable
{
    private readonly HttpServer _server;
    private readonly FileReceiver _receiver;
    private readonly SettingsStore _settingsStore;
    private readonly Action _onUploadCompleted;
    private readonly CancellationTokenSource _cts = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public HttpFileServer(string listeningUrl, SettingsStore settingsStore, Action onUploadCompleted)
    {
        _settingsStore = settingsStore;
        _onUploadCompleted = onUploadCompleted;
        _receiver = new FileReceiver(settingsStore.Current.SavePath);
        _server = new HttpServer(listeningUrl, HandleAsync);
    }

    public async Task RunAsync()
    {
        await _server.RunAsync(_cts.Token);
    }

    public void Stop() => _cts.Cancel();

    private async Task HandleAsync(System.Net.HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "/";
        var method = req.HttpMethod;

        try
        {
            if (method == "GET" && (path == "/" || path == ""))
            {
                await HandleGetHomeAsync(ctx);
                return;
            }
            if (method == "GET" && path == "/health")
            {
                await WriteJsonAsync(ctx, 200, new { ok = true });
                return;
            }
            if (method == "POST" && path == "/upload")
            {
                await HandleUploadAsync(ctx);
                return;
            }
            if (method == "OPTIONS")
            {
                ctx.Response.AddHeader("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
                ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Device-Id");
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Logger.Error($"HTTP 处理异常 path={path}", ex);
            try
            {
                ctx.Response.StatusCode = 500;
                using var sw = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8);
                await sw.WriteAsync("{\"error\":\"internal\"}");
            }
            catch { /* response may already be broken */ }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private async Task HandleGetHomeAsync(System.Net.HttpListenerContext ctx)
    {
        var html = LoadEmbeddedResource("LanFileShare.Resources.mobile.html");
        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = buffer.Length;
        ctx.Response.AddHeader("Cache-Control", "no-store");
        await ctx.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        ctx.Response.Close();
    }

    private async Task HandleUploadAsync(System.Net.HttpListenerContext ctx)
    {
        var req = ctx.Request;

        var contentType = req.ContentType ?? "";
        if (!contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(ctx, 415, new { error = "Content-Type 必须是 multipart/form-data" });
            return;
        }

        var deviceId = req.Headers["X-Device-Id"] ?? "未命名设备";
        Logger.Info($"收到上传请求：设备={deviceId}, ContentLength={req.ContentLength64}");

        // 读取整个 body 到 MemoryStream（受限于机器内存）
        long maxBytes = 4L * 1024 * 1024 * 1024; // 4GB 软上限
        if (req.ContentLength64 > maxBytes)
        {
            await WriteJsonAsync(ctx, 413, new { error = "请求体超过 4GB 限制" });
            return;
        }

        using var ms = new MemoryStream();
        await req.InputStream.CopyToAsync(ms, 81920);
        var body = ms.ToArray();

        // 处理
        var result = await _receiver.HandleAsync(body, contentType, deviceId);

        // 返回 JSON
        if (result.Files.Count == 0 && result.Errors.Count > 0)
        {
            await WriteJsonAsync(ctx, 400, new { error = "no_files", details = result.Errors });
            return;
        }

        await WriteJsonAsync(ctx, 200, new
        {
            success = true,
            count = result.Files.Count,
            files = result.Files.Select(f => new
            {
                name = f.SavedName,
                size = f.Size,
                device = f.DeviceName,
                folder = $"{f.DeviceName}/{f.DateFolder}",
            }),
            errors = result.Errors,
        });

        _onUploadCompleted?.Invoke();
    }

    private static async Task WriteJsonAsync(System.Net.HttpListenerContext ctx, int status, object payload)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入资源: {resourceName}");
        using var sr = new StreamReader(s, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        Stop();
        _server.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// HttpListener 启停 + 多请求并发。用 TaskCompletionSource 控制优雅停止。
/// </summary>
internal sealed class HttpServer : IDisposable
{
    private readonly System.Net.HttpListener _listener;
    private readonly Func<System.Net.HttpListenerContext, Task> _handler;

    public HttpServer(string url, Func<System.Net.HttpListenerContext, Task> handler)
    {
        _listener = new System.Net.HttpListener();
        _listener.Prefixes.Add(url);
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        Logger.Info($"HttpListener 已启动: {_listener.Prefixes.First()}");

        while (!ct.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ct.IsCancellationRequested || ex is System.Net.HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error("HttpListener 接收异常", ex);
                continue;
            }

            // 每个请求独立 Task（不阻塞接收循环）
            _ = Task.Run(async () =>
            {
                try { await _handler(ctx); }
                catch (Exception ex) { Logger.Error("请求处理异常", ex); }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }, ct);
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
