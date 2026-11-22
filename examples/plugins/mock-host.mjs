import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, readFile, mkdir, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";

const fixture = JSON.parse(await readFile(new URL("../../contracts/plugin-host/examples/v1.json", import.meta.url)));
const sessionId = fixture.command.request.sessionId;
const intermediate = "10";
const template = fixture.command.result;
const dataRoot = await mkdtemp(join(tmpdir(), "companion-sample-host-"));
const plugins = new Map([["command", {}], ["events", {}]]);
const submissions = new Map();
const clients = [];
let settingsUrl;
let stopping = false;
let subscriptions = 0;
let submitCount = 0;

const server = createServer((request, response) => {
  handle(request, response).catch(error => {
    response.destroy();
    console.error(error.message);
  });
});

try {
  await new Promise((ready, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", ready); });
  const address = "http://127.0.0.1:" + server.address().port;
  for (const [kind] of plugins) {
    const manifest = JSON.parse(await readFile(resolve(process.argv[2], kind, "plugin.json")));
    const directory = join(dataRoot, kind);
    await mkdir(directory);
    const executable = resolve(process.argv[2], kind, manifest.entry.executable);
    const child = spawn(executable, manifest.entry.args, {
      windowsHide: true,
      env: { ...process.env, STARDEW_PLUGIN_HOST_URL: address, STARDEW_PLUGIN_TOKEN: kind,
        STARDEW_PLUGIN_DATA_DIR: directory, STARDEW_PLUGIN_LOG_DIR: directory }
    });
    child.on("error", error => console.error(error.message));
    clients.push(child);
  }
  await waitFor(() => settingsUrl && plugins.get("command").state === fixture.configuration.state && subscriptions > 0);

  const settings = await fetch(settingsUrl).then(reply => reply.text());
  const csrf = /name="csrf" value="([a-f0-9]+)"/.exec(settings)?.[1];
  assert(csrf, "sample settings form");
  async function apply(intent, text = "") {
    const result = await fetch(settingsUrl, { method: "POST", redirect: "manual",
      body: new URLSearchParams({ csrf, intent, text }) });
    assert.equal(result.status, 303);
    return fetch(settingsUrl).then(reply => reply.text());
  }
  assert.match(await apply("save", fixture.command.request.text), /指令已保存/);
  assert.match(await apply("submit"), /partial/);
  assert.match(await apply("retry"), /partial/);
  assert.match(await apply("query"), /partially-succeeded/);
  assert.equal(submitCount, 2);
  assert.equal(submissions.size, 1);
  assert.equal(plugins.get("command").state, "ready");
  await waitFor(async () => {
    try {
      const file = await readFile(join(dataRoot, "events", "events.jsonl"), "utf8");
      const rows = file.trim().split("\n").map(line => JSON.parse(line));
      return rows.filter(row => row.type === "agent.reply.completed").length === 1
        && rows.some(row => row.type === "agent.action.completed" && row.payload.completedCount === 1);
    } catch { return false; }
  });
  stopping = true;
  await Promise.all(clients.map(async child => {
    const [code] = child.exitCode == null ? await once(child, "exit") : [child.exitCode];
    assert.equal(code, 0);
  }));
  console.log("Go command and C# event samples passed the shared v1 host fixture.");
} finally {
  for (const child of clients) if (child.exitCode == null) child.kill();
  server.closeAllConnections();
  server.close();
  await rm(dataRoot, { recursive: true, force: true });
}

async function waitFor(check) {
  for (let attempt = 0; attempt < 120; attempt++) {
    if (await check()) return;
    if (clients.some(child => child.exitCode != null)) throw new Error("A sample exited before acceptance finished.");
    await delay(100);
  }
  throw new Error("Timed out waiting for sample activity.");
}

async function handle(request, response) {
  const plugin = request.headers.authorization?.slice("Bearer ".length);
  const registration = plugins.get(plugin);
  const json = (status, payload) => {
    response.writeHead(status, { "content-type": "application/json" }).end(JSON.stringify(payload));
  };
  if (!registration) return json(401, fixture.expiredToken);
  const url = new URL(request.url, "http://127.0.0.1");
  let data;
  if (request.method === "POST") {
    const chunks = [];
    for await (const chunk of request) chunks.push(chunk);
    data = JSON.parse(Buffer.concat(chunks).toString());
  }
  if (url.pathname === "/v1/hello") {
    assert.equal(data.protocolMin, 1);
    assert.equal(data.protocolMax, 1);
    if (plugin === "command") settingsUrl = data.settingsUrl;
    return json(200, { ...fixture.hello.response, sessionId, heartbeatIntervalMs: 1000 });
  }
  if (url.pathname === "/v1/heartbeat") {
    registration.state = data.state;
    return json(200, { directive: stopping ? "stop" : "continue", sessionId });
  }
  if (url.pathname === "/v1/state")
    return json(200, { sessionId, cursor: intermediate, game: { connected: true }, agent: { phase: "idle" } });
  if (url.pathname === "/v1/events" && plugin === "events") {
    assert.equal(url.searchParams.get("sessionId"), sessionId);
    const start = Number(request.headers["last-event-id"] ?? intermediate);
    subscriptions++;
    response.writeHead(200, { "content-type": "text/event-stream" });
    const frame = (id, event, payload) => {
      if (id > start) response.write(
        "id: " + id + "\nevent: " + event + "\ndata: " + JSON.stringify({ sessionId, payload }) + "\n\n");
    };
    frame(11, "agent.action.completed", template.actions[0]);
    const reply = { messageId: "reply-1", text: "收到。已确认浇水一格。" };
    frame(12, "agent.reply.completed", reply);
    frame(13, "agent.reply.completed", reply);
    return;
  }
  if (url.pathname === "/v1/commands" && plugin === "command") {
    assert.equal(data.sessionId, sessionId);
    assert.equal(data.text, fixture.command.request.text);
    submitCount++;
    const result = { ...template, requestId: data.requestId };
    const status = submissions.has(data.requestId) ? 200 : 202;
    submissions.set(data.requestId, result);
    return json(status, result);
  }
  if (url.pathname.startsWith("/v1/commands/") && plugin === "command") {
    const result = submissions.get(url.pathname.split("/").at(-1));
    return result ? json(200, result) : json(404, { code: "not_found" });
  }
  return json(404, { code: "not_found" });
}
