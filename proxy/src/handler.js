// Quest Claude proxy — platform-agnostic request handler.
//
// The Anthropic API key lives ONLY in the deployment environment (env.ANTHROPIC_API_KEY).
// The headset authenticates with a revocable app token (env.APP_AUTH_TOKEN) and the
// proxy pipes Anthropic's SSE stream back verbatim, unbuffered.

const ANTHROPIC_URL = "https://api.anthropic.com/v1/messages";
const ANTHROPIC_VERSION = "2023-06-01";

export const DEFAULT_MODEL = "claude-haiku-4-5";
export const MODEL_ALLOW_LIST = [
  "claude-haiku-4-5",
  "claude-sonnet-4-6",
  "claude-sonnet-5",
];

export const MAX_TOKENS_CAP = 512;
const MAX_BODY_BYTES = 32 * 1024;
const MAX_SYSTEM_CHARS = 4 * 1024;
const MAX_HISTORY_MESSAGES = 20; // server-side backstop; the app also trims

export const RATE_LIMIT_WINDOW_MS = 60_000;
export const RATE_LIMIT_MAX = 30;

// Voice-optimised default system prompt: short spoken answers, no markup.
const DEFAULT_SYSTEM_PROMPT =
  "You are Claude, a spoken voice assistant running on a VR headset. " +
  "Reply conversationally and concisely, in one to three short sentences, unless the user " +
  "explicitly asks for more detail. Speak in plain prose: no markdown, no lists, no code " +
  "blocks, no emojis, no stage directions. If something can't be done by voice, say so briefly.";

/** Fixed-window per-IP rate limiter. In-memory: per Worker isolate, which is an
 *  acceptable best-effort guard for a single-user personal deployment. */
export function createRateLimiter(max = RATE_LIMIT_MAX, windowMs = RATE_LIMIT_WINDOW_MS) {
  const windows = new Map();
  return {
    allow(key, now = Date.now()) {
      const w = windows.get(key);
      if (!w || now - w.start >= windowMs) {
        windows.set(key, { start: now, count: 1 });
        return true;
      }
      w.count += 1;
      return w.count <= max;
    },
  };
}

function constantTimeEquals(a, b) {
  const enc = new TextEncoder();
  const ab = enc.encode(a);
  const bb = enc.encode(b);
  if (ab.length !== bb.length) return false;
  let diff = 0;
  for (let i = 0; i < ab.length; i++) diff |= ab[i] ^ bb[i];
  return diff === 0;
}

function jsonResponse(status, body) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

/** Structured error the app can show AND speak. */
function errorResponse(status, code, message, speak) {
  return jsonResponse(status, { error: { code, status, message, speak } });
}

function validateMessages(messages) {
  if (!Array.isArray(messages) || messages.length === 0) {
    return "messages must be a non-empty array";
  }
  for (const m of messages) {
    if (!m || typeof m !== "object") return "each message must be an object";
    if (m.role !== "user" && m.role !== "assistant") {
      return `illegal role "${m.role}" — must be "user" or "assistant"`;
    }
    if (typeof m.content !== "string" || m.content.trim().length === 0) {
      return "each message must have non-empty string content";
    }
  }
  if (messages[messages.length - 1].role !== "user") {
    return "the last message must have role \"user\"";
  }
  return null;
}

/** Keep the last N messages, then drop any leading assistant turns so the
 *  history always starts with a user message (Anthropic requires it). */
export function trimHistory(messages, maxMessages = MAX_HISTORY_MESSAGES) {
  let trimmed = messages.slice(-maxMessages);
  while (trimmed.length > 0 && trimmed[0].role !== "user") trimmed = trimmed.slice(1);
  return trimmed;
}

async function handleChat(request, env, deps) {
  const fetchImpl = deps?.fetch ?? fetch;

  // --- Auth ---
  const token = request.headers.get("x-app-token") ?? "";
  if (!env.APP_AUTH_TOKEN || !token || !constantTimeEquals(token, env.APP_AUTH_TOKEN)) {
    return errorResponse(401, "unauthorized", "Missing or invalid X-App-Token header.",
      "I'm not authorised. Check the app token.");
  }

  // --- Rate limit (per IP) ---
  const ip = request.headers.get("cf-connecting-ip")
    ?? request.headers.get("x-forwarded-for")
    ?? "unknown";
  if (deps?.rateLimiter && !deps.rateLimiter.allow(ip)) {
    return errorResponse(429, "rate_limited", "Too many requests from this address.",
      "Slow down a little — I'm getting too many requests.");
  }

  // --- Body validation ---
  const raw = await request.text();
  if (raw.length > MAX_BODY_BYTES) {
    return errorResponse(413, "body_too_large",
      `Request body exceeds ${MAX_BODY_BYTES} bytes.`,
      "That request was too long for me.");
  }
  let body;
  try {
    body = JSON.parse(raw);
  } catch {
    return errorResponse(400, "bad_json", "Request body is not valid JSON.",
      "Something went wrong with that request.");
  }

  const validationError = validateMessages(body.messages);
  if (validationError) {
    return errorResponse(400, "bad_messages", validationError,
      "Something went wrong with that request.");
  }

  // Model allow-list. Missing/empty model → default; unknown model → 400.
  let model = DEFAULT_MODEL;
  if (typeof body.model === "string" && body.model.length > 0) {
    if (!MODEL_ALLOW_LIST.includes(body.model)) {
      return errorResponse(400, "model_not_allowed",
        `Model "${body.model}" is not on the allow-list: ${MODEL_ALLOW_LIST.join(", ")}.`,
        "That model isn't available.");
    }
    model = body.model;
  }

  let system = DEFAULT_SYSTEM_PROMPT;
  if (typeof body.system === "string" && body.system.trim().length > 0) {
    system = body.system.slice(0, MAX_SYSTEM_CHARS);
  }

  const maxTokens = Math.min(
    Number.isInteger(body.max_tokens) && body.max_tokens > 0 ? body.max_tokens : MAX_TOKENS_CAP,
    MAX_TOKENS_CAP,
  );

  const upstreamBody = {
    model,
    max_tokens: maxTokens,
    system,
    stream: true,
    messages: trimHistory(body.messages),
  };

  let upstream;
  try {
    upstream = await fetchImpl(ANTHROPIC_URL, {
      method: "POST",
      headers: {
        "x-api-key": env.ANTHROPIC_API_KEY,
        "anthropic-version": ANTHROPIC_VERSION,
        "content-type": "application/json",
      },
      body: JSON.stringify(upstreamBody),
    });
  } catch {
    return errorResponse(502, "upstream_unreachable", "Could not reach the Anthropic API.",
      "I'm having trouble reaching my brain right now.");
  }

  if (!upstream.ok) {
    // Map upstream failures to a structured, speakable error. Pass through
    // retry-relevant statuses (429/529); collapse the rest to 502.
    const status = upstream.status === 429 || upstream.status === 529 ? upstream.status : 502;
    let detail = `Anthropic API returned ${upstream.status}.`;
    try {
      const errJson = await upstream.json();
      if (errJson?.error?.message) detail = errJson.error.message;
    } catch { /* keep generic detail */ }
    const speak = upstream.status === 429 || upstream.status === 529 || upstream.status >= 500
      ? "I'm a bit overloaded, try again in a moment."
      : "I'm having trouble reaching my brain right now.";
    return errorResponse(status, "upstream_error", detail, speak);
  }

  // Pipe the SSE stream back verbatim — no buffering, or the latency win is lost.
  return new Response(upstream.body, {
    status: 200,
    headers: {
      "content-type": "text/event-stream",
      "cache-control": "no-cache",
      // Hint to intermediaries not to buffer the stream.
      "x-accel-buffering": "no",
    },
  });
}

/** Route a request. `deps` allows tests (and the worker) to inject fetch + rate limiter. */
export async function handleRequest(request, env, deps) {
  const url = new URL(request.url);

  if (url.pathname === "/health") {
    return jsonResponse(200, { ok: true });
  }

  if (url.pathname === "/chat") {
    if (request.method !== "POST") {
      return errorResponse(405, "method_not_allowed", "Use POST for /chat.",
        "Something went wrong with that request.");
    }
    return handleChat(request, env, deps);
  }

  return errorResponse(404, "not_found", `No route for ${url.pathname}.`,
    "Something went wrong with that request.");
}
