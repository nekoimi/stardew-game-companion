# 文档站维护

本站使用 VitePress，发布目录为 `.vitepress/dist`，默认地址为 https://nekoimi.github.io/stardew-game-companion/ 。

```powershell
cd docs/site
npm ci
npm run dev
```

生产构建使用 `npm run build`，本地预览使用 `npm run preview`，访问终端显示的 `/stardew-game-companion/` 地址。

玩家教程位于 `guide/`，架构介绍位于 `development/`，插件概览位于 `plugins/`，截图等站点静态资源位于 `public/`。构建前由 `scripts/prepare-content.mjs` 按明确清单读取 `docs/development/` 的完整说明、仓库协议和更新日志，生成 `reference/`。生成页面不提交；修改原文件后重新运行 dev 或 build。

站点目录之外的说明、设计、TODO、原型与验收记录统一放在 `docs/development/`，维护入口为[开发资料索引](../development/README.md)。项目 README 等公开文档入口优先链接 GitHub Pages，源码路径和维护文档使用仓库内链接。

设计草案、TODO、原型与本地验收产物不进入站点。增加公开参考文档时，在生成脚本和侧栏中同时登记；跨文档链接会重写到站内已登记页面，其余链接指向 GitHub 原文件。

流程图使用 Mermaid 代码块，由站点插件渲染，支持浅色与深色主题。修改后运行正式构建，并在预览中检查图形与手机端显示。

首次部署先在仓库「Settings → Pages → Build and deployment → Source」选择 **GitHub Actions**。推送 `dev` 上相关文档或手动运行「Deploy documentation」即可部署，与程序 Release 独立。若环境分支规则限制部署，应允许 `dev`；使用自定义域名时相应调整 `.vitepress/config.mts` 的 `base`。
