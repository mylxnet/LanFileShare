using System.Windows;
using System.Windows.Threading;
using System.Threading;

namespace LanFileShare;

/// <summary>
/// App.xaml 的 code-behind：捕获全局异常，不让应用闪退。
/// 纯 WPF 模式（<UseWindowsForms> 已关闭），所有名称都唯一。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "LanFileShare.SingleInstance";
    private const string ActivateEventName = "LanFileShare.Activate";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateRegistration;
    private bool _ownsSingleInstanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activateEvent.Set();
            }
            catch (Exception ex)
            {
                Services.Logger.Error("通知已运行实例失败", ex);
            }

            Shutdown();
            return;
        }
        _ownsSingleInstanceMutex = true;

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(ActivateMainWindow),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateRegistration?.Unregister(null);
        _activateEvent?.Dispose();
        if (_ownsSingleInstanceMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void ActivateMainWindow()
    {
        var window = MainWindow;
        if (window == null)
            return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Logger.Error("未捕获异常", e.Exception);
        try
        {
            // 显式 owner：主窗口隐藏到托盘后仍可用；不可用则回退 ownerless。
            // MessageBox 自身失败也不能把进程弹死（记日志即可）。
            var owner = Current?.MainWindow;
            if (owner == null || !owner.IsLoaded || !owner.IsVisible)
                owner = null;
            MessageBox.Show(
                owner,
                $"程序内部异常：\n{e.Exception.Message}\n\n应用会继续运行，但本次操作可能未完成。",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception notifyEx)
        {
            Services.Logger.Error("错误弹窗显示失败", notifyEx);
        }
        e.Handled = true;
    }
}
