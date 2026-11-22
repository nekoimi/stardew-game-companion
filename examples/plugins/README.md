# 通用参考插件

```powershell
pwsh -File examples/plugins/build.ps1 -Output artifacts/plugin-samples
```

构建需要 Go 和 .NET 10 SDK。将生成的 ZIP 分别从桌面「插件中心」安装并启用，运行时使用插件包内的 Windows x64 EXE。

- **指令示例（Go）**。打开插件设置，保存自然语言指令。进入存档并交给 Agent 控制后，点击「提交已保存指令」。使用「查询结果」查看状态和原生动作回执，网络中断后可用「重试原请求」沿用原编号。
- **事件订阅示例（C#）**。启用后自动订阅，点击「打开日志目录」查看 `events.jsonl`。每行是一个快照或公开事件。订阅连接中断时从最后游标继续，失效时重取快照。

脱离游戏和桌面验证时，将构建输出路径传给 `node examples/plugins/mock-host.mjs <构建输出目录>`。模拟宿主以 v1 共享样例核对握手、指令和事件订阅。

在交互式 Windows 会话中，可运行 `pwsh -File tests/desktop-plugins/accept-game.ps1 -Archive <绿色包ZIP> -Samples <参考插件构建目录> -SavePath <隔离测试源存档> -GamePath <SMAPI游戏目录>`。脚本复制测试存档并使用本地模拟模型，验收两个安装包、原生行动与事件反馈。

[插件宿主 v1 契约](../../contracts/plugin-host/v1.md)
