# LanFileShare 开发文档

> 版本 v1.1.0 · 更新日期 2026-09-18 · 面向本项目的二次开发者与维护者

---

## 目录

1. [项目概览](#1-项目概览)
2. [系统架构](#2-系统架构)
3. [核心模块详解](#3-核心模块详解)
4. [HTTP 协议约定](#4-http-协议约定)
5. [设备目录命名规则](#5-设备目录命名规则)
6. [安全设计](#6-安全设计)
7. [测试体系](#7-测试体系)
8. [构建与发布](#8-构建与发布)
9. [版本历史](#9-版本历史)
10. [已知限制与路线图](#10-已知限制与路线图)
11. [常见开发问题](#11-常见开发问题)

---

## 1. 项目概览

### 1.1 这是什么

Windows 桌面工具（WPF, .NET 8）：启动后显示局域网 URL 二维码，手机微信/浏览器扫码打开上传页，即可把照片/文档传到电脑，自动按 **设备/日期** 归档。无需注册、无需安装手机 App。

### 1.2 核心特性（v1.1.0）

- 自研 TCP 用户态 HTTP 服务（不依赖 HttpListener/http.sys，规避其静默绑定失败问题）
- **流式 multipart 接收**：固定 1MB 滑动窗口，任意请求大小内存恒定，单请求上限 256MB
- **设备目录 = 手机型号**（如 `2210132C-qwz4`），拿不到型号回退 `用户N`；同型号手机用随机尾缀区分
- 中文文件名全程 UTF-8 正确保存；RFC 5987 `filename*` 支持
- 目录穿越防御（归一化校验最终路径在保存根内）
- 同名文件自动 `(1)` `(2)` 重命名，并发上传 CreateNew 竞态安全
- 托盘常驻、二维码扫码、路径记忆

### 1.3 技术栈

| 层 | 技术 |
|---|---|
| UI | WPF (.NET 8, C# 12), Hardcodet.NotifyIcon.Wpf 2.0.1（托盘） |
| 网络 | 自研 `TcpHttpServer`（TcpListener 用户态 HTTP/1.1 子集） |
| 上传解析 | 自研流式 multipart 状态机（`FileReceiver`） |
| 二维码 | QRCoder 1.6.0 |
| 配置 | JSON 手写持久化（`%APPDATA%\LanFileShare\settings.json`） |
| 手机页 | 单文件 HTML/JS（`Resources/mobile.html`，内联副本 `MobileHtml.cs`） |

---

## 2. 系统架构

### 2.1 进程内组件关系

```
┌────────────────────────────────────────────────────────────┐
│ App (WPF)                                                  │
│  └─ MainWindow                                             │
│      ├─ IpDiscovery   ── 选主 IP（过滤虚拟网卡）            │
│      ├─ PortSelector  ── 50000-50999 端口范围常量           │
│      ├─ PortProbe     ── 真绑定探测端口可用性               │
│      ├─ QrGenerator   ── PNG 二维码                         │
│      ├─ TrayIcon      ── 托盘图标/菜单（关于/退出）          │
│      └─ TcpHttpServer ── 0.0.0.0:{port} 监听                │
│          └─ FileReceiver ── 流式 multipart → 写盘           │
│              └─ AllowedExtensions ── 白名单校验             │
└────────────────────────────────────────────────────────────┘
         ▲ accept loop → 每连接 Task.Run(SafeHandle)
         │
   手机浏览器（mobile.html: 选择文件 → XHR POST /upload）
```

### 2.2 一次上传的完整数据流

```
手机 XHR POST /upload (multipart, X-Device-Id: 型号-尾缀 percent-encoded)
  → TcpHttpServer.HandleClientAsync
      逐字节读请求头（≤64KB）→ UTF-8 解码 → 剥离 query → 解析 Content-Length/头
  → HandleUploadAsync：UnescapeDataString(deviceId)
  → FileReceiver.HandleUploadAsync
      校验 Content-Length ≤ 256MB
      纯点设备名拒绝 → SanitizeFolder → SafeJoin(root, 设备, 日期) 穿越校验
      StreamMultipartAsync：1MB 滑动窗口状态机
        SeekBoundary → ReadPartHeader(≤64KB) → StreamPartBody → Done
        part 头 UTF-8 解析 filename/filename* → 白名单 → CreateNew 打开文件流
        body 逐块写盘（boundary 命中前只写安全区）
      EOF/截断对账：未走终止 boundary → 删半截文件，报错
  → JSON {"success":true,"saved":N} → 手机页逐文件标绿
  → onUploadCompleted 回调（主窗口刷新）
```

### 2.3 关键设计决策（为什么不用现成组件）

| 决策 | 原因 |
|---|---|
| 不用 HttpListener | 内部走 http.sys，部分 Windows 机器上对占用端口"静默成功"，实际包被路由到 System 进程，客户端连不上；raw TcpListener 用户态实现完全避开 |
| 不用 ASP.NET Core | 单文件自包含体积 +70MB，为一个上传端点不值得 |
| mobile.html 内联为 `MobileHtml.cs` 字符串 | 规避 EmbeddedResource + PublishSingleFile 的路径问题 |
| part 头/HTTP 头双重 64KB 上限 | 防恶意超长头把内存吃穿 |
| 固定 1MB 滑动窗口而非整包缓存 | 旧版 `new byte[bodyLength]` 在 200MB 时内存峰值 >600MB 且有硬上限；流式后内存恒定 |
| 请求体 256MB 上限 | 防恶意 Content-Length 无限喂字节；手机单文件场景足够 |

---

## 3. 核心模块详解

### 3.1 `TcpHttpServer`（Services/TcpHttpServer.cs）

- `TryStart(port, settings, onUploadCompleted, primaryIp?)`：`PortProbe.IsFree` 预检 → `TcpListener` 绑定 0.0.0.0 → 启动 accept loop 后台任务；失败返回 null
- `HandleClientAsync`：**逐字节**读头直到 `\r\n\r\n`（上限 64KB）→ UTF-8 解码（中文文件名/设备名关键）→ request line 剥离 `?` 后 query → 路由
- 路由表：`GET /`、`/index.html`、`/test`、`/info`、`/health`、`POST /upload`、`GET /favicon.ico`(204)、其余 404
- `JsonEscape`：错误信息嵌入 JSON 前转义，防注入
- 所有响应 `Connection: close`（每请求一连接，无 keep-alive 状态机）

### 3.2 `FileReceiver`（Services/FileReceiver.cs）— 最核心

**滑动窗口 `Window`**：1MB 连续缓冲 `[head, tail)`，`FillAsync` 顶部压缩后补数据；`IndexOf` 用 Span SIMD 扫描。

**状态机** `SeekBoundary → ReadPartHeader → StreamPartBody → Done`：

| 状态 | 找什么 | 关键细节 |
|---|---|---|
| SeekBoundary | `--boundary` | 首块行首；命中后看后 2 字节判 CRLF（进头解析）还是 `--`（终止）；假命中跳 1 字节 |
| ReadPartHeader | `\r\n\r\n` | 头整体保留跨块累积；**确定性 64KB 上限**（无论终止符是否已在窗口）；UTF-8 解码 |
| StreamPartBody | `\r\n--boundary` | 前 2 字节 CRLF 属 body；只写安全区 `min(idx, count-(len-1))`；**边界命中后必须 continue 继续内层循环**（小请求终止符已在窗口，break 回外层补块会死锁——v1.1.0 修掉的致命 bug） |
| Done | — | 终止 boundary 消费完 |

**冲突解决**：`ResolveConflict` 探测空闲名 + `FileMode.CreateNew` 直接打开；IOException 时序号重试 ≤20 次（并发同名两请求都能成功）。

**截断对账**：`consumed > bodyLength` 抛异常；EOF 且无进展且未 Done → 抛异常；finally 里删除半截文件。

**文件名处理**：`ExtractFilename`（`filename=` / RFC 5987 `filename*=` 双格式）→ `Path.GetFileName` 去路径 → `SanitizeFileName`（控制字符去除、非法字符替换、200 字限长）。

### 3.3 `IpDiscovery`（Services/IpDiscovery.cs）

枚举 `NetworkInterface`，过滤 Loopback / Down / 帧类型非 IPv4 / npcap / vmnet / Hyper-V / WSL / vEthernet 等虚拟网卡；多候选时优先私网段。**全项目唯一 IP 发现实现**（v1.1.0 前有三份：MainWindow、SettingsStore、IpDiscovery，已合并）。

### 3.4 `SettingsStore` / `Logger`

- 配置存 `%APPDATA%\LanFileShare\settings.json`：SavePath、LastPort、PrimaryIp 等；Load 容错（损坏回默认）
- 日志同目录 `logs/app-yyyy-MM-dd.log`；注意：`Logger` 每条 `File.AppendAllText` 开关文件，上传热路径里别加高频日志

### 3.5 `MobileHtml`（前端）

- **两份必须同步**：`Resources/mobile.html`（源）与 `MobileHtml.cs`（`"` → `""` 转义的逐字字符串）。改 html 后重新生成 cs（见 §11.3）
- 页面结构：渐变头部（设备名）→ 文件选择区 → 待传列表 → 并发 3 路上传 → 结果反馈
- 设备名：见 §5；`X-Device-Id` 头 = `encodeURIComponent(deviceName)`（中文安全过 ASCII 头通道）

---

## 4. HTTP 协议约定

### 4.1 请求

```
POST /upload HTTP/1.1
Content-Type: multipart/form-data; boundary=----WebKitFormBoundaryXXX
Content-Length: 12345
X-Device-Id: 2210132C-qwz4          ← percent-encoded（UTF-8→ASCII 安全）
Connection: close

multipart body：每个 part 一个文件，part 头 filename 支持 UTF-8 原文与 filename* 两种
```

### 4.2 响应（application/json）

| 场景 | 状态 | body |
|---|---|---|
| 成功 | 200 | `{"success":true,"saved":N}` |
| 白名单拒绝/穿越/截断/超限 | 400 | `{"success":false,"error":"..."}` |
| 服务端异常 | 500 | `{"success":false,"error":"..."}` |

### 4.3 端点一览

| 路径 | 方法 | 用途 |
|---|---|---|
| `/` `/index.html`（含 `?query`） | GET | 手机上传页 |
| `/upload` | POST | multipart 上传 |
| `/health` | GET | `{"success":true,"port":N}` 探活 |
| `/test` `/info` | GET | 调试 |
| 其余 | GET | 404 |

---

## 5. 设备目录命名规则

**目标格式**：`{保存根}/{手机型号}-{4位随机尾缀}/{yyyy-MM-dd}/文件`

### 5.1 检测优先级（mobile.html `getDeviceName` / `refineDeviceName`）

| 优先级 | 来源 | 得到 | 说明 |
|---|---|---|---|
| 1 | `navigator.userAgentData.getHighEntropyValues(['model'])` | 真实型号 | Chromium 内核；新版 UA 精简后 UA 字符串里型号是占位符 `K`，只有此 API 有真值 |
| 2 | UA 正则 `Android...; 型号 Build/` | 型号 | 旧版浏览器同步兜底；排除 `K` / `wv` |
| 3 | UA 含 iPhone/iPad | `iPhone` / `iPad` | iOS 隐私限制，拿不到具体型号 |
| 4 | 回退 | `用户N` | `lastUserNum` 递增（桌面浏览器访问等） |

### 5.2 稳定性与升级

- 型号 + 尾缀存 `localStorage.deviceName`，同一部手机永远同一目录；尾缀字符表去掉 `i/l/o/0/1`
- 旧版 `用户N` 缓存 → 下次访问自动升级型号名（**旧目录文件原样保留**）
- 异步 refine 拿到更精确型号后重写名字并同步页面显示与上传变量
- 型号白名单：`/^[A-Za-z0-9 ._,\-()\u4e00-\u9fa5]{2,40}$/`，非法（`../`、`a/b` 等）视为无型号

### 5.3 服务端防线

`SanitizeFolder`（非法字符→`_`、50 字限长、纯点映射）→ 纯点名直接 400 → `SafeJoin` 归一化校验最终路径仍在保存根内 → 落盘文件名再过 `SanitizeFileName`。

---

## 6. 安全设计

| 威胁 | 防线 |
|---|---|
| 目录穿越（`..`、绝对路径、非法字符） | SafeJoin 归一化校验 + 纯点拒绝 + Sanitize 双层清理 |
| 恶意超长头/超长 part 头 | HTTP 头 64KB、part 头 64KB（确定性校验），超限断连 |
| 恶意 Content-Length | 256MB 上限；实收字节与声明严格对账，截断拒绝并删半截文件 |
| 可执行文件上传 | 扩展名白名单（`AllowedExtensions`），双扩展伪装 `evil.jpg.exe` 拒绝 |
| 日志注入 | 错误进日志走 Logger；JSON 响应经 JsonEscape |
| 并发同名覆盖 | CreateNew 原子打开 + 序号重试 |
| 无认证局域网访问 | **设计取舍**：家庭/可信局域网场景；README 有提示。如需公网暴露必须先加认证 |

---

## 7. 测试体系

### 7.1 SelfTest（零依赖端到端）

`SelfTest/Program.cs`，34 项用例，**真实 socket 打真实 TcpHttpServer**（不是 mock）：

```powershell
dotnet run --project SelfTest -c Debug    # 退出码 0 = 全绿
```

覆盖：GET 路由全表、query 剥离、multipart 上传落盘归档、中文文件名、`filename*`、并发同名、白名单（`.exe`/双扩展）、非 multipart 拒绝、256MB 超限 400、`..`/纯点设备名拒绝、非法字符清洗、含空格型号设备名、8MB 大文件逐字节校验、48KB 跨块 part 头、超长头拒绝、截断请求无残留。

### 7.2 PreviewHost（预览宿主）

无头宿主复用 `TcpHttpServer`，把手机页挂 `http://127.0.0.1:50001/`：

```powershell
dotnet run --project PreviewHost -c Debug
```

保存根固定工作区 `preview-uploads/`（不碰用户真实配置）。运行细节见 `.freebuff/run.md`。

### 7.3 回归纪律

改动 `FileReceiver` / `TcpHttpServer` / `mobile.html` 后：必须跑 SelfTest 全绿；改 mobile.html 必须同步 MobileHtml.cs。

---

## 8. 构建与发布

### 8.1 日常开发

```powershell
dotnet build LanFileShare/LanFileShare.csproj -c Debug
dotnet run --project LanFileShare -c Debug      # 桌面运行
dotnet run --project SelfTest -c Debug          # 回归
```

### 8.2 发布单文件

```powershell
.\build.ps1                    # 默认 -Version 1.1.0
.\build.ps1 -Version 1.2.0     # 指定版本（写入程序集元数据 + ZIP 文件名）
```

产物：

| 文件 | 说明 |
|---|---|
| `publish/LanFileShare.exe` | 自包含单文件（含 .NET 运行时，约 10MB 级） |
| `dist/LanFileShare-v1.1.0-win-x64.zip` | 分发件（单 EXE 压缩） |

csproj 关键属性：`PublishSingleFile=true`、`SelfContained=true`、`RuntimeIdentifier=win-x64`、`IncludeNativeLibrariesForSelfExtract=true`。

> 注意：主项目自包含，被 `ProjectReference` 的工程（SelfTest/PreviewHost）必须同样 `SelfContained=true` + 同 RID，否则 NETSDK1151。

### 8.3 版本号规范

- 语义化版本：功能新增升 MINOR，修复升 PATCH
- 同步三处：csproj `<Version>` / build.ps1 默认值 / RELEASES.md 新增记录

---

## 9. 版本历史

### v1.1.0（2026-09-18）

- 流式 multipart 重写（1MB 窗口，去除 200MB 整包缓存与硬上限）
- 中文文件名 UTF-8 修复；`filename*` 支持
- 目录穿越防御；纯点设备名拒绝
- query string 剥离（二维码工具带参数不再 404）
- 并发同名 CreateNew 重试；截断请求删除半截文件
- part 头 64KB 确定性上限；边界已入窗口时的死锁修复
- 设备目录名升级为手机型号优先（+随机尾缀区分同型号）
- 托盘"关于"/全局异常弹窗 owner 修复 + 异常落日志
- IP 发现三处实现合并为 IpDiscovery 单一来源
- accept 白名单与页面选择器同步；删除 MultipartParser/PortSelector 死代码
- 新增 SelfTest 34 项端到端用例与 PreviewHost 预览宿主

### v1.0.0（2024-12-25）

首发：二维码扫码上传、设备/日期归档、同名重命名、托盘常驻、路径记忆。

（详细变更见 RELEASES.md）

---

## 10. 已知限制与路线图

| 限制 | 说明 | 缓解方向 |
|---|---|---|
| 无认证 | 任何局域网设备可上传 | 加 PIN 配对/Token 头 |
| 明文 HTTP | 内容可被局域网嗅探 | 可接受（家庭网）；公网需 TLS |
| 单请求 256MB | 更大文件被 400 | 提高上限或分片协议 |
| HTTP 头逐字节读 | 大量连接时性能一般 | 换缓冲读 + 超时（低优先级） |
| Logger 每条开关文件 | 上传热路径日志放大 IO | 改 StreamWriter 常驻 |
| Windows 专属 | WPF + win-x64 RID | 跨平台需换 UI 框架（MAUI/Avalonia） |

---

## 11. 常见开发问题

### 11.1 WPF 隐式 usings 缺失

WPF SDK 的 `ImplicitUsings` **不含** `System.IO` / `System.Net.Http` 等，新文件显式 `using System.IO;`。

### 11.2 SelfTest/PreviewHost 引用主项目报 NETSDK1151

主项目 `SelfContained=true` + `RuntimeIdentifier=win-x64`，引用方必须设置相同属性。

### 11.3 mobile.html 与 MobileHtml.cs 同步

改 `Resources/mobile.html` 后重新生成：

```powershell
$html = [IO.File]::ReadAllText('LanFileShare\Resources\mobile.html')
$escaped = $html.Replace('"','""')
# 用 $escaped 替换 MobileHtml.cs 中 verbatim 字符串体（见 git 历史 sync-mobilehtml.ps1）
```

### 11.4 端口被占调试

`netstat -ano | findstr :50001`；Git Bash 下 `taskkill /pid` 会被路径转换，用 `powershell Stop-Process -Id <pid>`。

### 11.5 测试设备名行为

无真实手机时用 curl 模拟：

```bash
curl -H "X-Device-Id: Pixel%207%20Pro-e2e9" -F "file=@test.txt" http://127.0.0.1:50001/upload
```
