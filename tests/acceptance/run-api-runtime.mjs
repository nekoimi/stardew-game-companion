import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createInterface } from "node:readline/promises";
import { setTimeout } from "node:timers/promises";

const [runtime, mod, evidence, ownerPid, provider, model, protocol, streamArg] = process.argv.slice(2);
const mock = process.env.STARDEW_ACCEPTANCE_SIMULATED === "1";
const secretNames = { deepseek: "DEEPSEEK_API_KEY", openai: "OPENAI_API_KEY",
  custom: "OPENAI_COMPATIBLE_API_KEY", "openai-compatible": "OPENAI_COMPATIBLE_API_KEY" };
const secretName = secretNames[provider];
const apiKey = mock ? "isolated-fixture" : process.env[secretName];
const custom = provider === "custom" || provider === "openai-compatible";
const baseUrl = custom ? (mock ? "https://gateway.invalid/fixture/v1" : process.env.OPENAI_COMPATIBLE_BASE_URL) : undefined;
const stream = streamArg === "true";
if (!runtime || !mod || !evidence || !Number(ownerPid) || !model || !apiKey
    || !Object.hasOwn(secretNames, provider) || (custom && !baseUrl)
    || !["chat-completions", "responses"].includes(protocol) || !["true", "false"].includes(streamArg)) {
  throw new Error("Invalid isolated model settings or missing API key.");
}
const token = randomBytes(32).toString("hex");
const stopFile = join(evidence, "agent-runtime-stop.txt");
const trace = join(evidence, "agent-runtime-events.jsonl");
const environment = { ...process.env, STARDEW_DESKTOP_TOKEN: token, STARDEW_BRIDGE_STOP_FILE: stopFile,
  STARDEW_AGENT_ACCEPTANCE_TRACE: trace };
for (const secret of Object.values(secretNames)) delete environment[secret];
delete environment.OPENAI_COMPATIBLE_BASE_URL;
delete environment.NODE_OPTIONS;
delete environment.STARDEW_MODEL_TEST_ONLY;
if (mock) environment.STARDEW_ACCEPTANCE_FIXTURE_PROTOCOL = protocol;
const entry = join(runtime, "dist", "bootstrap.js");
const options = mock ? ["--import", new URL("./preset-provider-preload.mjs", import.meta.url).href] : [];
if (process.env.STARDEW_ACCEPTANCE_TIMING === "1") {
  environment.STARDEW_ACCEPTANCE_TRANSPORT_METRICS = join(evidence, "model-transport-metrics.jsonl");
  options.push("--import", new URL("./model-transport-metrics.mjs", import.meta.url).href);
}
const child = spawn(process.execPath, ["--disable-warning=ExperimentalWarning", ...options, entry, "--desktop", mod, ownerPid], {
  env: environment, stdio: ["ignore", "pipe", "inherit"], windowsHide: true,
});
const output = createInterface({ input: child.stdout });
const exited = new Promise(resolve => child.once("close", resolve));
try {
  const handshake = await Promise.race([output[Symbol.asyncIterator]().next(),
    setTimeout(20_000, undefined, { ref: false })]);
  const endpoint = JSON.parse(handshake?.value ?? "{}");
  if (!Number.isInteger(endpoint.port) || endpoint.protocolVersion !== 2) throw new Error("No v2 runtime endpoint.");
  const selection = JSON.stringify({ selection: { provider, model, baseUrl, protocol, stream }, apiKey });
  for (const [operation, timeout] of [["test", stream ? 95_000 : 185_000], ["configure", 10_000]]) {
    const response = await fetch(`http://127.0.0.1:${endpoint.port}/v2/model/${operation}`, {
      method: "POST", signal: AbortSignal.timeout(timeout),
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: selection,
    });
    const result = await response.json();
    if (!response.ok || result.ready !== true || (operation === "test" && result.toolCalling !== true)) {
      const category = new Set(["authentication", "model-unavailable", "rate-limited", "network",
        "invalid-tool-response", "cancelled", "unsupported", "provider"]);
      const reason = category.has(result.code) ? result.code : `HTTP ${response.status}`;
      const message = `Model ${operation} failed for ${provider}/${model} (${reason}).`;
      await writeFile(join(evidence, "agent-startup-error.json"), JSON.stringify({ error: message }));
      throw new Error(message);
    }
  }
  await writeFile(join(evidence, "agent-runtime-ready.json"), JSON.stringify({ provider, model, protocol, stream, simulated: mock }));
  process.exitCode = (await exited) === 0 ? 0 : 1;
} finally {
  output.close();
  if (child.exitCode === null && child.signalCode === null) {
    await writeFile(stopFile, "stop");
    await Promise.race([exited, setTimeout(5_000, undefined, { ref: false })]);
    if (child.exitCode === null && child.signalCode === null) child.kill();
  }
}
