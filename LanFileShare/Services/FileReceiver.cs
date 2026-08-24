using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using LanFileShare.Helpers;
using LanFileShare.Models;

namespace LanFileShare.Services;

/// <summary>
/// 文件接收核心逻辑：负责解析 multipart、校验类型、按手机/日期建目录、流式写盘。
/// </summary>
public sealed class FileReceiver
{
    private readonly string _saveRoot;

    public FileReceiver(string saveRoot)
    {
        _saveRoot = saveRoot;
    }

    public sealed class SavedFile
    {
        public string DeviceName { get; set; } = "";
        public string DateFolder { get; set; } = "";
        public string OriginalName { get; set; } = "";
        public string SavedName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public long Size { get; set; }
    }

    public sealed class Result
    {
        public List<SavedFile> Files { get; } = new();
        public List<string> Errors { get; } = new();
        public bool HasFile => Files.Count > 0;
    }

    /// <summary>
    /// 处理 POST 上传请求体。
    /// </summary>
    /// <param name="body">原始 multipart body 字节（HttpListenerRequest 的完整输入流已读完）</param>
    /// <param name="contentType">Content-Type 头（含 multipart; boundary=...）</param>
    /// <param name="deviceId">手机端提供的设备标识（X-Device-Id 头）</param>
    public async Task<Result> HandleAsync(byte[] body, string contentType, string deviceId)
    {
        var result = new Result();
        var sanitizedDevice = SanitizeFolder(deviceId);
        if (string.IsNullOrEmpty(sanitizedDevice))
            sanitizedDevice = "未命名设备";

        var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
        var deviceDir = Path.Combine(_saveRoot, sanitizedDevice, dateFolder);
        Directory.CreateDirectory(deviceDir);

        var parser = new MultipartParser(body, contentType);

        foreach (var part in parser.Parse())
        {
            if (!part.IsFile) continue;

            // 1) 校验扩展名
            if (!AllowedExtensions.IsAllowed(part.FileName))
            {
                result.Errors.Add($"类型不允许: {part.FileName}");
                continue;
            }

            // 2) 提取原始文件名（去路径）、清理非法字符
            var origName = Path.GetFileName(part.FileName);
            if (string.IsNullOrEmpty(origName))
            {
                origName = $"upload-{DateTime.Now:HHmmssfff}.bin";
            }
            origName = SanitizeFileName(origName);

            // 3) 解决冲突重命名
            var finalName = ResolveConflict(deviceDir, origName);
            var fullPath = Path.Combine(deviceDir, finalName);

            // 4) 流式写盘
            try
            {
                using (var src = parser.OpenBodyStream(part))
                using (var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await src.CopyToAsync(fs, 81920);
                }

                var info = new FileInfo(fullPath);
                result.Files.Add(new SavedFile
                {
                    DeviceName = sanitizedDevice,
                    DateFolder = dateFolder,
                    OriginalName = origName,
                    SavedName = finalName,
                    FullPath = fullPath,
                    Size = info.Length,
                });
                Logger.Info($"已保存: {fullPath} ({info.Length} bytes), 设备={sanitizedDevice}");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"写入失败 {origName}: {ex.Message}");
                Logger.Error($"写盘失败 {fullPath}", ex);
                // 清理半成品
                try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
            }
        }

        return result;
    }

    /// <summary>注册表同步入口，便于日志反馈</summary>
    private static string ResolveConflict(string dir, string desired)
    {
        var candidate = desired;
        if (!File.Exists(Path.Combine(dir, candidate))) return candidate;

        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);

        // 已有同名 → 找最大 N，新文件叫 (N+1)；但同时需要考虑原始文件也占用"1 号位"
        int maxN = 0;
        foreach (var f in Directory.EnumerateFiles(dir, stem + "*" + ext))
        {
            var fn = Path.GetFileNameWithoutExtension(f);
            if (fn.Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                // 原文件存在，下一个就是 (1)
                maxN = Math.Max(maxN, 1);
            }
            else if (fn.StartsWith(stem + "(", StringComparison.OrdinalIgnoreCase) &&
                     fn.EndsWith(")", StringComparison.OrdinalIgnoreCase))
            {
                var mid = fn.Substring(stem.Length + 1, fn.Length - stem.Length - 2);
                if (int.TryParse(mid, out var n))
                    maxN = Math.Max(maxN, n + 1);
            }
        }

        if (maxN == 0) return desired;
        return stem + "(" + maxN + ")" + ext;
    }

    private static string SanitizeFolder(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var bad = Path.GetInvalidPathChars();
        var arr = s.Select(c => bad.Contains(c) || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|' ? '_' : c);
        var t = new string(arr.ToArray()).Trim();
        // 限制长度
        if (t.Length > 50) t = t.Substring(0, 50);
        return string.IsNullOrWhiteSpace(t) ? "" : t;
    }

    private static string SanitizeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var arr = s.Select(c => bad.Contains(c) ? '_' : c);
        var t = new string(arr.ToArray());
        if (t.Length > 200) t = t.Substring(0, 200);
        return t;
    }
}
