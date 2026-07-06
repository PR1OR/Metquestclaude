# Quest Claude — Handover

Everything a new maintainer needs to pick this up. Plain and honest, including
the rough edges.

## What this app does

- A hands-free, voice-controlled Claude assistant for the **Meta Quest 3**.
- Native VR loop: **listen → think → speak**, with multi-turn memory.
- Say a wake phrase ("Hey Claude"), ask a question, Claude answers aloud.
- Speech-to-text is Meta Voice SDK (Wit.ai); text-to-speech is Meta Voice SDK
  (Wit TTS); the LLM is the Anthropic Claude Messages API, streamed.
- Built to the spec in [`docs/BUILD_GUIDE.md`](docs/BUILD_GUIDE.md). That guide is
  the source of truth for intent; this file is the source of truth for *state*.

## Current state (be honest)

- **Proxy: done and tested.** 22 unit tests pass, no network needed.
- **Unity C#: code-complete.** Every pipeline component is written and self-contained.
- **Unity project itself: does NOT exist in this repo.** `unity-app/` contains only
  the `Assets/QuestClaude/` C# scripts — there is no `ProjectSettings/`, no
  `Packages/`, no `Main.unity` scene, no `AssistantConfig.asset`, and **no `.meta`
  files**. You must create a fresh Unity 6 project and copy the scripts in; Unity
  regenerates the `.meta` GUIDs on import.
- **Never run on a headset here.** The whole Editor + device half (project creation,
  SDK import, Wit linking, build, sideload, on-device acceptance checklist) is left
  to a human — it needs the Unity GUI, a Meta developer account, a Wit.ai app and a
  physical Quest 3. Build-order steps 3 and 13 in the README are the unfinished ones.
- **Premium TTS (`/tts` proxied ElevenLabs/OpenAI) is not built.** It is an optional
  post-v1 item (guide §5.6 / §10.18). Meta Wit TTS is the only voice path today.

## Architecture

```
Quest 3 (Unity app)                 Proxy (Cloudflare Workers)     Anthropic
  mic → Voice SDK (Wit.ai STT)  ─▶  validates X-App-Token          Claude
  ConversationManager (states)      holds ANTHROPIC_API_KEY   ─▶    Messages API
  ClaudeClient (SSE stream)     ◀─  pipes SSE back verbatim   ◀─    (stream: true)
  SentenceChunker → TTSSpeaker
```

- **The Anthropic key lives in exactly one place** — the proxy's environment. The
  APK only ever holds the proxy URL and a revocable app token.
- STT/TTS talk to Wit.ai directly via Meta's SDK (a separate path, not through our
  proxy) using the low-sensitivity Wit client token.

### Repo layout

| Path | What |
|---|---|
| `proxy/` | Cloudflare Workers streaming proxy. Platform-agnostic handler + 22 tests. |
| `unity-app/Assets/QuestClaude/Scripts/` | All pipeline C#. |
| `unity-app/Assets/QuestClaude/Editor/` | `SceneBootstrapper.cs` — one-click scene scaffolding. |
| `docs/BUILD_GUIDE.md` | The full build instruction guide (the spec). |
| `scripts/secret-sweep.sh` | Fails if an `sk-ant-` key appears anywhere. |
| `.github/workflows/ci.yml` | Secret sweep + proxy tests on every push/PR. |

### Proxy internals (`proxy/src/handler.js`)

- `handleRequest` routes `GET /health` and `POST /chat`; `handler.js` is pure
  `Request`/`Response` (no Cloudflare specifics), so a Vercel port is a ~5-line wrapper.
- `/chat` does: app-token auth → per-IP rate limit → body/JSON validation → model
  allow-list → `max_tokens` cap → history trim → forward to Anthropic with
  `stream: true` → **pipe the SSE stream straight back, unbuffered**.
- Every error body carries `error.speak` — a sentence the headset reads aloud.

### Unity pipeline (key files)

- `ConversationManager.cs` — the state machine (Idle/Listening/Thinking/Speaking),
  history + trimming, "new conversation" reset, barge-in, retry, watchdog, offline.
- `ClaudeClient.cs` + `SseDownloadHandler.cs` + `SseParser.cs` — streaming SSE over
  `UnityWebRequest` with a custom `DownloadHandlerScript` (the pattern that gets
  incremental bytes on Android/IL2CPP). First-byte 10 s / overall 60 s timeouts.
- `SentenceChunker.cs` — emits sentence-sized chunks to TTS as deltas arrive (low
  first-word latency).
- `VoiceInput.cs` / `VoiceOutput.cs` — **deliberately have no compile-time dependency
  on the Meta SDK**. They bridge to `AppDictationExperience` / `TTSSpeaker` through
  Inspector-wired `UnityEvent`s, so the project compiles before the SDK is imported
  and survives SDK renames. This is the main design decision to understand.
- `WakeWordDetector`, `BargeInDetector`, `TextSimilarity` — pure C#, fuzzy wake
  matching + self-hearing guard.
- `UIController`, `Earcons`, `HealthMonitor`, `LatencyTracker` — UI panel, audio cues
  (procedural beeps if no clips), `/health` pings, per-turn latency logging.

## How to run locally

### Proxy (fully runnable now)

```sh
cd proxy
npm test                          # 22 tests, no network, no `npm install` needed
cp .dev.vars.example .dev.vars    # fill in real values — .dev.vars is git-ignored
wrangler dev                      # local server on :8787
```

- Tests use only Node's built-in `node:test` — there are no npm dependencies and no
  `package-lock.json`. `wrangler` is expected to be installed globally, not as a dep.

### Unity (needs the project set up first)

- There is nothing to "run" until you create the Unity project (see Deploy below).
- Once set up, **Play Mode on the PC with a desktop mic exercises almost the whole
  pipeline** — wake phrase, streaming, TTS, latency logs — without a headset. Reserve
  headset builds for VR-specific behaviour (permissions, echo, UI comfort).
- Watch the Console for the `[QuestClaude]` and `[QuestClaude][Latency]` tags.

## How to deploy

### 1. Proxy → Cloudflare Workers

```sh
cd proxy
npm install -g wrangler                  # once
wrangler login                           # once
wrangler secret put ANTHROPIC_API_KEY    # from console.anthropic.com
wrangler secret put APP_AUTH_TOKEN       # e.g. `openssl rand -hex 32`
wrangler deploy
```

- **Deployed URL gotcha:** the worker is named `quest` in `wrangler.toml`, so the URL
  is `https://quest.<you>.workers.dev` — **not** `questclaude-proxy.…` as some docs
  read. Use the URL wrangler actually prints. It goes into `AssistantConfig.proxyBaseUrl`.
- Verify:
  ```sh
  curl https://quest.<you>.workers.dev/health           # {"ok":true}
  curl -i  .../chat -X POST -d '{"messages":[...]}'      # 401 without the token
  curl -N  .../chat -X POST -H 'x-app-token: <TOKEN>' \  # streams SSE deltas
    -H 'content-type: application/json' \
    -d '{"messages":[{"role":"user","content":"hi"}]}'
  ```

### 2. App → Quest 3 (human, in the Editor + on device)

Full detail in `unity-app/README.md`. In short:

1. Unity Hub → new **Universal 3D (URP)** project, Unity 6 LTS with Android Build Support.
2. Copy `unity-app/Assets/QuestClaude/` into the new project's `Assets/`.
3. Package Manager: add **Meta XR All-in-One SDK** + **Meta Voice SDK** (confirm current
   package IDs — they drift between releases). Run **Meta XR Project Setup Tool → Fix All**.
4. **Voice Hub** → link your Wit.ai app with the Server Access Token → generates a
   `WitConfiguration` asset.
5. Menu **QuestClaude → 1. Create Assistant Config**, fill in `proxyBaseUrl` + `appToken`.
6. Menu **QuestClaude → 2. Set Up Scene Objects** (wires all components + the UI panel).
7. Add the Meta XR camera rig, an `AppDictationExperience`, a `TTSService` + `TTSSpeaker`,
   then do the **Inspector wiring table** in `unity-app/README.md` (the only manual wiring).
8. Build → `QuestClaude.apk` → `adb install -r` (or SideQuest) → launch from
   **Library → Unknown Sources**.
9. Run the §9 acceptance checklist in `docs/BUILD_GUIDE.md` on the headset.

## Environment variables & where secrets live

| Name | Lives where | Notes |
|---|---|---|
| `ANTHROPIC_API_KEY` | **Proxy env only** — `wrangler secret put` (or Vercel project settings). Local dev: `proxy/.dev.vars`. | Never in the APK, Unity project, or git. This is the whole point of the proxy. |
| `APP_AUTH_TOKEN` | Proxy secret **and** `AssistantConfig.appToken` in Unity. Sent as the `X-App-Token` header. | Extractable from the APK **by design** — it only guards the proxy. If it leaks, rotate the proxy env var (seconds, no rebuild). Making it user-editable in-app avoids a rebuild entirely. |
| Wit.ai Client + Server tokens | The Wit.ai app + the generated `WitConfiguration` Unity asset. | The **client** token ships in the APK by design (low sensitivity, scoped to your Wit app). The Anthropic key never does. |

- Root `.env.example` and `proxy/.dev.vars.example` are **placeholders only**.
- `.gitignore` covers `.env*`, `.dev.vars`, `*.pem`, `*.local.md`, `secrets.local*`.
- **Key rotation:** rotating `ANTHROPIC_API_KEY` = `wrangler secret put` again, zero app
  rebuild. Rotating `APP_AUTH_TOKEN` needs the Unity config updated (or the in-app field).

## Guardrails already enforced (proxy)

- App-token auth (constant-time compare), `401` if missing/wrong.
- Per-IP rate limit: 30 req/min.
- `max_tokens` capped at 512; request body capped at 32 KB; history backstop 20 messages.
- Model allow-list; unknown model → 400.
- Voice-optimised default system prompt (short spoken sentences, no markdown/lists/emoji).
- No content logging (guide §8.4 forbids it) — do not add `console.log` of messages.

## Known issues & fragile bits (read before touching)

- **Model IDs are unverified — one looks wrong.** `MODEL_ALLOW_LIST` in
  `proxy/src/handler.js` is `["claude-haiku-4-5", "claude-sonnet-4-6", "claude-sonnet-5"]`
  with default `claude-haiku-4-5`. `claude-sonnet-4-6` is **not a known Anthropic model
  ID** (Sonnet aliases are `claude-sonnet-4-5` / `claude-sonnet-5`) and will 502 from
  upstream if requested. **Confirm every ID against current Anthropic docs before going
  live**, and fix the allow-list. The guide deliberately left these to build time.
- **No real Unity project in the repo** (see *Current state*). New GUIDs on import means
  no pre-wired scene survives — you rely on the `SceneBootstrapper` menu to rebuild it.
- **Rate limiter is in-memory, per Worker isolate.** It resets when the isolate recycles
  and isn't shared across isolates/regions. Fine for one user; not real abuse protection.
  Use Durable Objects / KV if you ever need it to be robust.
- **`.secrets.local.md` is referenced but absent.** `.gitignore` points at a "personal
  fill-in-the-blanks secret sheet (see SETUP: `.secrets.local.md`)", but there is no such
  file and no SETUP doc in the repo. The reference dangles — it's a personal, untracked
  sheet the original author kept locally.
- **Voice SDK event names drift between versions.** The wiring table may need the
  closest-match event (e.g. "On Finished Speaking"). If TTS playback-complete can't be
  wired, `VoiceOutput` falls back to a 20 s watchdog — the app still works, just with a
  laggier return to listening.
- **Self-hearing is the classic failure mode.** The headset mic can pick up the headset
  speakers. Guards exist (`BargeInDetector` + `TextSimilarity` self-hearing check,
  reduced sensitivity while speaking), but this needs real on-device tuning
  (`selfHearingSimilarityThreshold`, `bargeInMinSpeechMs` in `AssistantConfig`).
- **`anthropic-version` is pinned to `2023-06-01`.** Still the stable Messages API value,
  but confirm it's current when you touch the proxy.
- **Latency is unmeasured in practice.** Targets (< 1 s to first spoken token) are
  aspirational until run on hardware; `LatencyTracker` logs the stages to verify.

## Half-finished / next steps

- Create the actual Unity project and do the on-device acceptance run (guide §9).
- Fix + verify the model allow-list against live Anthropic model IDs and pricing; set a
  spend limit in the Anthropic Console.
- Tune on-device: endpointing, barge-in threshold, self-hearing similarity, relisten
  window, history length — all in `AssistantConfig`.
- Optional post-v1: premium TTS via a proxied `/tts` endpoint behind a `TTSProvider`
  interface (keeps those keys server-side too).
- Optional: remove or resolve the dangling `.secrets.local.md` / SETUP reference.
</content>
</invoke>
