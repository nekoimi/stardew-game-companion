# 开发资料索引

[在线文档站](https://nekoimi.github.io/stardew-game-companion/)是玩家和扩展开发者的阅读入口。`docs/site/` 维护站点，`docs/development/` 集中保存说明原文、设计、TODO、原型与历史记录。

## 公开文档与说明原文

- [在线玩家指南](https://nekoimi.github.io/stardew-game-companion/guide/getting-started)；[完整说明原文](desktop-portable.md)按清单进入网站，并随绿色包提供。
- [在线农场工作说明](https://nekoimi.github.io/stardew-game-companion/reference/farm-projects)；[原文](farm-projects.md)。
- [在线隔离实机回归](https://nekoimi.github.io/stardew-game-companion/reference/acceptance)；[原文](repeatable-companion-acceptance.md)。
- [在线更新日志](https://nekoimi.github.io/stardew-game-companion/reference/changelog)；[原文](../../CHANGELOG.md)。

## 开发者

- [源码构建、发布和贡献](../../CONTRIBUTING.md)
- [文档站维护](../site/README.md)与[截图维护](screenshots.md)
- [游戏通信协议](../../contracts/game-bridge/README.md)
- [桌面 HTTP 接口](../../contracts/desktop-api/README.md)
- [桌面插件宿主协议](../../contracts/plugin-host/README.md)
- [通用参考插件与绿色包验收](../../examples/plugins/README.md)
- [固定的隔离实机回归](repeatable-companion-acceptance.md)
- [执行可靠性与工作反馈优化进度](work-reliability-optimization.md)
- [Agent 批次工作与阶段规划方案](agent-batched-work-plan.md)、[实施 TODO](agent-batched-work-todo.md)、[效率基线](agent-batched-work-baseline.md)与[验收报告](agent-batched-work-acceptance.md)
- [持续经营规划与接续验收](ongoing-farm-planning-acceptance.md)
- [Agent 完整阶段规划与连续执行方案](agent-phase-planning-plan.md)与[实施 TODO](agent-phase-planning-todo.md)（待实施）
- [运行时提示词](../../resources/agent/prompts/agent-ts-instructions.md)与[游戏指导](../../resources/agent/prompts/agent-guidance)
- [TypeScript 运行时方案](agent-runtime-rewrite.md)
- [改造进度](agent-runtime-rewrite-todo.md)与[发行验收](agent-runtime-rewrite-phase3.md)
- [模型接入与设置页优化方案](model-connection-plan.md)、[实施 TODO](model-connection-todo.md)与[最终验收](model-connection-acceptance.md)
- [桌面插件中心与第三方服务托管方案](desktop-plugin-platform-plan.md)、[实施 TODO](desktop-plugin-platform-todo.md)与[插件中心界面原型](prototypes/desktop-plugin-center.html)

## 历史设计与验证记录

- [会话延续与伙伴能力设计](companion-efficiency-and-play-design.md)
- [双来源会话验证记录](dual-connection-validation.md)
- [原生动作与存档问题修复记录](csharp-reliability-fixes.md)
- [项目目录重构](architecture-refactor.md)与[当时的验收记录](architecture-refactor-acceptance.md)
