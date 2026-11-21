# 阶段 3 · API 绿色候选与实机回归入口

首个阶段 3 候选 ZIP 由干净源码快照构建。包根目录提供「星露谷游戏伴侣.exe」，配套 Mod 在 `Mods/`，Node、模型 SDK 和运行时在 `runtime/`，说明与许可在 `docs/`。构建用 .NET 10 SDK、校验后的 Node 22.22.2 和 npm 锁文件。归档校验逐项核对 11,439 个文件及 SHA256。

- 归档 `StardewGameCompanion-0.3.22-api-preview-windows-x64.zip`
- ZIP SHA256 `63fad08f4ad02fdbd504ae6afa55c58b89e09129ecfd5fced1cba58b18c639da`
- 游戏与 SMAPI 测试版本 1.6.15.24356 和 4.5.2

从同一个阶段 3 提交在两个独立克隆目录重建，两个 ZIP 均通过校验，完整文件清单 SHA256 一致。此前暂存源码快照与干净克隆的 EXE 和 Mod DLL 使用了不同的 Git 版本标识，造成两份文件哈希差异。上述 ZIP SHA256 对应实际跑过实机的候选包。

## 自动化与桌面

- TypeScript 测试 17 项、C# 传输测试 65 项和 Python 实机报告判定测试 5 项通过。混合掉落物夹具为原生位置读取初始化了游戏世界状态，C# Mod 的 Release 全量测试 731 项通过。原生扩展编译使用 `STAR_DOTNET_PATH` 指向隔离 .NET 6 SDK。
- 解压路径包含中文和空格。桌面与子进程 PATH 仅保留 Windows 系统路径，连接模拟 DeepSeek、执行原生移动、思考中和移动中按 F9 接管均通过。切到模拟 OpenAI 后，实测角色、记忆、历史、未完成目标和用量均保留。
- 待确认事项显示在「今天」。选择「留种」后立即写入 SQLite，界面上的待确认项关闭。
- 「打断并执行」停止旧的流式回合，改派的原生移动收到成功回执。退出到标题并重新加载同一隔离存档后，运行时使用新的游戏会话，保留旧目标，控制权由玩家持有。模拟回合测试覆盖明确交权后恢复原目标。
- 停止测试所属的 Node 后，桌面能重连同一游戏。托盘双击可重新打开窗口，退出桌面会清理自身运行时，已打开的游戏可继续使用。环境为现有 Windows 主机，测试以 PATH 隔离外部客户端。

## 模拟 API 实机回归

三组使用同一个隔离游戏、存档和模拟 DeepSeek 流。判定以原生状态、动作回执、动画帧和碰撞采样为准。隔离实例退出后，源存档和日常 Mod 的校验通过。

| 场景 | 耗时 | 已核对 |
| --- | --- | --- |
| `care-guidance` | 45.3 秒 | 补水，浇完作物，动作进行期间收到的新要求在下一工具边界生效。 |
| `harvest-logistics` | 23.4 秒 | 成熟作物收净，木材和作物入箱，之后提取作物出货，木材保留。 |
| `rest-forestry` | 94.9 秒 | 原生床恢复至 80 体力，两棵成熟树采伐并将木材入箱，幼树保留。 |

农事观察补验使用从 `b686231` 干净克隆构建的绿色候选。ZIP SHA256 为 `a5f73ece4db5d44d6ed5449e342beb5b0429139f9b3902bb110a2d7357972f93`。包内 11,439 个文件校验通过，使用随包 Node 跑过 18 项运行时测试。

三组模拟实机复跑全部通过。收获、采木和显式拾取的工具结果携带同一存档中动作后的附近掉落物和背包观察。显式拾取返回「地上无物品」时，模拟脚本复查背包后继续入箱，场景仍按原生箱内物品判定。过期场景观察的处理由自动化测试覆盖。

连接回执补验使用从 `1b9fb28` 干净克隆构建的候选包。ZIP SHA256 为 `3bc2f726040faf2875cc07f3439ba6f6fca242800d9ffadc09499e6020eb1bae`。11,440 个归档文件、19 项 TypeScript 测试和 5 项 Python 判定测试通过。随包 Node 的本地验收入口分别通过模拟 DeepSeek、OpenAI 的工具调用和文本确认，错误凭据返回鉴权分类。解压包再次通过三组模拟实机回归以及桌面启动、切换模型、F9、读档和重连。

### 动作回执恢复补验

从 `dc99849` 的干净克隆构建并校验 11,440 个文件。ZIP SHA256 为 `c3687056a4e2c73831af9271a3533e333a6fe82301dc85334d3a56e0c349ad70`，包清单 SHA256 为 `667a65b40a2a407db02a5bae93a9c396ee4f0094038cc6e87f2c73e72b91fdd6`。TypeScript 21 项、C# 传输 65 项、C# Mod Release 731 项和 Python 报告判定 5 项通过。

共享契约样例验证了 `skill.result` 内的 `unknown` 仍需核对。运行时回归验证了同一会话只有确认终态才能恢复派工，切换存档后丢失的旧回执不会阻塞新存档。

解压到包含中文和空格的路径后，用下方固定场景命令运行模拟 `deepseek-flash`。游戏 1.6.15.24356 与 SMAPI 4.5.2 下，`care-guidance`、`harvest-logistics`、`rest-forestry` 均通过，分别耗时 42.2、23.5 和 98.1 秒。总结果和各场景的原生证据保存在本地运行目录 `presets-1121-182636-888f-game`。

### 桌面重连状态补验

首轮桌面复测在重连后短暂启用了发送入口，随后游戏发来待核对状态，输入框再次禁用。`851447b` 将游戏快照与恢复状态一起作为桌面操作的前置条件。22 项 TypeScript 测试通过，其中 WebSocket 回归复现了快照先于恢复状态到达的顺序。

从 `851447b` 干净克隆重建。ZIP SHA256 为 `6f8a51abd263df4414081e7d108dec7c567fd33f59ed468a20815257c0766629`，包清单 SHA256 为 `9bb524633c60f86e13acff415119442ca566ac20366ff90ca546d2e6af8efae9`。归档校验通过 11,440 个文件。

新版解压包的桌面冒烟确认重连后会显示「核对旧动作」，发送入口保持禁用。核对完成后可发送消息，玩家继续持有控制权。两种 F9 接管、待确认事项、模拟模型切换、读档以及托盘和退出清理通过。

同一个包用模拟 `deepseek-flash` 再跑三组隔离实机，分别耗时 39.2、23.4 和 97.9 秒，全部通过。证据在本地运行目录 `presets-1121-183455-b5d7-game`。源存档、日常存档及日常 Mod 的运行前后哈希一致。

### 正式验收入口预检

固定场景脚本会先校验解压目录的发行清单，再构建 TestDriver 或启动隔离游戏。`851447b` 候选的 ZIP 和解压目录各通过 11,440 个文件校验。修改入口文件、删除入口文件的两份错误输入均在启动前被拒绝。

包验证接入后，三组模拟隔离实机回归全部通过，证据在 `presets-1121-184109-871b-game`。报告使用命令指定的验收模式，并与运行时的就绪证据核对。6 项 Python 判定测试通过，覆盖模拟服务启动失败时的报告分类。新报告入口的 `care-guidance` 模拟实机复测通过，证据在 `presets-1121-184411-7a04-game`。两家正式入口均因缺少本机凭据在启动前停止。

### 两种流式协议的固定场景补验

同一绿色候选和同一套场景判定可用 `-Provider deepseek -Simulated` 或 `-Provider openai -Simulated` 运行。两种模拟服务从各自的请求协议读取指令与原生结果，共用场景逻辑。OpenAI 模拟服务分段返回工具参数，并按请求生成独立的工具调用 ID。

`gpt-5-mini` 的三组模拟隔离实机均通过，分别耗时 39.7、23.4 和 84.4 秒。SQLite 中 26 条模型请求都保存了用量回执。证据在 `presets-1121-184803-b8ea-game`，包清单 SHA256 为 `9bb524633c60f86e13acff415119442ca566ac20366ff90ca546d2e6af8efae9`。23 项 TypeScript 测试通过，覆盖 OpenAI 参数分段、回执关联和取消时关闭 HTTP 响应。

`deepseek-flash` 使用同一候选包复跑三组，分别耗时 38.7、23.4 和 96.4 秒，全部通过。SQLite 中 26 条模型请求都保存了用量回执，证据在 `presets-1121-185115-7659-game`。两次运行均验证了源存档、日常存档和日常 Mod 的哈希一致。

### 桌面隔离验收入口

`tests/acceptance/start-desktop-acceptance.ps1` 从解压候选复制桌面和存档，装入仓库内的 TestDriver，记录发行清单哈希、游戏和 SMAPI 版本。桌面仍从原 EXE 配置模型、启动游戏。模拟参数会将两家模型请求导向本机测试服务。

使用新入口对 `851447b` 候选跑完桌面模拟冒烟。通过游戏菜单选择复制的存档后，验证了原生移动、思考中和移动中 F9、改派、待确认项、同一存档模型切换、断线核对、重载、托盘和退出。源存档哈希一致，链接形式的存档在启动前被拒绝。运行目录为 `desktop-1121-185757-c5da`，正式操作步骤见[桌面与同一存档切换](../repeatable-companion-acceptance.md#桌面与同一存档切换)。

正式模式的桌面预检也已打开并退出候选包，运行目录为 `desktop-1121-190409-9e56`。这次预检止于正式 API 凭据设置之前。

### 旧版独立回退预演

在新版桌面验收退出后，重新解压阶段 0 保留的旧绿色包。旧包清单记录源码 `812f290`，ZIP SHA256 为 `ecc9d8e2ee1503532c69cd47b56ca85043e9f133a3ef3005fefc1dcf0c8ff009`，清单的 1,793 个 Mod 文件全部校验通过。

使用旧目录的 EXE 和随包 Python，加载同一来源的隔离存档副本，通过模拟 Pi 下发原生移动。托盘恢复后收到成功回执，退出旧桌面结束其运行时，游戏仍可使用。证据在 `rollback-1121-190635-f942`。正式发布回退时，先在游戏中保存并退出，再从独立旧目录打开旧 EXE，使用旧目录保存的配置和伴侣数据。

TS-14 开始时，先将桌面调用归到 `/v2/`，再移除 `DesktopRuntimeApi` 的版本转换、旧客户端配置和导入入口。Mod 保留原生 transport、动作及回执，将源码调试和隔离验收迁到 v2 后移除旧启动分支。最后调整 Node 打包和开发测试入口，按调用方移除 Python 生产文件及旧 CLI 脚本。独立的验收判定脚本仍由开发环境运行。

### DeepSeek 官方与第三方 Responses

从 `a7b5f4d` 干净克隆构建候选包，ZIP SHA256 为 `723bcc45e5e2ecfdeae733d470a59803ed8b09a4a7968b77265ea138f202950c`，包清单 SHA256 为 `38c3ac0cb4fa8a23914d53219f0034c41b4ac604a9ab058af797f3c7d98cdba6`。11,440 个文件通过校验，24 项 TypeScript、65 项 C# 传输、731 项 C# Mod 和 7 项验收判定测试通过。

DeepSeek 官方 `deepseek-flash` 与第三方 Responses `gpt-6.1-sol` 均通过绿色包的「测试连接」，真实服务各完成工具调用、工具回执和确认文本。第三方端点的密钥独立于官方 OpenAI，验收从当前进程传入，临时配置留在本机。

用游戏 1.6.15.24356 和 SMAPI 4.5.2 在隔离存档运行固定场景。两家首轮的 `care-guidance` 与 `harvest-logistics` 通过。首轮 `rest-forestry` 分别遇到回合超时和快照超时。第三方 Responses 按默认预算单场景复测通过。DeepSeek 首次单场景复测的原生回执确认另一个农场箱收入 34 个木材，旧脚本只采样预设箱，因此报告未通过。随后验收改为核对农场箱内木材净增量，并对 Windows 短暂占用的快照请求在下一帧重试。

| 真实接口 | `care-guidance` | `harvest-logistics` | `rest-forestry` |
| --- | --- | --- | --- |
| DeepSeek 官方 | 通过，52.9 秒 | 通过，40.1 秒 | 修正判定后补验通过，120.7 秒 |
| 第三方 Responses | 通过，114.5 秒 | 通过，108.7 秒 | 按默认预算补验通过，166.3 秒 |

首轮入口为 `run-companion-presets.ps1 -ReleaseDir <解压目录> -SavePath <隔离来源存档> -Provider <服务商> -Model <模型> -TimeoutSeconds 150`，证据目录为 `presets-1121-200030-777a-game` 和 `presets-1121-200444-5fa7-game`。补验使用 `-Cases rest-forestry` 和默认 600 秒，最终通过的目录分别为 `presets-1121-201658-43c2-game` 和 `presets-1121-201335-68b0-game`。源存档、日常存档和日常 Mod 的哈希一致。

使用 `start-desktop-acceptance.ps1` 启动隔离桌面，在同一存档从 DeepSeek 切到第三方 Responses。新模型正确复述伙伴名、手记约定和未完成目标。SQLite 确认两轮对话共用同一存档且两家都有用量回执。证据目录为 `desktop-1121-201946-8c98`。真实 API 的 F9、改派、读档、重连和待确认项仍待补验。

### 真实桌面生命周期补验

真实 DeepSeek 桌面复测发现自动选中的监听端口偶尔被 Windows 拒绝，以及过长的路线预览可能在超时后继续占用命令通道。`d0e5b24` 对自动端口做有限重试，`f337409` 为路线预览结算超时并在 F9 或改派时结束查询。端口冲突用例、66 项 C# 传输测试和 3 项路线观察测试通过。

从 `f337409` 干净克隆构建候选，ZIP SHA256 为 `320d1b41b683c78068d43d19171226f266fb8470897bae9d62393ee9eac98c2e`，包清单 SHA256 为 `2c45844bff592d3153ae8d0171e9f0f1eec53b4f8ad2565af440a2cade9effac`。用 `start-desktop-acceptance.ps1 -ReleaseDir <解压目录> -SavePath <隔离存档> -GamePath <SMAPI 目录>`，以 DeepSeek `deepseek-flash` 在游戏 1.6.15.24356、SMAPI 4.5.2 复测。思考和原生移动分别实按 F9，改派等旧动作结算后回复，选择「留种」写入 SQLite，重连沿用游戏会话，重新读档建立新会话并保留玩家控制。证据目录为 `desktop-1121-204413-cfa2`，源存档哈希一致。

## TS-14 正式绿色包

`4b667e0` 将桌面与 Mod 切到 v2，移除旧 Python 生产运行时、CLI 适配器、外部会话接口和数据迁移入口。Python 只用于开发验收报告。首轮实机照料和收获出货通过，采木后的寻路一直停在准备阶段，超过了运行时等候回执的时间。`a12d312` 为寻路准备添加 20 秒原生超时，保留可核对的失败终态并释放执行权。导航回归验证了超时后下一条动作可以执行。

从 `a12d312` 干净克隆，用 .NET SDK 10.0.404 和锁定依赖构建 `StardewGameCompanion-0.3.22-windows-x64.zip`。归档及新解压的中文空格目录均通过 11,440 个文件与清单校验。Windows 系统 PATH 下随包 Node 22.22.2、SQLite 3.51.2、pi-ai 1.6.0 自检通过，`consult` 能读取随包执行指导。包内核对了 87 个生产 npm 依赖及许可证记录，并包含 Node、.NET 和项目许可。包内未发现旧生产脚本或临时模型配置值。

- ZIP SHA256 `1abc23f07aa65ab3da79ab5e8ea46f1da7efe8fe40972ac503f1b9bdac2eb421`
- 包清单 SHA256 `531b18293f9b6fb4b048f4d8a4c79de9d5d267cd6885cdda5d68100b96dc62d7`
- 源码测试通过 24 项 TypeScript、63 项 C# 传输（含与 TS 实连及共享样例）、684 项 C# Mod Release、7 项验收判定。

最终包使用真实 DeepSeek `deepseek-flash`，游戏 1.6.15.24356 和 SMAPI 4.5.2。按默认 600 秒预算运行，动作、物品、动画和碰撞以原生证据判定。

| 场景 | 结果 | 运行目录 |
| --- | --- | --- |
| `care-guidance` | 通过，50.3 秒。补水、浇水及工具边界的追加消息。 | `presets-1121-211241-d364-game` |
| `harvest-logistics` | 通过，41.6 秒。收获、入箱、出货及保留木材。 | `presets-1121-211241-d364-game` |
| `rest-forestry` | 单组复测通过，115.2 秒。原生床恢复至少 80 体力，采两棵成熟树，保留幼树，箱内木材从 0 增至 31。 | `presets-1121-211724-6d5c-game` |

三组同跑时采木场景提前询问玩家，第二棵成熟树和入箱未达标。按原自然语言和原判定单组重跑通过，保留两次证据。真实 Responses 验收使用第三方网关。官方 OpenAI 账户和待确认事项跨天本轮未验。

第三方 Responses `gpt-6.1-sol` 用最终包完成了连接预检、流式工具调用和回执确认。`launch-game.ps1 -Mode interactive` 用同一包绑定隔离存档，实体 F8 打开原生选项。结束后游戏正常退出，原存档与 Mod 哈希一致。证据目录为 `interactive-final-1121-212226`。

最终包的 WPF「测试连接」在两种真实协议下通过。两种模型均验证了思考中 F9 和移动中 F9，后者收到原生取消回执。DeepSeek 复测中，改派确认取消了旧动作，新模型请求失败。同一 Key 随后在独立最小请求中返回 HTTP 402。Responses 网关的改派复测在观察窗口内未开始原生移动。证据分别在 `desktop-1121-211935-d4ad` 与 `desktop-1121-212640-ae6a`。真实模型的完整生命周期记录见 [TS-13](#真实桌面生命周期补验)。

最终包使用模拟 DeepSeek 完成桌面复测。在 `desktop-1121-213013-b6f6` 验证了改派收到新回复，选择写入 SQLite，同一游戏内恢复旧动作和会话，读档生成新会话并保留玩家控制，托盘恢复和退出清理通过。两种 F9 使用实体按键，移动中的接管收到原生取消回执。源存档、日常存档和验收游戏的 Mod 文件哈希一致，验收证据无临时 API Key。

新版退出后，在新目录校验旧绿色包的 1,793 个文件。旧 EXE 可以独立启动，使用模拟 Pi 在隔离档中完成原生移动。隐藏窗口期间动作继续，托盘恢复时仍显示同一游戏会话的回执。退出旧桌面仅结束所属运行时，游戏保持运行。证据目录为 `rollback-final-1121-210453`。旧包 ZIP SHA256 与阶段 0 留存值一致。

## 复现命令

```powershell
pwsh -File packaging/windows/build-node-release.ps1 -Output artifacts/windows-x64
pwsh -File packaging/windows/verify-node-release.ps1 -Archive '<ZIP 路径>'
pwsh -File tools/dev/test.ps1 -GamePath '<SMAPI 目录>' -DotNet '<.NET 6 dotnet.exe>'
pwsh -File tests/acceptance/run-companion-presets.ps1 -ReleaseDir '<解压目录>' -SavePath '<春季隔离来源存档>' -Provider deepseek -Model deepseek-flash
pwsh -File tests/acceptance/run-companion-presets.ps1 -ReleaseDir '<解压目录>' -SavePath '<春季隔离来源存档>' -Provider deepseek -Model deepseek-flash -Cases rest-forestry
```

实机执行由已打包的运行时和 Mod 完成，TestDriver 布置存档、发送自然语言和采集原生结果。真实 API 从当前进程读取相应厂商或网关凭据。[三组固定场景和正式入口](../repeatable-companion-acceptance.md)
