import { createServer } from "node:http";

export async function createMockOpenAI() {
  const requests = [];
  const server = createServer(async (req, res) => {
    const body = JSON.parse(Buffer.concat(await Array.fromAsync(req)).toString());
    requests.push(body);
    res.writeHead(200, { "Content-Type": "text/event-stream" });
    const send = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
    const isContinuation = body.input.some(item => item.type === "function_call_output");
    const probe = body.tools?.some(item => item.name === "check_connection");
    const item = isContinuation
      ? { type: "message", id: "msg_fixture", role: "assistant", status: "completed",
          content: [{ type: "output_text", text: "已通过 OpenAI 回复。", annotations: [] }] }
      : { type: "function_call", id: "fc_fixture", call_id: "call_fixture",
          name: probe ? "check_connection" : "get_status", arguments: "{}", status: "completed" };
    send("response.created", { response: { id: "resp_fixture", status: "in_progress", output: [] } });
    send("response.output_item.added", { output_index: 0, item });
    if (item.type === "message") send("response.output_text.delta", { output_index: 0, delta: item.content[0].text });
    send("response.output_item.done", { output_index: 0, item });
    send("response.completed", { response: { id: "resp_fixture", model: body.model, status: "completed", output: [item],
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
