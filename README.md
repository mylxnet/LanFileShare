# LanFileShare · 局域网文件快传

> 📲 打开应用、二维码一开，手机扫码就能把照片/文档传到电脑，自动按手机分组、按日期归档，无需注册、无需安装 App。

---

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

```powershell
# 在项目根目录
.\run-dev.ps1
# 或直接：
dotnet run --project ./LanFileShare -c Debug
```

启动后行为：
- 主窗口出现（420 × 560）
- 首次使用会要求选择保存路径
- 自动选择可用端口（9000-9100），在窗口底部显示完整 URL
- 关闭窗口 → 隐藏到系统托盘（不退出进程）
- 右键托盘图标 → "退出" 才真正结束

---

## 📦 打包发布

```powershell
.\build.ps1 -Version 1.0.0
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
│   ├── Services/
│   │   ├── SettingsStore.cs         JSON 持久化
│   │   ├── Logger.cs                日志
│   │   ├── IpDiscovery.cs           选合适的局域网 IPv4
│   │   ├── PortSelector.cs          自动找空闲端口
│   │   ├── QrGenerator.cs           QRCoder 包装
│   │   ├── TrayIcon.cs              系统托盘
│   │   ├── HttpFileServer.cs        HttpListener 主服务
│   │   └── FileReceiver.cs          multipart 接收 + 写盘
│   │
│   ├── Helpers/
│   │   └── MultipartParser.cs       简化的 multipart 解析
│   │
│   └── Resources/
│       └── mobile.html              手机端上传页（嵌入资源）
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

### HttpListener 端口配置

应用启动时扫描 `9000-9100` 范围，自动选第一个空闲端口。原因是 80/443 通常被 IIS/Apache 占用，且 9000+ 一般无冲突。

### 文件类型白名单

`Models/AllowedExtensions.cs` 中维护，只允许图片 + 常见文档格式。`accept` 属性也会同步过滤 HTML 端文件选择器。

### 路径归档规则

```
{保存路径}/
  └── 用户N/                    (X-Device-Id 头决定)
        └── yyyy-MM-dd/         (按上传日期)
              ├── IMG_001.jpg
              └── 报告.pdf
```

同名文件自动加 `(1)`、`(2)` 前缀，不覆盖任何已有文件。

### 设备名生成

手机首次访问时，浏览器 localStorage 会：
1. 读 `lastUserNum`（默认 0）+1
2. 命名为 `用户N`，写回 `localStorage`
3. 后续访问沿用同一个名字

详见 `Resources/mobile.html` 里的 `getDeviceName()` 函数。

---

## 🐛 常见问题

### Q: 防火墙弹窗被关闭了，扫码扫不上？
**A**: 重新启动 EXE 时会再次弹窗。或去 控制面板 → Windows Defender 防火墙 → 允许应用通过防火墙 → 找到 LanFileShare 勾选。

### Q: 杀毒软件误报？
**A**: 自包含 .NET 应用偶尔被 360 / 火绒误报。可加白名单，或考虑购买代码签名证书。

### Q: 端口被占用？
**A**: 9000-9100 全占时，应用会弹窗提示。关掉占用端口的程序（IIS、Apache、其他服务），或修改 `PortSelector.cs` 的范围。

### Q: 文件传输一半断网了？
**A**: 已写入部分会保留为一个完整文件（流式写），前端会显示红色"失败"提示，用户可重试（重试会作为新文件，叠加 `(1)` 等后缀）。

---

## 📜 许可证

仅供个人学习与内部使用，请勿用于商业分发。

---

## ✨ 设计回顾

完整的设计过程见 `/项目设计方案.html` —— 包含 18 个设计决策、技术选型对比、UI 草图、验收标准。
