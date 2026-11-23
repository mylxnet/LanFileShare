using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private List<NetworkCandidate> _nicCandidates = new();
    private NetworkCandidate? _selectedNic;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{GetApplicationVersion()} · by Mr lin";
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Activated += MainWindow_Activated;
    }

    private static string GetApplicationVersion()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未知版本";
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Load();

            // 1) 启动托盘
            _tray = new TrayIcon(ShowWindow, ExitApp);

            // 2) 没有有效保存路径时不再弹窗打断，先把窗口交给用户，由界面选择
            if (string.IsNullOrEmpty(_settings.Current.SavePath) || !Directory.Exists(_settings.Current.SavePath))
            {
                PathText.Text = "（未选择保存路径，请点击更改）";
                UrlText.Text = "请先选择保存目录";
                NicButton.IsEnabled = false;
                Logger.Info("未配置有效保存路径，等待用户在界面选择");
                return;
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
        if (_httpServer != null) return;

        // 枚举候选网卡：优先用上次手动选择的 IP，否则自动挑最佳
        _nicCandidates = IpDiscovery.GetCandidates();
        var preferredIp = _settings.Current.PreferredIp;
        _selectedNic = _nicCandidates.FirstOrDefault(c => c.Ip == preferredIp)
                       ?? _nicCandidates.FirstOrDefault();
        var preferredMissing = !string.IsNullOrEmpty(preferredIp) && _selectedNic?.Ip != preferredIp;
        if (preferredMissing)
        {
            // 上次选择的网卡已不存在，清掉记录，避免每次启动都提示
            _settings.Current.PreferredIp = "";
            _settings.Save();
        }
        var primaryIp = _selectedNic?.Ip ?? IpDiscovery.GetLocalIp();
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
        ShowQrAndUrl(url);

        PathText.Text = _settings.Current.SavePath;
        UpdateNicButton();
        if (_nicCandidates.Count == 0)
            SetNicHint("未发现可用网卡");
        else if (preferredMissing)
            SetNicHint("原网卡不可用，已自动切换");
        else
            SetNicHint("");
        await Task.CompletedTask;
    }

    /// <summary>渲染网址与二维码（切换网卡时复用）。</summary>
    private void ShowQrAndUrl(string url)
    {
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
            UrlText.ToolTip = url;
        }
        catch (Exception ex)
        {
            Logger.Error("QR generation failed (server is fine, just no QR)", ex);
            UrlText.Text = url + "  (QR failed)";
            UrlText.ToolTip = url;
        }
    }

    /// <summary>网卡按钮文案：默认只显示「网卡」，手动选过之后才显示网卡名。</summary>
    private void UpdateNicButton()
    {
        bool manual = !string.IsNullOrEmpty(_settings.Current.PreferredIp) && _selectedNic != null;
        if (manual && _selectedNic != null)
        {
            NicButton.Content = _selectedNic.IsVirtual
                ? $"{_selectedNic.InterfaceName} · 虚拟 ▾"
                : $"{_selectedNic.InterfaceName} ▾";
        }
        else
        {
            NicButton.Content = "网卡 ▾";
        }
        NicButton.IsEnabled = _httpServer != null && _nicCandidates.Count > 0;
    }

    private void SetNicHint(string text)
    {
        NicHint.Text = text;
        NicHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>单击「网卡」弹出候选菜单。</summary>
    private void OnNicButtonClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = NicButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        if (_nicCandidates.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "未发现可用网卡", IsEnabled = false });
        }
        else
        {
            foreach (var c in _nicCandidates)
            {
                var item = new MenuItem
                {
                    Header = c.Display,
                    IsCheckable = true,
                    IsChecked = _selectedNic?.Ip == c.Ip,
                    Tag = c,
                };
                item.Click += OnNicMenuItemClick;
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    /// <summary>用户手动选定网卡：保存选择、显示网卡名，并刷新地址与二维码。</summary>
    private void OnNicMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: NetworkCandidate item }) return;
        if (_selectedNic?.Ip == item.Ip && _settings.Current.PreferredIp == item.Ip) return;

        _selectedNic = item;
        _settings.Current.PreferredIp = item.Ip;
        _settings.Save();
        Logger.Info($"用户选择网卡：{item.Ip}（{item.InterfaceName}）");

        UpdateNicButton();
        SetNicHint("");
        UpdateUrlAndQr(item.Ip);
    }

    private void UpdateUrlAndQr(string ip)
    {
        if (_httpServer == null) return;
        var url = $"http://{ip}:{_httpServer.Port}/";
        Logger.Info($"网卡切换后 URL：{url}");
        ShowQrAndUrl(url);
    }

    /// <summary>窗口重新激活时刷新网卡列表，兼容中途插网线 / 开热点。</summary>
    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_httpServer == null) return;

        var latest = IpDiscovery.GetCandidates();
        if (latest.Select(c => c.Ip).SequenceEqual(_nicCandidates.Select(c => c.Ip))) return;

        var currentIp = _selectedNic?.Ip ?? "";
        _nicCandidates = latest;
        bool stillThere = latest.Any(c => c.Ip == currentIp);

        if (stillThere)
        {
            _selectedNic = latest.First(c => c.Ip == currentIp);
            UpdateNicButton();
            return;
        }

        // 原网卡已消失：回退到最佳候选，并清掉失效的偏好
        bool wasManual = !string.IsNullOrEmpty(currentIp) && _settings.Current.PreferredIp == currentIp;
        _selectedNic = latest.FirstOrDefault();
        if (wasManual)
        {
            _settings.Current.PreferredIp = "";
            _settings.Save();
        }
        UpdateNicButton();

        if (_selectedNic != null)
        {
            SetNicHint(wasManual ? "原网卡不可用，已自动切换" : "");
            UpdateUrlAndQr(_selectedNic.Ip);
        }
        else
        {
            SetNicHint("未发现可用网卡");
        }
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

    private async void OnChangePathClick(object sender, RoutedEventArgs e)
    {
        if (!ChooseAndSavePath()) return;

        // 首次选择目录后才启动服务，避免二维码指向一个还不能接收的页面
        if (_httpServer == null)
            await StartHttpServerAsync();
    }

    private bool ChooseAndSavePath()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose save folder",
            InitialDirectory = SafeInitialDir(_settings.Current.SavePath),
            Multiselect = false,
        };
        if (dlg.ShowDialog() != true) return false;

        _settings.Current.SavePath = dlg.FolderName;
        _settings.Save();
        _httpServer?.UpdateSavePath(_settings.Current.SavePath);
        PathText.Text = _settings.Current.SavePath;
        Logger.Info($"Save folder changed: {_settings.Current.SavePath}");
        return true;
    }

    private static string SafeInitialDir(string? current)
    {
        if (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            return current;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, "Documents");
    }

    private void OnUploadCompleted(IReadOnlyList<FileReceiver.SavedFile> files)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var names = string.Join("、", files.Select(file => file.SavedName));
            _tray?.ShowBalloon("文件上传完成", $"{files.Count} 个文件已保存：{names}");
        });
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
