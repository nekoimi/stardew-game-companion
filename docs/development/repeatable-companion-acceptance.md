# 伙伴实机回归

“伙伴实机回归”使用隔离存档，由配置的模型 API 接收自然语言请求，依据原生状态和工具回执验收。模拟服务也可运行同一套场景，报告标注「模拟 API」。

默认固定运行三组，起点、请求和判定均版本化保存在 `tests/acceptance/cases.json`。固定的是目标与验收，不规定模型的坐标、工具顺序或推理路径。

批次工作验收可加 `-Cases water-refill,care-guidance,harvest-logistics,rest-forestry,empty-farm`。模拟模型请求阶段工作流，`work-stage` trace 记录每阶段的子动作数、成果与阶段内模型请求数；空农田必须确认三类工作均无目标、原生动作调用为零。阶段效率对比和正式 API 限制见[批次工作验收报告](agent-batched-work-acceptance.md)。

| 场景 | 固定行动 | 主要验收 |
| --- | --- | --- |
| care-guidance | 浇水、补水；动作中追加照料后的询问 | 作物浇完，新要求在工具边界送达 |
| harvest-logistics | 收获、拾取、入箱；再取出作物出货 | 中途箱内数量正确，最终出货数量正确、木材保留 |
| rest-forestry | 休息至 80 体力、采两棵成熟树、拾取入箱 | 真实上床恢复、幼树保留、农场箱内木材增多且伙伴背包无剩余木材 |

所有场景共用一次移动动画、碰撞、终态检查，并记录耗时、工具错误、聊天数量。不会替模型执行、重发失败任务或凭模型自述判通过。整套只启动一次隔离游戏，三组连续使用同一存档和模型会话；仅在动作与回合结束后重置测试布置和资源；不含公开分发的游戏资产。

回合结束后收起菜单再采样，不替玩家回答选项；拾取任务额外清理无关杂物属于范围偏离，应在报告中指出。

涉及掉落物时，旁观玩家停在远离任务区的农场可通行位置，并核对实际落点，避免抢先吸取物品或被农舍入口重定位后堵门。

运行 `tests/acceptance/run-companion-presets.ps1 -ReleaseDir <新解压的绿色包目录> -SavePath <春季测试存档目录>`。脚本先按发行清单校验 API 绿色包的全部文件，再复制 Mod 和存档，在隔离实例使用随包运行时。默认执行三组，也可用 `-Cases care-guidance` 单独复测。

正式 API 验收从当前进程读取 `DEEPSEEK_API_KEY` 或 `OPENAI_API_KEY`。使用 `-Provider openai -Model gpt-5-mini` 可选择 OpenAI。为任一服务商加 `-Simulated` 可用其本地流式协议跑相同的固定场景。已有游戏运行时拒绝启动。每组验收预算由 `-TimeoutSeconds` 设置，默认 600 秒。

第三方网关使用 `-Provider custom -Protocol responses -Model <网关模型 ID>` 或 `-Provider custom -Protocol chat-completions -Model <网关模型 ID>`。正式验收从当前进程读取 `OPENAI_COMPATIBLE_BASE_URL` 和 `OPENAI_COMPATIBLE_API_KEY`。各入口默认流式，加 `-NonStreaming` 改用完整响应。模拟网关加 `-Simulated`。

每组证据在 `.test-runs/presets-*-game/evidence/<场景名>/`，总结果在该运行目录的 `summary.md`。启动游戏前测试模型工具调用及回执后的确认文本。报告记录服务商、模型、协议、流式开关、游戏和 SMAPI 版本、包清单哈希、自然语言指令、状态采样和失败原因。画面观感、选项跨天、读档与执行中退出需单独核对。

## 桌面与同一存档切换

```powershell
pwsh -File tests/acceptance/start-desktop-acceptance.ps1 -ReleaseDir '<解压目录>' -SavePath '<测试存档目录>' -GamePath '<SMAPI 游戏目录>'
```

脚本将绿色包和存档复制到 `.test-runs/desktop-*`，用该副本的伴侣启动器打开桌面。在桌面「设置」分别填写两家凭据，点击「测试连接」，从桌面启动游戏并在游戏菜单加载复制出的存档。加 `-Simulated` 时填写 `accept-usage` 和 `accept-openai`，桌面使用本地模拟服务。

1. 用 DeepSeek 执行自然语言移动指令，核对原生位置和桌面动作回执。分别在模型思考和移动时按 F9，核对控制权回到玩家。交权后测「打断并执行」，核对旧动作终态和新指令结果。
2. 写入伙伴名、手记并留下未完成目标。请求伙伴给出一项选择，提交后检查待确认项关闭。模拟 DeepSeek 可用「等待接管」「走到出口附近」「记住今日目标」和「请向我确认是否留种」触发对应动作。
3. 在同一存档应用 OpenAI 模型并询问上一轮约定，核对伙伴名、手记、历史、目标以及两家用量仍可读取。正式 API 同时核对模型的回答。
4. 让隔离桌面的运行时断线，从桌面重连并核对旧动作。退出到标题并重载同一存档，检查会话更新、控制权由玩家持有、目标仍在。

在运行目录的 `evidence` 留存包版本、进程与回执证据，并记录各项通过、失败或未执行。自动事件记录在 `agent-runtime-events.jsonl`，用量与角色数据保存在副本的 `desktop/data/companion.sqlite`。结束时退出隔离游戏和桌面。

### 自动桌面控制回归

同一入口增加 `-Simulated -ControlRegression -DesktopDotNet <.NET 10 dotnet.exe>`，会在隔离副本加载桌面测试宿主，自动启动和加载游戏，检查思考／移动中 F9 接管、打断改派、所属运行时断线后核对旧动作、原生就寝和次日目标接续。报告为 `evidence/desktop-controls.json`；模拟模型只按固定目标发出工具请求，游戏状态和结果仍来自正式 Mod，不接触日常存档。

测试宿主是源码验收入口，不进入玩家绿色包。正常启动仍使用根目录「星露谷游戏伴侣.exe」。该验收会在副本保存并退出游戏，只清理本次测试所属进程；启动前须退出已有游戏。
