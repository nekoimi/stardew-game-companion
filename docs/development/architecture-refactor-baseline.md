# 架构重构基线与路径清单

## SGC-01 构建基线

基线提交为 `529a96c6278747767aa0bea9da78b9eab44a2629`。验证环境为 Windows x64、游戏 1.6.15.24356、SMAPI 4.5.2、Python 3.12.11、uv 0.7.14、.NET 8.0.428（Mod）、.NET 10.0.404（WPF）与 .NET 6.0.428（原生扩展编译）。

在基线提交的独立工作树，从根目录设置 `STARDEW_GAME_PATH` 为测试机的游戏目录。构建 Mod 和运行 C# 测试使用 .NET 8，将环境变量 `STAR_DOTNET_PATH` 指向独立的 .NET 6 SDK。以下 `$desktopSdk` 指向已安装的 .NET 10 SDK 的 `dotnet.exe`。

```powershell
uv sync --project runtime --locked
dotnet build src/StardewAI.Companion.Mod -c Release
& $desktopSdk publish src/StardewAI.Companion.Desktop -c Release -o artifacts/desktop
uv run --project runtime --locked python tools/build-release.py --version 0.3.22 --mod-source artifacts/bin/StardewAI.Companion.Mod/Release/net6.0 --desktop-source artifacts/desktop
dotnet test src/StardewAI.Companion.Mod.Tests
dotnet test src/StardewAI.Companion.Transport.Tests
Remove-Item Env:STAR_DOTNET_PATH -ErrorAction SilentlyContinue
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/build-mod.ps1 -Configuration Debug
uv run --project runtime --locked pytest -q
```

本机保留了基线绿色包，SHA256 为 `6469acd00def7ebe97e3d71dd3da40b6de59e4a2255eedfa098c0efa6d495a85`。重新计算的 ZIP 哈希与签出时生成的校验值一致，Python 校验了 Mod 清单中的 1775 个文件。该 SHA256 用于识别此处实测包，重新构建可能因 ZIP 文件时间戳产生不同哈希。

基线运行 731 个 Mod 测试、64 个传输测试。第一次 Python 检查的四个安装测试缺少 `artifacts/dist/` 输入，Release 构建后其中两个仍要求 PDB。补做 Debug 分发构建后，五项安装检查全部通过，其余基线结果为 1615 项运行测试通过及 2 项跳过。后续验证需区分基线准备问题和目录迁移回归。

本机使用同一隔离存档夹具和模拟 Pi，对比桌面启动、交权、自然语言下发与原生导航终态。测试夹具为本机单人隔离副本，验收不读取日常存档并且不把测试数据发布到绿色包。正式 mcode 客户端未在本机 PATH 或 npm 安装位置发现，三组真实模型用例需要可用的正式客户端。

## SGC-02 路径与契约

| 原路径或入口 | 目标位置或兼容路径 | 调用方与验证 |
| --- | --- | --- |
| `src/StardewAI.Companion.Desktop` | `apps/desktop/src` | `dotnet publish`、绿色包目录、WPF 的 `AppContext.BaseDirectory`。用完整 ZIP 在带空格的目录验证。 |
| `src/StardewAI.Companion.Mod`、Mod/Transport 测试 | `apps/game-mod/src`、`apps/game-mod/tests` | .NET 项目引用、直接链接的 Transport 源文件、Mod清单、编译产物仍由根目录的 `Directory.Build.props` 路由。 |
| `runtime` | `apps/runtime` | uv、pytest、CI、Python 子进程、MCP 注册和编译扩展。当前包内继续使用 `runtime/src/stardew_ai_runtime` 和 `python -m stardew_ai_runtime.*`。 |
| `protocol`、跨语言样例生成 | `contracts/game-bridge`、`tests/contracts` | JSON Schema、Python 消息生成、C# DTO、传输模拟。共享一份生成样本。 |
| 桌面 HTTP 路由 | `contracts/desktop-api` | 保留 `/v1`、本机鉴权、请求 ID、当前游戏会话校验与 SSE 游标。 |
| `docs/agent-*` 生产提示词、`agent-skills` | `resources/agent/prompts`、`resources/agent/skills` | 加载器和打包器使用同一逻辑清单。领域指导相对提示词索引存放，源码迁入 `prompts/agent-guidance`。包内保留原路径与会话指纹。 |
| 发包与校验脚本 | `packaging/windows` | 单文件桌面、内置 Python、清单与许可证。PowerShell 检查包内同一逻辑资源路径。 |
| 源码安装脚本、开发自检 | `tools/dev` | 原生启动仍查找配套包中的 `tools/start-companion.ps1`，隔离验收负责在自己的 Mod 副本放置该脚本。 |
| TestDriver、固定用例、隔离启动 | `tests/acceptance` | 隔离存档、只清理所属进程。原 `run-skill-orchestrator.py` 在基线仓库无实现，唯一运行时引用来自旧场景编排分支。固定实机任务从 runner 驱动，迁移时移除该死分支。 |
| 存档及供应商会话 | 已有包内位置 | 保留 Mod `UniqueID`、`config.json`、`data/desktop-settings.json`、`chat_sessions.json` 和已有按存档分区。用旧格式读取、断点续导与 Pi 原会话接续验证。 |

后续拆分时按[架构方案](architecture-refactor.md)核对既有行为。构建和模拟联调结果登记在[重构 TODO](architecture-refactor-todo.md)。
