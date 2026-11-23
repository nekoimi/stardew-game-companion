# 模型接入与设置页验收

## 构建与测试

从 `7ad078f` 的干净克隆构建 Windows x64 绿色包，使用 .NET SDK 10.0.404。ZIP 与含中文和空格的解压目录各校验 11,442 个文件。系统 PATH 下的随包 Node 22.22.2、SQLite 3.51.2 和 pi-ai 1.6.0 通过自检，随包 `models.json` 与运行时输出一致。包内检查了本地验收产物与临时模型凭据，均未发现。

- 绿色包 `StardewGameCompanion-0.3.22-windows-x64.zip`
- ZIP SHA256 `ba7025be6c41319822ffe4641b9dc816fcb57cc5b0129e0c7aa5c41cacc56d9b`
- 发行清单 SHA256 `a2eb6ff32cb0a40cb91b12cbca0c0d7bc3c144d6375ac1939d50dbcccd776bdd`
- 28 项 TypeScript、63 项 C# 传输与跨语言契约、684 项 C# Mod、7 项验收判定测试全部通过。

## 协议

四种自定义组合均用模拟服务核对了请求地址、实际 `stream` 值、工具调用、调用 ID 和回执后的确认文本。两家官方预设在模拟服务上也各通过流式与非流式验证。非流式半截 HTTP 响应可取消，流式未完成的工具参数不会产生可执行调用。模型拒绝所选模式时，工具验证返回错误并保持用户的配置。

最终包内的运行时使用真实网关模型 `gpt-6.1-sol`，在 Chat Completions 与 Responses 下分别通过流式、非流式工具往返。DeepSeek 官方的两种请求均返回余额或额度不足。OpenAI 官方由于缺少凭据未执行真实测试。

## 隔离实机

游戏 1.6.15.24356、SMAPI 4.5.2。真实网关模型 `gpt-6.1-sol` 使用 Responses 非流式，在同一场游戏、同一存档中通过三组固定场景。证据目录为 `presets-1121-224044-cd59-game`。源存档、日常存档与验收游戏 Mod 的前后哈希一致。

| 场景 | 结果 |
| --- | --- |
| `care-guidance` | 通过，88.1 秒。原生补水和浇水完成，中途追加要求在工具边界送达。 |
| `harvest-logistics` | 通过，104.2 秒。核对收获、入箱、出货及木材保留。 |
| `rest-forestry` | 通过，258.5 秒。核对原生床恢复、两棵成熟树、幼树保留和入箱。 |

同一真实网关的 Chat Completions 流式也通过了 `care-guidance`，耗时 80.9 秒。证据目录为 `presets-1121-224922-60de-game`。

## 桌面

阶段 2 的隔离桌面运行 `desktop-1121-223616-4e2a` 验证了三页键盘切换、780 × 575 窗口下的滚动布局、未读档提示、跨页草稿及首个缺失字段聚焦。测试连接未写入设置，保存模型后重启恢复非流式开关。Windows 凭据使用测试替身验证了旧 Responses 凭据复用和不同地址隔离。

同一存档从模拟 DeepSeek 非流式切换到模拟 OpenAI 流式后，桌面收到了新模型的回复，伙伴偏好保留。两个隔离存档加载了各自的偏好。在非流式请求等待期间按实体 F9，游戏保持响应，控制权回到玩家，旧回合停止且未调度原生动作。

最终包的模拟桌面运行 `desktop-1121-224822-4ea6` 验证了思考和移动时的 F9、原生动作取消回执、改派、选择、同会话重连、重新读档、托盘恢复与退出清理。回合测试另确认迟到回复无法执行工具或进入新存档历史，切换模型后同一存档的历史和目标继续沿用。

`desktop-1121-225110-228c` 使用最终包验证了 OpenAI 官方预设的默认模型、流式测试、关闭流式后的再次测试及独立保存。此运行使用模拟服务，设置文件只保存模型配置。

## 复现入口

```powershell
pwsh -File tools/dev/test.ps1 -GamePath '<SMAPI 游戏目录>' -DotNet '<.NET 6 dotnet.exe>' -Python '<验收 Python>'
pwsh -File packaging/windows/build-node-release.ps1 -Output artifacts/model-connections -GamePath '<SMAPI 游戏目录>'
pwsh -File packaging/windows/verify-node-release.ps1 -Archive '<ZIP 路径>'
pwsh -File tests/acceptance/run-companion-presets.ps1 -ReleaseDir '<解压目录>' -SavePath '<隔离来源存档>' -Provider custom -Protocol responses -Model gpt-6.1-sol -NonStreaming
```

测试用第三方网关凭据从进程环境读取，复用 `OPENAI_COMPATIBLE_BASE_URL` 和 `OPENAI_COMPATIBLE_API_KEY`。[隔离验收说明](repeatable-companion-acceptance.md)
