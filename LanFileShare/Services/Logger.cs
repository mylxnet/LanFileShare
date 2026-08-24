using System;
using System.IO;
using System.Text;

namespace LanFileShare.Services;

/// <summary>
/// 简易日志：写文件 + 7 天滚动删除。线程安全。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static readonly string LogDir = SettingsStore.GetLogsDir();
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;   // 单文件 5 MB
    private const int RetentionDays = 7;

    private static void Write(string level, string message, Exception? ex = null)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        if (ex != null) line += Environment.NewLine + ex.ToString();

        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                var file = Path.Combine(LogDir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
                // 单文件超阈值则换名
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxFileSizeBytes)
                    file = Path.Combine(LogDir, $"app-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
                CleanupOld();
            }
        }
        catch { /* 日志写失败不抛出 */ }

        // 同时输出到调试器/控制台
        System.Diagnostics.Debug.WriteLine(line);
    }

    private static void CleanupOld()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in Directory.GetFiles(LogDir, "app-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", msg, ex);
}
