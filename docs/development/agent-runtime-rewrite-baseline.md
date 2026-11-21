# TypeScript 改造基线

实施前的源码为 `f59bd41`，分支为 `dev`。旧绿色包保存在本地 `artifacts/releases/gui-single-player-cn/StardewGameCompanion-0.3.22-windows-x64.zip`，SHA256 为 `ecc9d8e2ee1503532c69cd47b56ca85043e9f133a3ef3005fefc1dcf0c8ff009`。已校验包内 1793 个 Mod 文件。使用隔离存档和模拟 Pi 复跑绿色包，原生移动在隐藏窗口时完成，恢复后显示回执，退出伴侣后游戏仍运行。

本机基线为 Windows x64、游戏 1.6.15.24356、SMAPI 4.5.2、WPF SDK 10.0.404、.NET SDK 8.0.428 和 uv 0.7.14。隔离验收沿用[三组固定场景](../repeatable-companion-acceptance.md)，基线的桌面联调使用本地隔离存档和模拟进程。正式厂商 API 与三组实机结果仍待后续阶段。

| 当前调用点 | 替换或保留 |
| --- | --- |
| `apps/desktop/src/DesktopProcesses.cs`、`DesktopInstallation.cs` | Python 子进程和旧导入入口移除。桌面模型设置、托盘、状态、退出边界迁到新宿主接口。 |
| `apps/runtime/src/stardew_ai_runtime/application/turns.py`、`chat_bridge.py`、`game_tools.py` | 回合、观察、八工具、游戏规则和控制边界迁到 TS。第三方客户端启动、会话绑定及 Python 运行时退场。 |
| `apps/game-mod/src/Transport/`、`ModEntry.Desktop.cs`、`ModEntry.SaveLifecycle.cs` | 保留游戏主线程观察与动作、收回控制权、原生回执与存档读写。新版信封和桌面宿主分阶段接线。 |
| `resources/agent/` | 保留核心提示词、领域指导和技能，切换时改加载器与厂商会话说明。 |
| `tests/acceptance/run-companion-presets.ps1`、`runner.py`、`cases.json` | 保留三组自然语言和证据判定，换成 TS 运行时入口。独立判定脚本可继续作为开发工具。 |
| `packaging/windows/build-release.py`、`tools/dev/` | 发布改为随包 Node，移除生产 Python 与客户端安装、MCP 注册和会话适配的旧调用点。 |

阶段 0 使用 `apps/runtime-ts/` 建立新工程，用[桌面 API v2](../../contracts/desktop-api/v2.md)和[游戏传输 v2](../../contracts/game-bridge/v2.md)约定边界。旧包仍运行旧协议。C# 与 TS 共用同一份动作生命周期样例。

```powershell
npm --prefix apps/runtime-ts ci --ignore-scripts
npm --prefix apps/runtime-ts test
dotnet test apps/game-mod/tests/StardewAI.Companion.Transport.Tests
pwsh -NoProfile -File packaging/windows/stage-node-runtime.ps1 -Output artifacts/node-runtime-stage-0
```

随包环境采用 Windows x64 Node 22.22.2、npm 10.9.7、`pi-ai` 1.6.0。SQLite 由 Node 提供，锁定的版本在发布环境验证。Node ZIP 通过固定 SHA256 校验，npm 依赖按锁文件安装，编译和生产依赖分目录。运行目录附带 Node 许可、npm 依赖许可清单及各依赖自带的许可证。
