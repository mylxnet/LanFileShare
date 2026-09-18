using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace LanFileShare.Services;

/// <summary>
/// 系统托盘图标：用 Hardcodet.Wpf.TaskbarNotification 实现（纯 WPF，零 WinForms 依赖）。
/// 应用运行期间提供恢复窗口、关于和退出菜单。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _notify;
    private readonly Action _showMainWindow;
    private readonly Action _exitApp;
    private bool _disposed;

    public TrayIcon(Action showMainWindow, Action exitApp)
    {
        _showMainWindow = showMainWindow;
        _exitApp = exitApp;

        _notify = new TaskbarIcon
        {
            Icon = BuildAppIcon(),
            ToolTipText = "局域网文件快传（运行中）",
            Visibility = Visibility.Visible,
        };

        // 右键菜单
        var menu = new ContextMenu();

        var openItem = new MenuItem
        {
            Header = "打开主界面",
            FontWeight = FontWeights.Bold,
        };
        openItem.Click += (_, _) => _showMainWindow();
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        var aboutItem = new MenuItem { Header = "关于" };
        aboutItem.Click += (_, _) => ShowAbout();
        menu.Items.Add(aboutItem);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => _exitApp();
        menu.Items.Add(exitItem);

        _notify.ContextMenu = menu;
        _notify.TrayMouseDoubleClick += (_, _) => _showMainWindow();
    }

    /// <summary>
    /// 显示"关于"弹窗。显式用主窗口做 owner（主窗口只是隐藏，仍可用），
    /// 不可用时回退 ownerless；弹窗本身失败也不能拖垮进程（托盘图标仍在）。
    /// </summary>
    private void ShowAbout()
    {
        Logger.Info("Tray about dialog requested");
        try
        {
            var owner = System.Windows.Application.Current?.MainWindow;
            if (owner == null || !owner.IsLoaded || !owner.IsVisible)
                owner = null; // 隐藏窗口场景：交给 Win32 用桌面做父窗口
            MessageBox.Show(
                owner,
                $"局域网文件快传 v{GetApplicationVersion()}\nby Mr lin\n\n手机扫码把照片 / 文档传到电脑，\n无需注册、无需安装 App。",
                "关于",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("关于弹窗失败（不影响应用运行）", ex);
        }
    }

    private static string GetApplicationVersion()
    {
        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "未知版本";
    }

    public void ShowBalloon(string title, string text, BalloonIcon icon = BalloonIcon.Info)
    {
        _notify.ShowBalloonTip(title, text, icon);
    }

    /// <summary>
    /// 程序运行时画一个 32x32 图标（蓝色 QR 风格 + 绿色上传箭头），无外部 .ico 依赖。
    /// </summary>
    private static System.Drawing.Icon BuildAppIcon()
    {
        // 1) 从 WPF embedded resource 加载 PNG（在 Resources/appicon.png）
        var uri = new Uri("pack://application:,,,/Resources/appicon.png");
        var info = System.Windows.Application.GetResourceStream(uri);

        // 2) 解码为 System.Drawing.Bitmap 并缩到 32x32
        using var src = new System.Drawing.Bitmap(info.Stream);
        var bmp = new System.Drawing.Bitmap(src, new System.Drawing.Size(32, 32));

        // 3) GetHicon 拿到原生 handle，再克隆 Icon（这样 bmp 释放不影响 Icon）
        var hIcon = bmp.GetHicon();
        var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        // 释放临时资源
        System.Drawing.Icon.FromHandle(hIcon).Dispose();
        bmp.Dispose();
        src.Dispose();
        return icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notify.Visibility = Visibility.Collapsed;
        _notify.Dispose();
    }
}
