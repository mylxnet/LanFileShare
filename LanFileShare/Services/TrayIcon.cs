using System;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace LanFileShare.Services;

/// <summary>
/// 系统托盘图标：用 Hardcodet.Wpf.TaskbarNotification 实现（纯 WPF，零 WinForms 依赖）。
/// 主窗口关闭时调用 Hide() 隐藏到托盘；右键菜单 / 双击恢复。
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
        aboutItem.Click += (_, _) => MessageBox.Show(
            "局域网文件快传 v1.0.0\n\n手机扫码把照片 / 文档传到电脑，\n无需注册、无需安装 App。\n\n关闭后仍在后台运行，可右键托盘图标退出。",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        menu.Items.Add(aboutItem);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => _exitApp();
        menu.Items.Add(exitItem);

        _notify.ContextMenu = menu;
        _notify.TrayMouseDoubleClick += (_, _) => _showMainWindow();
    }

    public void ShowBalloon(string title, string text, BalloonIcon icon = BalloonIcon.Info)
    {
        _notify.ShowBalloonTip(title, text, icon);
    }

    /// <summary>
    /// 程序运行时画一个 32x32 蓝色圆角图标，无外部 .ico 依赖。
    /// </summary>
    private static System.Drawing.Icon BuildAppIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, 32, 32),
                System.Drawing.Color.FromArgb(255, 37, 99, 235),
                System.Drawing.Color.FromArgb(255, 124, 58, 237), 45f);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int r = 6;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(32 - r, 0, r, r, 270, 90);
            path.AddArc(32 - r, 32 - r, r, r, 0, 90);
            path.AddArc(0, 32 - r, r, r, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);
            g.FillRectangle(System.Drawing.Brushes.White, 8, 8, 6, 6);
            g.FillRectangle(System.Drawing.Brushes.White, 18, 8, 6, 6);
            g.FillRectangle(System.Drawing.Brushes.White, 8, 18, 6, 6);
            g.FillRectangle(System.Drawing.Brushes.White, 16, 16, 4, 4);
            g.FillRectangle(System.Drawing.Brushes.White, 22, 18, 2, 2);
            g.FillRectangle(System.Drawing.Brushes.White, 18, 22, 6, 2);
        }
        var hIcon = bmp.GetHicon();
        return System.Drawing.Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notify.Visibility = Visibility.Collapsed;
        _notify.Dispose();
    }
}
