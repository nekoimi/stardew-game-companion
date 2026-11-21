import { PI_AI_VERSION } from "@earendil-works/pi-ai";
import { deepseekProvider } from "@earendil-works/pi-ai/providers/deepseek";
import { DatabaseSync } from "node:sqlite";
import { startDesktopService } from "./adapters/desktop/desktop-service.js";
import { listModelProviders } from "./adapters/model/model-catalogue.js";

export function checkRuntime(): { node: string; sqlite: string; sdk: string } {
  const database = new DatabaseSync(":memory:");
  try {
    database.exec("CREATE TABLE probe (value TEXT NOT NULL)");
    database.prepare("INSERT INTO probe (value) VALUES (?)").run("星露谷");
    if (database.prepare("SELECT value FROM probe").get()?.value !== "星露谷") {
      throw new Error("Bundled SQLite failed to round-trip text");
    }
    const sqlite = database.prepare("SELECT sqlite_version() AS version").get()?.version;
    if (typeof sqlite !== "string") throw new Error("Bundled SQLite returned no version");
    if (deepseekProvider().getModels().length === 0) throw new Error("DeepSeek model catalogue is empty");
    return { node: process.version, sqlite, sdk: PI_AI_VERSION };
  } finally {
    database.close();
  }
}

if (process.argv[2] === "--check") {
  process.stdout.write(JSON.stringify(checkRuntime()) + "\n");
} else if (process.argv[2] === "--catalog") {
  process.stdout.write(JSON.stringify(listModelProviders()) + "\n");
} else if (process.argv[2] === "--desktop") {
  const runDirectory = process.argv[3];
  if (!runDirectory) throw new Error("Missing Mod directory");
  await startDesktopService(runDirectory, process.env.STARDEW_DESKTOP_TOKEN ?? "",
    process.env.STARDEW_BRIDGE_STOP_FILE ?? "", Number(process.argv[4]), process.env.STARDEW_MODEL_TEST_ONLY === "1");
}
