using System.Net;
using System.Net.Sockets;
using System.IO;
using LanFileShare.Services;

namespace PreviewHost;

/// <summary>
/// 无头预览宿主：复用主项目 TcpHttpServer，把手机上传页挂在 http://127.0.0.1:50001/ 供 Preview 标签页实时查看。
/// 端口固定 50001（主程序首选 50000，避开以免冲突）；Ctrl+C 退出。
/// </summary>
internal static class Program
{
    public const int Port = 50001;

    private static async Task<int> Main()
    {
        // 保存根：工作区的 preview-uploads/（避免写到用户真实目录）。
        // 不硬编码 bin 深度：先按 sln 定位仓库根，找不到再退回 cwd / exe 目录。
        var wsRoot = FindWorkspaceRoot();
        var baseDir = wsRoot
            ?? (File.Exists(Path.Combine(Environment.CurrentDirectory, "LanFileShare.sln"))
                ? Environment.CurrentDirectory
                : AppContext.BaseDirectory);
        var saveRoot = Path.Combine(baseDir, "preview-uploads");
        Directory.CreateDirectory(saveRoot);

        var settings = new SettingsStore();
        settings.Current.SavePath = saveRoot;

        var server = TcpHttpServer.TryStart(Port, settings, OnUploadCompleted);
        if (server == null)
        {
            Console.Error.WriteLine($"[PreviewHost] FATAL: port {Port} not available.");
            return 1;
        }

        Console.WriteLine($"[PreviewHost] serving mobile page: http://127.0.0.1:{Port}/");
        Console.WriteLine($"[PreviewHost] save root: {saveRoot}");
        Console.WriteLine("[PreviewHost] Ctrl+C to stop.");

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
        await tcs.Task;

        server.Stop();
        server.Dispose();
        Console.WriteLine("[PreviewHost] stopped.");
        return 0;
    }

    private static void OnUploadCompleted()
        => Console.WriteLine($"[PreviewHost] upload completed -> {DateTime.Now:HH:mm:ss}");

    /// <summary>从可执行文件位置向上找含 LanFileShare.sln 的目录（仓库根），避免硬编码 bin 深度。</summary>
    private static string? FindWorkspaceRoot()
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
}
