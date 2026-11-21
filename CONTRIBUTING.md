# 参与贡献

请在问题或 Pull Request 中附上复现步骤、涉及的玩法、版本和验证结果。分享诊断前移除 Key、私人路径和存档信息。

## 从源码构建

准备 PowerShell 7、.NET 10 SDK、已安装 SMAPI 的 Windows x64 游戏。使用新输出目录，构建脚本按锁文件准备随包 Node 和模型 SDK。

```powershell
pwsh -File packaging/windows/build-node-release.ps1 -GamePath '<SMAPI 游戏目录>' -Output artifacts/windows-x64
pwsh -File packaging/windows/verify-node-release.ps1 -Archive '<ZIP 路径>'
```

解压 ZIP 后从根目录的「星露谷游戏伴侣.exe」启动。归档包含配套 Mod、Node、许可清单和 SHA256 文件清单。

## 开发与测试

- apps/desktop/src 负责 WPF、Windows 凭据和运行时宿主。
- apps/runtime 负责 TypeScript 回合、模型工具、SQLite 和两侧接口。
- apps/game-mod/src 负责 SMAPI、游戏观察、主线程原生动作和回执。

准备 Node 22.22.2、npm 10.9.7、可编译 Mod 的 .NET SDK，以及安装了 pytest 的开发用 Python。在仓库根目录运行 `pwsh -File tools/dev/test.ps1 -GamePath '<SMAPI 游戏目录>'`。原生扩展编译用例可通过 `-DotNet` 指向本地 .NET 6 SDK。Python 用于隔离验收报告判定。

实际游戏测试使用复制出的隔离存档。桌面交互和三组固定场景的复现见[隔离实机回归](docs/repeatable-companion-acceptance.md)。测试用存档、截图、游戏文件和模型密钥留在本地。

调试游戏内 F8 界面时，先运行 `tests/acceptance/build-testdriver.ps1`，再通过 `tests/acceptance/launch-game.ps1 -Mode interactive -Scenario free-mode-control -FrozenProdDir '<解压目录>' -CustomSavePath '<测试存档目录>' -ApiProvider deepseek -ApiModel deepseek-flash -TimeoutSeconds 1800` 启动隔离游戏。DeepSeek 密钥从当前进程的 `DEEPSEEK_API_KEY` 读取，打开游戏后从标题菜单读档。
