import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const site = fileURLToPath(new URL('..', import.meta.url));
const repo = resolve(site, '../..');
const github = 'https://github.com/nekoimi/stardew-game-companion/blob/dev/';

// Explicit publication list: design notes, TODOs and local evidence never enter the site.
const sources = {
  'docs/development/desktop-portable.md': 'desktop-guide',
  'docs/development/farm-projects.md': 'farm-projects',
  'CONTRIBUTING.md': 'contributing',
  'docs/site/README.md': 'documentation',
  'CHANGELOG.md': 'changelog',
  'contracts/game-bridge/v2.md': 'game-protocol',
  'contracts/game-bridge/production-chain.md': 'game-production',
  'contracts/game-bridge/spatial-construction.md': 'game-construction',
  'contracts/desktop-api/v2.md': 'desktop-api',
  'contracts/plugin-host/v1.md': 'plugin-protocol',
  'contracts/plugin-host/catalog-v1.md': 'plugin-catalog',
  'examples/plugins/README.md': 'plugin-examples',
  'docs/development/repeatable-companion-acceptance.md': 'acceptance',
};

function rewriteLink(target, source) {
  const website = 'https://nekoimi.github.io/stardew-game-companion/';
  if (target.startsWith(website)) return '/' + target.slice(website.length);
  if (/^(?:[a-z][a-z\d+.-]*:|\/|#)/i.test(target)) return target;
  const split = target.search(/[?#]/);
  const path = split < 0 ? target : target.slice(0, split);
  const suffix = split < 0 ? '' : target.slice(split);
  const key = relative(repo, resolve(repo, dirname(source), decodeURI(path))).replaceAll('\\', '/');
  if (key === 'docs/site/public/desktop-home.png') return `/desktop-home.png${suffix}`;
  if (sources[key]) return `/reference/${sources[key]}${suffix}`;
  return `${github}${key.split('/').map(encodeURIComponent).join('/')}${suffix}`;
}

mkdirSync(resolve(site, 'reference'), { recursive: true });
for (const [source, page] of Object.entries(sources)) {
  let markdown = readFileSync(resolve(repo, source), 'utf8');
  markdown = markdown.replace(/(!?\[[^\]\n]*\]\()([^\s)]+)(\))/g,
    (_, opening, target, closing) => opening + rewriteLink(target, source) + closing);
  const notice = `::: info 文档来源\n本页构建时读取[仓库原文](${github}${source})，修改请直接编辑原文件。\n:::\n\n`;
  writeFileSync(resolve(site, `reference/${page}.md`), notice + markdown);
}
console.log(`Prepared ${Object.keys(sources).length} reference pages.`);
