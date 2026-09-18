# LanFileShare · 局域网文件快传

> 📲 打开应用、二维码一开，手机扫码就能把照片/文档传到电脑，自动按手机分组、按日期归档，无需注册、无需安装 App。

---

## 🚀 首次运行（3 步必看）

| 步骤 | 操作 |
|------|------|
| 1️⃣ **首次必须跑一次 setup.cmd** | 双击 `setup.cmd`（会自提权）—— 添加 Windows 防火墙放行规则 |
| 2️⃣ **启动应用** | 双击 `dist\LanFileShare-v1.0.0-win-x64.exe`（或解压后跑同名 EXE）|
| 3️⃣ **扫码传文件** | 选保存路径 → 出现二维码 → 手机扫码 |

> ⚠️ **重要**：第一次启动时 Windows 会弹"是否允许网络访问"——**必须勾选"专用网络"和"家庭/工作网络"，点"允许访问"**。如果点了阻止，运行 `setup.cmd` 重置。

### 出现问题？

跑 `diagnose.cmd` 看完整诊断报告（不需要管理员）。

## 🖼 用户视角（3 步上手）

1. 解压 ZIP → 双击 `LanFileShare.exe`
2. Windows 防火墙弹窗 → 勾选"专用网络"和"家庭/工作网络"→ **允许访问**
3. 看到二维码 → 用手机微信/浏览器扫码 → 选择文件 → 上传

文件会按 `保存路径/用户N/2024-12-25/IMG_001.jpg` 这样归档，每次启动自动记住上次路径。

---

## 🛠 开发环境

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10 (1809+) 或 Windows 11 |
| 运行时 | .NET 8 SDK（用于编译） |
| IDE | Visual Studio 2022 / Rider / 纯命令行均可 |

> 编译一次后产出的 EXE 是 **自包含** 的（包含 .NET 运行时），用户无需安装任何 .NET。

---

## 🚀 开发流程

### 在 VS 中调试

```
1. 用 VS 2022 打开 LanFileShare/LanFileShare.sln （如果没有，先 dotnet new sln）
2. 右键 LanFileShare 项目 → "设为启动项目"
3. F5 启动
```

### 命令行启动

**PowerShell**（推荐，语法更干净）
```powershell
.\run-dev.ps1
# 或直接：
dotnet run --project ./LanFileShare -c Debug
```

**cmd**（如果你用的是传统命令提示符）
```cmd
run-dev.cmd
REM 或直接：
dotnet run --project .\LanFileShare -c Debug
```

启动后行为：
- 主窗口出现（420 × 560）
- 首次使用会要求选择保存路径
- 自动选择可用端口（50000-50999，避开 WinNAT/Hyper-V/System 等占用的 0-30000 区段），在窗口底部显示完整 URL
- 关闭窗口 → 隐藏到系统托盘（不退出进程）
- 右键托盘图标 → "退出" 才真正结束

---

## 📦 打包发布

**PowerShell**（推荐）
```powershell
.\build.ps1 -Version 1.0.0
```

**cmd**（传统命令提示符）
```cmd
build.cmd 1.0.0
```

构建流程：
1. `dotnet publish` 编译为单文件 EXE（含自包含 .NET 运行时）
2. 自动打包为 ZIP 放在 `dist/LanFileShare-v1.0.0-win-x64.zip`

预计产物：

| 文件 | 大小 | 备注 |
|------|------|------|
| `publish/LanFileShare.exe` | ~10 MB | 临时构建产物 |
| `dist/LanFileShare-v1.0.0-win-x64.zip` | ~5.6 MB | 唯一分发件 |

---

## 📁 项目结构

```
LanFileShare/
├── LanFileShare/
│   ├── LanFileShare.csproj          项目文件
│   ├── App.xaml / App.xaml.cs       WPF 入口
│   ├── MainWindow.xaml / .cs        主窗口（QR码+路径）
│   │
│   ├── Models/
│   │   ├── AppSettings.cs           配置 DTO
│   │   └── AllowedExtensions.cs     文件白名单
│   │
│   ├── MobileHtml.cs                mobile.html 的内嵌副本（两者需同步）
│   │
│   ├── Services/
│   │   ├── SettingsStore.cs         JSON 持久化
│   │   ├── Logger.cs                日志
│   │   ├── IpDiscovery.cs           选合适的局域网 IPv4（唯一实现）
│   │   ├── PortSelector.cs          端口范围常量（50000-50999）
│   │   ├── PortProbe.cs             端口真绑定探测
│   │   ├── TcpHttpServer.cs         TcpListener 用户态 HTTP 服务
│   │   ├── FileReceiver.cs          流式 multipart 接收 + 写盘
│   │   ├── QrGenerator.cs           QRCoder 包装
│   │   └── TrayIcon.cs              系统托盘
│   │
│   └── Resources/
│       └── mobile.html              手机端上传页（嵌入资源）
│
├── SelfTest/                        零依赖端到端自测（dotnet run --project SelfTest）
├── PreviewHost/                     无头预览宿主（复用 TcpHttpServer，端口 50001）
│
├── build.ps1                        构建脚本
├── run-dev.ps1                      开发启动脚本
├── README.md                        本文件
└── RELEASES.md                      发布日志模板
```

---

## ✅ 验收清单（按设计文档）

| 项 | 验证方法 |
|----|---------|
| ✅ Windows 兼容 | 在 Win10/11 双击 EXE 启动成功 |
| ✅ 路径记忆 | 关闭重开路径自动填充（路径丢失提示重选）|
| ✅ 多文件并发 | 20 个文件同时上传，所有完成 |
| ✅ 多设备隔离 | 同时 3 部手机扫码，子文件夹不串 |
| ✅ 异常场景 | 路径丢失/端口占用/类型不符/同名前缀重命名 |

---

## 🔍 关键模块说明

### 自研 TCP HTTP 服务与端口配置

服务器是 `TcpHttpServer`：直接在 `TcpListener` 上用用户态代码实现 HTTP（不走 HttpListener/http.sys，避免其 "静默绑定失败" 坑）。应用启动时扫描 `50000-50999` 范围（1000 个端口），自动选第一个空闲端口。低端口（0-30000）被各种 Windows 系统服务、Hyper-V 端口映射、WinNAT 等 "PID 4 (System)" 偷偷占着；50000+ 是 IANA 不分配的区段，绝少被任何系统服务占；如果上一次用的端口被占，会顺延到下一个空闲端口。

### 文件类型白名单

`Models/AllowedExtensions.cs` 中维护，只允许图片 + 常见文档格式。手机页 `accept` 属性由 `AllowedExtensions.HtmlAccept` 生成（mobile.html 与 MobileHtml.cs 两处同步），与服务端白名单保持一致。单请求体上限 256MB（`FileReceiver.MaxBodyBytes`），超限明确返回 400。

### 上传接收（流式）

`FileReceiver` 边收边解析 multipart：固定 1MB 滑动窗口，文件体逐块写盘，任意请求大小内存占用恒定。设备名归一化后校验最终路径仍在保存根内（拒绝 `..` 等穿越）；截断/超长的请求会被拒绝并删除半截文件。

### 路径归档规则

```
{保存路径}/
  └── 用户N/                    (X-Device-Id 头决定)
        └── yyyy-MM-dd/         (按上传日期)
              ├── IMG_001.jpg
              └── 报告.pdf
```

同名文件自动加 `(1)`、`(2)` 前缀，不覆盖任何已有文件。

### 设备名生成（手机型号优先）

目录名优先用手机型号（如 `2210132C-qwz4`、`Pixel 7 Pro-aywk`），拿不到再回退 `用户N`：

1. **异步精确检测**：`navigator.userAgentData.getHighEntropyValues(['model'])`（Chromium 内核：Chrome/微信 XWeb/Edge；新版 UA 精简后 UA 字符串里型号只剩占位符 `K`，只有这个 API 拿得到真实型号）
2. **同步兑底**：UA 正则提取安卓型号（旧版浏览器仍带真实型号）；iOS 永远只报 `iPhone`（苹果隐私限制）
3. **回退**：型号完全拿不到时沿用旧版 `用户N` 递增（桌面浏览器访问等场景）

为区分两台同型号手机，型号后追加 4 位随机尾缀（去易混淆字符），与检测结果一起存 localStorage，同一部手机永远用同一个目录。旧版生成的 `用户N` 会在拿到型号后自动升级覆盖（旧目录和文件原样保留）。型号字符串在客户端做白名单过滤 + 服务端 `SanitizeFolder`/`SafeJoin` 双重防护。

详见 `Resources/mobile.html` 里的 `getDeviceName()` / `refineDeviceName()` 函数。

---

## 🐛 常见问题

### Q: 防火墙弹窗被关闭了，扫码扫不上？
**A**: 重新启动 EXE 时会再次弹窗。或去 控制面板 → Windows Defender 防火墙 → 允许应用通过防火墙 → 找到 LanFileShare 勾选。

### Q: 杀毒软件误报？
**A**: 自包含 .NET 应用偶尔被 360 / 火绒误报。可加白名单，或考虑购买代码签名证书。

### Q: 端口被占用？
**A**: 50000-50999 全占时（极罕见），应用会弹窗提示。关掉占用端口的程序，或修改 `PortSelector.cs` 的范围。

### Q: 文件传输一半断网了？
**A**: 传输中断的请求会被整体拒绝（不会留下半截文件），前端显示红色"失败"提示，用户重试即可（同名会叠加 `(1)` 等后缀）。

---

## 📜 许可证

仅供个人学习与内部使用，请勿用于商业分发。

---

## ✨ 设计回顾

完整的设计过程见 `/项目设计方案.html` —— 包含 18 个设计决策、技术选型对比、UI 草图、验收标准。
