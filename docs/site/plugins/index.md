# 插件接入概览

插件是伴侣启动和管理的独立 Windows x64 进程，可以使用 Go、C# 或其他能提供对应 EXE 的语言实现。插件自己的账号、业务规则和配置页面由插件维护。

## 最小接入流程

1. 按[宿主协议 v1](/reference/plugin-protocol)准备 `plugin.json`、入口 EXE 和 `files.json`，打包为 ZIP。
2. 读取宿主启动时提供的 URL、实例 Token、数据目录与日志目录。
3. 发送 `POST /v1/hello` 协商协议，定期发送心跳。
4. 获取公开状态快照、订阅 SSE 事件，或提交自然语言指令并查询结果。
5. 收到停止指示后结束业务并退出。

支持设置页的插件自己提供回环 HTTP(S) 页面，在握手时登记 `settingsUrl`。宿主负责打开页面，页面内容由插件实现。

## 与 Agent 交互

通过 `POST /v1/commands` 提交自然语言要求，按原请求编号查询或幂等重试。遇到忙碌、暂停或玩家接管等状态，按照协议等待与重试；不能绕过玩家控制权。

插件可以订阅游戏状态、Agent 状态、动作结果和伙伴回复。断线后按游标续读，游标失效或会话变化时重新获取快照。

## 参考实现与发布

仓库提供 Go 指令示例和 C# 事件订阅示例，包含模拟宿主与绿色包验收说明。

- [构建和运行参考插件](/reference/plugin-examples)
- [宿主接口、生命周期与错误码](/reference/plugin-protocol)
- [在线目录、签名、更新与回退](/reference/plugin-catalog)
- [清单 Schema](https://github.com/nekoimi/stardew-game-companion/tree/dev/contracts/plugin-host/schemas)

桌面宿主只处理通用生命周期、消息、事件与 Agent 指令，插件不需要把自己的业务逻辑加入主项目。
