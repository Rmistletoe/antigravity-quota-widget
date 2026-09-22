# 📊 Antigravity Usage Panel

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows&logoColor=white" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/UI-Browser%20%2F%20HTML-10B981" />
  <img src="https://img.shields.io/badge/Overhead-0%20Token%20Cost-brightgreen" />
  <img src="https://img.shields.io/badge/License-MIT-blue.svg" />
</p>

本地监控 [Google Antigravity](https://antigravity.google/) 的**配额**与 **Token 用量**。没有 GUI 程序、没有悬浮球、没有托盘 —— 双击 `start.bat` 起一个纯本地服务，浏览器自动打开面板。

数据全部来自本机 Antigravity LanguageServer 的内部 RPC 接口，**不消耗任何云端 API 额度**。

---

## ✨ 核心特性

- 🚀 **0 Token 消耗**：直接对接本地运行中的 Antigravity LanguageServer 内部 RPC，耗时 < 2 毫秒，不占用任何配额。
- 📊 **配额实时监控**：5 小时滚动限额 + 本周限额，实时百分比与重置倒计时（Gemini 与 Claude/GPT 两组合并展示）。
- 📈 **Token 用量统计（日 / 周 / 月）**：逐条调用的用量账目 —— 输入 / 输出 / 其中思考 / 缓存读取 / 调用次数，并按模型拆分。
- 🗄️ **本地账本 + 3 个月滚动保留**：数据落地为 `%APPDATA%\AntigravityQuota\usage\YYYY-MM.jsonl`（append-only、按月分文件），超期月份自动清理。**官方会裁剪旧会话，账本保证你的历史不再丢**。
- 📉 **可视化面板**：今日/本周/本月 KPI 卡、每日用量趋势堆叠柱状图（可缩放）、分模型占比环形图、分模型明细表（日/周/月切换）、配额进度条。
- 🎨 **前端即改即生效**：面板就是 `web/index.html` 一个纯静态文件。改样式、加图表，**刷新浏览器即可，不用重新编译**。
- 💾 **一键导出 CSV**：访问 `/api/export.csv` 下载全量明细（带 BOM，Excel 直接打开不乱码）。
- 🛡️ **零依赖、零权限**：内置极简 HTTP 服务基于 `TcpListener`，**不需要管理员权限、不需要 netsh urlacl 预留**。只监听 `127.0.0.1`。
- 🔒 **单例运行**：重复点击 `start.bat` 只会把浏览器重新指到已在运行的服务，不会起第二个进程。
- 🚀 **随系统开机自启**：`AntigravityQuota.exe --autostart on`（静默采集，不弹浏览器）。

---

## 📦 快速使用

### 1. 启动
双击根目录 **`start.bat`** —— 无窗口启动采集服务，浏览器会自动打开面板。

### 2. 停止
双击 **`scripts\stop.bat`**。

### 3. 脚本一览

| 脚本 | 作用 |
|---|---|
| `start.bat` | 关闭旧实例 → 启动服务 → 浏览器自动打开面板 |
| `scripts\build.bat` | 一键编译（输出到 `bin\`） |
| `scripts\stop.bat` | 停止服务 |
| `scripts\package.bat` | 编译并打包出发布 zip 到 `releases\`（发布用） |

> 程序是**无控制台窗口**类型（WinExe），启动后不会留任何黑框。
> 想再打开一次面板，直接再双击 `start.bat` 即可。
>
> **运行环境要求**：Windows 10 / 11 + [.NET 10 运行时](https://dotnet.microsoft.com/download)（框架依赖构建，
> 只用到基础运行时 `Microsoft.NETCore.App`，**不需要 Desktop Runtime**）。

### 4. 命令行参数

```bash
AntigravityQuota.exe                 # 启动服务并打开浏览器
AntigravityQuota.exe --no-browser    # 只启动服务，不打开浏览器（开机自启用它）
AntigravityQuota.exe --open          # 只把浏览器指到已在运行的服务
AntigravityQuota.exe --autostart on  # 开启开机自启
AntigravityQuota.exe --autostart off # 关闭开机自启
```

---

## 📂 项目工程结构

```
antigravity-quota-widget/
├── AntigravityQuota.slnx           # Visual Studio 解决方案文件
├── LICENSE                         # MIT 开源许可证
├── README.md                       # 项目说明文档
├── .gitignore
├── start.bat                       # 一键启动（起服务 + 开浏览器）
│
├── bin/                            # 编译输出
│   ├── AntigravityQuota.exe        # 采集服务 + 本地 HTTP 服务（无窗口）
│   └── web/
│       └── index.html              # 面板前端（纯静态，改它不用重编译）
│
├── scripts/
│   ├── build.bat                   # 一键编译
│   ├── package.bat                 # 编译 + 打包发布 zip
│   └── stop.bat                    # 停止服务
│
└── src/AntigravityQuota/
    ├── AntigravityQuota.csproj
    ├── Program.cs                  # 入口：单例、端口选择、开浏览器、日志
    ├── AppState.cs                 # 采集编排 + JSON 快照（无 UI）
    ├── HttpServer.cs               # 极简本地 HTTP 服务（TcpListener）
    ├── RpcDiscovery.cs             # LanguageServer 端点发现与 RPC 调用
    ├── QuotaService.cs             # 5 小时 + 每周配额
    ├── TokenUsageService.cs        # Token 用量增量采集 + 模型名映射
    ├── TokenLedger.cs              # 本地用量账本（JSONL，按月滚动保留）
    ├── UsageAggregator.cs          # 日 / 周 / 月聚合
    ├── ConfigManager.cs            # 配置读写
    ├── AutoStartHelper.cs          # 开机自启（注册表）
    └── web/index.html              # 面板前端
```

---

## 🖥️ 面板说明

服务默认监听 `http://127.0.0.1:8917/`（被占用时自动顺延到 8918、8919…，并把选中的端口写入
`%APPDATA%\AntigravityQuota\runtime.json`）。页面每 10 秒自动拉一次数据，切回标签页时也会立即刷新。

| 接口 | 说明 |
|---|---|
| `GET /` | 面板页面 |
| `GET /api/data` | 配额 + 用量的 JSON 快照 |
| `GET /api/refresh` | 触发一次立即刷新 |
| `GET /api/export.csv` | 导出账本全量明细为 CSV |

**统计口径**（面板与 CSV 一致）：

- **总 Token = 输入 + 输出 + 缓存读取**。缓存读取即「缓存命中」，也是模型实际读取并处理的 token，因此计入总量。
- **输出**已包含**思考**（`outputTokens = thinkingOutputTokens + responseOutputTokens`），思考只作子项展示，**不重复相加**。
- 明细**分列展示**（输入 / 输出 / 其中思考 / 缓存读取），任何时候都能单独看某一部分。注意缓存读取的量级通常远超输入（提示缓存会被逐步骤回读），看占比时心里有数。
- 时间统一按**本地时区**归日（服务器返回的是 UTC 纳秒时间戳）。

---

## 🔧 配置

`%APPDATA%\AntigravityQuota\config.json`

```jsonc
{
  "UsageRetentionMonths": 3,      // 账本滚动保留月数
  "UsageScanIntervalSec": 60,     // 用量扫描间隔（秒），最低 30
  "UsageBackfillCompleted": true  // 是否已完成首次全量回填
}
```

**想重建账本**（例如官方裁剪过历史、或想修正模型名）：把 `UsageBackfillCompleted` 改成 `false`，
删掉 `usage\*.jsonl`，重启服务即可全量重灌。

---

## 🚀 版本更新记录

### v2.0.2 (2026-09-22)
- 🎛️ **布局调整**：额度进度条移到页面最上方（最先看到"会不会跑爆额度"）；日/周/月切换按钮移到「分模型占比」卡片标题栏 —— 它本来就是这个图（和下方明细表）的切换开关，放在同一行因果关系才明确；明细表标题右侧标注当前区间。
- 🐛 **修复环形图图例显示原始 ID**：模型名映射表（`modelmap.json`）是改动前缓存的，24 小时内不会刷新，导致图例里出现 `gemini-3.8-flash-tiered` 这类裸 slug。为该缓存加了**格式版本号**，版本不符立即作废重建 —— 以后改动名称生成逻辑只要 +1 即可。
- 🏷️ 图例名称截断长度放宽到 26 字符（`Gemini 3.8 Flash (High)` 现在能完整显示）。

### v2.0.1 (2026-09-22)
- 🐛 **修复图表刷新后消失**：每次渲染前都清空了图表容器，把 ECharts 自己插入的 canvas 删掉了，而实例还在复用 → 第二次自动刷新（10 秒后）图就空了。改为**只在首次创建实例时清空容器**，并在无数据/库未加载时正确 `dispose`。
- 📈 **总 Token 口径调整**：缓存读取（缓存命中）现在计入总量，`总 Token = 输入 + 输出 + 缓存读取`；KPI 卡、趋势图、占比图、明细表占比条统一改用该口径。
- 📊 趋势图新增「缓存读取」堆叠系列，一眼看出缓存占了多大比例（原图只画输入+输出）。

### v2.0.0 (2026-09-22) —— 架构重写：去 GUI，改浏览器面板
- 🖥️ **彻底移除 WPF / 悬浮球 / 系统托盘 / WebView2**，改为「**无窗口采集服务 + 本地 HTTP 服务 + 浏览器面板**」。双击 `start.bat` 直接看到网页。
- 🧩 依赖大幅精简：不再需要 WPF/WinForms/WebView2 运行时；源码 16 个 `.cs` → **9 个 `.cs` + 1 个 `index.html`**，输出目录从十余个 DLL 降到 6 个文件。
- 🪟 程序改为**无控制台窗口**（WinExe），启动不留黑框；因此启动脚本也不需要额外的 `.vbs` 隐藏窗口。
- 🌐 新增 `HttpServer`（基于 `TcpListener`），**无需管理员权限、无需 URL 预留**；新增 `/api/export.csv` 导出接口。
- 📉 面板升级为可缩放网页：趋势图支持滚轮/拖动缩放，明细表带占比条，新增状态栏（配额刷新时间 / 扫描时间 / 自动刷新倒计时 / 错误提示）。
- 🧹 **脚本收敛为 3 个**（`start.bat` / `build.bat` / `stop.bat`）；清掉 `.vs/`、`releases/`、`scripts/poc/`、旧 Debug 输出与重复脚本。
- 🐛 修复 `build.bat` 一直引用不存在的 `.sln` 文件（仓库里只有 `.slnx`），改为直接编译 csproj。

### v1.3.0 (2026-09-22)
- 🖥️ WebView2 用量面板（v2.0.0 已改为浏览器面板）
- 🔧 修复分模型统计中的「未知模型」：系统步骤按 `executionId` 继承该轮模型
- 🔧 修复 emoji 显示为方块（字体回退列表）
- 🛡️ 账本清理加固：时间戳异常兜底、年份不合理不删

### v1.2.0 (2026-09-22)
- 📈 新增 Token 用量统计（日/周/月）、本地账本与 3 个月滚动保留、胶囊常驻今日用量、CSV 导出
- 🧩 抽出 `RpcDiscovery`，配额与用量共用端点缓存

### v1.1.0 (2026-09-03)
- ⚡ 支持 Gemini 3.8 Flash；模型与状态详情板；动态模型识别与降序排列
- 📐 上下屏幕智能自适应展开；屏幕位置自动记忆；随系统开机自启
- 🔔 满血恢复与低配额预警；常驻核心模型自由切换；透明度调节

### v1.0.0 (2026-09-02)
- 🎉 首次开源发布：0 Token 本地 RPC 配额监控、真·置顶防最小化、系统托盘常驻。

---

## 🛠️ 本地构建

环境要求：Windows 10 / 11，已安装 [.NET 10 SDK](https://dotnet.microsoft.com/)。

```bash
scripts\build.bat
# 或
dotnet build src/AntigravityQuota/AntigravityQuota.csproj -c Release -o bin
```

---

## 📤 发布流程

`bin/` 已移出版本控制，所以 **zip 是唯一的分发渠道**，发布时别漏了上传附件。

1. **打包**：双击 `scripts\package.bat` → 生成 `releases\antigravity-quota-widget-v<版本>.zip`

2. **推送代码与标签**：
   ```bash
   git push origin main --tags
   ```

3. **在 GitHub 网页建 Release**：打开
   `https://github.com/Rmistletoe/antigravity-quota-widget/releases/new`
   - *Choose a tag* 选 `v<版本>`（本地已打好 tag）
   - 标题 / 说明照抄本 README 的版本更新记录
   - 把 `releases\antigravity-quota-widget-v<版本>.zip` 拖进附件区
   - 点 **Publish release**

> 发布包结构（与 v1.x 一致）：`bin/` + `scripts/` + `start.bat` + `README.md` + `LICENSE`。
> 解压后双击 `start.bat` 即可运行，无需编译 —— 但用户机器需装 .NET 10 运行时。

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
