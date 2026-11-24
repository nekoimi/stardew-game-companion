import { readFileSync } from "node:fs";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";

const marker = "<!-- stardew-companion-update-catalog-v1 -->";
const pages = JSON.parse(readFileSync(0, "utf8"));
const releases = pages.flat().flatMap((release) => {
  if (release.draft || !release.published_at || !release.body?.includes(marker)) return [];

  const archive = release.assets.find((asset) =>
    asset.state === "uploaded" && /^StardewGameCompanion-.+-windows-x64\.zip$/.test(asset.name));
  const sha256 = /^sha256:([a-f\d]{64})$/i.exec(archive?.digest)?.[1];
  if (!sha256 || !archive.browser_download_url?.startsWith("https://")) {
    throw new Error(`Release ${release.tag_name} is missing a downloadable ZIP with a SHA256 digest.`);
  }
  return [{
    tag: release.tag_name,
    channel: release.prerelease ? "preview" : "stable",
    publishedAt: release.published_at,
    url: archive.browser_download_url,
    sha256,
    notes: release.body.replace(marker, "").trim().slice(0, 2000),
  }];
});

releases.sort((left, right) => right.publishedAt.localeCompare(left.publishedAt));
const output = process.argv[2];
await mkdir(dirname(output), { recursive: true });
await writeFile(output, JSON.stringify({ schemaVersion: 1, releases }, null, 2) + "\n");
