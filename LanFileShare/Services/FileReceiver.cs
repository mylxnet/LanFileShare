using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanFileShare.Models;

namespace LanFileShare.Services;

/// <summary>
/// 文件接收核心逻辑：负责解析 multipart、校验类型、按手机/日期建目录、流式写盘。
///
/// 实现为「边收边解析」：从 NetworkStream 按块读取，维护一个滑动窗口，
/// 找到 part 头后直接把文件体逐块写入磁盘 —— 任意请求体大小都只占用固定内存（1MB 窗口），
/// 不再把整个 body 缓存到内存（旧实现 new byte[bodyLength] 有 200MB 上限且内存峰值高）。
///
/// 防御：
///   - 设备名归一化后校验最终路径仍在保存根内（防 '..' / 绝对路径 / 非法字符逃逸）
///   - 文件名双重清理：去控制字符 + 非法字符替换 + 长度限制；支持 RFC 5987 filename*
///   - Content-Length 与实收字节严格对账，截断即报错并删除半截文件
/// </summary>
public sealed class FileReceiver
{
    private string _saveRoot;

    /// <summary>单个上传请求体上限（256MB，防止恶意 Content-Length 无限喂字节）。</summary>
    public const long MaxBodyBytes = 256L * 1024 * 1024;

    /// <summary>窗口大小：需容纳「最大 part 头 + 2×boundary + CRLF」；HTTP 头上限 64KB → 1MB 足够。</summary>
    private const int WindowSize = 1024 * 1024;

    public FileReceiver(string saveRoot)
    {
        _saveRoot = saveRoot;
    }

    public void UpdateSaveRoot(string saveRoot)
    {
        if (string.IsNullOrWhiteSpace(saveRoot))
            throw new ArgumentException("保存路径不能为空", nameof(saveRoot));
        Interlocked.Exchange(ref _saveRoot, saveRoot);
        Logger.Info($"[FileReceiver] save root updated: {saveRoot}");
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
    /// 从 NetworkStream 流式接收并解析 multipart，逐块写盘。
    /// </summary>
    public async Task<Result> HandleUploadAsync(NetworkStream stream, long contentLength, string deviceId, string contentType)
    {
        Logger.Info($"[FileReceiver] enter: contentLength={contentLength}, deviceId={deviceId}, contentType={contentType}");
        try
        {
            if (contentLength <= 0)
            {
                Logger.Warn("[FileReceiver] contentLength<=0, rejecting");
                return Failure("missing Content-Length");
            }
            if (contentLength > MaxBodyBytes)
            {
                Logger.Warn($"[FileReceiver] body too large: {contentLength} > {MaxBodyBytes}");
                return Failure($"文件过大：单次请求上限 {MaxBodyBytes / 1024 / 1024}MB");
            }

            // ===== 目录穿越防御：归一化后最终路径必须仍在保存根内 =====
            var sanitizedDevice = SanitizeFolder(deviceId);
            if (string.IsNullOrEmpty(sanitizedDevice))
                sanitizedDevice = "未命名设备";
            // 纯点设备名（"." / ".." / "..."）是典型的穿越尝试：SanitizeFolder 虽会把点
            // 映射成下划线（如 ".." → "__"），安全但产生无意义目录，这里显式拒绝。
            if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Trim().All(c => c == '.'))
            {
                Logger.Warn($"[FileReceiver] 纯点设备名（穿越尝试），拒绝: '{deviceId}'");
                return Failure("无效的设备名");
            }
            var deviceDir = SafeJoin(_saveRoot, sanitizedDevice, DateTime.Now.ToString("yyyy-MM-dd"));
            if (deviceDir == null)
            {
                Logger.Warn($"[FileReceiver] 设备名解析后逃逸保存根，拒绝: '{deviceId}' -> '{sanitizedDevice}'");
                return Failure("无效的设备名");
            }

            Logger.Info($"[FileReceiver] save-target: {deviceDir}");
            Directory.CreateDirectory(deviceDir);

            var boundary = ExtractBoundary(contentType);
            if (string.IsNullOrEmpty(boundary))
            {
                Logger.Warn($"[FileReceiver] no boundary in Content-Type: {contentType}");
                return Failure("不是有效的 multipart 上传");
            }

            var dateFolder = Path.GetFileName(deviceDir)!;                          // yyyy-MM-dd
            var deviceFolder = Path.GetFileName(Path.GetDirectoryName(deviceDir))!; // sanitizedDevice
            var result = await StreamMultipartAsync(stream, contentLength, boundary, deviceDir, deviceFolder, dateFolder);
            Logger.Info($"[FileReceiver] saved {result.Files.Count} file(s)");
            return result;
        }
        catch (Exception ex)
        {
            Logger.Error("HandleUploadAsync 异常", ex);
            return Failure(ex.Message);
        }
    }

    private static Result Failure(string error)
    {
        var result = new Result();
        result.Errors.Add(error);
        return result;
    }

    /// <summary>
    /// 安全拼接子路径：规范化后的最终路径必须仍在 root 下，否则返回 null。
    /// 防御 '..'、绝对路径、根目录名、非法字符残留逃逸。
    /// </summary>
    internal static string? SafeJoin(string root, string segment1, string segment2)
    {
        try
        {
            var fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString()) &&
                !fullRoot.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                fullRoot += Path.DirectorySeparatorChar;

            var combined = Path.GetFullPath(Path.Combine(fullRoot, segment1, segment2));
            if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return null;
            // 额外防御：拼出来的路径不允许恰好等于 root 本身（比如段全是 '.'）
            if (combined.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                return null;
            return combined;
        }
        catch
        {
            return null;
        }
    }

    // =====================================================================
    // 流式 multipart 解析
    //
    // 状态机：
    //   SeekBoundary   → 找 "--boundary"（首块）或 part 之间的 "\r\n--boundary"
    //   ReadPartHeader → 读到 "\r\n\r\n" 为止（part 头，量小，攒在内存）
    //   StreamPartBody → 找 "\r\n--boundary"，找到前把安全区间的字节逐块写盘
    //   Done           → 终止 boundary "--boundary--"
    //
    // 实现：一个 1MB 滑动窗口。每次从网络补块后用 SIMD 向量化 Span 扫描找模式；
    // 模式未完整命中就保留尾部（needle 长度-1 字节）等下一块补齐。
    // =====================================================================

    private enum ParseState { SeekBoundary, ReadPartHeader, StreamPartBody, Done }

    /// <summary>滑动窗口：连续缓冲 + 有效区间 [head, tail)。扫描用 Span 零拷贝。</summary>
    private sealed class Window
    {
        private readonly byte[] _buf = new byte[WindowSize];
        private int _head;   // 窗口内有效数据起点
        private int _tail;   // 窗口内有效数据终点（不含）

        public int Count => _tail - _head;
        public byte[] Buffer => _buf;
        public int Head => _head;
        public ReadOnlySpan<byte> Span => new(_buf, _head, Count);

        /// <summary>丢掉窗口前 n 字节。</summary>
        public void Skip(int n)
        {
            if (n <= 0) return;
            n = Math.Min(n, Count);
            _head += n;
            if (_head == _tail) { _head = 0; _tail = 0; }
        }

        /// <summary>尽量从流里补数据。返回本次补到的字节数（0 = EOF）。</summary>
        public async Task<int> FillAsync(NetworkStream stream, CancellationToken ct)
        {
            if (_tail == _buf.Length)
            {
                if (_head > 0)
                {
                    // 压缩：把有效数据挪到开头
                    System.Buffer.BlockCopy(_buf, _head, _buf, 0, Count);
                    _tail -= _head;
                    _head = 0;
                }
                else
                {
                    // 窗口满且顶到头 —— caller 应已消费；防御性丢最旧一半
                    Skip(_buf.Length / 2);
                }
            }
            int n = await stream.ReadAsync(_buf, _tail, _buf.Length - _tail, ct);
            _tail += n;
            return n;
        }

        public int IndexOf(ReadOnlySpan<byte> needle, int from)
        {
            if (from >= Count) return -1;
            var r = Span[from..].IndexOf(needle);
            return r < 0 ? -1 : r + from;
        }

        public bool StartsWithAt(ReadOnlySpan<byte> needle, int at)
            => at >= 0 && at + needle.Length <= Count && Span[at..].StartsWith(needle);
    }

    private async Task<Result> StreamMultipartAsync(
        NetworkStream stream, long bodyLength, string boundary,
        string deviceDir, string sanitizedDevice, string dateFolder)
    {
        var boundaryLine = Encoding.ASCII.GetBytes("--" + boundary);          // 不含前置 CRLF
        var boundaryFull = Encoding.ASCII.GetBytes("\r\n--" + boundary);      // part 之间
        var crlf = Encoding.ASCII.GetBytes("\r\n");
        var dashes = Encoding.ASCII.GetBytes("--");
        var headerTerm = Encoding.ASCII.GetBytes("\r\n\r\n");

        var window = new Window();
        var state = ParseState.SeekBoundary;
        var result = new Result();
        long consumed = 0;      // 已从网络读走的总字节
        FileStream? fs = null;  // 当前正在写的文件（null = 该 part 丢弃）
        long fsWritten = 0;
        string? fsPath = null;
        string? originalName = null;

        try
        {
            // 注意：外层循环条件只看 state，不看 consumed ——
            // 最后一个字节读完后终止 boundary（--boundary--）还在窗口里等着消费，
            // 提前退出会把完整请求误判为截断。
            while (state != ParseState.Done)
            {
                // 尽量补满窗口
                int got = await window.FillAsync(stream, CancellationToken.None);
                if (got > 0) consumed += got;
                if (consumed > bodyLength)
                    throw new InvalidDataException($"请求体超出声明长度：{consumed} > {bodyLength}");

                bool progressed = false;

                while (true)
                {
                    if (state == ParseState.SeekBoundary)
                    {
                        int idx = window.IndexOf(boundaryLine, 0);
                        if (idx < 0)
                        {
                            // 保留尾部可能的半个 boundary，其余丢弃
                            int keep = Math.Min(boundaryLine.Length - 1, window.Count);
                            window.Skip(window.Count - keep);
                            break;
                        }

                        int after = idx + boundaryLine.Length;
                        // 数据不足以判定是 CRLF 还是 -- → 等下一块（EOF 则报截断）
                        if (window.Count < after + 2)
                        {
                            if (got == 0) throw new IOException("unexpected EOF: boundary 后数据不足");
                            window.Skip(idx);
                            break;
                        }
                        if (window.StartsWithAt(crlf, after))
                        {
                            window.Skip(after + 2); // 吃掉 boundary + CRLF
                            state = ParseState.ReadPartHeader;
                            progressed = true;
                            continue;
                        }
                        if (window.StartsWithAt(dashes, after))
                        {
                            // 终止 boundary "--boundary--"
                            state = ParseState.Done;
                            progressed = true;
                            break;
                        }
                        // 数据里恰好出现 boundary 字样但既非行首边界也非终止 → 跳过这个假命中
                        window.Skip(idx + 1);
                        progressed = true;
                    }
                    else if (state == ParseState.ReadPartHeader)
                    {
                        int idx = window.IndexOf(headerTerm, 0);
                        // part 头必须完整保留（跨块累积），不能像 preamble/body 那样丢头留尾。
                        // 上限 64KB：无论终止符是否已在窗口（一次大块送达时 idx>=0），
                        // 累计头长度超限即拒 —— 否则限制只在慢速交付时生效，行为不确定。
                        int headerLen = idx >= 0 ? idx : window.Count;
                        if (headerLen > 64 * 1024)
                            throw new InvalidDataException("part 头过大");
                        if (idx < 0)
                            break;

                        var partHeader = Encoding.UTF8.GetString(window.Buffer, window.Head, idx);
                        window.Skip(idx + headerTerm.Length);
                        state = ParseState.StreamPartBody;
                        progressed = true;

                        var filename = ExtractFilename(partHeader);
                        Logger.Info($"[FileReceiver] part: filename={filename ?? "(null)"}");

                        if (filename == null || !AllowedExtensions.IsAllowed(filename))
                        {
                            if (filename != null)
                                Logger.Warn($"类型不允许: {filename}");
                            fs = null; // 该 part 内容直接丢弃
                        }
                        else
                        {
                            var origName = Path.GetFileName(filename);
                            if (string.IsNullOrEmpty(origName))
                                origName = $"upload-{DateTime.Now:HHmmssfff}.bin";
                            origName = SanitizeFileName(origName);
                            originalName = origName;

                            // 直接拿到已用 CreateNew 打开的流（占位即打开，避免双开竞态）
                            (fs, fsPath) = OpenConflictFree(deviceDir, origName);
                            fsWritten = 0;
                        }
                    }
                    else if (state == ParseState.StreamPartBody)
                    {
                        int idx = window.IndexOf(boundaryFull, 0);
                        // boundaryFull 的前 2 字节（CRLF）属于 part body 的结尾 → 安全区到 idx
                        int safe = idx >= 0 ? idx : window.Count - (boundaryFull.Length - 1);

                        if (safe > 0)
                        {
                            if (fs != null)
                            {
                                // 零拷贝：直接写窗口内部缓冲的 [Head, Head+safe)
                                await fs.WriteAsync(window.Buffer, window.Head, safe);
                                fsWritten += safe;
                            }
                            window.Skip(safe);
                            progressed = true;
                        }

                        if (idx >= 0)
                        {
                            // part 结束：跳过 CRLF（boundaryFull 的前 2 字节）
                            window.Skip(2);
                            state = ParseState.SeekBoundary;

                            if (fs != null)
                            {
                                await fs.FlushAsync();
                                fs.Dispose();
                                fs = null;
                                Logger.Info($"已保存: {fsPath} ({fsWritten} bytes), 设备={sanitizedDevice}");
                                result.Files.Add(new SavedFile
                                {
                                    DeviceName = sanitizedDevice,
                                    DateFolder = dateFolder,
                                    OriginalName = originalName!,
                                    SavedName = Path.GetFileName(fsPath!),
                                    FullPath = fsPath!,
                                    Size = fsWritten
                                });
                                originalName = null;
                            }
                            // 关键：终止 boundary 可能已在窗口里（小请求一次到齐），
                            // 必须 continue 让 SeekBoundary 立即处理；
                            // 若 break 回外层补块，客户端已发完数据等响应，
                            // ReadAsync 会永久阻塞 → 死锁直到客户端超时。
                            continue;
                        }
                        break; // 边界未出现，需要补数据
                    }
                    else // Done
                    {
                        break;
                    }
                }

                // EOF 且本轮无任何进展、状态机又没到终点 → 请求体被截断
                if (got == 0 && !progressed && state != ParseState.Done)
                    throw new IOException($"unexpected EOF: 已收 {consumed}/{bodyLength} 字节（multipart 未收完）");
            }

            // 收尾对账：正常结束必须走过终止 boundary，否则视为截断
            if (state != ParseState.Done)
                throw new IOException("请求体被截断（multipart 未正常收尾）");
        }
        finally
        {
            if (fs != null)
            {
                var p = fsPath!;
                fs.Dispose();
                try { File.Delete(p); } catch { } // 半截文件不留残骸
            }
        }
        return result;
    }

    /// <summary>
    /// 并发安全的冲突解决：ResolveConflict 选名 + FileMode.CreateNew 直接打开并返回流。
    /// CreateNew 与探测之间存在竞态（两个并发同名上传），IOException 则递增序号重试。
    /// </summary>
    /// <returns>(已打开的写流, 完整路径)；重试耗尽返回 (null, null)</returns>
    private static (FileStream? fs, string? path) OpenConflictFree(string dir, string desired)
    {
        var candidate = ResolveConflict(dir, desired);
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var fullPath = Path.Combine(dir, candidate);
            try
            {
                var fs = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                return (fs, fullPath);
            }
            catch (IOException) when (File.Exists(fullPath))
            {
                // 被并发请求抢先 → 换下一个序号
                candidate = NextCandidate(candidate);
                continue;
            }
        }
        return (null, null);
    }

    private static string NextCandidate(string current)
    {
        var stem = Path.GetFileNameWithoutExtension(current);
        var ext = Path.GetExtension(current);
        // 形如 stem(n).ext → stem(n+1).ext；否则 stem(1).ext
        if (stem.EndsWith(")") && stem.LastIndexOf('(') is int i && i >= 0 &&
            int.TryParse(stem[(i + 1)..^1], out var n))
            return $"{stem[..i]}({n + 1}){ext}";
        return $"{stem}(1){ext}";
    }

    private static string ResolveConflict(string dir, string desired)
    {
        if (!File.Exists(Path.Combine(dir, desired))) return desired;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (int n = 1; n < 10000; n++)
        {
            var candidate = $"{stem}({n}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
        return $"{stem}({DateTime.Now:HHmmssfff}){ext}";
    }

    private static string ExtractBoundary(string header)
    {
        // Content-Type: multipart/form-data; boundary=----WebKitFormBoundaryxxx
        var idx = header.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var start = idx + "boundary=".Length;
        var end = header.IndexOfAny(new[] { '\r', '\n', ';', ' ' }, start);
        if (end < 0) end = header.Length;
        var b = header[start..end].Trim();
        // 兼容带引号的 boundary
        if (b.Length >= 2 && b[0] == '"' && b[^1] == '"') b = b[1..^1];
        return b;
    }

    private static string? ExtractFilename(string partHeader)
    {
        // Content-Disposition: form-data; name="file"; filename="xxx.jpg"
        // 兼容 RFC 5987: filename*=UTF-8''xxx（浏览器对非 ASCII 文件名常用此形式）
        var cdIdx = partHeader.IndexOf("Content-Disposition", StringComparison.OrdinalIgnoreCase);
        if (cdIdx < 0) return null;

        var fnStarIdx = partHeader.IndexOf("filename*=", cdIdx, StringComparison.OrdinalIgnoreCase);
        if (fnStarIdx >= 0)
        {
            var start = fnStarIdx + "filename*=".Length;
            var end = partHeader.IndexOfAny(new[] { '\r', '\n', ';', ' ' }, start);
            if (end < 0) end = partHeader.Length;
            var raw = partHeader[start..end].Trim().Trim('"');
            // 形如 UTF-8''%E6%B5%8B%E8%AF%95.pdf
            int tick = raw.IndexOf("''", StringComparison.Ordinal);
            var encoded = tick >= 0 ? raw[(tick + 2)..] : raw;
            try
            {
                var decoded = Uri.UnescapeDataString(encoded);
                if (!string.IsNullOrWhiteSpace(decoded)) return decoded;
            }
            catch { /* fallthrough to filename= */ }
        }

        var fnIdx = partHeader.IndexOf("filename=", cdIdx, StringComparison.OrdinalIgnoreCase);
        if (fnIdx < 0) return null;
        var s = fnIdx + "filename=".Length;
        if (s >= partHeader.Length) return null;
        char quote = partHeader[s];
        if (quote != '"' && quote != '\'')
        {
            // 无引号形式：取到行尾/分号
            var e2 = partHeader.IndexOfAny(new[] { '\r', '\n', ';', ' ' }, s);
            if (e2 < 0) e2 = partHeader.Length;
            var v = partHeader[s..e2].Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        s++;
        var end2 = partHeader.IndexOf(quote, s);
        if (end2 < 0) return null;
        return partHeader[s..end2];
    }

    private static string SanitizeFolder(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        bool onlyDots = s.All(c => c == '.');
        var bad = Path.GetInvalidPathChars();
        var arr = s.Select(c => bad.Contains(c) || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || (c == '.' && onlyDots) ? '_' : c);
        var t = new string(arr.ToArray()).Trim();
        if (t.Length > 50) t = t.Substring(0, 50);
        return string.IsNullOrWhiteSpace(t) ? "" : t;
    }

    private static string SanitizeFileName(string s)
    {
        // 去掉控制字符（0x00-0x1F）再替换非法字符
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c)) continue;
            sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        }
        var t = sb.ToString().Trim();
        if (t.Length > 200) t = t.Substring(0, 200);
        return string.IsNullOrWhiteSpace(t) ? $"upload-{DateTime.Now:HHmmssfff}.bin" : t;
    }
}
