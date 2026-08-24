using System.Windows;
using System.Windows.Threading;

namespace LanFileShare;

/// <summary>
/// App.xaml 的 code-behind：捕获全局异常，不让应用闪退。
/// 纯 WPF 模式（<UseWindowsForms> 已关闭），所有名称都唯一。
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Logger.Error("未捕获异常", e.Exception);
        MessageBox.Show(
            $"程序内部异常：\n{e.Exception.Message}\n\n应用会继续运行，但本次操作可能未完成。",
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
