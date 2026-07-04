# Quest Claude

A hands-free, voice-controlled Claude assistant for the Meta Quest 3. Speak a wake
phrase, ask a question, and Claude answers aloud — a continuous **listen → think →
speak** loop with multi-turn memory, all in native VR.

Built to the spec in [`docs/BUILD_GUIDE.md`](docs/BUILD_GUIDE.md).

## Architecture

```
Quest 3 (Unity app)                    Proxy (Cloudflare Workers)        Anthropic
  mic → Voice SDK (Wit.ai STT)   ──▶   validates X-App-Token             Claude
  ConversationManager (states)         holds ANTHROPIC_API_KEY     ──▶   Messages API
  ClaudeClient (SSE stream)      ◀──   pipes SSE back verbatim     ◀──   (stream: true)
  SentenceChunker → TTSSpeaker
```

The **Anthropic API key exists in exactly one place** — the proxy's environment
variables. The APK contains only the proxy URL and a revocable app token.

## Repo layout

| Path | What |
|---|---|
| [`proxy/`](proxy/) | Streaming proxy backend (guide §4). Cloudflare Workers; platform-agnostic handler with 22 unit tests. |
| [`unity-app/`](unity-app/) | Native Quest 3 Unity app (guide §5–§8). All pipeline C# + an Editor scene bootstrapper. |
| [`docs/BUILD_GUIDE.md`](docs/BUILD_GUIDE.md) | The full build instruction guide. |
| [`scripts/secret-sweep.sh`](scripts/secret-sweep.sh) | Fails if an `sk-ant-` key is anywhere in the repo. |
| `.github/workflows/ci.yml` | Runs the secret sweep + proxy tests on every push. |

## Quick start

**1. Deploy the proxy** ([`proxy/README.md`](proxy/README.md)):

```sh
cd proxy
npm test                                   # 22 tests, no network
wrangler secret put ANTHROPIC_API_KEY      # from console.anthropic.com
wrangler secret put APP_AUTH_TOKEN         # openssl rand -hex 32
wrangler deploy
```

**2. Build the app** ([`unity-app/README.md`](unity-app/README.md)): Unity 6 LTS +
Meta XR All-in-One SDK + Meta Voice SDK. Menu **QuestClaude → Set Up Scene Objects**
scaffolds everything; a short Inspector wiring table connects the two Voice SDK
touch points. Build → sideload → run from Unknown Sources.

## Build order (guide §10)

1. ✅ Repo scaffold — `.gitignore`, `.env.example`, README.
2. ✅ Proxy — `/chat` streaming pass-through + `/health`, app-token auth, rate
   limit, model allow-list, `max_tokens` cap, voice system prompt. **Verify:**
   `npm test` (22 pass); `curl -N` streams SSE with the token, 401 without it.
3. ⏳ Unity project + Meta XR SDK + camera rig *(human, in the Editor)*.
4. ✅ `AssistantConfig` + `PermissionManager`.
5. ✅ STT bridge (`VoiceInput`) — wired to `AppDictationExperience` in the Inspector.
6. ✅ TTS bridge (`VoiceOutput`) — sentence queue + `StopAll()`.
7. ✅ Claude streaming client — `DownloadHandlerScript` SSE parser, sentence chunker, timeouts, cancellation.
8. ✅ `ConversationManager` state machine — history + trimming, "new conversation", auto-relisten.
9. ✅ Hands-free activation — fuzzy wake phrase, earcons, settings toggle.
10. ✅ Barge-in + self-hearing guards + silence/garbage handling.
11. ✅ In-VR UI — status chip, transcript, streamed reply, connectivity dot, lazy head-follow.
12. ✅ Hardening — error→spoken mapping, retry/offline, watchdog, latency logging.
13. ⏳ Release build + sideload + the §9 on-device checklist *(human, on the headset)*.
14. ✅ Security sweep — `scripts/secret-sweep.sh` + CI.

Steps left to the human are the ones requiring the Unity Editor GUI, a Meta
developer account, a Wit.ai app, and the physical headset — everything else is
code-complete and tested.

## Model & pricing note

The proxy defaults to `claude-haiku-4-5` (cheapest, fastest — right for short
spoken turns) and also allows `claude-sonnet-4-6` / `claude-sonnet-5`. Confirm
current pricing on Anthropic's pricing page and set a spend limit in the console
(guide §8.3) before going live.

## Security

- The Anthropic key never leaves the proxy environment — not in the APK, the Unity
  project, or git.
- The app token is revocable at the proxy in seconds (rotate the env var) without
  an app rebuild; rotating the Anthropic key needs no rebuild either (guide §8.5).
- CI blocks any commit containing an `sk-ant-` string.
