using System.Collections.Generic;

namespace LanFileShare.Models;

/// <summary>
/// 允许上传的文件类型白名单（图片 + 文档）。
/// 扩展名大小写不敏感比较；ContentType 仅供 HTML 端 accept 属性使用。
/// </summary>
public static class AllowedExtensions
{
    public static readonly HashSet<string> Set = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // ===== 图片 =====
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif", ".tiff",
        // ===== 文档 =====
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".txt", ".md", ".rtf", ".csv",
        ".zip", ".rar", ".7z"
    };

    /// <summary>用于 HTML &lt;input accept&gt; 属性。必须与上方白名单同步（与 Set 完全一致）。</summary>
    public const string HtmlAccept =
        "image/*," +   // 覆盖 .jpg/.jpeg/.png/.gif/.bmp/.webp/.heic/.heif/.tiff
        ".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx," +
        ".txt,.md,.rtf,.csv," +
        ".zip,.rar,.7z";

    public static bool IsAllowed(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = System.IO.Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && Set.Contains(ext);
    }
}
