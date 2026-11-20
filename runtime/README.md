# 星露谷游戏伴侣 · Python 运行时

运行时负责模型会话、游戏工具、任务执行、按存档保存的工作记忆和用量统计。桌面启动运行时并通过本地 HTTP 交互，运行时通过本地通信连接 SMAPI Mod，由 Mod 在游戏中执行动作。

完整安装和使用说明见[仓库首页](../README.md)。在仓库根目录运行：

```powershell
uv sync --project runtime --locked
uv run --project runtime pytest runtime/tests
uv run --project runtime ruff check runtime
```

源码调试独立聊天服务和 MCP 时，参见[源码调试与 MCP 接入](../docs/mcp.md)。运行时随桌面绿色包发布，项目的 Apache-2.0 许可及原作者署名见 [LICENSE](../LICENSE) 和 [NOTICE](../NOTICE)。
