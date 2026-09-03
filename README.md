# ⚡ Antigravity Quota Widget

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows-0078D6?logo=windows&logoColor=white" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" />
  <img src="https://img.shields.io/badge/UI-WPF%20%2F%20DirectX-10B981" />
  <img src="https://img.shields.io/badge/Overhead-0%20Token%20Cost-brightgreen" />
  <img src="https://img.shields.io/badge/License-MIT-blue.svg" />
</p>

常驻 Windows 桌面的轻量级、高颜值 Google Antigravity AI 配额监控悬浮球组件。秒级平滑倒计时、0 Token 开销、原生 Direct3D 硬件加速，支持单例运行与系统托盘。

---

## ✨ 核心特性

- 🚀 **0 Token 消耗与秒开响应**：直接对接本地运行中的 Antigravity LanguageServer 内部 RPC 协议，耗时 < 2 毫秒，不消耗任何云端 API 额度。
- 📊 **双重配额实时监控**：
  - ⚡ **5小时滚动限额**：实时跟踪 Gemini 3.8 Flash 及核心模型平滑配额与重置倒计时。
  - 📅 **本周限额总量**：实时跟踪本周总可用额度。
- 🎨 **高颜值浅色珍珠白磨砂胶囊**：采用紧凑极简药丸（Pill）形态，珍珠微透磨砂玻璃质感，搭配高对比深空灰文字与呼吸发光状态灯。
- 📋 **点击弹出「模型与状态详情板」**：单击悬浮球即可展开精美详情面板（动态加载 Antigravity 全量可用模型清单、Thinking 深度与 Fast 标签，支持点击模型名称一键复制到剪贴板）；点击外部区域**常驻不自动关闭**，仅在**再次点击悬浮球**或面板收起按钮时关闭。
- 🔍 **智能悬停浮窗（Hover ToolTip）**：未展开面板时，鼠标悬停在胶囊上自动浮现快速明细与账户信息，移开自动隐藏。
- 📌 **真·底层硬件级置顶防隐藏**：底层集成 Win32 `WndProc` 消息钩子与 `WS_EX_TOOLWINDOW` 样式，有效拦截 Windows “显示桌面”（Win+D / 点击任务栏最右端）的最小化指令，永远不被其他窗口覆盖。
- 💾 **屏幕位置自动记忆**：拖拽到屏幕任意位置后自动持久化到本地配置，再次启动自动停留在上次的位置。
- 🚀 **随系统开机自启动**：右键菜单一键开启/关闭随 Windows 开机启动（无管理员 UAC 弹窗干扰）。
- 🔔 **满血恢复提醒与低额度呼吸预警**：5 小时滚动配额冷却完毕满血恢复时，自动推送系统托盘通知；配额低于 20% 时由绿变橙，低于 10% 时变红并发光增强，防编码中断。
- 🎯 **常驻主显示项自由切换**：右键快速切换胶囊常驻第一指标（`Gemini 3.8 Flash` / `Claude Sonnet 4.6` / `本周总额度` / `纯倒计时`）。
- 👻 **胶囊透明度调节 & 🧲 贴边吸附半隐藏**：支持多档透明度（100% / 85% / 70% / 50%）；拖到屏幕边缘自动磁吸附，鼠标离开后微缩半透明收起，鼠标移入滑出，绝不遮挡视野。
- 🛡️ **单例模式（Single-Instance）**：利用全局 `Mutex` 防止重复多开，重复启动时自动激活唤醒已有实例。
- 🎈 **丰富退出方式**：胶囊自带一键关闭 `✕` 按钮、系统托盘右键菜单、胶囊右键菜单及 `scripts/stop.bat` 一键脚本，再也不用开任务管理器杀进程。

---

## 📂 项目工程结构

```
antigravity-quota-widget/
├── AntigravityQuota.slnx          # Visual Studio 解决方案文件
├── LICENSE                        # MIT 开源许可证
├── README.md                      # 项目说明文档
├── .gitignore                     # Git 忽略配置
├── start.bat                      # 根目录快捷启动
│
├── bin/                           # 编译输出的原生程序
│   └── AntigravityQuota.exe       # 独立可执行程序
│
├── releases/                      # 本地发布包归档目录
│   └── antigravity-quota-widget-v1.1.0.zip
│
├── src/                           # 源码目录
│   └── AntigravityQuota/
│       ├── AntigravityQuota.csproj
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml / MainWindow.xaml.cs
│       ├── QuotaService.cs
│       ├── ConfigManager.cs
│       ├── AutoStartHelper.cs
│       └── IconHelper.cs
│
└── scripts/                       # 运维与构建脚本目录
    ├── start.bat                  # 启动脚本
    ├── start_silent.vbs           # 静默启动脚本（无黑框）
    ├── stop.bat                   # 一键退出脚本
    ├── build.bat                  # 一键编译构建脚本
    └── package.bat                # 一键发布打包脚本
```

---

## 🚀 版本更新记录

### v1.1.0 (2026-09-03)
- ⚡ **支持 Gemini 3.8 Flash**：胶囊最顶层核心监控更新为最新版 3.8 Flash。
- 📋 **模型与状态详情板**：左键单击展开全量模型与多组限额详情板；点击外部常驻不收起，再次单击胶囊关闭；智能区分拖拽与单击。
- 🔮 **动态模型识别与降序排列**：根据版本语义倒序展示所有官方模型，高亮标注 **「🔥 最新」** 徽章，点击模型名称一键复制到剪贴板。
- 📐 **上下屏幕智能自适应展开**：在屏幕下半部分自动向上展开，在屏幕上半部分自动向下展开，展开/收起时悬浮胶囊绝对坐标固定不跳位。
- 💾 **屏幕位置自动记忆**：拖拽位置自动保存至 `%APPDATA%\AntigravityQuota\config.json`，重启后精准恢复。
- 🚀 **随系统开机自启**：右键一键勾选/取消开机启动（无管理员提权弹窗）。
- 🔔 **满血恢复与低配额预警**：5 小时配额重置满血时发送 Windows 托盘气泡通知；低额度时呼吸发光变色预警。
- 🎯 **常驻核心模型自由切换**：右键快速在 `Gemini 3.8 Flash` 与 `Claude Sonnet 4.6` 间切换主监控项。
- 👻 **透明度自由调节**：支持 100% / 85% / 70% / 50% 四档透明度调节。

### v1.0.0 (2026-09-02)
- 🎉 首次开源发布，实现 0 Token 本地 RPC 配额监控、真·置顶防最小化、系统托盘常驻。

---

## 📦 快速使用

### 1. 运行方式
直接在仓库根目录下双击：
* **`start.bat`** 或进入 `scripts/` 双击 **`start_silent.vbs`**（无黑框静默启动）
* 或直接进入 `bin/` 目录运行 **`AntigravityQuota.exe`**

### 2. 快捷操作
* **拖拽移动**：鼠标左键按住胶囊任意位置即可随意拖拽到屏幕任何角落（支持 144Hz 高刷，自动记忆位置）。
* **展开详情**：单击胶囊展开/收起全量模型与配额详情看板，支持一键复制模型名称。
* **右键菜单**：切换常驻模型、调节透明度、开启开机自启、刷新与退出。
* **快速退出**：点击胶囊右侧的 `✕` 按钮，或右键托盘图标点击“退出程序”，也可双击 `scripts/stop.bat`。

---

## 🛠️ 本地构建与打包

环境要求：Windows 10 / 11，已安装 [.NET 10 SDK](https://dotnet.microsoft.com/)。

1. **一键构建**：直接双击运行 `scripts/build.bat`。
2. **一键发布打包**：双击运行 `scripts/package.bat`，自动在 `releases/` 目录生成最新版本 zip 压缩包。
3. **命令行构建**：
   ```bash
   dotnet build src/AntigravityQuota/AntigravityQuota.csproj -c Release -o bin
   ```

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
