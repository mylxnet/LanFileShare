using System.Net;
using System.Net.Sockets;

namespace LanFileShare.Services;

/// <summary>
/// 端口范围常量：偏好上次用过的端口（_settings.LastPort），被占则由
/// MainWindow.BuildPortCandidates + PortProbe.IsFree 换下一个。
///
/// 默认 50000+：
///   - 0-9999：       IANA "well-known ports"，大量系统服务占
///   - 10000-19999：  WinNAT / Hyper-V / Container 动态分配区
///   - 20000-39999：  企业应用常用
///   - 40000-49999：  部分游戏 / 直播软件
///   - 50000-50999：  IANA 不分配，绝少被任何系统服务占，桌面工具最理想
/// 一些机器上 PID 4 (System) 会通过 Hyper-V 端口映射、WinNAT、Defender Network Inspection
/// 偷偷占用 9000-10999 整个段。在这种机器上，绑定成功的回调可能"假装"成功，
/// 但实际的 TCP 包被路由到 System 进程，结果"目标主动拒绝"或超时。
/// </summary>
public static class PortSelector
{
    public const int StartPort = 50000;
    public const int EndPort = 50999;
}
