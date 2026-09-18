using System;
using System.Net;
using System.Net.Sockets;

namespace LanFileShare.Services;

/// <summary>
/// 端口可用性探测（裸 socket + IPAddress.Any，跟系统服务真抢端口）。
/// 关键：用 0.0.0.0 试绑（不是 127.0.0.1），这样能检出 System (PID 4) 在某个 IP 占着的端口。
///
/// 旧的 HttpFileServer.cs 已经被 TcpHttpServer.cs 替代（避免 HttpListener / http.sys 在某些机器上
/// 出现 silent-failure —— 那种情况 Start() 不抛异常但实际 OS 路由已把请求抢走）。
/// 这个文件仅保留 PortProbe 工具方法。
/// </summary>
internal static class PortProbe
{
    public static bool IsFree(int port)
    {
        Socket? sock = null;
        try
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.Bind(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException ex)
        {
            Logger.Info($"port {port} busy: {ex.SocketErrorCode}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Info($"port {port} probe error: {ex.Message}");
            return false;
        }
        finally
        {
            try { sock?.Close(); } catch { /* ignore */ }
        }
    }
}
