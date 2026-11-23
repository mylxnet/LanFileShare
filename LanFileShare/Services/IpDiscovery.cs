using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanFileShare.Services;

/// <summary>
/// 单张网卡上的一个可用 IPv4 候选。
/// 虚拟网卡只标注、不隐藏：用户可能正是通过 VPN / 手机热点上网。
/// </summary>
public sealed record NetworkCandidate(string InterfaceName, string Ip, bool IsVirtual, bool IsPrivate)
{
    /// <summary>下拉框显示文本，例如「192.168.1.5 · 以太网」。</summary>
    public string Display => IsVirtual
        ? $"{Ip} · {InterfaceName} · 虚拟"
        : $"{Ip} · {InterfaceName}";
}

/// <summary>
/// 枚举本机所有可用 IPv4，并给出「最像局域网」的默认选择。
/// 过滤项：
///   - 跳过回环（127.x）
///   - 跳过链路本地 / 自动配置失败（169.254.x）
///   - 虚拟网卡（Hyper-V / vEthernet / VMware / WSL / Docker / VPN / 蓝牙等）标记为虚拟并排后
///   - 优先选 192.168.x / 10.x / 172.16-31.x（最像家用/办公路由）
/// </summary>
public static class IpDiscovery
{
    private static readonly string[] VirtualKeywords =
    {
        "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "docker", "wsl",
        "bluetooth", "npcap", "vmnet", "tailscale", "zerotier", "wireguard",
        "openvpn", "tap-", "tap ", "tun", "radmin", "hamachi", "vpn", "virtual",
        "pseudo", "teredo", "isatap"
    };

    /// <summary>
    /// 返回所有可用 IPv4 候选：真实网卡在前、私网优先、虚拟网卡在后。
    /// </summary>
    public static List<NetworkCandidate> GetCandidates()
    {
        var result = new List<NetworkCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // 跳过没启用或不在线的网卡
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            bool virtualNic = IsVirtual(ni.Name, ni.Description);

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;

                var ip = addr.Address.ToString();
                if (!seen.Add(ip)) continue;

                var bytes = addr.Address.GetAddressBytes();
                // 跳过链路本地 / 自动配置失败
                if (bytes[0] == 169 && bytes[1] == 254) continue;

                // Hyper-V 共享（ICS）默认网段，本质上也是虚拟网卡
                bool ics = bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 137;
                bool isPrivate =
                    (bytes[0] == 192 && bytes[1] == 168) ||
                    (bytes[0] == 10) ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);

                result.Add(new NetworkCandidate(ni.Name, ip, virtualNic || ics, isPrivate));
            }
        }

        return result
            .OrderBy(c => c.IsVirtual)
            .ThenByDescending(c => c.IsPrivate)
            .ThenBy(c => c.InterfaceName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool IsVirtual(string name, string description)
    {
        var text = (name + " " + description).ToLowerInvariant();
        foreach (var kw in VirtualKeywords)
        {
            if (text.Contains(kw)) return true;
        }
        return false;
    }

    /// <summary>
    /// 自动挑选最合适的 IPv4（保留给未指定网卡的调用方）。
    /// </summary>
    public static string GetLocalIp()
    {
        var best = GetCandidates().FirstOrDefault();
        if (best != null)
        {
            Logger.Info($"已选用网卡 IP: {best.Ip}（接口：{best.InterfaceName}{(best.IsVirtual ? "，虚拟" : "")}）");
            return best.Ip;
        }

        Logger.Warn("未发现可用网卡 IP，尝试 DNS 兜底");
        var hostEntry = Dns.GetHostEntry(Dns.GetHostName());
        var local = hostEntry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        var last = local?.ToString() ?? "127.0.0.1";
        Logger.Warn($"最末兜底返回: {last}");
        return last;
    }
}
