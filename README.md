# 星露谷游戏伴侣

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)
[![平台：Windows x64](https://img.shields.io/badge/Windows-x64-0078D4.svg)](docs/desktop-portable.md)

星露谷游戏伴侣是《星露谷物语》的开源桌面工具。用自然语言和 AI 伙伴商量农活、改派任务，或把单人存档交给 AI 操作。项目源自 [CSXizhang/star](https://github.com/CSXizhang/star)，由 nekoimi 和贡献者独立维护和发布。

[下载绿色包](https://github.com/nekoimi/stardew-game-companion/releases) · [使用指南](docs/desktop-portable.md) · [更新日志](CHANGELOG.md) · [参与贡献](CONTRIBUTING.md)

## 快速开始

1. 准备 Windows x64、《星露谷物语》和 SMAPI。[SMAPI](https://smapi.io/)
2. 完整解压绿色包，双击根目录的 **星露谷游戏伴侣.exe**。桌面运行库和 Node 随包提供。
3. 在「设置」选择游戏目录、玩法及模型，填写 API Key 后点击「测试连接」。再点击「启动游戏」，进入单人存档后在桌面「今天」发送指令。

可从源码构建 Windows 绿色包，旧版可保存在独立目录用于回退。[构建说明](CONTRIBUTING.md#从源码构建)

关闭窗口时可选「最小化到托盘」，让伴侣继续运行。双击 Windows 通知区域的伴侣图标可重新打开窗口。

## 游玩方式

| 模式 | 谁操作角色 |
| --- | --- |
| 伙伴模式 | 你照常操作玩家，AI 控制拥有独立背包、工具和体力的伙伴。 |
| AI 自己玩（首次启动默认） | 进入单人存档后，在桌面点「交给 AI」。在游戏窗口按默认快捷键 **F9** 立即接管。 |

可以交代“把农舍前的作物浇完”，也可以说“接下来农场交给你，先发展种植，保留幼树”。「今天」的输入框附近会显示控制权和指令进度，左下角常驻 Agent 状态。「农场工作」可查看目标及进度，「手记」可查看存档的记忆与约定。

目前主要支持单人农场协作，包括种植、收获、动物照料、加工、仓储和部分布局施工。社区中心献祭由玩家提交。[农场工作与施工范围](docs/farm-projects.md)

伙伴会根据收获和清障后的掉落物、背包和原生回执安排剩余工作。

## 模型与数据

支持 DeepSeek 官方、OpenAI 官方和第三方 OpenAI Responses。第三方网关需要 Base URL、API Key 和模型 ID，桌面可以检测流式工具调用和回执。模型用量按所选服务计费。

存档由游戏保存，伙伴资料与聊天按存档写入绿色包的 `data/companion.sqlite`，API Key 由 Windows 凭据管理器保管。运行时会向所选模型发送聊天、相关游戏状态和工具结果。[使用指南](docs/desktop-portable.md)

## 开发与贡献

桌面使用 C# / WPF，TypeScript 运行时负责模型接入、伙伴数据和任务调度，SMAPI Mod 负责读取游戏状态和执行原生动作。欢迎提交可复现的问题报告、修复和验证结果。

[参与开发](CONTRIBUTING.md) · [文档索引](docs/README.md) · [通信协议](contracts/game-bridge/README.md) · [隔离实机回归](docs/repeatable-companion-acceptance.md)

`apps/` 存放 Windows 桌面程序、共享 TypeScript 运行时和 SMAPI Mod。协议位于 `contracts/`，游戏提示词与指导位于 `resources/agent/prompts/`。[架构方案](docs/development/agent-runtime-rewrite.md) · [实施进度](docs/development/agent-runtime-rewrite-todo.md)

## 许可与致谢

项目采用 [Apache License 2.0](LICENSE)。感谢原作者 **CSXizhang** 及原项目贡献者，源码来源和本项目的版权声明见 [NOTICE](NOTICE)。绿色包随附第三方依赖的许可文件。

本项目为非官方项目。《星露谷物语》、SMAPI 和第三方软件及服务的名称、资源与权利归各自权利人所有。
