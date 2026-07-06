# CLAUDE.md

Context for future Claude sessions. See `HANDOVER.md` for the full picture and
`docs/BUILD_GUIDE.md` for the spec.

## What this is

- **Quest Claude:** a hands-free voice Claude assistant for the Meta Quest 3.
- Loop: wake phrase → dictation (Wit.ai STT) → proxy → Claude (streamed) → Wit TTS.
- Two parts: `proxy/` (Cloudflare Workers) and `unity-app/` (native Quest C#).

## Stack

- **Proxy:** JavaScript, ES modules, Cloudflare Workers. Tests via Node's built-in
  `node:test`. No npm dependencies, no lockfile. `wrangler` is global, not a dep.
- **App:** Unity 6 LTS, C#, IL2CPP + ARM64, Meta XR All-in-One SDK + Meta Voice SDK.
- **LLM:** Anthropic Claude Messages API, `stream: true`, SSE piped verbatim.

## Conventions

- Proxy handler (`proxy/src/handler.js`) is **platform-agnostic** (pure
  `Request`/`Response`); `worker.js` is the thin Cloudflare entry point. Keep it that way.
- Every proxy error returns `error.speak` — a sentence the headset reads aloud. Preserve this.
- Unity `VoiceInput`/`VoiceOutput` have **no compile-time Meta SDK dependency** — they
  bridge via Inspector-wired `UnityEvent`s. Do not add `using Meta…`/SDK references to them.
- All C# is under namespace `QuestClaude`; log tag is `[QuestClaude]` (+ `[Latency]`).
- All tunables live in the `AssistantConfig` ScriptableObject — no magic numbers in logic.
- UK English in docs and user-facing strings.
- **Never log message content** (proxy or app) — privacy rule from guide §8.4.

## Secrets (critical)

- `ANTHROPIC_API_KEY` lives **only** in the proxy env (`wrangler secret put` / `.dev.vars`).
  Never in the APK, Unity, or git. CI's `scripts/secret-sweep.sh` fails on any `sk-ant-`.
- `APP_AUTH_TOKEN` = proxy secret + Unity `AssistantConfig.appToken`, sent as `X-App-Token`.
  Extractable from the APK by design; rotate at the proxy if leaked.
- `.env*`, `.dev.vars`, `*.local.md` are git-ignored. `.secrets.local.md` is referenced in
  `.gitignore` but **does not exist** in the repo (personal, untracked) — don't chase it.

## Deploy quirks

- Worker is named `quest` in `wrangler.toml`, so the URL is `quest.<you>.workers.dev` —
  **not** `questclaude-proxy.…` despite some doc text. Use what wrangler prints.
- `unity-app/` is **scripts only** — no Unity project, no `.meta`/scene/asset/ProjectSettings.
  A human must create the Unity 6 project, import the SDKs, link Wit.ai, and build on device.
- `proxy` tests run with just `npm test` (no install). Don't add deps unless necessary.

## Model IDs — verify, don't trust

- `MODEL_ALLOW_LIST` = `claude-haiku-4-5` (default), `claude-sonnet-4-6`, `claude-sonnet-5`.
- **`claude-sonnet-4-6` is almost certainly wrong** (no such model). Confirm all IDs against
  current Anthropic docs before relying on them; the guide left model IDs to build time.
- When working on anything LLM-shaped here, load the `claude-api` skill — don't answer model
  or pricing questions from memory.

## GitHub identity gotchas

- Remote/owner is `PR1OR/Metquestclaude` (GitHub scope shows it lowercase as
  `pr1or/metquestclaude` — GitHub owners are case-insensitive; the on-disk folder is
  `Metquestclaude`). Same repo, don't be thrown by the casing.
- Commits are authored by both `Claude <noreply@anthropic.com>` and `PR1OR`.
- Do **not** put the model identifier / undercover model name in commits, PR text, or code.
- Work on the branch you're told to; push with `git push -u origin <branch>`. Only open a
  PR when explicitly asked.
- No `gh` CLI here — use the GitHub MCP tools (`mcp__github__*`) for GitHub actions.

## Fragile / hacky (tread carefully)

- Rate limiter is in-memory per Worker isolate — best-effort, resets on recycle.
- Self-hearing/barge-in guards need real on-device tuning (`AssistantConfig`).
- Voice SDK event names drift between versions; wiring uses closest-match, with a
  `VoiceOutput` watchdog fallback if playback-complete can't be wired.
- No headset run has ever happened; latency targets are aspirational until measured.
</content>
