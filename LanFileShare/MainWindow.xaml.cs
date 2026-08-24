using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using LanFileShare.Services;

namespace LanFileShare;

/// <summary>
/// 主窗口 code-behind：装配所有服务、显示二维码、处理路径变更、最小化到托盘。
/// 纯 WPF 模式（<UseWindowsForms> 已关闭），无命名空间冲突。
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsStore _settings = new();
    private HttpFileServer? _httpServer;
    private TrayIcon? _tray;
    private bool _reallyClose;

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
                    "首次使用：请选择一个文件夹作为保存路径。\n后续启动会自动记忆。",
                    "欢迎使用",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                ChooseAndSavePath();
                if (string.IsNullOrEmpty(_settings.Current.SavePath))
                {
                    MessageBox.Show("未选择保存路径，应用将退出。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Application.Current.Shutdown();
                    return;
                }
            }

            // 3) 启动 HTTP 服务
            await StartHttpServerAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("初始化失败", ex);
            MessageBox.Show("初始化失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 同步方法：启动 HTTP 服务、生成二维码。
    /// HTTP 服务本身在后台 Task 运行，主线程完成即返回。
    /// 返回 Task.CompletedTask 是为了与调用方 await 兼容。
    /// </summary>
    private Task StartHttpServerAsync()
    {
        var ip = IpDiscovery.GetLocalIp();
        var port = PortSelector.PickFreePort(_settings.Current.LastPort);
        if (port < 0)
        {
            MessageBox.Show("9000-9100 端口全部被占用，请关闭占用端口的程序后重试。",
                "端口不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            return Task.CompletedTask;
        }

        _settings.Current.LastPort = port;
        _settings.Save();

        var url = $"http://{ip}:{port}/";
        Logger.Info($"应用启动：{url}");

        // 生成二维码
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
            Logger.Error("生成二维码失败", ex);
            UrlText.Text = "(二维码生成失败)";
        }

        // 启动服务（后台线程）
        _httpServer = new HttpFileServer($"http://+:{port}/", _settings, OnUploadCompleted);
        _ = Task.Run(async () =>
        {
            try { await _httpServer.RunAsync(); }
            catch (Exception ex) { Logger.Error("HTTP 服务异常", ex); }
        });

        PathText.Text = _settings.Current.SavePath;
        return Task.CompletedTask;
    }

    private void OnChangePathClick(object sender, RoutedEventArgs e)
    {
        ChooseAndSavePath();
    }

    /// <summary>
    /// 用 .NET 8 内置的 Microsoft.Win32.OpenFolderDialog（纯 WPF，无 WinForms）。
    /// </summary>
    private void ChooseAndSavePath()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择文件保存目录（用户N的子文件夹会自动建在这里）",
            InitialDirectory = SafeInitialDir(_settings.Current.SavePath),
            Multiselect = false,
        };
        if (dlg.ShowDialog() == true)
        {
            _settings.Current.SavePath = dlg.FolderName;
            _settings.Save();
            PathText.Text = _settings.Current.SavePath;
            Logger.Info($"保存路径已变更: {_settings.Current.SavePath}");
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
        // 主窗口静默接收，仅记录日志
        // 未来可扩展：底部状态栏显示"刚刚收到 N 个文件"
    }

    // ===== 窗口关闭 → 托盘 =====

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        Hide();
        _tray?.ShowBalloon("仍在后台运行", "双击托盘图标重新打开主界面，或右键退出。");
        Logger.Info("主窗口隐藏到托盘");
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
        try { _httpServer?.Stop(); } catch { }
        try { _httpServer?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        Dispatcher.Invoke(() => Application.Current.Shutdown());
    }
}
