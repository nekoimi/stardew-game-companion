# 完整阶段规划契约

状态：阶段 A～F 已实现并验收，完整日常与远树采木已通过正式模型验收。原生全图分页观察与缓存、`submit_work.execute_phase`、稳定前缀和 GUI 反馈均已接入；正式协议限制见 `docs/development/agent-phase-planning-acceptance.md`。

## 观察身份

`PhaseObservation` 由运行时创建，包含 `ref`、`binding`、`revision`、`complete`、`scopeRef`、`destinations` 和 `facts`。`binding` 固定存档、游戏会话、角色模式、目标、原要求、最新要求版本、日期和地图；模型只能引用不能填写身份。完整性为假时不得按全图目标派工，陈旧引用必须重新准备观察。

后续原生分页查询需返回实际地图尺寸、当前页范围、下一页、观察版本和完整性。未加载、未知、截断和失败不能转为空集合。地图、水源和设施缓存只提供候选，原生动作使用前核对真实状态。

阶段 B 的原生查询为 `inspect-phase-map`，参数 `locationId`、`scanOffset`、`scanLimit`（0～512）；0 只查询地图头，其他值逐页读取。结果 `details.page` 包含宽高、offset/end/nextOffset（-1 表示结束）、fingerprint、capturedRevision、trees、waterTiles、fixtures、scannedTiles 和 complete。单页完成标记不代替整图完整性，运行时验证全部页覆盖后才视为完整。共享样例见 `examples/phase-map-page.json`。

原生事件及内容失效更新指纹，读档重建会话盐；数据库缓存按存档、模式和地图隔离。命中前查询权威地图头，指纹不同重扫；水源执行失败还会主动刷新并尝试其他岸边。玩家模式首版仅允许 Farm 全图业务观察，其他地图返回不可用，避免绕过可见区域约束。

## 计划字段

TypeScript 正本为 `apps/runtime/src/application/phase-plan.ts` 的 `phasePlanSchema` 与 `bindPhasePlan`。保持八个模型工具，`execute_phase` 已接入 `submit_work` 的动作联合。

| 字段 | 约束 |
| --- | --- |
| `observation_ref`、`scope_ref` | 当前有效观察与范围引用 |
| `objective` | 可展示的本阶段成果说明，不能授予额外权限 |
| `completion.metric` | 入箱：`new-wood-stored`、`wood-stock-at-least`、`trees-stored`、`daily-care-stored`；留包：`new-wood-collected`、`trees-collected`、`daily-care-completed`；授权作物出货：`daily-care-shipped` |
| `completion.quantity` | 正整数；木材为件数、树木为棵数；日常指标使用 1。随机产量不得按树数换算固定收益 |
| `constraints` | `stamina_floor`、`keep`、`include_existing`；只有入箱及满包入箱分支才要求已观察的 `destination_ref`，出货要求显式 `ship_item_ids` 白名单；不能关闭既有保护 |
| `steps` | 1～8 个语义步骤；`id` 唯一；`operation` 为照料、收获、采木、收集产物、产物入库或 `ship-products`；`after` 为前置步骤，`inputs` 为前置产物；日常阶段内采木须填 `target_quantity` 棵数 |
| `branches` | 0～4 个异常分支；条件唯一。不可达可跳过目标，满包可入库后续作；其他情况返回重规划。备用去向必须是当前观察内已授权设施 |

步骤按拓扑顺序给出，禁止重复 ID、循环、引用未来或外部结果。收集和入库必须引用前置结果；全部产物步骤必须能追溯到最终入库步骤。schema 中的枚举由本地校验器实际验证，不只提供给模型。

2026-11-26 补充：上述“全部产物入库”仅适用于入箱成果指标；留包指标只要求关联收集，纯照料无需收集或箱子。日常出货引用收集结果，且只转移 `ship_item_ids` 列出的已授权物品，其他产物留包；入箱指标禁止同时出货。日常可串联照料→收获→收集→出货→限量采木→收集；没有箱子不算受阻，满包没有允许的入箱分支时停止，不擅自丢弃或出售。局部树木不可达按 skip-target 处理时继续后续已规划的独立工作，目标不足仍返回部分完成。

结果新增 `shippedItems`、`backpackItems`，与 `storedItems` 分别计量，不能把出货当作立即增加金币。普通原生动作改变世界后，下一模型请求重建阶段资源观察、复用已核验的地图缓存；观察新鲜度和八个工具入口保持原边界。地图导航算法本轮未调整。

`constraints.include_existing` 必填数组，包含 `item_id` 和 `count`，仅用于玩家已授权处置的原有背包物品或现存地面材料；显式空数组表示保留全部原有库存。选入材料须满足对应成果：入箱指标要求入箱，留包指标要求背包可确认，出货只处理出售清单内材料。正常新产物按真实获得量转移，自动吸入及倒树延迟掉落继续核对，累计转移不重置成果量。桌面使用原生显示名和确认数量，不直接显示内部物品 ID。

地面观察的可选 `sourceId` 是原生物品/掉落物实例的短期身份，移动跨格不改变，读档或重启不沿用，也不授予拾取权限。阶段按动作前后来源差异及生产位置关联新产物；原有掉落物移动不算新产物，同格含未授权物品时停止整格拾取，避免扩大收集范围。

计划只在当前回合存活，不是原生命令数组，不在数据库保存待执行步骤；立即按真实观察逐动作执行。现有回执保存已发生事实，摘要保存实际成果。执行器处理自动拾取、资源检查和预先指定分支，正常步骤之间不调用模型。

## 结果与中断

最终结果需区分 `completed`、`stopped`、`interrupted`、`recovery-pending`，携带已确认成果、库存去向、未完成量或未知范围、分支原因、回执引用及执行期模型请求计数。具体数量只认原生终态与物品/库存观察，部分动作成功不代表整个阶段完成。

F8 新要求、F9 接管、读档和跨日使后续计划失效；暂停恢复重新核对前提。未知终态先查询既有动作和世界状态，不继续派工或重发。回执确认失败后释放动作执行权，不后台恢复旧导航。就寝优先于普通工作，没有总时长上限打断仍有进展的任务。

验证见运行时 `phase-plan.test.mjs`、`phase-observation.test.mjs`、`phase-executor.test.mjs`、回合与协议回归，以及 `docs/development/agent-phase-planning-acceptance.md` 中的原生共享样例、隔离实机和发行证据。
