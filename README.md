# 星露谷游戏伴侣

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)
[![平台：Windows x64](https://img.shields.io/badge/Windows-x64-0078D4.svg)](docs/desktop-portable.md)

星露谷游戏伴侣是《星露谷物语》的开源桌面工具。用自然语言和 AI 伙伴商量农活、改派任务，或把单人存档交给 AI 操作。项目源自 [CSXizhang/star](https://github.com/CSXizhang/star)，由 nekoimi 和贡献者独立维护和发布。

[下载绿色包](https://github.com/nekoimi/stardew-game-companion/releases) · [使用指南](docs/desktop-portable.md) · [更新日志](CHANGELOG.md) · [参与贡献](CONTRIBUTING.md)

下一代运行时将内置 Node 和 Pi 模型 SDK，通过 API Key 直连模型服务。[改造方案](docs/development/agent-runtime-rewrite.md) · [实施进度](docs/development/agent-runtime-rewrite-todo.md)

## 快速开始

1. 准备 Windows x64、《星露谷物语》、[SMAPI](https://smapi.io/) 和已登录的模型客户端。项目的游戏和 SMAPI 基线为 1.6.15 和 4.2.1。
2. 完整解压 Release 中的 `StardewGameCompanion-版本号-windows-x64.zip`，双击根目录的 **星露谷游戏伴侣.exe**。桌面运行库已打包到 EXE 中，Python 和配套 Mod 位于 `Mods/`，更新日志和许可在 `docs/`。
3. 在左侧「设置」选择游戏目录、游玩方式和伙伴来源，再点击「启动游戏」。进入单人存档后，回到桌面「今天」发送第一条要求。

若 Releases 中还没有绿色包，可[从源码构建](CONTRIBUTING.md#从源码构建)。旧安装的数据可在「设置 → 从原安装导入」迁移。

关闭窗口时可选「最小化到托盘」，让伴侣继续运行。双击 Windows 通知区域的伴侣图标可重新打开窗口。

## 游玩方式

| 模式 | 谁操作角色 |
| --- | --- |
| 伙伴模式 | 你照常操作玩家，AI 控制拥有独立背包、工具和体力的伙伴。 |
| AI 自己玩（首次启动默认） | 进入单人存档后，在桌面点「交给 AI」。在游戏窗口按默认快捷键 **F9** 立即接管。 |

可以交代“把农舍前的作物浇完”，也可以说“接下来农场交给你，先发展种植，保留幼树”。「今天」的输入框附近会显示控制权和指令进度，左下角常驻 Agent 状态。「农场工作」可查看目标及进度，「手记」可查看存档的记忆与约定。

目前主要支持单人农场协作，包括种植、收获、动物照料、加工、仓储和部分布局施工。社区中心献祭由玩家提交。[农场工作与施工范围](docs/farm-projects.md)

## 模型与数据

支持 Pi、DeepSeek（dsh）、Codex 和 MiniMax Code，可在 GUI 配置模型与推理档位，也可接续选定的 MiniMax Code 原会话。模型客户端自行安装与登录，订阅或 API 用量按所选服务计费。

存档由游戏保存，桌面设置写入绿色包的 `data/desktop-settings.json`，伙伴数据随绿色包保存。运行时会向玩家选用的模型发送聊天、相关游戏状态和工具结果。[后端验证范围](docs/tested-backends.md)

## 开发与贡献

桌面使用 C# / WPF，模型适配、会话和任务管理由 Python 运行时处理，SMAPI Mod 负责读取游戏状态和执行原生动作。欢迎提交可复现的问题报告、修复和验证结果。

[参与开发](CONTRIBUTING.md) · [文档索引](docs/README.md) · [通信协议](contracts/game-bridge/README.md) · [隔离实机回归](docs/repeatable-companion-acceptance.md)

`apps/` 分别存放 Windows 桌面程序、共享 Python 运行时和 SMAPI Mod。协议位于 `contracts/`，游戏提示词与技能位于 `resources/agent/`。[架构实施方案](docs/development/architecture-refactor.md) · [重构 TODO](docs/development/architecture-refactor-todo.md)

## 许可与致谢

项目采用 [Apache License 2.0](LICENSE)。感谢原作者 **CSXizhang** 及原项目贡献者，源码来源和本项目的版权声明见 [NOTICE](NOTICE)。绿色包随附第三方依赖的许可文件。

本项目为非官方项目。《星露谷物语》、SMAPI 和第三方软件及服务的名称、资源与权利归各自权利人所有。
