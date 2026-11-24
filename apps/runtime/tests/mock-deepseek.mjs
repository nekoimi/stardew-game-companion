import { createServer } from "node:http";

/** The mock speaks the provider's SSE protocol and never forwards traffic. */
export async function createMockDeepSeek(plan) {
  const calls = [];
  const server = createServer(async (req, res) => {
    const body = JSON.parse(Buffer.concat(await Array.fromAsync(req)).toString());
    const key = req.headers.authorization;
    calls.push(body);
    if (key === "Bearer accept-quota") {
      res.writeHead(429, { "Content-Type": "application/json" });
      return res.end(JSON.stringify({ error: { code: "insufficient_quota", message: "fixture response must stay private" } }));
    }
    if (key === "Bearer accept-401" || key === "Bearer accept-429" || key === "Bearer accept-404") {
      const status = Number(key.slice(-3));
      res.writeHead(status, { "Content-Type": "application/json" });
      return res.end(JSON.stringify({ error: { message: "fixture response must stay private" } }));
    }
    if (key === "Bearer reject-stream") {
      res.writeHead(400, { "Content-Type": "application/json" });
      return res.end(JSON.stringify({ error: { message: "stream mode is unsupported" } }));
    }
    const chunk = (delta, finish = null, usage) => {
      if (!res.headersSent) res.writeHead(200, { "Content-Type": "text/event-stream" });
      res.write(`data: ${JSON.stringify({ id: "chatcmpl-local", object: "chat.completion.chunk",
        created: 1, model: body.model, choices: usage ? [] : [{ index: 0, delta, finish_reason: finish }], usage })}\n\n`);
    };
    if (key === "Bearer accept-drop") {
      chunk({ role: "assistant", content: "未完成" });
      return res.socket.destroy();
    }
    const last = body.messages.at(-1);
    const user = String(body.messages.findLast(item => item.role === "user")?.content ?? "");
    const latestTool = body.messages.findLast(item => item.role === "tool");
    if (user.includes("等待接管")) {
      if (body.stream !== false) chunk({ role: "assistant", content: "" });
      await new Promise(resolve => { const timer = setTimeout(resolve, 15000); res.on("close", () => { clearTimeout(timer); resolve(); }); });
      if (res.destroyed) return;
    }
    let name, args = {};
    const probe = body.tools?.some(tool => tool.function.name === "check_connection");
    if (probe && !latestTool) name = "check_connection";
    else if (probe) {
      const result = JSON.parse(latestTool.content);
      if (body.stream === false) return sendComplete(null, result.verificationCode ?? "");
      chunk({ role: "assistant", content: result.verificationCode ?? "" });
      chunk({}, "stop");
      return res.end("data: [DONE]\n\n");
    }
    else if (plan) ({ name, args } = plan(body));
    else if (user === "等到明天再确认农场情况") {
      if (last.role === "user") {
        name = "manage_arrangements";
        args = { change: { manage_goal: { action: "add", text: user } } };
      } else if (body.messages.findLast(item => item.tool_calls)?.tool_calls[0].function.arguments.includes('"manage_goal"')) {
        name = "manage_arrangements";
        args = { change: { set_goal_continuation: { status: "wait", reason: "等到明天再确认农场情况", next_day: true } } };
      }
    }
    else if (user === "现在回床睡觉") {
      if (last.role === "user") { name = "submit_work"; args = { action: { sleep: {} } }; }
    }
    else if (user.startsWith("继续当前目标：等到明天再确认农场情况")) {
      if (last.role === "user") name = "get_status";
      else if (body.messages.findLast(item => item.tool_calls)?.tool_calls[0].function.name === "get_status") {
        name = "manage_arrangements";
        args = { change: { manage_goal: { action: "complete", summary: "已读取次日农场观察" } } };
      }
    }
    else if (user.includes("记住今日目标") && last.role === "user") {
      name = "manage_arrangements";
      args = { change: { manage_goal: { action: "add", text: "今天先照料田地", summary: "等玩家交代范围" } } };
    }
    else if (user.includes("请向我确认是否留种") && last.role === "user") {
      name = "request_player_decision";
      args = { message: "这批种子要留种吗？", choices: ["留种", "出货"] };
    }
    else if (last.role === "user") name = "get_status";
    else if (latestTool && body.messages.findLast(item => item.tool_calls)?.tool_calls[0].function.name === "get_status") {
      const observed = JSON.parse(latestTool.content).world.companion;
      name = "submit_work";
      args = { action: { navigate_to: { location_id: observed.locationId,
        tile: user.includes("走到出口") ? { x: 3, y: 9 } : { x: observed.tileX - 1, y: observed.tileY } } } };
    }
    function sendComplete(call, text) {
      const usage = key === "Bearer accept-no-usage" ? undefined : { prompt_tokens: 12, completion_tokens: 6, total_tokens: 18 };
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ id: "chatcmpl-local", object: "chat.completion", model: body.model,
        choices: [{ index: 0, finish_reason: call ? "tool_calls" : "stop",
          message: { role: "assistant", content: text, tool_calls: call ? [call] : undefined } }], usage }));
    }
    if (body.stream === false) {
      return sendComplete(name ? { id: "call_" + calls.length, type: "function",
        function: { name, arguments: key === "Bearer invalid-arguments" ? "{broken" : JSON.stringify(args) } } : null,
      name ? null : "收到游戏回执。");
    }
    if (name) {
      const content = JSON.stringify(args);
      const index = Math.floor(content.length / 2);
      chunk({ role: "assistant", tool_calls: [{ index: 0, id: "call_" + calls.length, type: "function",
        function: { name, arguments: content.slice(0, index) } }] });
      if (key === "Bearer interrupted-tool") return res.end();
      chunk({ tool_calls: [{ index: 0, function: { arguments: content.slice(index) } }] });
      chunk({}, "tool_calls");
    } else {
      chunk({ role: "assistant", content: "收到游戏回执。" });
      chunk({}, "stop");
    }
    if (key !== "Bearer accept-no-usage") chunk({}, null, { prompt_tokens: 12, completion_tokens: 6, total_tokens: 18 });
    res.end("data: [DONE]\n\n");
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  return { address: `http://127.0.0.1:${server.address().port}`, calls,
    unref: () => server.unref(),
    close: () => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }) };
}

export function redirectDeepSeek(address) {
  const original = globalThis.fetch;
  globalThis.fetch = (input, options) => {
    const request = new Request(input, options);
    const url = new URL(request.url);
    if (url.hostname !== "api.deepseek.com") throw new Error("Test attempted to contact an external endpoint");
    return original(new Request(address + url.pathname + url.search, request));
  };
  return () => { globalThis.fetch = original; };
}
