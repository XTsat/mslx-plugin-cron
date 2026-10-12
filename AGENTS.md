# AGENTS.md — mslx-plugin-cron

> 本文件是任何 AI 编码代理（agent）进入本项目时的**必读**工作说明书。
> 它约定项目的架构、代码规范、文档维护与提交流程。任何对代码、配置、行为的改动，都必须同时遵守本文档的规则。
>
> 本文件基于 agent-templates 的 `generic` 通用模板（https://github.com/XTsat/agent-templates），技术栈占位已回填为项目实际值。

---

## 1. 项目概览

**MSLX 开服器插件**：多服定时任务。同一任务可绑定**多台服务器**统一执行，支持从宿主「服务器绑定任务」一键迁移（带预览确认），迁移后宿主旧任务被禁用、由插件自调度器按每台服务器的 `LastRunTime` 独立调度。

- 项目类型：插件（宿主 MSLX 进程内加载的 .NET Library）
- 主语言：C# 13 / .NET 10（`net10.0`）；前端 TypeScript + Vue 3
- 包管理：后端 NuGet（`MSLX.Plugin.Cron.csproj` 的 `PackageReference`）；前端 pnpm（`Frontend/`）
- 构建工具：`dotnet build`（Release 下 ILRepack 合并依赖）+ `pnpm build`（前端产物嵌入资源）

核心文件清单：

| 文件 | 必选 | 职责 |
|------|------|------|
| `PluginEntry.cs` | ✅ | 插件入口：实现 `IPlugin`，注册服务/端点，启动调度器 |
| `Controllers/CronTaskController.cs` | ✅ | REST API：任务 CRUD + 宿主任务迁移接口 |
| `Services/CronTaskStore.cs` | ✅ | 数据存储：cron-tasks.json / cron-migration.json 读写 |
| `Services/CronSchedulerService.cs` | ✅ | 自调度器：1s tick、按服务器独立 LastRunTime |
| `Services/CronMigrationService.cs` | ✅ | 宿主任务迁移/还原逻辑 |
| `Models/CronTaskDefinition.cs` | ✅ | 任务数据定义 |
| `Frontend/` | ✅ | Vue 3 前端（独立子项目，产物 `dist/mslx-plugin-entry.js` 被嵌入） |
| `CHANGELOG.md` | ✅ | 变更日志（格式见第 7 章） |
| `README.md` | ✅ | 中文文档 |
| `MSLX.Plugin.Cron.csproj` | ✅ | 依赖声明 + Release 合并目标 |

---

## 2. 目录结构与架构

### 2.1 标准目录结构

```
project-root/
├── PluginEntry.cs             # 插件入口（IPlugin）
├── Controllers/               # REST API 层
├── Services/                  # 业务层（存储 / 调度 / 迁移）
├── Models/                    # 数据定义层
├── Frontend/                  # Vue 3 独立前端子项目
│   ├── src/views/             # 页面（TaskListPage.vue）
│   ├── src/api/               # 后端 API 封装
│   ├── src/utils/             # cron 工具
│   └── dist/                  # 构建产物（嵌入资源，勿手改）
├── Properties/                # launchSettings.json
├── appsettings.json           # 宿主标准配置
├── CHANGELOG.md
├── README.md
└── MSLX.Plugin.Cron.csproj
```

### 2.2 架构原则

- 依赖方向严格单向：`Controllers → Services → Models`；`PluginEntry → Services`；`Models` 不依赖任何人。
- 前端为独立子项目：`Frontend/` 内的 Vue 代码禁止直接引用后端类型；后端仅通过 `Frontend/dist` 嵌入产物交互。
- 新增文件先判断归属目录，**遵循相邻相似文件的既有写法**，保持整体一致性。
- 单一职责：一个文件/模块只做一件事；公共常量放所属类内 `private const`，禁止散落硬编码字符串。
- 调度语义复刻宿主 `TaskSchedulerService`：1s tick、UTC 基准、Cronos `IncludeSeconds`、`GetNextOccurrence + TimeZoneInfo.Local`——修改调度器前先读 `CronSchedulerService` 头注释，保持与宿主语义一致。

---

## 3. 技术栈与依赖

- **语言版本**：C# 13（`net10.0`，`Nullable` + `ImplicitUsings` 开启）；前端 TypeScript ~6.0 + Vue 3。
- **包管理**：后端 NuGet，`MSLX.Plugin.Cron.csproj` 为唯一声明来源；前端 pnpm（`Frontend/package.json` + `pnpm-lock.yaml` 提交入库）。
- **构建/运行**：前端 `cd Frontend && pnpm build`（产物进 `dist/`）；后端 `dotnet build -c Release`（`MergeDependencies` 目标用 ILRepack 合并依赖 DLL 为单文件，并删除 appsettings/deps 等垃圾文件）。
- **测试**：无强制测试框架，改动后自检语法与逻辑；前端有类型检查（`vue-tsc -b`，已并入 `pnpm build`）。
- **禁止引入**：不引入与原功能无关的依赖；非必要不新增依赖声明。后端禁止引入宿主已提供的组件（`MSLX.SDK` 以 `ExcludeAssets="runtime"` 引用，运行时由宿主提供，合并时除外）。
- 技术栈升级（语言版本、框架、主要依赖）属于用户可见变更：必须同步更新 README 对应章节，并先与用户确认。

---

## 4. 核心代码规范

### 4.1 命名约定

| 元素 | 约定 |
|------|------|
| 类 / 类型 / 命名空间 | `PascalCase`（`CronTaskStore`、`CronSchedulerService`） |
| 文件 / 目录 | `PascalCase` 与后端一致（`Controllers/`、`Services/`）；前端 Vue 组件 `PascalCase.vue`，其余文件 camelCase |
| 方法 / 局部变量 | camelCase |
| 私有字段 | `_` 前缀 + camelCase（`_store`、`_lock`） |
| 常量 | 类内 `private const` / `static readonly`（`PascalCase` 或 `UPPER_SNAKE`，与相邻代码一致） |

### 4.2 类型与语法

- 函数签名尽量完整类型标注（参数 + 返回类型）；控制器动作返回 `IActionResult`，异步路径用 `async Task`。
- 禁止裸 `object` 丢类型、禁止无理由的类型忽略；`null!` 仅用于 DI 后赋值字段（参照 `PluginEntry.Instance`）。
- 空值语义：`string?` 与 `string` 严格区分，可空性由编译器校验（`Nullable` 已开启）。

### 4.3 注释规范

- **注释只写 non-obvious reason**：解释「为什么这么做」，而不是「做了什么」（代码本身应自文档化）
  - 好：`// 不再还原宿主任务：宿主进程每次停止都会触发 OnUnload，若自动还原会把已接管的宿主任务重新启用，导致双重执行`
  - 坏：`// 启动调度器`
- **禁止残留 intermediate attempts**：不保留被注释掉的旧代码、TODO 但未完成的方案、调试输出。
- 类/公共方法写 `/// <summary>` 中文 docstring；逻辑明显的私有方法可省略。
- 长代码分区用 `// ---- 分区名 ----` 分隔条（参照 `CronTaskController.cs` 既有风格）。

### 4.4 日志

- 统一使用 `MSLX.SDK.MSLX.Logger.Info/Warn/Error`。
- **日志必须带 `[mslx-plugin-cron]` 前缀**，错误日志带异常对象（`Error(message, ex)`）。
- 按级别区分：`Info` 常规/启动/停止、`Warn` 可恢复问题、`Error` 失败，禁止无差别全打 Info。

### 4.5 错误处理与防御性编程

- 外部输入必须校验并优雅 fallback：非法任务定义、脏 JSON、空列表都要有默认行为，不因单条脏数据崩溃。
- 外部 IO（读 `cron-tasks.json` 失败）必须 try/except 并记录日志，返回空列表降级，**不允许静默吞异常**。
- 精确捕获异常类型（`Exception` 兜底可，但能更精确就精确）；空 catch 必须注释说明理由（参照 `CronSchedulerService.Stop`）。
- 降级链路清晰：首选方案 → 兜底方案 → 明确的失败提示（API 返回 `ApiResponse { Code, Message }`）。

### 4.6 并发

- 数据读写必须持锁：`CronTaskStore` 用 `lock (_lock)` 保护全部读写路径，**禁止绕过 Store 直接碰文件**。
- 调度循环用 `CancellationTokenSource` + `Task.Run`，停止时 `Cancel + Dispose`（参照 `CronSchedulerService`）。
- 文件写入必须原子：先写 `.tmp` 再 `File.Replace`/`Move`（参照 `CronTaskStore.AtomicWrite`）。

### 4.7 数据与文件

- 数据文件统一 UTF-8 JSON（Newtonsoft.Json，`Formatting.Indented`）。
- 数据目录取自 `PluginEntry.Instance.Config().GetDataPath()`，禁止硬编码路径或散落写入仓库内。
- 迁移记录（`cron-migration.json`）与任务定义（`cron-tasks.json`）分离存储，职责单一。

---

## 5. 配置与数据文件

- **宿主标准配置**：`appsettings.json` / `appsettings.Development.json`（仅 Logging/AllowedHosts，Release 输出时被删除，插件不使用运行时配置）。
- **插件数据**：`<宿主数据目录>/plugins/mslx-plugin-cron/` 下：
  - `cron-tasks.json`：多服任务定义（`List<CronTaskDefinition>`）
  - `cron-migration.json`：迁移状态记录（是否已迁移、数量、时间）
- 新增配置/数据字段 → 同时改：`Models/CronTaskDefinition.cs`（或 Store 读写逻辑）+ README + CHANGELOG。
- 迁移语义（重要，勿改）：`OnLoad` **不自动迁移**（可能使宿主旧任务失效），只记录状态供页面提示；`OnUnload` **不自动还原**宿主任务（宿主进程每次停止都会触发 OnUnload，自动还原会导致双重执行）。

---

## 6. 对外接口 / 命令清单

REST API 前缀：`api/plugins/mslx-plugin-cron/tasks`

| 接口 | 方法 | 说明 |
|------|------|------|
| `/tasks/list` | GET | 全部任务（非管理员按权限过滤绑定服务器） |
| `/tasks` 系列 | GET/POST/PUT/DELETE | 任务 CRUD |
| `/tasks/migrate` 系列 | POST | 宿主任务迁移（预览/确认/还原） |

- 鉴权模式与宿主 `CronTaskController` 一致：`UserId` claim + `HasResourcePermission("server", instanceId)`，shell 类型仅管理员。
- 响应统一 `ApiResponse<T> { Code, Message, Data }`；错误用 `Forbidden/BadRequestMsg/NotFoundMsg` 辅助方法。
- 接口清单以 `Controllers/CronTaskController.cs` 为准，README 同步维护概览。

---

## 7. 文档维护规范（强制，不可省略）

> 本项目的**硬性要求**：任何改动在合并前，必须同步维护 `README.md` 与 `CHANGELOG.md`。未同步文档 = 任务未完成。

### 7.1 CHANGELOG 维护

#### 流程

- 日常改动先记在 `## [Unreleased]` 段（顶部固定）。**发版时**再把内容移动到带版本号的新段。
- 多个不相关改动在同一工作周期内，各自独立记入 `[Unreleased]`，**不要**合并为一条。
- **发版日期**：版本段日期 `(YYYY-MM-DD)` 填**当前日期**（以发版当天的系统时间为准）。

#### 格式

统一使用 **分组式**（与 scaffold 的 `CHANGELOG.md.template` 一致）：按分类用 `###` 子标题分组，条目用「标题 + 冒号 + 说明」，**仅重点条目/重点内容才加粗**：

```markdown
# Changelog

## [Unreleased]

> 一句话总结这个版本（可选）

### 新增
- 功能 A：说明
- **功能 B**：说明

### 修复
- 问题 C：说明

## v1.1.0 (2026-10-12)

> 一句话总结这个版本（可选）

### 新增
- 功能 D：说明
```

分组子标题分类：`新增` / `修复` / `变更` / `移除` / `性能`（只保留本次有内容的分类，无内容的分类不写空标题）。

**加粗规则**：条目默认不加粗；写 changelog 时先识别本次改动中的**重点改动**（核心新功能、重要修复、破坏性变更等），只对重点改动用 `**加粗**` 标题强调，其余条目一律不加粗。

**版本总结**：版本标题（`## [Unreleased]` / `## vX.Y.Z (YYYY-MM-DD)`）与分类子标题之间，可加一行 `> 引用` 形式的一句话总结（概括该版本/该批改动的主旨），可选，不强制。

#### 版本号联动

采用 **SemVer**：新功能 → `minor` 递增；bug 修复/小改进 → `patch` 递增。涉及功能新增/删除、行为变化时，**各处同步递增**：

| 位置 | 说明 |
|------|------|
| `PluginEntry.cs` | `Version` 属性（与 CHANGELOG 最新版本一致） |
| `CHANGELOG.md` | 发版时从 `[Unreleased]` 移入 `## vX.Y.Z (YYYY-MM-DD)` |
| `README.md` | 版本徽章 / 安装版本号（如有） |

纯修复/内部重构可只更新 `[Unreleased]` 条目，不递增版本号。

#### 多会话并行开发

**⚠️ 更新 `CHANGELOG.md` 前必须先读取当前文件内容**，识别并保留其他会话已写入的既有条目（含 `[Unreleased]` 下未提交的功能），只追加自己的条目，**严禁整文件覆盖或删除他人记录**。

### 7.2 README 维护

- **功能/介绍**：项目是什么、核心功能、特性列表。
- **快速开始**：构建（前端 pnpm build → 后端 dotnet build -c Release）、安装（release DLL 放入宿主插件目录）。
- **配置**：配置项/数据文件说明。
- **其它**：常见问题、许可等。

#### 何时必须更新 README

| 改动类型 | 必须更新位置 |
|----------|-------------|
| 功能新增/删除 | 功能/介绍 |
| 使用方式/API 变化 | 快速开始、接口章节 |
| 数据/行为变化 | 配置、常见问题 |

**何时不需要更新 README：**
- 纯内部重构（行为完全不变）、样式调整、bug 修复（除非修复改变了用户可见行为）。

#### 格式与多会话约束

- **禁止无意义的换行**：正文长句不要人为软换行（一段文字保持一行，由 Markdown 自动折行显示）；空行只用于分隔标题、表格、列表、代码块等结构性区块。
- **多会话并行修改**：更新 `README.md` 前必须先读取当前文件内容，识别并保留其他会话已写入的既有内容，只追加/修改自己的部分，**严禁整文件覆盖或删除他人记录**。

### 7.3 PR 描述规范

- **PR 描述只写最终行为**：描述最终完成了什么，而不是过程中尝试了什么。
- 禁止提及：尝试过的方案、被否定的设计、从未合入的中间状态。
- 只写 diff 里看不出来的取舍理由（如「选用方案A因为性能比B高20%」）。

---

## 8. 提交与发布流程

### 8.1 提交规范（Conventional Commits）

提交信息遵循 **Conventional Commits**，格式：

```
<type>(<scope>): <subject>
```

- `<type>` 用英文小写，可选：`feat`（新功能）/ `fix`（修复）/ `docs`（文档）/ `refactor`（重构）/ `perf`（性能）/ `test`（测试）/ `build`（构建）/ `chore`（杂务）/ `style`（格式）。
- `<scope>` 可选：受影响模块名（如 `controller`、`scheduler`、`frontend`）。
- `<subject>` 用**中文短句式**描述最终行为，不带句号。

示例：

```
feat: 新增任务绑定多服务器
fix(controller): 修复非管理员列表越权
docs: 补充迁移说明
```

- **禁止**将未完成的功能、多轮迭代的中间状态、被废弃的方案写进提交信息。
- **提交前必须先向用户展示改动内容和提交标题**，等用户确认后再执行 commit。
- 推荐顺序：完成功能代码 → 跑通验证 → 更新 CHANGELOG 与 README → 提交。
- 文档更新与代码改动**在同一提交中完成**（不单独拆「补文档」提交）。
- 只有用户明确要求时才 commit / push。

### 8.2 版本号与发布

- 采用 **SemVer**：新功能 → `minor` 递增；bug 修复/小改进 → `patch` 递增。
- `PluginEntry.cs` 的 `Version` 与 `CHANGELOG.md` 最新版本必须一致。
- 发版时打 `git tag vX.Y.Z`，tag 推送同样需用户确认。

### 8.3 测试与验证

- 后端：`dotnet build -c Release` 通过（Release 会跑 ILRepack 合并，能暴露依赖问题）。
- 前端：`cd Frontend && pnpm build` 通过（含 `vue-tsc -b` 类型检查）。
- 无单测框架，改动后至少跑通入口冒烟（宿主内加载插件、调度器启动无异常日志）。

---

## 9. 最终检查清单（每次任务完成前）

- [ ] 技术栈：第 3 章与实际项目一致（依赖增删已同步）
- [ ] 架构：改动符合 `Controllers → Services → Models` 单向依赖；前端改动在 `Frontend/` 内完成
- [ ] 注释：只写 non-obvious reason，无残留中间状态、无被注释掉的旧代码
- [ ] 日志：带 `[mslx-plugin-cron]` 前缀，异常分类记录，无空 catch
- [ ] CHANGELOG：`[Unreleased]` 已追加条目，保留了他人的既有记录
- [ ] README：涉及用户可见变更时已同步更新
- [ ] 版本号：`PluginEntry.cs` Version 与 `CHANGELOG` 最新版本一致（如需发版）
- [ ] 并发：数据读写持锁、文件写入原子、调度器停止释放资源
- [ ] 提交信息：Conventional Commits 格式，中文短句式 subject
- [ ] 验证：`dotnet build -c Release` 与 `pnpm build` 已跑通（如改动涉及对应端）
