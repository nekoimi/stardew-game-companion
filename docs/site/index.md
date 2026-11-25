---
layout: home
hero:
  name: 星露谷游戏伴侣
  text: 和 AI 一起照料农场
  tagline: 用自然语言商量农活、推进目标，或把单人角色交给 AI。解压启动，配置模型，即可开始。
  actions:
    - theme: brand
      text: 开始使用
      link: /guide/getting-started
    - theme: alt
      text: 下载 Windows 绿色包
      link: https://github.com/nekoimi/stardew-game-companion/releases
    - theme: alt
      text: GitHub 源码
      link: https://github.com/nekoimi/stardew-game-companion
features:
  - title: 自然语言交代工作
    details: 一次规划完整工作阶段，连续完成采木、收集和入库；日常照料缺水自动补水。长期目标和当天计划分开保留，可以随时暂停或改派。
  - title: 伙伴帮忙，也能交给 AI
    details: 独立伙伴拥有自己的背包、工具和体力；AI 自玩模式可以随时按 F9 接管。
  - title: 模型由你选择
    details: 接入 DeepSeek、OpenAI 或自定义 Chat Completions / Responses 接口，支持流式与非流式响应。
  - title: 插件扩展
    details: 在插件中心管理第三方功能，通过通用协议订阅状态、提交指令和查询结果。
---

<script setup>
import { withBase } from 'vitepress';
</script>

<div class="home-preview">
  <img :src="withBase('/desktop-home.png')" alt="星露谷游戏伴侣启动后的今天页面，包含对话区、侧边导航和 Agent 状态" width="1066" height="738" />
  <p>当前桌面程序的实际启动界面。项目处于 0.x 开发阶段，主要支持 Windows x64 单人农场协作。</p>
</div>
