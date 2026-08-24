using System.Text.Json.Serialization;

namespace LanFileShare.Models;

/// <summary>
/// 应用配置（持久化到 %APPDATA%\LanFileShare\settings.json）
/// </summary>
public sealed class AppSettings
{
    /// <summary>用户选定的保存路径，上次关闭后保留</summary>
    public string SavePath { get; set; } = "";

    /// <summary>上次成功监听的端口（启动时优先尝试，不可用则扫描空闲）</summary>
    public int LastPort { get; set; } = 0;

    /// <summary>关闭按钮是否最小化到托盘（默认开）</summary>
    public bool MinimizeToTray { get; set; } = true;

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(SavePath);
}
