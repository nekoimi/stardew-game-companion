# 模型配置

在「设置 → 模型连接」选择服务商，填写 API Key 与模型，运行「测试连接」后保存。

## 支持的接入方式

| 方式 | 需要填写 |
| --- | --- |
| DeepSeek 官方 | 官方 API Key、模型。 |
| OpenAI 官方 | 官方 API Key、模型。 |
| 自定义接口 | Chat Completions 或 Responses 协议、HTTPS Base URL、模型 ID 和接口提供方的 Key。 |

Base URL 保留 `/v1` 等前缀，程序会拼接 `/chat/completions` 或 `/responses`。自定义模型 ID 应与接口提供方要求一致。

## 流式与推理档位

支持流式和非流式响应。如果接口不支持流式，在设置中关闭。官方接入的高级设置会在有已知支持时显示推理档位。

「测试连接」会验证模型工具调用与回执，能够普通聊天不代表具备控制游戏所需的能力。

## 保存与切换

API Key 由 Windows 凭据管理器保存，保存后显示掩码。留空表示沿用已保存的 Key，填写新值会替换它。换电脑后需要重新填写。

读档后，可在空闲时点击「应用模型」切换连接；当前存档的角色、对话、手记与目标继续保留。

模型会接收聊天、相关游戏观察、工具结果和手记。当前数据位置的 `data/companion.sqlite` 保存伙伴资料，`data/desktop-settings.json` 保存桌面设置。

完整字段说明见[模型配置](/reference/desktop-guide#模型配置)。
