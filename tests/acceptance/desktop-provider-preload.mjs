import { createMockDeepSeek } from "../../apps/runtime-ts/tests/mock-deepseek.mjs";
import { createMockOpenAI } from "../../apps/runtime-ts/tests/mock-openai.mjs";

const deepseek = await createMockDeepSeek();
const openai = await createMockOpenAI();
const addresses = new Map([["api.deepseek.com", deepseek.address], ["api.openai.com", openai.address]]);
const fetchLocal = globalThis.fetch;
globalThis.fetch = (input, options) => {
  const request = new Request(input, options);
  const url = new URL(request.url);
  const address = addresses.get(url.hostname);
  if (!address) throw new Error("Desktop fixture attempted to contact an external endpoint");
  return fetchLocal(new Request(address + url.pathname + url.search, request));
};
deepseek.unref();
openai.unref();
