# 架构重构验收记录

[实施方案](architecture-refactor.md) · [重构 TODO](architecture-refactor-todo.md) · [基线与路径清单](architecture-refactor-baseline.md)

## 候选包

构建提交 `46d736c7d30a4e62491f7e570acfc9fffed7d3f9`，ZIP 为 `StardewGameCompanion-0.3.22-windows-x64.zip`，SHA256 为 `dd9961bc503bd0f5910e9d9dfea3e2fbc6f275b5dfb14613aebb05a68689586b`。独立 Windows 克隆构建 Release Mod 和自包含 WPF，解压后通过内置 Python 完整性检查，共校验 1797 个 Mod 文件。生产提示词、领域指导和技能与基线包逐文件比对一致。

环境为 Windows x64、Stardew Valley 1.6.15.24356、SMAPI 4.5.2、Python 3.12.11 和 uv 0.7.14。WPF 使用 .NET SDK 10.0.404，Mod 与传输测试使用系统 .NET 8 SDK，原生扩展的独立编译使用 .NET SDK 6.0.428。

### 构建和测试命令

在克隆根目录执行。`$gameDirectory` 为测试用游戏安装目录，`$desktopSdk` 和 `$nativeSdk` 分别指向两个独立 SDK 的 `dotnet.exe`。

```powershell
$env:STARDEW_GAME_PATH = $gameDirectory
uv sync --project apps/runtime --python 3.12 --locked
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/dev/build-mod.ps1 -Configuration Release
& $desktopSdk publish apps/desktop/src -c Release -o artifacts/desktop/win-x64-single-file
uv run --project apps/runtime --locked python packaging/windows/build-release.py --version 0.3.22 --desktop-source artifacts/desktop/win-x64-single-file
$env:STAR_DOTNET_PATH = $nativeSdk
dotnet test apps/game-mod/tests/StardewAI.Companion.Mod.Tests
dotnet test apps/game-mod/tests/StardewAI.Companion.Transport.Tests
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/acceptance/build-testdriver.ps1 -Configuration Debug
uv run --project apps/runtime --locked ruff check apps/runtime tests/contracts
uv run --project apps/runtime --locked python packaging/windows/check_release_boundaries.py
$env:DOTNET_ROOT = Split-Path -Parent $nativeSdk
uv run --project apps/runtime --locked pytest -o addopts= -q --disable-warnings -r s
```

Mod 731 项、传输 64 项通过，TestDriver 构建成功。Python 全量测试在相同源码的独立克隆通过 1625 项，当时真实编译器用例跳过两项。在候选克隆设置 `DOTNET_ROOT` 后，两项真实 SDK 用例补跑通过。

### 联调与兼容

| 检查 | 实际结果 |
| --- | --- |
| 绿色包启动与结果展示 | 从带空格的新解压目录启动，隔离存档加载后交权，模拟 Pi 用现有工具触发原生移动。等待时显示时钟暂停，完成后解除暂停，桌面显示成功回执。 |
| 接管与重连 | 玩家接管后终止包所属 Python 进程，游戏继续运行。恢复前桌面阻止派工，核对后保持玩家控制，显式交权后第二次任务沿用同一个游戏进程、游戏会话与 Pi 会话。 |
| 改派和回执超时 | 桌面回归覆盖先等待旧动作终态再提交新指令，原生结果未知时保持阻塞。传输与调度回归覆盖查询并恢复已有终态，缺失回执保持未知且不重发。 |
| 导入与回退 | 候选包从已校验的基线包导入 8 个伴侣数据文件和 1 个 Pi 会话。首次指令从新目录接续本地会话文件。返回旧包后再次启动并接续原会话，旧包设置、记忆和隔离存档保留。自动化覆盖旧 Mod 数据、配置冲突和中断后重试。 |

## 清理后复验

干净克隆提交 `8c924c468211fa2a9072597fb2558c56c2664bc1`。Windows ZIP SHA256 为 `05c3adfe732d2cafebb85f6300d09020bcbad5c02f8259a3f44a313df3d8ff51`。Python 与 PowerShell 完整校验通过，共 1796 个 Mod 文件。根目录包含启动器、使用说明、`Mods/` 和 `docs/`，检查通过的发行边界涵盖许可、TestDriver 和本地玩家数据。包内实际加载的提示词指纹 `cb514d1354f6fc10` 与基线一致。

按上方命令验证最终构建提交，Python 共 1628 项通过。全量运行通过 1626 项，两项安装检查在隔离游戏运行期间按设计拒绝安装，游戏退出后以 `pytest --lf` 补跑通过。Mod 731 项、传输 64 项通过，TestDriver 成功构建。Windows 上程序集枚举测试曾在清理临时 DLL 时失败，已将相关测试组串行化并等待卸载，随后完整 C# 测试通过。

以校验通过的旧包和本次复验包重复隔离联调。桌面重连保留同一游戏进程、存档与 Pi 会话。候选包再次导入 8 个数据文件和 1 个 Pi 会话，旧包回退启动后保留设置、记忆、存档和原会话。游戏内调试入口使用隔离 Mod 副本和模拟 Pi 自动启动服务，接收一条自然语言指令并取得原生移动成功回执。

## 正式伙伴实机回归

已在隔离工具目录安装官方 `@minimax-ai/code@0.6.2`。用最终 ZIP 的独立解压副本配置 mcode，`start-companion.ps1 -CheckOnly` 通过完整性、内置 Python 和正式客户端启动检查。TestDriver 构建通过，验收用单人存档为第一年春季第 5 天。正式 CLI 执行仍提示 `Sign in to MiniMax to use Agent features`，三组均待登录后执行。 [固定实机用例](repeatable-companion-acceptance.md)

| 场景 | 结果 |
| --- | --- |
| `care-guidance` | 待正式 mcode 登录。 |
| `harvest-logistics` | 待正式 mcode 登录。 |
| `rest-forestry` | 待正式 mcode 登录。 |

登录后，在可调用 `mcode` 的 PowerShell 中运行下列命令。`$releaseDir` 指当前验证版本的新解压目录，`$isolatedSave` 指准备好的单人春季验收存档。

```powershell
& tests/acceptance/run-companion-presets.ps1 -ReleaseDir $releaseDir -SavePath $isolatedSave
```
