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
- 🎨 **高颜值浅色珍珠白磨砂胶囊**：采用紧凑极简药丸（Pill）形态，珍珠微透磨砂玻璃质感，搭配高对比深空灰文字与呼吸发光状态灯。
- ⚡ **Gemini 3.7 Flash 默认优先置顶**：实时显示当前核心模型的剩余配额百分比与 5 小时 Sprint 滚动重置倒计时。
- 🔍 **智能悬停浮窗（Hover ToolTip）**：鼠标移动到悬浮胶囊上自动浮现多模型明细（Gemini 3.7 Flash、Gemini 3.1 Pro、Claude Sonnet 4.6、GPT-OSS 120B 等）与账户信息，移开自动隐藏。
- 📌 **真·底层硬件级置顶防隐藏**：底层集成 Win32 `WndProc` 消息钩子与 `WS_EX_TOOLWINDOW` 样式，有效拦截 Windows “显示桌面”（Win+D / 点击任务栏最右端）的最小化指令，永远不被其他窗口覆盖。
- 🛡️ **单例模式（Single-Instance）**：利用全局 `Mutex` 防止重复多开，重复启动时自动激活唤醒已有实例。
- 🎈 **丰富退出方式**：胶囊自带一键关闭 `✕` 按钮、系统托盘右键菜单、胶囊右键菜单及 `stop.bat` 一键脚本，再也不用开任务管理器杀进程。

---

## 📦 快速使用

### 1. 运行方式
直接在仓库根目录下双击运行：
* **`start.bat`** 或 **`start_silent.vbs`**（无黑框静默启动）
* 或直接进入 `bin/` 目录运行 **`AntigravityQuota.exe`**

### 2. 快捷操作
* **拖拽移动**：鼠标左键按住胶囊任意位置即可随意拖拽到屏幕任何角落（支持 144Hz 高刷）。
* **查看详情**：鼠标悬停在胶囊上即可查看所有模型的配额进度条与重置倒计时。
* **快速退出**：点击胶囊右侧的 `✕` 按钮，或右键托盘图标点击“退出程序”。

---

## 🛠️ 本地构建 (Build from Source)

环境要求：Windows 10 / 11，已安装 [.NET 10 SDK](https://dotnet.microsoft.com/)。

```bash
# 克隆仓库
git clone https://github.com/your-username/antigravity-quota-widget.git
cd antigravity-quota-widget

# 编译生成 Release 版本
dotnet build src_wpf/AntigravityQuota.csproj -c Release -o bin
```

编译完成后可执行文件将输出至 `bin/AntigravityQuota.exe`。

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源。
