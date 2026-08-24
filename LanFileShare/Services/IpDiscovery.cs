using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanFileShare.Services;

/// <summary>
/// 从本机所有网卡中挑一个"对得上局域网"的 IPv4。
/// 过滤项：
///   - 跳过回环（127.x）
///   - 跳过链路本地 / 自动配置失败（169.254.x）
///   - 跳过 Hyper-V 共享（192.168.137.x 等虚拟地址）
///   - 跳过虚拟机常见网段
///   - 优先选 192.168.x / 10.x / 172.16-31.x（最像家用/办公路由）
/// </summary>
public static class IpDiscovery
{
    public static string GetLocalIp()
    {
        string? fallback = null;

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 跳过没启用或不在线的网卡
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            // 虚拟网卡常见特征
            var name = ni.Name.ToLowerInvariant();
            if (name.Contains("hyper-v") || name.Contains("vmware") || name.Contains("virtualbox") ||
                name.Contains("docker") || name.Contains("wsl") || name.Contains("bluetooth"))
                continue;

            var props = ni.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (IPAddress.IsLoopback(addr.Address)) continue;

                // 偏好 C 类 / A 类私网
                var bytes = addr.Address.GetAddressBytes();
                bool isPrivate =
                    (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 10) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
                // 跳过链路本地
                if (bytes[0] == 169 && bytes[1] == 254) continue;

                if (isPrivate)
                {
                    Logger.Info($"已选用网卡 IP: {ip}（接口：{ni.Name}）");
                    return ip;
                }

                fallback ??= ip;
            }
        }

        if (fallback != null)
        {
            Logger.Warn($"未发现私网 IP，使用兜底: {fallback}");
            return fallback;
        }

        var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
        var local = hostEntry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var last = local?.ToString() ?? "127.0.0.1";
        Logger.Warn($"最末兜底返回: {last}");
        return last;
    }
}
