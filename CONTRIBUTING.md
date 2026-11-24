# 参与贡献

请在问题或 Pull Request 中附上复现步骤、涉及的玩法、版本和验证结果。分享诊断前移除 Key、私人路径和存档信息。

## 从源码构建

准备 PowerShell 7、.NET 10 SDK、已安装 SMAPI 的 Windows x64 游戏。使用新输出目录，构建脚本按锁文件准备随包 Node 和模型 SDK。

产品版本统一写在仓库根目录的 `VERSION`。当前开发版本为 `0.1.0`。Mod 清单、.NET 程序集和绿色包文件名由构建流程读取同一个版本源，协议版本和存档数据版本单独维护。

```powershell
pwsh -File packaging/windows/build-node-release.ps1 -GamePath '<SMAPI 游戏目录>' -Output artifacts/windows-x64
pwsh -File packaging/windows/verify-node-release.ps1 -Archive '<ZIP 路径>'
```

解压 ZIP 后从根目录的「星露谷游戏伴侣.exe」启动。归档包含配套 Mod、Node、许可清单和 SHA256 文件清单。

## 发布 Windows 绿色包

发布使用 GitHub 托管的 Windows runner。首次发布时，在已安装游戏和 SMAPI 的 Windows 电脑上用 .NET 10 SDK 导出仅含编译元数据的引用包。

```powershell
dotnet tool install --global JetBrains.Refasmer.CliTool --version 2.0.3
pwsh -File packaging/windows/export-build-references.ps1 -GamePath '<SMAPI 游戏目录>' -Output artifacts/stardew-build-references.zip
```

将引用包放在私有存储中，在 GitHub 的「Settings → Secrets and variables → Actions」添加 Secret `STARDEW_BUILD_REFS_URL`，值为 runner 可访问的 HTTPS 下载链接，并添加 Variable `STARDEW_BUILD_REFS_SHA256`，值为导出脚本输出的哈希。更新游戏或 SMAPI 编译目标时重新导出并更新配置。

发布工作流合并到默认分支后，在「Actions → Release Windows → Run workflow」选择要构建的 Git 引用。`tag` 留空时生成 `v<VERSION>-dev.<北京时间日期>.<运行编号>.<重跑次数>`，例如 `v0.1.0-dev.20261123.42.1`，并标记为预发布；每次新运行或重跑构建都会生成不同标签。手填时使用指定标签，可以新建或复用指向同一提交的标签，已有 Release 保持原样。

Release 展示名称直接使用标签，例如 `v0.1.0-dev.20261123.2.1`。下载包名为 `StardewGameCompanion-<VERSION>-windows-x64.zip`，附带 `.sha256`。包名、程序集和 Mod 版本统一读取 `VERSION`，预发布构建由所属 Release 的标签区分。[更新器实现与验收](docs/development/portable-update-design.md)

## 开发与测试

文档站独立维护在 `docs/site/`，进入该目录后运行 `npm ci`、`npm run dev` 即可预览；`npm run build` 检查正式构建与链接。GitHub Pages 的首次设置和文档来源规则见[文档站维护说明](docs/site/README.md)。

- apps/desktop/src 负责 WPF、Windows 凭据和运行时宿主。
- apps/runtime 负责 TypeScript 回合、模型工具、SQLite 和两侧接口。
- apps/game-mod/src 负责 SMAPI、游戏观察、主线程原生动作和回执。

准备 Node 22.22.2、npm 10.9.7、可编译 Mod 的 .NET SDK，以及安装了 pytest 的开发用 Python。在仓库根目录运行 `pwsh -File tools/dev/test.ps1 -GamePath '<SMAPI 游戏目录>'`。原生扩展编译用例可通过 `-DotNet` 指向本地 .NET 6 SDK。Python 用于隔离验收报告判定。

插件宿主可脱离游戏验证。准备 Go 和 .NET 10 SDK，运行 `pwsh -File tests/desktop-plugins/run.ps1`。使用 `python -m pip install -r tests/requirements.txt` 安装验收依赖，再运行 `python tests/contracts/check-plugin-contract.py` 检查清单 Schema。

运行 `dotnet run --project tests/desktop-updates` 验证更新器、SHA256 校验、数据导入和模拟 GitHub Release。

实际游戏测试使用复制出的隔离存档。桌面交互和三组固定场景的复现见[隔离实机回归](https://nekoimi.github.io/stardew-game-companion/reference/acceptance)。测试用存档、截图、游戏文件和模型密钥留在本地。

调试游戏内 F8 界面时，先运行 `tests/acceptance/build-testdriver.ps1`，再通过 `tests/acceptance/launch-game.ps1 -Mode interactive -Scenario free-mode-control -FrozenProdDir '<解压目录>' -CustomSavePath '<测试存档目录>' -ApiProvider deepseek -ApiModel deepseek-flash -TimeoutSeconds 1800` 启动隔离游戏。DeepSeek 密钥从当前进程的 `DEEPSEEK_API_KEY` 读取，打开游戏后从标题菜单读档。
