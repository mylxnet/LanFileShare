using System.Text;

namespace LanFileShare;

/// <summary>
/// Mobile upload page, inlined as a verbatim string to avoid
/// EmbeddedResource + single-file publish issues.
/// Source of truth: Resources/mobile.html (keep that file in sync).
/// </summary>
internal static class MobileHtml
{
    public static readonly string Content = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1, maximum-scale=1, user-scalable=no"">
<meta name=""theme-color"" content=""#2563EB"">
<title>发送文件到电脑</title>
<style>
  :root {
    --blue: #2563EB;
    --blue-dark: #1D4ED8;
    --blue-light: #DBEAFE;
    --blue-50: #EFF6FF;
    --green: #10B981;
    --green-light: #D1FAE5;
    --red: #DC2626;
    --red-light: #FEE2E2;
    --amber: #F59E0B;
    --amber-light: #FEF3C7;
    --text: #111827;
    --muted: #6B7280;
    --border: #E5E7EB;
    --bg: #F3F4F6;
    --shadow: 0 1px 3px rgba(0,0,0,0.04), 0 4px 16px rgba(0,0,0,0.04);
  }
  * { box-sizing: border-box; margin: 0; padding: 0; -webkit-tap-highlight-color: transparent; }
  html, body {
    font-family: -apple-system, BlinkMacSystemFont, ""PingFang SC"", ""Microsoft YaHei"", sans-serif;
    background: var(--bg);
    color: var(--text);
    min-height: 100vh;
    -webkit-font-smoothing: antialiased;
  }

  /* ==== 顶部蓝色 header ==== */
  .hero {
    background: linear-gradient(135deg, var(--blue) 0%, var(--blue-dark) 100%);
    color: white;
    padding: 28px 20px 60px;
    text-align: center;
    position: relative;
    overflow: hidden;
  }
  .hero::before {
    content: '';
    position: absolute;
    inset: 0;
    background-image:
      radial-gradient(circle at 20% 0%, rgba(255,255,255,0.1) 0%, transparent 50%),
      radial-gradient(circle at 80% 100%, rgba(255,255,255,0.08) 0%, transparent 50%);
  }
  .hero .icon {
    width: 56px; height: 56px;
    background: rgba(255,255,255,0.18);
    border-radius: 16px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 12px;
    backdrop-filter: blur(8px);
    position: relative;
  }
  .hero .icon svg { width: 30px; height: 30px; fill: white; }
  .hero h1 {
    font-size: 22px;
    font-weight: 600;
    margin-bottom: 8px;
    letter-spacing: 0.3px;
    position: relative;
  }
  .hero .device {
    font-size: 13px;
    opacity: 0.92;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    position: relative;
  }
  .hero .device::before {
    content: '';
    display: inline-block;
    width: 6px; height: 6px;
    border-radius: 50%;
    background: #34D399;
    box-shadow: 0 0 8px #34D399;
  }

  /* ==== 内容卡片 ==== */
  .container {
    max-width: 480px;
    margin: 0 auto;
    padding: 0 16px 32px;
    margin-top: -40px;
    position: relative;
  }

  .card {
    background: white;
    border-radius: 16px;
    box-shadow: var(--shadow);
    margin-bottom: 16px;
    overflow: hidden;
  }

  /* ==== Drop zone ==== */
  .drop-zone {
    padding: 36px 20px;
    text-align: center;
    cursor: pointer;
    transition: all .15s;
    border: 2px dashed transparent;
    border-radius: 16px;
    margin: 16px 0;
  }
  .drop-zone:active { background: var(--blue-50); border-color: var(--blue); }
  .drop-zone .pic {
    font-size: 56px;
    margin-bottom: 12px;
    line-height: 1;
  }
  .drop-zone .title {
    color: var(--blue);
    font-size: 16px;
    font-weight: 600;
    margin-bottom: 4px;
  }
  .drop-zone .subtitle {
    color: var(--muted);
    font-size: 12px;
  }
  .drop-zone.hidden { display: none; }

  /* ==== File list ==== */
  .file-list {
    background: white;
    border-radius: 16px;
    box-shadow: var(--shadow);
    margin-bottom: 16px;
    overflow: hidden;
  }
  .file-list.hidden { display: none; }
  .file-list-header {
    padding: 14px 16px;
    border-bottom: 1px solid var(--border);
    display: flex;
    justify-content: space-between;
    align-items: center;
    background: #FAFBFC;
  }
  .file-list-header .count {
    font-size: 14px;
    font-weight: 600;
    color: var(--text);
  }
  .file-list-header .clear {
    font-size: 13px;
    color: var(--muted);
    cursor: pointer;
    border: none;
    background: transparent;
    padding: 4px 8px;
    border-radius: 6px;
  }
  .file-list-header .clear:hover { color: var(--red); background: var(--red-light); }

  .file-row {
    display: flex;
    align-items: center;
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
    gap: 12px;
  }
  .file-row:last-child { border-bottom: 0; }
  .file-row .info { flex: 1; min-width: 0; }
  .file-row .name {
    font-size: 14px;
    font-weight: 500;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text);
  }
  .file-row .meta {
    font-size: 12px;
    color: var(--muted);
    margin-top: 2px;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .file-row .badge {
    display: inline-block;
    padding: 1px 8px;
    border-radius: 10px;
    font-size: 11px;
    font-weight: 500;
  }
  .badge-pending { background: #F3F4F6; color: var(--muted); }
  .badge-uploading { background: var(--blue-light); color: var(--blue-dark); }
  .badge-done { background: var(--green-light); color: #065F46; }
  .badge-fail { background: var(--red-light); color: #991B1B; }

  .file-row .retry {
    border: none;
    background: var(--blue-light);
    color: var(--blue-dark);
    font-size: 12px;
    font-weight: 600;
    padding: 6px 12px;
    border-radius: 8px;
    cursor: pointer;
  }
  .file-row .retry:active { background: var(--blue); color: white; }

  .progress-track {
    height: 4px;
    background: #F3F4F6;
    border-radius: 2px;
    overflow: hidden;
    margin-top: 6px;
  }
  .progress-fill {
    height: 100%;
    background: linear-gradient(90deg, var(--blue), #60A5FA);
    width: 0%;
    transition: width 0.3s;
    border-radius: 2px;
  }
  .progress-fill.fail { background: var(--red); }
  .progress-fill.done { background: var(--green); }

  .file-row .icon-col {
    width: 36px; height: 36px;
    border-radius: 10px;
    background: var(--blue-50);
    color: var(--blue);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 18px;
    flex-shrink: 0;
  }
  .file-row .icon-col.done { background: var(--green-light); color: #065F46; }
  .file-row .icon-col.fail { background: var(--red-light); color: #991B1B; }

  /* ==== 上传按钮 ==== */
  .upload-btn {
    width: 100%;
    padding: 16px;
    background: linear-gradient(135deg, var(--blue) 0%, var(--blue-dark) 100%);
    color: white;
    border: none;
    border-radius: 14px;
    font-size: 16px;
    font-weight: 600;
    cursor: pointer;
    box-shadow: 0 4px 12px rgba(37, 99, 235, 0.3);
    transition: all .15s;
    display: none;
  }
  .upload-btn.show { display: block; }
  .upload-btn:active { transform: scale(0.98); }
  .upload-btn:disabled {
    background: #D1D5DB;
    box-shadow: none;
    cursor: not-allowed;
  }

  /* ==== 完成态 ==== */
  .done-screen {
    background: white;
    border-radius: 16px;
    box-shadow: var(--shadow);
    padding: 36px 24px 28px;
    text-align: center;
    margin-bottom: 16px;
    display: none;
  }
  .done-screen.show { display: block; }
  .done-screen .big-icon {
    width: 72px;
    height: 72px;
    border-radius: 50%;
    margin: 0 auto 16px;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 36px;
  }
  .done-screen.success .big-icon {
    background: var(--green-light);
    color: #065F46;
  }
  .done-screen.partial .big-icon {
    background: var(--amber-light);
    color: #92400E;
  }
  .done-screen h2 {
    font-size: 20px;
    font-weight: 600;
    margin-bottom: 8px;
  }
  .done-screen .summary {
    color: var(--muted);
    font-size: 14px;
    margin-bottom: 20px;
  }

  .done-actions {
    display: flex;
    gap: 8px;
  }
  .done-actions button {
    flex: 1;
    padding: 12px 16px;
    border-radius: 10px;
    font-size: 14px;
    font-weight: 600;
    border: none;
    cursor: pointer;
  }
  .btn-primary {
    background: var(--blue);
    color: white;
  }
  .btn-primary:active { opacity: 0.85; }
  .btn-secondary {
    background: var(--bg);
    color: var(--text);
  }
  .btn-secondary:active { background: #E5E7EB; }

  /* ==== 全局进度 ==== */
  .global-progress {
    background: white;
    border-radius: 16px;
    box-shadow: var(--shadow);
    margin-bottom: 16px;
    padding: 16px 20px;
    display: none;
  }
  .global-progress.show { display: block; }
  .global-progress .label {
    display: flex;
    justify-content: space-between;
    font-size: 13px;
    margin-bottom: 8px;
    color: var(--muted);
  }
  .global-progress .label .pct { color: var(--blue); font-weight: 600; }
  .global-progress .bar {
    height: 8px;
    background: var(--bg);
    border-radius: 4px;
    overflow: hidden;
  }
  .global-progress .bar-fill {
    height: 100%;
    background: linear-gradient(90deg, var(--blue), #60A5FA);
    width: 0%;
    transition: width 0.3s;
    border-radius: 4px;
  }

  /* ==== 错误提示（顶部 toast）==== */
  .toast {
    position: fixed;
    top: 16px;
    left: 50%;
    transform: translateX(-50%) translateY(-100px);
    background: var(--red);
    color: white;
    padding: 12px 18px;
    border-radius: 12px;
    font-size: 14px;
    box-shadow: 0 4px 16px rgba(0,0,0,0.15);
    z-index: 1000;
    max-width: 90%;
    transition: transform 0.3s;
    font-weight: 500;
  }
  .toast.show {
    transform: translateX(-50%) translateY(0);
  }
  .toast.info { background: var(--blue); }
</style>
</head>
<body>

<div class=""hero"">
  <div class=""icon"">
    <svg viewBox=""0 0 24 24""><path d=""M12 2L2 7v10l10 5 10-5V7L12 2zm0 2.18L19.82 8 12 11.82 4.18 8 12 4.18zM4 9.27l7 3.5v6.96l-7-3.5V9.27zm9 10.46v-6.96l7-3.5v6.96l-7 3.5z""/></svg>
  </div>
  <h1>发送文件到电脑</h1>
  <div class=""device""><span id=""deviceNameText"">用户1</span> · 已连接</div>
</div>

<div class=""container"">
  <!-- Drop zone -->
  <div class=""drop-zone"" id=""dropZone"">
    <div class=""pic"">📁</div>
    <div class=""title"">点击选择文件</div>
    <div class=""subtitle"">支持图片和文档，可多选</div>
  </div>

  <!-- 全局进度 -->
  <div class=""global-progress"" id=""globalProgress"">
    <div class=""label"">
      <span id=""progressLabel"">上传中…</span>
      <span class=""pct"" id=""progressPct"">0%</span>
    </div>
    <div class=""bar""><div class=""bar-fill"" id=""progressBar""></div></div>
  </div>

  <!-- 文件列表 -->
  <div class=""file-list hidden"" id=""fileList"">
    <div class=""file-list-header"">
      <div class=""count"" id=""fileCount"">0 个文件</div>
      <button class=""clear"" id=""clearBtn"">清空</button>
    </div>
    <div id=""fileRows""></div>
  </div>

  <!-- 上传按钮 -->
  <button class=""upload-btn"" id=""uploadBtn"">开始上传</button>

  <!-- 完成态 -->
  <div class=""done-screen"" id=""doneScreen"">
    <div class=""big-icon"" id=""doneIcon"">✓</div>
    <h2 id=""doneTitle"">上传完成</h2>
    <div class=""summary"" id=""doneSummary""></div>
    <div class=""done-actions"" id=""doneActions"">
      <button class=""btn-secondary"" onclick=""resetForMore()"">继续上传</button>
    </div>
  </div>
</div>

<div class=""toast"" id=""toast""></div>

<input type=""file"" id=""fileInput"" multiple style=""display:none""
       accept=""image/*,.pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.md,.rtf,.csv,.zip,.rar,.7z"">

<script>
/* ==== 工具 ==== */
const $ = id => document.getElementById(id);

function showToast(text, kind) {
  const el = $('toast');
  el.textContent = text;
  el.className = 'toast ' + (kind === 'info' ? 'info' : '') + ' show';
  setTimeout(() => el.classList.remove('show'), 3000);
}

function fmtSize(n) {
  if (n < 1024) return n + ' B';
  if (n < 1024*1024) return (n/1024).toFixed(1) + ' KB';
  if (n < 1024*1024*1024) return (n/(1024*1024)).toFixed(1) + ' MB';
  return (n/(1024*1024*1024)).toFixed(1) + ' GB';
}

/* ==== 设备名管理 ==== */
/*
 * 目录命名策略：手机型号优先（如 2210132C、Pixel 7 Pro），拿不到再退回“用户N”。
 * 为区分同型号的不同手机，型号名后追加 4 位随机尾缀（如 2210132C-a3f2），
 * 尾缀与检测结果一起存 localStorage，同一部手机此后永远用同一个名字。
 * 旧版生成的“用户N”会在拿到型号后自动升级覆盖（旧目录保留不动）。
 */

// UA 里能出现的合法型号字符白名单；型号本身来自设备厂商，不该有路径特殊字符
function isValidModel(m) {
  return typeof m === 'string'
    && m.length >= 2 && m.length <= 40
    && /^[A-Za-z0-9 ._,\-()\u4e00-\u9fa5]+$/.test(m);
}

// 同步检测：UA 正则提取安卓型号；iOS 的 UA 永远只说 iPhone（拿不到具体型号）
function detectModelFromUA() {
  const ua = navigator.userAgent;
  const m = ua.match(/Android[^;]*;\s*([^;)]+?)\s*(?:Build\/|\))/);
  if (m && isValidModel(m[1]) && m[1] !== 'K' && m[1] !== 'wv') return m[1].trim();
  if (/iPhone|iPod/.test(ua)) return 'iPhone';
  if (/iPad/.test(ua)) return 'iPad';
  return null;
}

function randomSuffix() {
  const chars = 'abcdefghjkmnpqrstuvwxyz23456789'; // 去掉易混淆的 i/l/o/0/1
  let s = '';
  const rnd = new Uint32Array(4);
  (window.crypto || {}).getRandomValues ? crypto.getRandomValues(rnd)
                                         : rnd.forEach((_, i) => rnd[i] = Math.floor(Math.random() * 0x100000000));
  for (let i = 0; i < 4; i++) s += chars[rnd[i] % chars.length];
  return s;
}

function makeModelName(model) {
  return model + '-' + randomSuffix();
}

// 编号回退：沿用旧版 lastUserNum 递增（仅在型号完全拿不到时）
function makeFallbackUserName() {
  let last = parseInt(localStorage.getItem('lastUserNum') || '0', 10);
  last += 1;
  localStorage.setItem('lastUserNum', String(last));
  return '用户' + last;
}

// 立即可用的名字：缓存命中 / 旧“用户N”占位；可能异步升级
function getDeviceName() {
  const saved = localStorage.getItem('deviceName');
  if (saved && !/^用户\d+$/.test(saved)) return saved;          // 已是型号名
  const uaModel = detectModelFromUA();
  if (uaModel) {
    const name = makeModelName(uaModel);                         // 缓存缺失或旧“用户N” → 升级
    localStorage.setItem('deviceName', name);
    return name;
  }
  if (saved) return saved;                                       // UA 拿不到，沿用旧编号
  return makeFallbackUserName();
}

// 异步精确检测：userAgentData.getHighEntropyValues（新版 Chromium UA 精简后型号只剩占位符 K）
// 拿到精确型号后重写 deviceName（尾缀重新生成）；deviceName 变量同步更新，让之后的上传立刻用新名
let deviceName = getDeviceName();
async function refineDeviceName() {
  try {
    const uad = navigator.userAgentData;
    if (!uad || !uad.getHighEntropyValues) return;
    const { model } = await uad.getHighEntropyValues(['model']);
    if (!isValidModel(model) || model === 'K') return;
    const saved = localStorage.getItem('deviceName');
    // 当前名已是“该型号+尾缀”则不动；否则换精确型号重新生成尾缀
    if (saved && saved.startsWith(model + '-')) return;
    deviceName = makeModelName(model);
    localStorage.setItem('deviceName', deviceName);
    const el = document.getElementById('deviceNameText');
    if (el) el.textContent = deviceName;
  } catch { /* 检测失败保持现有名字 */ }
}

// 上传时取设备名：优先用 refine 升级后的精确名，否则用同步检测结果
function getUploadDeviceName() { return deviceName || getDeviceName(); }

document.getElementById('deviceNameText').textContent = deviceName;
refineDeviceName();

/* ==== 文件管理 ==== */
let allFiles = [];  // [{id, file, status, progress, error}]

function renderFileList() {
  const list = $('fileList');
  const rows = $('fileRows');
  const uploadBtn = $('uploadBtn');

  if (allFiles.length === 0) {
    list.classList.add('hidden');
    uploadBtn.classList.remove('show');
    return;
  }

  list.classList.remove('hidden');
  uploadBtn.classList.add('show');

  $('fileCount').textContent = `${allFiles.length} 个文件`;

  rows.innerHTML = '';
  allFiles.forEach((item, idx) => {
    const row = document.createElement('div');
    row.className = 'file-row';

    const iconCol = document.createElement('div');
    iconCol.className = 'icon-col';
    if (item.status === 'done') iconCol.classList.add('done');
    else if (item.status === 'fail') iconCol.classList.add('fail');
    iconCol.textContent = item.status === 'done' ? '✓' : (item.status === 'fail' ? '!' : '📄');
    row.appendChild(iconCol);

    const info = document.createElement('div');
    info.className = 'info';

    const name = document.createElement('div');
    name.className = 'name';
    name.textContent = item.file.name;
    info.appendChild(name);

    const meta = document.createElement('div');
    meta.className = 'meta';
    const sizeSpan = document.createElement('span');
    sizeSpan.textContent = fmtSize(item.file.size);
    meta.appendChild(sizeSpan);

    const sep = document.createElement('span');
    sep.textContent = '·';
    sep.style.color = '#D1D5DB';
    meta.appendChild(sep);

    const badge = document.createElement('span');
    badge.className = 'badge';
    if (item.status === 'pending') { badge.classList.add('badge-pending'); badge.textContent = '待上传'; }
    else if (item.status === 'uploading') { badge.classList.add('badge-uploading'); badge.textContent = item.progress + '%'; }
    else if (item.status === 'done') { badge.classList.add('badge-done'); badge.textContent = '已完成'; }
    else if (item.status === 'fail') { badge.classList.add('badge-fail'); badge.textContent = '失败'; }
    meta.appendChild(badge);
    info.appendChild(meta);

    // 进度条
    if (item.status === 'uploading' || item.status === 'fail') {
      const track = document.createElement('div');
      track.className = 'progress-track';
      const fill = document.createElement('div');
      fill.className = 'progress-fill';
      if (item.status === 'fail') fill.classList.add('fail');
      fill.style.width = (item.progress || 0) + '%';
      track.appendChild(fill);
      info.appendChild(track);
    }

    // 失败时显示错误信息和重试按钮
    if (item.status === 'fail') {
      const err = document.createElement('div');
      err.className = 'meta';
      err.style.color = '#991B1B';
      err.style.marginTop = '4px';
      err.textContent = '⚠ ' + (item.error || '上传失败');
      info.appendChild(err);

      const retry = document.createElement('button');
      retry.className = 'retry';
      retry.textContent = '重试';
      retry.onclick = () => retryOne(idx);
      row.appendChild(info);
      row.appendChild(retry);
    } else {
      row.appendChild(info);
    }

    // 删除按钮（已完成/失败之外都可删）
    const del = document.createElement('button');
    del.className = 'retry';
    del.style.background = '#F3F4F6';
    del.style.color = '#6B7280';
    del.textContent = '×';
    del.onclick = () => {
      allFiles.splice(idx, 1);
      renderFileList();
    };
    row.appendChild(del);

    rows.appendChild(row);
  });
}

/* ==== 选择文件 ==== */
$('dropZone').addEventListener('click', () => $('fileInput').click());

$('fileInput').addEventListener('change', (e) => {
  const files = Array.from(e.target.files || []);
  files.forEach(f => allFiles.push({ file: f, status: 'pending', progress: 0 }));
  $('fileInput').value = '';
  renderFileList();
});

$('clearBtn').addEventListener('click', () => {
  allFiles = allFiles.filter(f => f.status === 'uploading'); // 保留上传中
  renderFileList();
});

/* ==== 上传 ==== */
function uploadOne(item, deviceName) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/upload', true);
    // 中文 deviceName 通过 percent-encode 安全传入 HTTP header（ISO-8859-1 兼容）
    xhr.setRequestHeader('X-Device-Id', encodeURIComponent(deviceName));
    xhr.timeout = 0;

    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) {
        item.progress = Math.round((e.loaded / e.total) * 100);
        renderFileList();
      }
    };

    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          const resp = JSON.parse(xhr.responseText);
          if (resp.success || resp.ok) {
            resolve(resp);
          } else {
            reject(new Error(resp.error || '服务器拒绝'));
          }
        } catch (e) {
          reject(new Error('响应解析失败: ' + (xhr.responseText || '').substring(0, 200)));
        }
      } else {
        let msg = `HTTP ${xhr.status}`;
        try {
          const resp = JSON.parse(xhr.responseText);
          msg = resp.error || msg;
        } catch {}
        reject(new Error(msg));
      }
    };
    xhr.onerror = () => reject(new Error('网络错误'));
    xhr.ontimeout = () => reject(new Error('上传超时'));

    const fd = new FormData();
    fd.append('file', item.file, item.file.name);
    xhr.send(fd);
  });
}

async function uploadFiles(items) {
  const device = getUploadDeviceName();
  const concurrency = 3;
  let cursor = 0;
  let success = 0;
  let fail = 0;

  function nextItem() {
    while (cursor < items.length && items[cursor].status !== 'pending') cursor++;
    return cursor < items.length ? items[cursor++] : null;
  }

  async function worker() {
    while (true) {
      const item = nextItem();
      if (!item) break;
      const idx = allFiles.indexOf(item);
      if (idx < 0) continue;
      item.status = 'uploading';
      item.progress = 0;
      item.error = null;
      renderFileList();
      updateGlobalProgress();
      try {
        await uploadOne(item, device);
        item.status = 'done';
        item.progress = 100;
        success++;
      } catch (err) {
        item.status = 'fail';
        item.error = err.message || String(err);
        fail++;
      }
      renderFileList();
      updateGlobalProgress();
    }
  }

  await Promise.all(Array.from({ length: concurrency }, () => worker()));

  const doneScreen = $('doneScreen');
  doneScreen.classList.remove('show', 'success', 'partial');
  $('globalProgress').classList.remove('show');

  if (success > 0 && fail === 0) {
    // === 完全成功：显示大对勾 ===
    doneScreen.classList.add('show', 'success');
    $('doneIcon').textContent = '✓';
    $('doneTitle').textContent = '上传完成';
    $('doneSummary').textContent = `已成功上传 ${success} 个文件`;
    $('doneActions').innerHTML = '<button class=""btn-primary"" onclick=""resetForMore()"">继续上传</button>';
  } else if (success > 0 && fail > 0) {
    // === 部分成功：显示警告，但不显示""上传完成"" ===
    doneScreen.classList.add('show', 'partial');
    $('doneIcon').textContent = '!';
    $('doneTitle').textContent = '部分上传完成';
    $('doneSummary').textContent = `成功 ${success} 个 · 失败 ${fail} 个，请检查错误后重试`;
    $('doneActions').innerHTML = `
      <button class=""btn-secondary"" onclick=""resetForMore()"">继续上传</button>
      <button class=""btn-primary"" onclick=""retryAllFailed()"">重试失败项</button>
    `;
  } else {
    // === 全部失败：完全不显示""完成""标识，留在原页面 ===
    // 让用户清楚看到是失败的，可以重试
    showToast('上传失败：' + (allFiles[0]?.error || '请重试'), 'error');
    // 已上传的文件状态已设为 fail，列表里能看到逐个错误
  }
}

function updateGlobalProgress() {
  const total = allFiles.filter(f => f.status === 'done' || f.status === 'fail').length;
  const totalAll = allFiles.length;
  if (totalAll === 0) return;
  const pending = allFiles.filter(f => f.status === 'pending').length;
  const uploading = allFiles.filter(f => f.status === 'uploading').length;
  if (uploading === 0 && pending === 0) return;
  $('globalProgress').classList.add('show');
  const completed = allFiles.filter(f => f.status === 'done' || f.status === 'fail').length;
  const pct = Math.round((completed / totalAll) * 100);
  $('progressPct').textContent = pct + '%';
  $('progressBar').style.width = pct + '%';
  $('progressLabel').textContent = `已处理 ${completed} / ${totalAll}`;
}

$('uploadBtn').addEventListener('click', async () => {
  const queue = allFiles.filter(f => f.status === 'pending' || f.status === 'fail');
  if (queue.length === 0) return;
  $('uploadBtn').disabled = true;
  $('uploadBtn').textContent = '上传中…';
  $('doneScreen').classList.remove('show');
  await uploadFiles(queue);
  $('uploadBtn').disabled = false;
  $('uploadBtn').textContent = '开始上传';
});

async function retryOne(idx) {
  const item = allFiles[idx];
  if (!item || item.status !== 'fail') return;
  item.status = 'pending';
  item.progress = 0;
  item.error = null;
  renderFileList();
  $('uploadBtn').click();
}

async function retryAllFailed() {
  let any = false;
  allFiles.forEach(f => {
    if (f.status === 'fail') {
      f.status = 'pending';
      f.progress = 0;
      f.error = null;
      any = true;
    }
  });
  if (any) {
    renderFileList();
    $('uploadBtn').click();
  }
}

function resetForMore() {
  // 全部清掉，重新开始
  allFiles = [];
  renderFileList();
  $('doneScreen').classList.remove('show', 'success', 'partial');
}

renderFileList();
</script>
</body>
</html>
";

    public static readonly int Length = Content.Length;
    public static readonly byte[] Utf8Bytes = Encoding.UTF8.GetBytes(Content);
}
