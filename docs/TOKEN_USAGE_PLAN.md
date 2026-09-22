# Token 用量统计 扩展方案（v2.0 规划）

> 目标：在现有 5 小时 / 每周配额悬浮球基础上，新增**每日 / 每周 / 每月 token 用量统计**，本地保留 **3 个月**历史。
>
> 本方案的所有技术结论均已在**本机 Antigravity v1.1.0 环境实测验证**，非推测。

---

## 一、可行性结论（结论先行）

**可行，且比预想的更干净。** Antigravity 的 LanguageServer 内部 RPC 已经暴露了**逐条调用级的 token 账目**，位置精确到「哪一步、哪个模型、输入/输出/思考/缓存各多少 token、发生在哪纳秒」。

关键实测数据：

| 探针 | 结果 |
|---|---|
| `GetCascadeTrajectorySteps`（参数 `cascadeId`） | ✅ HTTP 200，6451 条 step，其中 **3150 条带用量记录** |
| 增量拉取 `stepOffset=6438` | ✅ 仅 13 步 / **0.07 MB / 39 ms** |
| 全量拉取 `stepOffset=0` | 6451 步 / 37 MB / 1218 ms（只在首次回填用一次） |
| 模型真名映射 `GetAvailableModels` | ✅ 拿到 **26 个** `MODEL_PLACEHOLDER_Mxxx → 显示名` |
| 现有本地历史 | 仅回溯到 **2026-09-02（20 天）**，且官方**正在删除旧会话** |

**最后一行是本方案的核心动因**：`%USERPROFILE%\.gemini\antigravity\annotations\` 有 26 个会话标记，但 `conversations\` 只剩 14 个 `.db`——**12 个会话的正文已被官方裁掉，用量数据随之永久消失**。所以自建账本不是"加分项"，是"三个月"这个需求能否成立的前提。

---

## 二、数据源实测结构

### 2.1 主数据源：`GetCascadeTrajectorySteps`

```
POST https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/GetCascadeTrajectorySteps
Headers: Connect-Protocol-Version: 1
         x-codeium-csrf-token: {token}
Body:    {"metadata":{"csrf_token":"...","ide_name":"antigravity"},
          "cascadeId":"35fb5e58-...","stepOffset":6438}
```

**⚠️ 参数坑**：字段名必须是 `cascadeId`，传 `trajectoryId` / `conversationId` 一律返回
`{"code":"unknown","message":"trajectory not found"}`。
`GetAllCascadeTrajectories` 返回的 **对象 Key** 才是 `cascadeId`；里面的 `trajectoryId` 字段是另一套 ID，不能用。

响应里每条 step 的用量字段（数值是**字符串**，需 `int.Parse`）：

```jsonc
{
  "steps": [{
    "type": "CORTEX_STEP_TYPE_PLANNER_RESPONSE",
    "metadata": {
      "createdAt": "2026-09-22T07:35:47.990533800Z",   // UTC + 纳秒
      "source": "CORTEX_STEP_SOURCE_MODEL",
      "generatorModel": "MODEL_PLACEHOLDER_M298",
      "sourceTrajectoryStepInfo": { "cascadeId": "...", "stepIndex": 2 },
      "modelUsage": {                                   // ← 主用量块
        "model": "MODEL_PLACEHOLDER_M318",
        "inputTokens": "22419",
        "outputTokens": "187",
        "thinkingOutputTokens": "137",
        "responseOutputTokens": "50",
        "apiProvider": "API_PROVIDER_GOOGLE_GEMINI",
        "messageId": "bot-4ea8c25d-..."
      },
      "retryInfos": [ { "usage": { /* 同结构，重试/checkpoint 也计入 */ } } ]
    }
  }]
}
```

**两个必须处理的细节**：
1. `retryInfos[].usage` 是**同一笔调用的重试记录**，也带 token。实测 checkpoint 型 step 的用量只出现在 `retryInfos` 里 —— 漏了就少算。策略：`modelUsage` 存在时用它，否则回退到 `retryInfos[].usage`，同 `messageId` 去重。
2. 一个轨迹里**约一半 step 没有任何用量**（用户输入、工具执行、文件变更类）。必须按"有 usage 才算一条账"过滤。

### 2.2 模型真名映射

用量里的 `model` 是内部枚举 `MODEL_PLACEHOLDER_M318`，**不能直接给用户看**。映射来自：

```
POST .../GetAvailableModels
→ {"response":{"models":{
     "gemini-3.8-flash-high": { "displayName": "Gemini 3.8 Flash (High)",
                                "model": "MODEL_PLACEHOLDER_M318", ... }, ...}}}
```

即 **`models[*].model` → `models[*].displayName`**。已抓到的完整表（26 条，建议随程序缓存一份 `modelmap.json`）：

| 占位符 | 显示名 | 占位符 | 显示名 |
|---|---|---|---|
| M318 | Gemini 3.8 Flash (High) | M299 | Gemini 3.7 Flash (Medium) |
| M319 | Gemini 3.8 Flash (Medium) | M300 | Gemini 3.7 Flash (Low) |
| M320 | Gemini 3.8 Flash (Low) | M84 | Gemini 3.5 Flash (High) |
| M298 | Gemini 3.7 Flash (High) | M20 | Gemini 3.5 Flash (Medium) |
| M72 | Gemini 3.6 Flash (Medium) | M187 | Gemini 3.5 Flash (Low) |
| M71 | Gemini 3.6 Flash (High) | M35 | Claude Sonnet 4.6 (Thinking) |
| M73 | Gemini 3.6 Flash (Low) | M26 | Claude Opus 4.6 (Thinking) |
| M16 / M36 / M37 | Gemini 3.1 Pro (High/Low) | M50 | Gemini 3.1 Flash Lite |

（另有 7 个无 `displayName` 的内部模型，遇到时原样显示占位符即可。）

**备用映射路径**：`GetCascadeTrajectoryGeneratorMetadata` 返回的 `customMetadata.model_enum` 与 `responseModel` 成对出现（如 `M298 → "gemini-3.7-flash"`），可作为兜底。该接口还额外提供 `contextWindowMetadata.estimatedTokensUsed` / `maxContextTokens`，是"上下文占用率"功能的数据源。

### 2.3 兜底数据源：本地 SQLite

`%USERPROFILE%\.gemini\antigravity\conversations\<cascadeId>.db`（14 个库 / 228 MB）

```
trajectory_meta(trajectory_id, cascade_id, trajectory_type, source)
steps(idx, step_type, status, metadata BLOB, step_payload BLOB, ...)
trajectory_metadata_blob(id, data BLOB)
```

`steps.metadata` 是 **protobuf BLOB**。能解析，但要手写 varint 解码，且**跨版本极易碎**。
→ **定位为兜底**：仅当 RPC 完全不可用（LanguageServer 未运行但用户想查历史）时启用，不作为主路径。

### 2.4 已废弃的路径（省得再试）

- `GetUserAnalyticsSummary` → 返回 `{}`，需额外入参，未启用
- `GetModelStatuses` → 返回 `{}`
- `GetConversationMetadata` → 需 `conversation_id`，与 cascadeId 体系不通用
- `transcript.jsonl` / `transcript_full.jsonl`（`brain\` 下，各 11–14 MB）→ **不含任何 token 字段**，grep 命中 0

---

## 三、方案设计

### 3.1 分层

```
LanguageServer RPC ─┐
                    ├─→ 采集层 TokenUsageService ─→ 账本 TokenLedger ─┬─→ 聚合 UsageAggregator ─→ UI
本地 .db（兜底）     ┘        （增量+去重+映射）      （JSONL, 90天）    │
                                                                       └─→ CSV 导出
```

### 3.2 新增模块

| 文件 | 职责 |
|---|---|
| `RpcDiscovery.cs` | 从 `QuotaService` **抽取**已有的进程发现逻辑（WMI 找 `language_server.exe` + `iphlpapi` 找端口 + 正则抠 `--csrf_token`），供两个 Service 共用，避免重复代码 |
| `TokenUsageService.cs` | 调 `GetAllCascadeTrajectories` → 对每个 cascade 从账本 `lastStepIndex` 起 `GetCascadeTrajectorySteps(cascadeId, stepOffset)` → 归一化 → 追加账本。含模型映射缓存与首次全量回填 |
| `TokenLedger.cs` | append-only JSONL 账本，按 `cascadeId + stepIndex` 幂等去重，月度分文件，启动时清理超过保留期的文件 |
| `UsageAggregator.cs` | 日 / 周（ISO 周，周一起）/ 月 三档聚合，按模型、按 workspace 拆分；带内存缓存，只重算受影响的桶 |

### 3.3 账本格式

路径：`%APPDATA%\AntigravityQuota\usage\YYYY-MM.jsonl`

```jsonc
{"v":1,"ts":"2026-09-22T07:35:47.990Z","cid":"35fb5e58","si":2,"eid":"82eac196",
 "m":"MODEL_PLACEHOLDER_M318","p":"GOOGLE_GEMINI",
 "in":22419,"out":187,"think":137,"resp":50,"cache":0}
```

字段极简（短键）是为了控制在 3 个月 / 约 2 万行 / **< 2 MB** 的体量。

**为什么不用 SQLite**：量太小，JSONL 无依赖、append 天然崩溃安全、用户能直接 `vim` 开来看、月度分文件让"删最老月份"变成一次 `File.Delete`。

**去重键**：`cid + si`（cascadeId + stepIndex）。同一 step 因重试/回填被重复拉取时幂等丢弃。
**注意**：`ts` 在写入时**必须转成本地时区**（徐州 UTC+8），否则凌晨 0–8 点的用量会被算到前一天。

### 3.4 采集时机

- 复用现有 `StartBackgroundPolling()`（已是 30 秒一次），采集走**独立定时器，间隔 60 秒**
- LanguageServer 不在运行 → 直接跳过，静默降级
- 首次运行做一次全量回填（37 MB / 1.2 s，后台 Task，不阻塞 UI）
- 单次拉取设上限保护（如最多 5000 步），防异常数据打爆内存
- **失败绝不影响现有配额显示**——这是硬约束，不许让新功能拖垮老功能

### 3.5 一个必须澄清的统计口径

配额接口的官方描述原文：

> *Quota is consumed proportionally to the **cost** of the tokens.*

**即 token 数 ≠ 配额消耗量。** 别在 UI 上暗示"用了 21M token 所以配额掉了这么多"。
另外实测发现 **`cacheReadTokens` 的量级远超输入**（9/22：输入 21.75 M，缓存读取 131.3 M）。

→ **UI 上必须把「输入 / 输出 / 思考 / 缓存读取」四列分开显示**，绝不合并成一个"总 token"。否则用户看到"今日 1.5 亿 token"会以为出 bug 了。（缓存读单价通常远低于正常输入，合并统计会严重误导成本判断。）

### 3.6 实测跑出的真实基线（2026-09-02 ~ 09-22）

| 日期 | 输入 | 输出 | 思考 | 缓存读取 | 调用次数 |
|---|---|---|---|---|---|
| 09-02 | 311 K | 18.7 K | 5.7 K | 540 K | 21 |
| 09-07 | 4.39 M | 235 K | 80 K | 24.95 M | 210 |
| 09-17 | 13.03 M | 413 K | 235 K | 74.13 M | 617 |
| 09-18 | 22.06 M | 421 K | 223 K | 74.88 M | 667 |
| 09-22 | 21.75 M | 724 K | 372 K | 131.32 M | 1108 |
| **合计** | **79.35 M** | **2.50 M** | **1.27 M** | **415.81 M** | **3644** |

模型分布：`M318 (Gemini 3.8 Flash High)` 占 **80.6 M / 81.2 M —— 约 99%**。
结论：默认常驻指标显示 Gemini 3.8 Flash 是对的，其他模型基本没在用。

---

## 四、配置扩展

`AppConfig` 新增：

```csharp
public bool   UsageTrackingEnabled    { get; set; } = true;
public int    UsageRetentionMonths    { get; set; } = 3;
public int    UsageScanIntervalSec    { get; set; } = 60;
public bool   UsageBackfillCompleted  { get; set; } = false;
```

---

## 五、分阶段落地

| 阶段 | 内容 | 验收标准 |
|---|---|---|
| **P0 · 采集先行** | `RpcDiscovery` 抽取 + `TokenUsageService` + `TokenLedger`，UI 只加一个"导出 CSV"按钮 | 连跑 2 天，账本行数与 Antigravity 界面实际对话量对得上；导出 CSV 数字与本文档基线同量级 |
| **P1 · 聚合与面板** | `UsageAggregator` + 详情面板新增「用量」页签，日/周/月三档切换，近 14 天迷你柱状图，分模型 Top 列表 | 三档数字自洽（周 = 周一~周日之和；月 = 该月日之和） |
| **P2 · 常驻展示** | 胶囊 Hover 浮窗加一行"今日输入/输出"；右键菜单加「打开用量详情 / 导出 CSV」 | 不影响胶囊原有 1 秒倒计时刷新性能 |
| **P3 · 增值：配额成本拟合** | 把现有 30 秒配额采样落盘 `(ts, bucketId, remainingFraction)`，用每个 5h 窗口的 fraction 降幅 ÷ 窗口内 token 数，回归估各模型的**相对配额成本系数** | 能回答"用 3.8 Flash (High) 比 (Low) 贵几倍"——这是官方 UI 不给的信息 |

**建议先做 P0 并让它跑起来**：账本一旦开始攒，后面所有功能都只是读数据；而拖着不做，官方每裁一次会话就永久少一段历史。

---

## 六、风险与对策

| 风险 | 对策 |
|---|---|
| 内部 RPC 协议非公开，升级可能改字段名 | 解析走**宽容模式**：字段缺失就跳过该 step，绝不抛异常打断；账本写 `"v":1` 版本号，便于将来迁移 |
| `MODEL_PLACEHOLDER_Mxxx` 新增枚举 | 映射表本地缓存成 json 并设过期重建；未知 ID 原样显示，不猜 |
| 会话被官方删除 | **不影响已入账数据**——这正是自建账本的意义 |
| 首发时不满 3 个月 | 官方已裁到只剩 20 天，UI 明确提示"数据自 2026-09-02 起"，别假装有 90 天 |
| 全量首拉 37 MB 卡顿 | 后台 Task + 进度提示；只在 `UsageBackfillCompleted=false` 时执行一次 |
| 重复计入 | `cid + si` 幂等键 + `messageId` 二级去重 |
| `cacheReadTokens` 误导成本 | 四列分列显示，永不合并 |

---

## 七、附：PoC 验证脚本

本次调研用的探针脚本（`GetAllCascadeTrajectories` → `GetCascadeTrajectorySteps` → 模型映射 → 按日聚合 → 输出 CSV/JSON）已验证可用。核心逻辑可直接移植进 `TokenUsageService`：

1. PowerShell `Get-CimInstance Win32_Process` 拿 `language_server.exe` 的 PID + CommandLine
2. 正则 `--csrf_token\s+([a-f0-9\-]+)` 抠 token
3. `netstat -ano` 匹配 PID 拿 LISTENING 端口（可能有多个，逐个试）
4. HTTPS（忽略自签证书）POST，带 `Connect-Protocol-Version: 1` + `x-codeium-csrf-token`
5. 遍历 `steps[].metadata`，取 `modelUsage` / `retryInfos[].usage`
