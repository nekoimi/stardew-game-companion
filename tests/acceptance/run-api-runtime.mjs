import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createInterface } from "node:readline/promises";
import { setTimeout } from "node:timers/promises";

const [runtime, mod, evidence, ownerPid, provider, model] = process.argv.slice(2);
const mock = process.env.STARDEW_ACCEPTANCE_SIMULATED === "1";
const secretName = provider === "openai" ? "OPENAI_API_KEY" : "DEEPSEEK_API_KEY";
const apiKey = mock ? "isolated-fixture" : process.env[secretName];
if (!runtime || !mod || !evidence || !Number(ownerPid) || !model || !apiKey
    || !["deepseek", "openai"].includes(provider)) {
  throw new Error("Invalid isolated model settings or missing API key.");
}
const token = randomBytes(32).toString("hex");
const stopFile = join(evidence, "agent-runtime-stop.txt");
const trace = join(evidence, "agent-runtime-events.jsonl");
const environment = { ...process.env, STARDEW_DESKTOP_TOKEN: token, STARDEW_BRIDGE_STOP_FILE: stopFile,
  STARDEW_AGENT_ACCEPTANCE_TRACE: trace };
delete environment.DEEPSEEK_API_KEY;
delete environment.OPENAI_API_KEY;
delete environment.NODE_OPTIONS;
delete environment.STARDEW_MODEL_TEST_ONLY;
const entry = join(runtime, "dist", "bootstrap.js");
const options = mock ? ["--import", new URL("./preset-provider-preload.mjs", import.meta.url).href] : [];
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
  const response = await fetch(`http://127.0.0.1:${endpoint.port}/v2/model/configure`, {
    method: "POST", signal: AbortSignal.timeout(10_000),
    headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
    body: JSON.stringify({ selection: { provider, model }, apiKey }),
  });
  if (!response.ok) throw new Error("The v2 runtime refused the model configuration.");
  await writeFile(join(evidence, "agent-runtime-ready.json"), JSON.stringify({ provider, model, simulated: mock }));
  process.exitCode = (await exited) === 0 ? 0 : 1;
} finally {
  output.close();
  if (child.exitCode === null && child.signalCode === null) {
    await writeFile(stopFile, "stop");
    await Promise.race([exited, setTimeout(5_000, undefined, { ref: false })]);
    if (child.exitCode === null && child.signalCode === null) child.kill();
  }
}
