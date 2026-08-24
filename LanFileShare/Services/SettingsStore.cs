using System;
using System.IO;
using System.Text.Json;
using LanFileShare.Models;

namespace LanFileShare.Services;

/// <summary>
/// 配置持久化：JSON 文件存到 %APPDATA%\LanFileShare\settings.json
/// </summary>
public sealed class SettingsStore
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanFileShare");

    private static readonly string SettingsPath =
        Path.Combine(AppDataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AppSettings Current { get; private set; } = new();

    /// <summary>启动时调用</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取配置失败，使用默认: {ex.Message}");
            Current = new AppSettings();
        }
        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"保存配置失败: {ex.Message}");
        }
    }

    public static string GetLogsDir() => Path.Combine(AppDataDir, "logs");

    public static string GetConfigPath() => SettingsPath;
}
