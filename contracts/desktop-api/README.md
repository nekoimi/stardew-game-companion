# 桌面 HTTP 接口 v1

WPF 启动 Python 后从子进程标准输出读取本次端口。服务监听 `127.0.0.1`，每次启动生成凭据，每个请求携带 `Authorization: Bearer <token>`。POST 使用 JSON 对象，最大 8192 字节。除伙伴接续和用量查询之外，POST 请求绑定当前 `gameSessionId`。

| 方法与路由 | 行为 |
| --- | --- |
| GET `/v1/status` | 读取连接、当前存档、控制权和工作状态。 |
| GET `/v1/events` | SSE 输出 `type` 与 `payload`。状态变化单独发送，事件按编号投递，客户端通过 `Last-Event-ID` 继续读取缓冲区。 |
| GET `/v1/commands/{request_id}` | 按请求编号读取接收或执行结果。找不到编号时返回 404。 |
| POST `/v1/commands` | 接收 `requestId`、`text`、`mode`（`send` / `interrupt`）和可选 `decisionId`。新请求返回 202，已存在的请求返回 200 及已有记录。 |
| POST `/v1/control` | 暂停、继续、取消、接管、交权或核对；取消可带 `goalId`。 |
| POST `/v1/history`、`/v1/messages` | 读取对话与伙伴消息；可传 `before` 翻页，消息可传 `readId` 标为已读。 |
| POST `/v1/notices` | 关闭或恢复指定的待确认事项。 |
| POST `/v1/profile`、`/v1/memory` | 读取或修改设置与记忆，修改时包含 `expectedRevision`。 |
| POST `/v1/initiative`、`/v1/milestones` | 保存主动帮忙设置、采纳或暂缓计划。计划操作绑定当前记录的 `updatedAt`。 |
| POST `/v1/partner`、`/v1/usage` | 管理原伙伴接入，读取全局或当前存档用量。 |

鉴权失败返回 401，格式错误返回 400，请求过长返回 413。存档已切换、权限冲突或请求暂不可执行返回 409。游戏连接中断或等待超时返回 503，结果仍待原生回执确认。HTTP 接收成功与游戏动作成功分别以请求状态和原生回执判断。

Python 路由实现与桌面联调测试分别位于 [运行时源码](../../apps/runtime/src/stardew_ai_runtime/desktop_api.py)和[桌面服务测试](../../apps/runtime/tests/test_desktop_service.py)。游戏端消息见 [WebSocket 契约](../game-bridge/README.md)。
