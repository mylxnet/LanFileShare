using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace LanFileShare.Services;

/// <summary>
/// 在 [StartPort, EndPort] 区间内扫描找到第一个空闲端口。
/// 用 TcpListener.Bind(0) → 立即获取系统分配 → 关闭 → 即为可用。
/// </summary>
public static class PortSelector
{
    public const int StartPort = 9000;
    public const int EndPort = 9100;

    /// <summary>
    /// 优先尝试 preferred；不可用则在区间内扫描。
    /// </summary>
    /// <returns>选中的端口；整个区间都被占时返回 -1</returns>
    public static int PickFreePort(int preferred = 0)
    {
        if (preferred >= StartPort && preferred <= EndPort && IsFree(preferred))
            return preferred;

        for (int p = StartPort; p <= EndPort; p++)
        {
            if (IsFree(p))
            {
                Logger.Info($"已选用端口: {p}" + (preferred > 0 ? $"（原偏好 {preferred} 不可用）" : ""));
                return p;
            }
        }

        Logger.Error($"端口区间 {StartPort}-{EndPort} 全部被占用");
        return -1;
    }

    private static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
