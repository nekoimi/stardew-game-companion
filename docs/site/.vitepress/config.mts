import { withMermaid } from 'vitepress-plugin-mermaid';

const repo = 'https://github.com/nekoimi/stardew-game-companion';

export default withMermaid({
  lang: 'zh-CN',
  title: '星露谷游戏伴侣',
  description: '星露谷游戏伴侣的玩家指南、开发文档和插件协议。',
  base: '/stardew-game-companion/',
  srcExclude: ['README.md', 'node_modules/**'],
  cleanUrls: true,
  lastUpdated: true,
  vite: {
    optimizeDeps: {
      // Mermaid loads these CommonJS modules dynamically; dev mode must convert them to ESM.
      include: ['fastdom', 'fastdom/extensions/fastdom-promised.js'],
    },
  },
  head: [['meta', { name: 'theme-color', content: '#285541' }]],
  themeConfig: {
    nav: [
      { text: '玩家指南', link: '/guide/getting-started' },
      { text: '开发指南', link: '/development/architecture' },
      { text: '插件开发', link: '/plugins/' },
      { text: '更新日志', link: '/reference/changelog' },
      { text: '下载', link: `${repo}/releases` },
    ],
    sidebar: [
      { text: '玩家指南', items: [
        { text: '安装与首次启动', link: '/guide/getting-started' },
        { text: '模型配置', link: '/guide/models' },
        { text: '游玩模式与控制权', link: '/guide/play-modes' },
        { text: '桌面页面说明', link: '/guide/interface' },
        { text: '农场工作与施工范围', link: '/reference/farm-projects' },
        { text: '插件中心', link: '/guide/plugins' },
        { text: '常见问题与日志', link: '/guide/faq' },
        { text: '完整使用说明', link: '/reference/desktop-guide' },
      ] },
      { text: '开发指南', collapsed: false, items: [
        { text: '架构与目录', link: '/development/architecture' },
        { text: '构建、测试与发布', link: '/reference/contributing' },
        { text: '文档站维护', link: '/reference/documentation' },
        { text: '游戏通信 v2', link: '/reference/game-protocol' },
        { text: '桌面 HTTP API v2', link: '/reference/desktop-api' },
        { text: '原生生产动作', link: '/reference/game-production' },
        { text: '空间观察与施工', link: '/reference/game-construction' },
        { text: '隔离实机回归', link: '/reference/acceptance' },
      ] },
      { text: '插件开发', collapsed: false, items: [
        { text: '接入概览', link: '/plugins/' },
        { text: '宿主协议 v1', link: '/reference/plugin-protocol' },
        { text: '在线目录与版本更新', link: '/reference/plugin-catalog' },
        { text: '参考插件', link: '/reference/plugin-examples' },
      ] },
    ],
    socialLinks: [{ icon: 'github', link: repo }],
    search: {
      provider: 'local',
      options: { locales: { root: { translations: {
        button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' },
        modal: { noResultsText: '没有找到相关内容', resetButtonTitle: '清除搜索',
          footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' } },
      } } } },
    },
    outline: { label: '本页目录', level: [2, 3] },
    docFooter: { prev: '上一页', next: '下一页' },
    lastUpdated: { text: '更新于' },
    darkModeSwitchLabel: '切换主题',
    darkModeSwitchTitle: '切换至深色主题',
    lightModeSwitchTitle: '切换至浅色主题',
    skipToContentLabel: '跳到正文',
    sidebarMenuLabel: '导航目录',
    returnToTopLabel: '返回顶部',
    footer: {
      message: `基于 Apache-2.0 许可开源 · <a href="${repo}/blob/dev/NOTICE">版权与致谢</a> · 非官方项目`,
      copyright: '由 nekoimi 和贡献者维护',
    },
  },
});
