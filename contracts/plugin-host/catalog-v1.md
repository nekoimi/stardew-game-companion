# 在线插件目录 v1

目录运营方通过 HTTPS 提供 `catalog.json` 和插件 ZIP。桌面「插件中心 → 在线目录」使用运营方提供的目录地址与签名公钥。加载时验证签名和有效期，列表显示发布者、版本和宿主协议兼容性。安装和更新均需确认。

## 签名格式

目录有两个字段。`payload` 是目录内容的 UTF-8 字节经 Base64 编码的结果。`signature` 是对相同字节进行 ECDSA P-256 与 SHA-256 签名后的 DER 编码，再转换成 Base64。桌面输入的目录公钥使用 Base64 编码的 DER SubjectPublicKeyInfo。

目录内容含 `catalogVersion: 1`、`expiresAt`（ISO 日期时间）、`publishers`（每项有 `name` 和 `publicKey`）及 `plugins`。发布者公钥采用同一编码。每条插件版本记录含 `id`、`name`、`publisher`、`version`、`hostProtocol`、`downloadUrl`、`sha256`、`packageSignature` 和整数 `dataVersion`。

下载时先检查目录的版本、有效期和兼容性，再检查 ZIP 的 SHA256 与发布者签名。发布者签名对原始 32 字节 SHA256 摘要执行 ECDSA P-256 与 SHA-256 签名，使用同样的 DER 与 Base64 编码。包内清单、文件哈希及目录声明在安装前再次核对。已通过目录安装或更新的插件绑定其发布者公钥。

## 更新和数据

使用更高版本更新，`dataVersion` 保持或增加。供应方负责启动时的数据迁移，可在完成后用 `ready` 或 `needs_configuration` 报告可用，对失败报告 `degraded`。宿主先验证下载包，再停止旧插件并备份其数据。更新验证新实例的握手与可用状态，确认后保存上一版本和更新前的数据备份。已停用的插件仍停用，在下一次启用时启动新版本。

更新失败或中途退出时恢复旧版本、旧数据和启动偏好。用户也可在详情页回退到备份版本，恢复更新前的数据。备份覆盖 `STARDEW_PLUGIN_DATA_DIR`，日志及插件使用的外部存储由插件自己管理。

## 准备目录

用两份 P-256 私钥分别签发目录和插件包。开发工具接收 PEM 私钥及同一发布者的独立 ZIP。

```powershell
pwsh -File tools/dev/sign-plugin-catalog.ps1 -Publisher '<清单中的发布者>' -PublisherPrivatePem '<发布者私钥文件>' -CatalogPrivatePem '<目录私钥文件>' -BaseUrl 'https://example.org/plugins' -Output artifacts/catalog -Archives 'command-0.1.0-win-x64.zip'
```

将生成的 `catalog.json` 和 ZIP 放到所选 HTTPS 目录，独立提供 `catalog-public-key.txt` 中的公钥。两份私钥保存在运营方环境。本项目的客户端可连接指定目录，仓库发行包使用本地 ZIP 作为开箱入口。
