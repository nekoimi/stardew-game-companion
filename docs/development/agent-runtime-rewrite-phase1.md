# TypeScript 预览包验证

阶段 1 的 Windows 样机从 `packaging/windows/build-node-preview.ps1` 构建，输入为当前 Release WPF 和 SMAPI Mod。产物根目录包含启动器、Node 运行时、Mod、许可证与 SHA256 文件清单。自检使用随包 Node 22.22.2，运行时载入 Pi 模型 SDK 1.6.0 和 SQLite 3.51.2。

```powershell
npm --prefix apps/runtime-ts ci --ignore-scripts
npm --prefix apps/runtime-ts test
dotnet test apps/game-mod/tests/StardewAI.Companion.Transport.Tests
pwsh -NoProfile -File packaging/windows/build-node-preview.ps1 -Output artifacts/node-preview -DesktopSource <WPF发布目录> -ModSource <Mod构建目录>
```

在 Windows x64 上复制绿色目录到独立路径，使用隔离游戏与存档，并让测试模型服务按 DeepSeek SSE 协议提供文本、分片工具参数和用量。启动进程的 PATH 只包含 Windows 系统目录。模型连接测试、Windows 凭据读取、WPF → TS → Mod 原生移动和桌面回执通过。游戏中按 F9 分别取消模型思考和正在走路的原生动作，控制权回到玩家。

TypeScript 测试覆盖模型鉴权、限流、断流、缺失用量、迟到的模型调用、改派等待原生终态和遗失回执的核对。使用模拟 API 的结果与正式 API 分开记录。DeepSeek 有效生产凭据的验证仍待补跑。
