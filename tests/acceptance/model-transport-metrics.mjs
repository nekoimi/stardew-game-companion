import { appendFileSync } from "node:fs";

// Test-only instrumentation shared by frozen old and new packages. The clock
// covers HTTP request through response consumption, not native game execution.
// Never retain URL, headers, messages, tools, response text or credentials.
const destination = process.env.STARDEW_ACCEPTANCE_TRANSPORT_METRICS;
if (destination) {
  const original = globalThis.fetch;
  let sequence = 0;
  globalThis.fetch = async (input, init) => {
    let gameRequest = false;
    try {
      const body = typeof init?.body === "string" ? JSON.parse(init.body)
        : await new Request(input, init).clone().json();
      gameRequest = body.tools?.some(tool => (tool.name ?? tool.function?.name) === "submit_work") === true;
    } catch { /* Other network requests are outside this experiment. */ }
    if (!gameRequest) return original(input, init);
    const index = ++sequence, started = Date.now();
    let recorded = false;
    const record = status => {
      if (recorded) return;
      recorded = true;
      appendFileSync(destination, JSON.stringify({ index, startedAt: started, elapsedMs: Date.now() - started, status }) + "\n");
    };
    try {
      const response = await original(input, init);
      if (!response.body) { record(response.status); return response; }
      const reader = response.body.getReader();
      const body = new ReadableStream({
        async pull(controller) {
          try {
            const next = await reader.read();
            if (next.done) { record(response.status); controller.close(); }
            else controller.enqueue(next.value);
          } catch (error) { record("stream-error"); controller.error(error); }
        },
        async cancel(reason) { record("cancelled"); await reader.cancel(reason); },
      });
      return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
    } catch (error) { record("network-error"); throw error; }
  };
}
