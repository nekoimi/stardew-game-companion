# 参与开发

欢迎提交修复和验证结果。星露谷游戏伴侣在 `nekoimi/stardew-game-companion` 独立开发和发布，游戏版本基线为 Stardew Valley 1.6.15、SMAPI 4.2.1。[Pull Requests](https://github.com/nekoimi/stardew-game-companion/pulls)

## 从源码构建

需要游戏与 SMAPI、Python 3.11～3.13、uv，以及编译 Mod 和桌面程序所需的 .NET SDK（桌面需要 .NET 10）。在克隆的仓库根目录打开 PowerShell：

```powershell
Copy-Item .env.example .env.local
uv sync --project apps/runtime --locked
```

编辑 `.env.local` 填写游戏与 SMAPI 路径，再构建 Mod 和桌面绿色包。将示例版本号改为 Mod 清单中的版本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dev/build-mod.ps1 -Configuration Release
dotnet publish apps/desktop/src -c Release -o artifacts/desktop/win-x64-single-file
uv run --project apps/runtime python packaging/windows/build-release.py --version 0.3.22 --desktop-source artifacts/desktop/win-x64-single-file
```

桌面项目默认发布为自包含单文件。请使用新的发布目录，避免上次构建的散装 DLL 混在里面。解压后运行 `星露谷游戏伴侣.exe`。需要调试 Mod 内的原生界面或手动 MCP 接入时，源码开发脚本可同步 DLL 与本地配对清单，见[接入指南](docs/mcp.md#源码开发接入)。

## 代码结构

- `apps/desktop/src`：WPF 桌面界面、游戏和运行时启动、设置与存档数据导入。
- `apps/game-mod/src`：游戏状态观察、原生寻路与动作执行、控制权和本地通信。
- `apps/runtime/src/stardew_ai_runtime`：MCP 工具、模型后端、计划执行、工作记忆与用量统计。
- `contracts/`：桌面 HTTP、游戏 WebSocket 契约和样例。
- `resources/agent/`：提示词、领域指导和技能。
- `packaging/windows/`：绿色包生成和发包校验。
- `apps/game-mod/tests/`、`apps/runtime/tests/`、`tests/`：组件测试、跨语言契约与隔离验收。

游戏对象的读写在游戏主线程执行。模型发出任务意图，具体动作由 Mod 执行；修改动作逻辑时需要同时检查前置条件、物品消耗和最终结果。

伙伴与自玩共用运行时，通过桌面选择模式。Mod 中的 `PlayerMode` 负责接管逻辑，原生设置入口供源码调试和隔离验收使用。项目约定见 `AGENTS.md`。

下一阶段的目标目录、迁移边界和任务顺序见[架构重构实施方案](docs/development/architecture-refactor.md)与[重构 TODO](docs/development/architecture-refactor-todo.md)。本页构建命令对应当前源码。

## 发布

维护 `CHANGELOG.md`，同步 Mod 清单和项目文件的版本，再构建 Windows ZIP 与 SHA256。验证新解压包、配套文件校验和旧版本迁移，发布时将两份构建文件上传到本项目 Releases。[更新与迁移](docs/desktop-portable.md#更新与迁移)

绿色包携带项目的 `LICENSE`、`NOTICE`、.NET 与 Python 的许可声明以及依赖清单。Mod 的 SMAPI `UniqueID` 沿用旧值，供桌面识别可迁移的安装和既有存档。

## 验证改动

在仓库根目录运行：

```powershell
uv sync --project apps/runtime --locked
uv run --project apps/runtime ruff check runtime
uv run --project apps/runtime pytest apps/runtime/tests tests
uv run --project apps/runtime python packaging/windows/check_release_boundaries.py
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dev/build-mod.ps1 -Configuration Release
```

C# 测试需要本机游戏程序集和 .NET SDK。可以使用 `dotnet test` 分别运行 `src` 下的测试项目，并通过 `StardewGamePath` 构建参数指定游戏目录。验证原生扩展编译时，将 `STAR_DOTNET_PATH` 指向可运行 .NET 6 SDK 的 `dotnet.exe`，并在环境中设置 `STARDEW_GAME_PATH`。

CI 检查 Python、协议和生产边界。C# 构建与依赖游戏的测试在本地运行，提交时注明已验证与尚未验证的范围。

离线测试不应调用付费模型服务。涉及游戏动作的验证请使用备份存档，记录实际消耗与结果，不要仅凭模型回复判断成功。

## 验证其他后端

伙伴的行为质量取决于所选模型。欢迎为后端补充决策评测、会话接续或实机验收。[后端验证范围](docs/tested-backends.md)

## 提交问题和修改

通过 Pull Request 提交修复或文档补充时，请附游戏、SMAPI 和模型客户端版本、复现步骤、预期结果及实际结果。日志应去掉账号、令牌、私人路径和无关聊天内容。

提交修改时说明问题、验证方式和已知限制。贡献代码按项目的 Apache-2.0 许可提供，保留已有署名和第三方许可。游戏程序集、存档、模型凭据和本地运行产物留在本机。[LICENSE](LICENSE) · [NOTICE](NOTICE)
