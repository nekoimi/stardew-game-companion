import assert from "node:assert/strict";
import { once } from "node:events";
import { GameConnection } from "../../apps/runtime/dist/adapters/game/game-connection.js";

const game = new GameConnection(process.argv[2]);
const signal = AbortSignal.timeout(8000);
game.start();
try {
  while (!game.binding) await once(game, "changed", { signal });
  assert.deepEqual(game.binding, { saveId: "save-123", gameSessionId: "session-456" });
  assert.equal(game.state.recoveryPending, false);
} finally { game.stop(); }
