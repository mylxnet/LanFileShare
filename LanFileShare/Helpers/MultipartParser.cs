using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanFileShare.Helpers;

/// <summary>
/// multipart/form-data 解析（简化版）。
///
/// 策略：
///   1) 把整个请求体读到 MemoryStream（64 位 .NET 单个 stream 可支撑 GB 级）
///   2) 在 MemoryStream 中扫描字节定位 boundary、headers 终止符
///   3) 文件 part 提取 (offset, length)，由 caller 用 FileStream 流式写入磁盘
///   4) 普通字段 part 同时返回字符串内容
///
/// 数据格式（RFC 2388）：
///   --&lt;boundary&gt;\r\n
///   headers...\r\n\r\n
///   body\r\n
///   --&lt;boundary&gt;\r\n
///   headers...\r\n\r\n
///   body...
///   --&lt;boundary&gt;--\r\n
///
/// 第一个 boundary 在 body 起始（无前置 \r\n），其余 boundary 都以 \r\n--&lt;boundary&gt; 出现。
/// </summary>
public sealed class MultipartParser
{
    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    public sealed class Part
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";

        /// <summary>内部：文件 part 的 body 在源流中的位置 + 长度；非文件则 IsFile = false</summary>
        public long BodyOffset { get; internal set; }
        public long BodyLength { get; internal set; }

        /// <summary>是否文件 part（含 filename）</summary>
        public bool IsFile => !string.IsNullOrEmpty(FileName);

        /// <summary>内部：非文件 part 的内容（已读到内存）</summary>
        public string TextValue { get; internal set; } = "";
    }

    private readonly MemoryStream _src;
    private readonly byte[] _delim;
    private readonly byte[] _finalDelim;
    private readonly byte[] _firstDelim;

    public MultipartParser(byte[] body, string contentType)
    {
        var boundary = ExtractBoundary(contentType)
            ?? throw new InvalidDataException("Content-Type 缺少 boundary");

        _src = new MemoryStream(body, writable: false);
        _delim = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        _firstDelim = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
        _finalDelim = Encoding.ASCII.GetBytes("--" + boundary + "--");
    }

    /// <summary>
    /// 顺序解析每个 part。每遇到文件 part，文件 part 的 (BodyOffset, BodyLength) 指向源 body 中的位置。
    /// 非文件 part 文本会读入 Part.TextValue。
    /// </summary>
    public IEnumerable<Part> Parse()
    {
        // 1) 跳过 preamble（首段之前的字节），找到第一个 boundary 行
        long firstIdx = IndexOf(0, _src.Length, _firstDelim);
        if (firstIdx < 0) yield break;

        long cursor = firstIdx + _firstDelim.Length;
        bool finished = false;

        while (!finished)
        {
            // 2) headers（直到 \r\n\r\n）
            int hdrTermIdx = IndexOf(cursor, _src.Length, Crlf);
            if (hdrTermIdx < 0) yield break;
            var headerLine = Encoding.UTF8.GetString(_src.GetBuffer(), (int)cursor, hdrTermIdx - (int)cursor);
            cursor = hdrTermIdx + 2;

            // 找结束 \r\n
            int endHdr = IndexOf(cursor, _src.Length, Crlf);
            if (endHdr < 0) yield break;
            var headerText = Encoding.UTF8.GetString(_src.GetBuffer(), (int)cursor, endHdr - (int)cursor);
            cursor = endHdr + 2;

            var part = new Part
            {
                Name = ExtractAttr(headerText, "Content-Disposition", "name"),
                FileName = ExtractAttr(headerText, "Content-Disposition", "filename"),
                ContentType = ExtractHeader(headerText, "Content-Type") ?? "application/octet-stream",
                BodyOffset = cursor,
            };

            // 3) 找下一个 delim（可能是 \r\n--<boundary> 或 \r\n--<boundary>--）
            int nextDelim = IndexOf(cursor, _src.Length, _delim);
            if (nextDelim < 0) yield break;

            // 检查是否 final
            long bodyEnd = nextDelim;
            int finalCheck = nextDelim + 2; // 跳到 _delim 末尾 "--..." 部分
            if (finalCheck + 2 <= _src.Length)
            {
                var src = _src.GetBuffer();
                // _delim 是 \r\n--<boundary>
                // finalDelim 是 --<boundary>--
                // nextDelim 起 delim.Length 个字节构成 _delim
                // 再往后的 _finalDelim.Length 字节如果 = finalDelim 则是末段
                var test = new byte[_finalDelim.Length];
                int offsetToFinal = nextDelim + _delim.Length - _finalDelim.Length;
                if (offsetToFinal >= 0 && offsetToFinal + _finalDelim.Length <= _src.Length)
                {
                    Array.Copy(src, offsetToFinal, test, 0, _finalDelim.Length);
                    if (BytesEqual(test, _finalDelim))
                    {
                        finished = true;
                    }
                }
            }

            long bodyLen = bodyEnd - cursor;
            part.BodyLength = bodyLen;

            if (!part.IsFile)
            {
                part.TextValue = Encoding.UTF8.GetString(_src.GetBuffer(), (int)cursor, (int)bodyLen);
            }

            yield return part;

            // 推进 cursor：跳过 _delim
            cursor = nextDelim + _delim.Length;
        }
    }

    /// <summary>
    /// 用源 byte[] 作只读流，从 (offset, length) 范围产生一个 Stream。
    /// </summary>
    public Stream OpenBodyStream(Part p)
    {
        return new MemoryStream(_src.GetBuffer(), (int)p.BodyOffset, (int)p.BodyLength, writable: false);
    }

    // ========== 字节搜索 ==========

    private int IndexOf(long fromInclusive, long toExclusive, byte[] needle)
    {
        if (_src.Length < needle.Length) return -1;
        var b = _src.GetBuffer();
        long nlen = needle.Length;
        long start = Math.Max(fromInclusive, 0);
        long end = toExclusive - nlen;
        if (end < start) return -1;

        for (long i = start; i <= end; i++)
        {
            bool m = true;
            for (int j = 0; j < nlen; j++)
                if (b[i + j] != needle[j]) { m = false; break; }
            if (m) return (int)i;
        }
        return -1;
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static string? ExtractBoundary(string contentType)
    {
        foreach (var p in contentType.Split(';'))
        {
            var t = p.Trim();
            if (t.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                var b = t.Substring("boundary=".Length);
                if (b.StartsWith("\"") && b.EndsWith("\"")) b = b.Substring(1, b.Length - 2);
                return b;
            }
        }
        return null;
    }

    private static string? ExtractHeader(string raw, string name)
    {
        foreach (var line in raw.Split(new[] { "\r\n" }, StringSplitOptions.None))
        {
            if (string.IsNullOrEmpty(line)) continue;
            var idx = line.IndexOf(':');
            if (idx < 0) continue;
            var k = line.Substring(0, idx).Trim();
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(idx + 1).Trim();
        }
        return null;
    }

    private static string ExtractAttr(string raw, string headerName, string attrName)
    {
        var val = ExtractHeader(raw, headerName) ?? "";
        foreach (var part in val.Split(';'))
        {
            var kv = part.Trim().Split(new[] { '=' }, 2);
            if (kv.Length != 2) continue;
            var k = kv[0].Trim();
            var v = kv[1].Trim().Trim('"');
            if (string.Equals(k, attrName, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return "";
    }
}
