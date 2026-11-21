# 阶段 3 · API 绿色候选与实机回归入口

候选 ZIP 由干净源码快照构建。包根目录提供「星露谷游戏伴侣.exe」，配套 Mod 在 `Mods/`，Node、模型 SDK 和运行时在 `runtime/`，说明与许可在 `docs/`。构建用 .NET 10 SDK、校验后的 Node 22.22.2 和 npm 锁文件。归档校验逐项核对 11,439 个文件及 SHA256。

- 归档 `StardewGameCompanion-0.3.22-api-preview-windows-x64.zip`
- ZIP SHA256 `63fad08f4ad02fdbd504ae6afa55c58b89e09129ecfd5fced1cba58b18c639da`
- 游戏与 SMAPI 测试版本 1.6.15.24356 和 4.5.2

从同一个阶段 3 提交在两个独立克隆目录重建，两个 ZIP 均通过校验，完整文件清单 SHA256 一致。此前暂存源码快照与干净克隆的 EXE 和 Mod DLL 使用了不同的 Git 版本标识，造成两份文件哈希差异。上述 ZIP SHA256 对应实际跑过实机的候选包。

## 自动化与桌面

- TypeScript 测试 17 项、C# 传输测试 65 项和 Python 实机报告判定测试 5 项通过。混合掉落物夹具为原生位置读取初始化了游戏世界状态，C# Mod 的 Release 全量测试 731 项通过。原生扩展编译使用 `STAR_DOTNET_PATH` 指向隔离 .NET 6 SDK。
- 解压路径包含中文和空格。桌面与子进程 PATH 仅保留 Windows 系统路径，连接模拟 DeepSeek、执行原生移动、思考中和移动中按 F9 接管均通过。切到模拟 OpenAI 后，实测角色、记忆、历史、未完成目标和用量均保留。
- 待确认事项显示在「今天」。选择「留种」后立即写入 SQLite，界面上的待确认项关闭。
- 「打断并执行」停止旧的流式回合，改派的原生移动收到成功回执。退出到标题并重新加载同一隔离存档后，运行时使用新的游戏会话，保留旧目标，控制权由玩家持有。模拟回合测试覆盖明确交权后恢复原目标。
- 停止测试所属的 Node 后，桌面能重连同一游戏。托盘双击可重新打开窗口，退出桌面会清理自身运行时，已打开的游戏可继续使用。环境为现有 Windows 主机，测试以 PATH 隔离外部客户端。

## 模拟 API 实机回归

三组使用同一个隔离游戏、存档和模拟 DeepSeek 流。判定以原生状态、动作回执、动画帧和碰撞采样为准。隔离实例退出后，源存档和日常 Mod 的校验通过。

| 场景 | 耗时 | 已核对 |
| --- | --- | --- |
| `care-guidance` | 45.3 秒 | 补水，浇完作物，动作进行期间收到的新要求在下一工具边界生效。 |
| `harvest-logistics` | 23.4 秒 | 成熟作物收净，木材和作物入箱，之后提取作物出货，木材保留。 |
| `rest-forestry` | 94.9 秒 | 原生床恢复至 80 体力，两棵成熟树采伐并将木材入箱，幼树保留。 |

## 复现命令

```powershell
pwsh -File packaging/windows/build-node-release.ps1 -Output artifacts/api-preview
pwsh -File packaging/windows/verify-node-release.ps1 -Archive <候选ZIP路径>
pwsh -File tests/acceptance/run-companion-presets.ps1 -ReleaseDir <解压目录> -SavePath <春季隔离来源存档> -Simulated
```

实机执行由已打包的运行时和 Mod 完成，TestDriver 只布置存档、发送自然语言和采集原生结果。模拟报告单独标记，正式运行从当前进程读取模型厂商凭据。正式 DeepSeek、OpenAI API 与三组固定场景的正式验收待补，Python 生产链路随最终切换任务处理。[三组固定场景和正式入口](../repeatable-companion-acceptance.md)
