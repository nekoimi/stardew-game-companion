import { createServer } from "node:http";

export async function createMockOpenAI(plan) {
  const requests = [];
  const server = createServer(async (req, res) => {
    const body = JSON.parse(Buffer.concat(await Array.fromAsync(req)).toString());
    requests.push(body);
    const id = requests.length;
    res.writeHead(200, { "Content-Type": "text/event-stream" });
    const send = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
    const toolOutput = body.input.findLast(item => item.type === "function_call_output");
    const probe = body.tools?.some(item => item.name === "check_connection");
    const text = probe && toolOutput ? JSON.parse(toolOutput.output).verificationCode : "已通过 OpenAI 回复。";
    const next = probe ? { name: toolOutput ? undefined : "check_connection" }
      : plan ? plan(body) : { name: toolOutput ? undefined : "get_status" };
    const item = next.name
      ? { type: "function_call", id: `fc_fixture_${id}`, call_id: `call_fixture_${id}`,
          name: next.name, arguments: JSON.stringify(next.args ?? {}), status: "completed" }
      : { type: "message", id: `msg_fixture_${id}`, role: "assistant", status: "completed",
          content: [{ type: "output_text", text, annotations: [] }] };
    send("response.created", { response: { id: `resp_fixture_${id}`, status: "in_progress", output: [] } });
    if (item.type === "function_call") {
      send("response.output_item.added", { output_index: 0, item: { ...item, arguments: "", status: "in_progress" } });
      const split = Math.floor(item.arguments.length / 2);
      for (const delta of [item.arguments.slice(0, split), item.arguments.slice(split)]) {
        send("response.function_call_arguments.delta", { output_index: 0, item_id: item.id, delta });
      }
    } else {
      send("response.output_item.added", { output_index: 0, item });
      send("response.output_text.delta", { output_index: 0, delta: item.content[0].text });
    }
    if (next.onResponseClose) { res.once("close", next.onResponseClose); return; }
    send("response.output_item.done", { output_index: 0, item });
    send("response.completed", { response: { id: `resp_fixture_${id}`, model: body.model, status: "completed", output: [item],
      usage: { input_tokens: 12, output_tokens: 4, total_tokens: 16,
        input_tokens_details: { cached_tokens: 2 }, output_tokens_details: { reasoning_tokens: 0 } } } });
    res.end();
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  return { address: `http://127.0.0.1:${server.address().port}`, requests, unref: () => server.unref(),
    close: () => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }) };
}

export function redirectOpenAI(address) {
  const original = globalThis.fetch;
  globalThis.fetch = (input, options) => {
    const request = new Request(input, options);
    const url = new URL(request.url);
    if (url.hostname !== "api.openai.com") return original(input, options);
    return original(new Request(address + url.pathname + url.search, request));
  };
  return () => { globalThis.fetch = original; };
}
