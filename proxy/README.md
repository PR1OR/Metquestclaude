# Quest Claude — Proxy Backend

A tiny streaming proxy between the Quest headset app and the Anthropic Messages API
(guide §4). Its whole job:

- **Hold the Anthropic API key server-side.** The APK never contains it.
- Authenticate the app via a revocable `X-App-Token` header.
- Enforce guardrails: per-IP rate limit (30 req/min), `max_tokens` cap (512),
  model allow-list, 32 KB body cap, 20-message history backstop.
- Forward `/chat` to `POST https://api.anthropic.com/v1/messages` with `stream: true`
  and **pipe the SSE stream back verbatim, unbuffered**.
- Return structured, *speakable* errors: every error body includes
  `error.speak` — a sentence the headset can read aloud.

Primary target is **Cloudflare Workers** (native response streaming, free tier is
plenty for one user). `src/handler.js` is platform-agnostic (standard
`Request`/`Response`), so porting to Vercel Edge Functions is a ~5-line wrapper.

## Endpoints

| Method & path | Auth | Purpose |
|---|---|---|
| `POST /chat` | `X-App-Token` header | Streamed conversation. Body: `{"messages":[{"role":"user"...}], "system"?: "...", "model"?: "<allow-listed>", "max_tokens"?: n}` → `text/event-stream` (Anthropic SSE events piped through). |
| `GET /health` | none | `200 {"ok":true}` — drives the headset's connectivity dot. |

Defaults: model `claude-haiku-4-5` (allow-list also permits `claude-sonnet-4-6`
and `claude-sonnet-5`), `max_tokens` 512, and a voice-optimised system prompt
(short spoken sentences, no markdown/lists/emoji). An empty or missing
`model`/`system` in the request body falls back to these defaults.

## Deploy (human steps)

```sh
cd proxy
npm install -g wrangler          # once
wrangler login                   # once
wrangler secret put ANTHROPIC_API_KEY   # from console.anthropic.com
wrangler secret put APP_AUTH_TOKEN      # e.g. `openssl rand -hex 32` — also goes in the Unity AssistantConfig
wrangler deploy
```

Note the deployed URL (e.g. `https://questclaude-proxy.<you>.workers.dev`) — it goes
into the Unity `AssistantConfig` asset along with the app token.

Key rotation (guide §8.5): rotating the **Anthropic key** is
`wrangler secret put ANTHROPIC_API_KEY` again — zero app rebuild. Rotating the
**app token** requires updating the Unity config (or the in-app settings field).

## Verify after deploying

```sh
# No token → 401
curl -i https://<your-worker>/chat -X POST -d '{"messages":[{"role":"user","content":"hi"}]}'

# Health
curl https://<your-worker>/health

# Streaming chat — you should see SSE deltas arrive incrementally
curl -N https://<your-worker>/chat -X POST \
  -H 'content-type: application/json' \
  -H 'x-app-token: <APP_AUTH_TOKEN>' \
  -d '{"messages":[{"role":"user","content":"What is the capital of Australia?"}]}'
```

## Test & local dev

```sh
npm test                                  # 22 unit tests, no network needed
cp .dev.vars.example .dev.vars            # fill in real values (git-ignored)
wrangler dev                              # local server on :8787
```

## Logging policy

Cloudflare's default request logs carry timestamps/status only. Do **not** add
`console.log` of message content — guide §8.4 forbids logging transcripts.
