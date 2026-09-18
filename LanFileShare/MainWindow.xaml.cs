using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using LanFileShare.Services;
namespace LanFileShare;

/// <summary>
/// 主窗口 code-behind：先确保 HTTP 服务真正启动，再生成二维码。
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsStore _settings = new();
    private TcpHttpServer? _httpServer;
    private TrayIcon? _tray;
    private bool _reallyClose;
    private bool _resourcesDisposed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Load();

            // 1) 启动托盘
            _tray = new TrayIcon(ShowWindow, ExitApp);

            // 2) 检查保存路径
            if (string.IsNullOrEmpty(_settings.Current.SavePath) || !Directory.Exists(_settings.Current.SavePath))
            {
                MessageBox.Show(
                    "First time: please choose a folder to save files.\nYour choice will be remembered next time.",
                    "Welcome", MessageBoxButton.OK, MessageBoxImage.Information);
                ChooseAndSavePath();
                if (string.IsNullOrEmpty(_settings.Current.SavePath))
                {
                    MessageBox.Show("No save folder selected, the app will exit.", "Info", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _reallyClose = true;
                    Application.Current.Shutdown();
                    return;
                }
            }

            // 3) 启动 HTTP 服务 + 生成二维码
            await StartHttpServerAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Init failed", ex);
            MessageBox.Show("Init failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 真正绑端口 → 确认成功后 → 才生成 URL 和二维码。
    /// 使用 TcpHttpServer（raw socket）而非 HttpListener，绕开 http.sys 在某些机器上的 silent failure。
    /// </summary>
    private async Task StartHttpServerAsync()
    {
        // 统一 IP 发现：优先私网、过滤虚拟网卡与链路本地（IpDiscovery 内含多层兑底）
        var primaryIp = IpDiscovery.GetLocalIp();
        Logger.Info($"Primary LAN IP for QR: {primaryIp}");

        // 候选端口顺序：先 LastPort，然后向外顺延（+1, -1, +2, -2, ...），最后从头再扫
        var candidates = BuildPortCandidates(_settings.Current.LastPort);
        Logger.Info($"Will try {candidates.Count} port candidates, starting from {_settings.Current.LastPort}");

        TcpHttpServer? server = null;
        int chosenPort = -1;
        foreach (var tryPort in candidates)
        {
            Logger.Info($"Trying port {tryPort} ...");
            // 先用 raw socket 探测（IPAddress.Any 真绑），再让 TcpHttpServer 起服务
            if (!PortProbe.IsFree(tryPort))
            {
                continue;
            }
            var s = TcpHttpServer.TryStart(tryPort, _settings, OnUploadCompleted, primaryIp);
            if (s != null)
            {
                server = s;
                chosenPort = tryPort;
                break;
            }
        }

        if (server == null || chosenPort < 0)
        {
            UrlText.Text = "(No free port)";
            MessageBox.Show(
                $"Ports {PortSelector.StartPort}-{PortSelector.EndPort} all occupied (extremely rare).\n\n"
                + "Tips:\n"
                + "1) Close apps that may occupy ports (IIS, SQL Server, Docker, ...)\n"
                + "2) Or modify PortSelector.cs and recompile\n"
                + "3) Reboot PC often frees stuck ports",
                "No Free Port", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _httpServer = server;
        _settings.Current.LastPort = chosenPort;
        _settings.Save();

        Logger.Info($"TcpHttpServer accepted-loop spun up on port {chosenPort}");

        // 等 acceptor 进 AcceptTcpClientAsync 状态
        await Task.Delay(300);

        // 回环自检：失败也只是警告，QR 照常显示
        var smokeOk = await DoSmokeTest(chosenPort);
        if (!smokeOk)
        {
            Logger.Warn($"Smoke test failed for port {chosenPort}; showing QR anyway");
        }

        // ===== 生成 QR（二维码永远显示，让手机扫码试试）=====
        var url = server.PrimaryUrl;
        Logger.Info($"HTTP server ready, URL: {url}");

        // 生成二维码（端口已确认）
        try
        {
            var png = QrGenerator.GeneratePng(url, size: 240);
            var ms = new MemoryStream(png);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            QrImage.Source = img;
            UrlText.Text = url;
        }
        catch (Exception ex)
        {
            Logger.Error("QR generation failed (server is fine, just no QR)", ex);
            UrlText.Text = url + "  (QR failed)";
        }

        PathText.Text = _settings.Current.SavePath;
        await Task.CompletedTask;
    }

    /// <summary>
    /// 端口候选顺序：先 LastPort，再 ±1, ±2, ±3, ... ，最后从头扫一遍兜底。
    /// 端口范围跟随 PortSelector，避免硬编码。
    /// </summary>
    private static List<int> BuildPortCandidates(int preferred)
    {
        int span = PortSelector.EndPort - PortSelector.StartPort + 1;
        var result = new HashSet<int>(span);
        if (preferred >= PortSelector.StartPort && preferred <= PortSelector.EndPort)
        {
            result.Add(preferred);
            for (int off = 1; off < span; off++)
            {
                int up = preferred + off;
                int dn = preferred - off;
                if (up >= PortSelector.StartPort && up <= PortSelector.EndPort) result.Add(up);
                if (dn >= PortSelector.StartPort && dn <= PortSelector.EndPort) result.Add(dn);
                if (up > PortSelector.EndPort && dn < PortSelector.StartPort) break;
            }
        }
        for (int p = PortSelector.StartPort; p <= PortSelector.EndPort; p++) result.Add(p);
        return result.ToList();
    }

    /// <summary>
    /// TCP 层面验证端口真在听：用 TcpClient.ConnectAsync 直接 TCP 握手。
    /// HttpClient 框架层有时会被 LSP/WinHTTP 拦，但 TcpClient 走裸 socket，必然能定位真实端口状态。
    /// </summary>
    private static async Task<bool> DoSmokeTest(int port)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync("127.0.0.1", port, cts.Token);
            sw.Stop();
            Logger.Info($"Smoke test: TCP 127.0.0.1:{port} connected in {sw.ElapsedMilliseconds}ms - OK");

            // 第二步：再走一次 HTTP 协议层（防 LSP 拦 socket 但能让 HTTP 过）
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var resp = await client.GetAsync($"http://127.0.0.1:{port}/test");
                var body = await resp.Content.ReadAsStringAsync();
                Logger.Info($"Smoke test: HTTP /test -> {body.Substring(0, Math.Min(60, body.Length))}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Smoke test: TCP 通但 HTTP 不通 (LSP / 杀软可能拦) — {ex.Message}");
                return true; // TCP 通就够用，手机扫码走 HTTP 不需经过 LSP
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.Error($"Smoke test: TCP 127.0.0.1:{port} failed in {sw.ElapsedMilliseconds}ms - {ex.Message}");
            return false;
        }
    }

    private void OnChangePathClick(object sender, RoutedEventArgs e)
    {
        ChooseAndSavePath();
    }

    private void ChooseAndSavePath()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose save folder",
            InitialDirectory = SafeInitialDir(_settings.Current.SavePath),
            Multiselect = false,
        };
        if (dlg.ShowDialog() == true)
        {
            _settings.Current.SavePath = dlg.FolderName;
            _settings.Save();
            PathText.Text = _settings.Current.SavePath;
            Logger.Info($"Save folder changed: {_settings.Current.SavePath}");
        }
    }

    private static string SafeInitialDir(string? current)
    {
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            return current;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Documents");
    }

    private void OnUploadCompleted()
    {
        // silent receive, log only
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClose)
        {
            _reallyClose = true;
            Logger.Info("Window closing; shutting down services");
        }

        DisposeResources();
    }

    private void ShowWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        });
    }

    private void ExitApp()
    {
        _reallyClose = true;
        DisposeResources();
        Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
            return;

        _resourcesDisposed = true;
        try { _httpServer?.Stop(); } catch (Exception ex) { Logger.Error("停止 HTTP 服务失败", ex); }
        try { _httpServer?.Dispose(); } catch (Exception ex) { Logger.Error("释放 HTTP 服务失败", ex); }
        try { _tray?.Dispose(); } catch (Exception ex) { Logger.Error("释放托盘图标失败", ex); }
        _httpServer = null;
        _tray = null;
    }
}
