import { test } from "node:test";
import assert from "node:assert/strict";
import {
  handleRequest,
  createRateLimiter,
  trimHistory,
  DEFAULT_MODEL,
  MAX_TOKENS_CAP,
} from "../src/handler.js";

const ENV = { ANTHROPIC_API_KEY: "test-key", APP_AUTH_TOKEN: "test-app-token" };
const BASE = "https://proxy.example";

function chatRequest(body, { token = "test-app-token", ip = "1.2.3.4" } = {}) {
  const headers = { "content-type": "application/json", "cf-connecting-ip": ip };
  if (token !== null) headers["x-app-token"] = token;
  return new Request(`${BASE}/chat`, {
    method: "POST",
    headers,
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

function sseStream(events) {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const e of events) controller.enqueue(encoder.encode(e));
      controller.close();
    },
  });
}

/** Fake upstream fetch that records the request and returns a canned response. */
function fakeFetch(response) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init, body: JSON.parse(init.body) });
    return response();
  };
  fn.calls = calls;
  return fn;
}

const okUpstream = () =>
  new Response(sseStream(["event: message_start\ndata: {}\n\n", "event: message_stop\ndata: {}\n\n"]), {
    status: 200,
    headers: { "content-type": "text/event-stream" },
  });

test("GET /health returns ok", async () => {
  const res = await handleRequest(new Request(`${BASE}/health`), ENV, {});
  assert.equal(res.status, 200);
  assert.deepEqual(await res.json(), { ok: true });
});

test("unknown route returns 404", async () => {
  const res = await handleRequest(new Request(`${BASE}/nope`), ENV, {});
  assert.equal(res.status, 404);
});

test("/chat without token returns 401", async () => {
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }, { token: null }),
    ENV, {});
  assert.equal(res.status, 401);
  const body = await res.json();
  assert.equal(body.error.code, "unauthorized");
  assert.ok(body.error.speak.length > 0);
});

test("/chat with wrong token returns 401", async () => {
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }, { token: "wrong" }),
    ENV, {});
  assert.equal(res.status, 401);
});

test("/chat GET is rejected with 405", async () => {
  const res = await handleRequest(
    new Request(`${BASE}/chat`, { headers: { "x-app-token": "test-app-token" } }),
    ENV, {});
  assert.equal(res.status, 405);
});

test("valid /chat forwards to Anthropic with server-side headers and pipes SSE", async () => {
  const f = fakeFetch(okUpstream);
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "What is the capital of Australia?" }] }),
    ENV, { fetch: f });

  assert.equal(res.status, 200);
  assert.equal(res.headers.get("content-type"), "text/event-stream");

  // Upstream call carries the API key and version header — never the app token.
  assert.equal(f.calls.length, 1);
  const { init, body } = f.calls[0];
  assert.equal(init.headers["x-api-key"], "test-key");
  assert.equal(init.headers["anthropic-version"], "2023-06-01");
  assert.equal(init.headers["x-app-token"], undefined);
  assert.equal(body.model, DEFAULT_MODEL);
  assert.equal(body.stream, true);
  assert.equal(body.max_tokens, MAX_TOKENS_CAP);
  assert.ok(body.system.length > 0, "default voice system prompt applied");

  const text = await res.text();
  assert.ok(text.includes("message_start"));
  assert.ok(text.includes("message_stop"));
});

test("stream is piped incrementally, not buffered", async () => {
  let releaseSecond;
  const gate = new Promise((r) => { releaseSecond = r; });
  const encoder = new TextEncoder();
  const upstream = () => new Response(new ReadableStream({
    async start(controller) {
      controller.enqueue(encoder.encode("event: message_start\ndata: {}\n\n"));
      await gate; // second chunk held back until the test saw the first one
      controller.enqueue(encoder.encode("event: message_stop\ndata: {}\n\n"));
      controller.close();
    },
  }), { status: 200 });

  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }),
    ENV, { fetch: fakeFetch(upstream) });

  const reader = res.body.getReader();
  const first = await reader.read();
  assert.ok(new TextDecoder().decode(first.value).includes("message_start"),
    "first chunk arrives before upstream finished");
  releaseSecond();
  const second = await reader.read();
  assert.ok(new TextDecoder().decode(second.value).includes("message_stop"));
});

test("model not on allow-list returns 400", async () => {
  const res = await handleRequest(
    chatRequest({ model: "claude-opus-4-8", messages: [{ role: "user", content: "hi" }] }),
    ENV, { fetch: fakeFetch(okUpstream) });
  assert.equal(res.status, 400);
  assert.equal((await res.json()).error.code, "model_not_allowed");
});

test("allow-listed model is passed through", async () => {
  const f = fakeFetch(okUpstream);
  await handleRequest(
    chatRequest({ model: "claude-sonnet-4-6", messages: [{ role: "user", content: "hi" }] }),
    ENV, { fetch: f });
  assert.equal(f.calls[0].body.model, "claude-sonnet-4-6");
});

test("max_tokens is capped", async () => {
  const f = fakeFetch(okUpstream);
  await handleRequest(
    chatRequest({ max_tokens: 100000, messages: [{ role: "user", content: "hi" }] }),
    ENV, { fetch: f });
  assert.equal(f.calls[0].body.max_tokens, MAX_TOKENS_CAP);
});

test("history is trimmed to the last 20 messages and starts with user", async () => {
  const messages = [];
  for (let i = 0; i < 15; i++) {
    messages.push({ role: "user", content: `q${i}` });
    messages.push({ role: "assistant", content: `a${i}` });
  }
  messages.push({ role: "user", content: "final question" });

  const f = fakeFetch(okUpstream);
  await handleRequest(chatRequest({ messages }), ENV, { fetch: f });
  const sent = f.calls[0].body.messages;
  assert.ok(sent.length <= 20);
  assert.equal(sent[0].role, "user");
  assert.equal(sent[sent.length - 1].content, "final question");
});

test("trimHistory drops leading assistant messages after the cut", () => {
  const messages = [
    { role: "assistant", content: "a" },
    { role: "user", content: "u" },
  ];
  const trimmed = trimHistory(messages, 2);
  assert.equal(trimmed[0].role, "user");
});

test("empty messages array returns 400", async () => {
  const res = await handleRequest(chatRequest({ messages: [] }), ENV, {});
  assert.equal(res.status, 400);
});

test("illegal role returns 400", async () => {
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "system", content: "hi" }] }), ENV, {});
  assert.equal(res.status, 400);
});

test("last message must be from the user", async () => {
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }, { role: "assistant", content: "yo" }] }),
    ENV, {});
  assert.equal(res.status, 400);
});

test("oversized body returns 413", async () => {
  const big = JSON.stringify({ messages: [{ role: "user", content: "x".repeat(40 * 1024) }] });
  const res = await handleRequest(chatRequest(big), ENV, {});
  assert.equal(res.status, 413);
});

test("malformed JSON returns 400", async () => {
  const res = await handleRequest(chatRequest("{not json"), ENV, {});
  assert.equal(res.status, 400);
});

test("upstream 529 maps to structured speakable error", async () => {
  const f = fakeFetch(() => new Response(
    JSON.stringify({ type: "error", error: { type: "overloaded_error", message: "Overloaded" } }),
    { status: 529 }));
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }), ENV, { fetch: f });
  assert.equal(res.status, 529);
  const body = await res.json();
  assert.equal(body.error.code, "upstream_error");
  assert.match(body.error.speak, /overloaded/i);
});

test("upstream 500 maps to 502", async () => {
  const f = fakeFetch(() => new Response("boom", { status: 500 }));
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }), ENV, { fetch: f });
  assert.equal(res.status, 502);
});

test("network failure maps to 502", async () => {
  const f = async () => { throw new Error("connect refused"); };
  const res = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }), ENV, { fetch: f });
  assert.equal(res.status, 502);
  assert.equal((await res.json()).error.code, "upstream_unreachable");
});

test("rate limiter blocks the 31st request in a window", async () => {
  const limiter = createRateLimiter(30, 60_000);
  const deps = { fetch: fakeFetch(okUpstream), rateLimiter: limiter };
  let last;
  for (let i = 0; i < 31; i++) {
    last = await handleRequest(
      chatRequest({ messages: [{ role: "user", content: "hi" }] }), ENV, deps);
  }
  assert.equal(last.status, 429);
  // a different IP is unaffected
  const other = await handleRequest(
    chatRequest({ messages: [{ role: "user", content: "hi" }] }, { ip: "9.9.9.9" }), ENV, deps);
  assert.equal(other.status, 200);
});

test("rate limiter window resets", () => {
  const limiter = createRateLimiter(2, 1000);
  assert.equal(limiter.allow("ip", 0), true);
  assert.equal(limiter.allow("ip", 10), true);
  assert.equal(limiter.allow("ip", 20), false);
  assert.equal(limiter.allow("ip", 1500), true); // new window
});
