# Build Instruction Guide: "Quest Claude" — A Hands-Free, Voice-Controlled Claude AI Assistant for Meta Quest 3

**Version:** 1.0 · **Date:** 4 July 2026 · **Audience:** Claude Code (executing agent) + a UK-based technical product builder (supervising human)

**Purpose of this document:** This is the *instruction set*, not the code. A separate step generates the code. Claude Code should follow this guide top-to-bottom; the human handles the account sign-ups, headset pairing, and anything requiring a browser or a physical device.

---

## 1. Overview & Architecture

### 1.1 What we are building

A native Unity app for Meta Quest 3 (Meta Horizon OS / Android) that lets the user hold a fully hands-free spoken conversation with Claude:

1. The user speaks (activated by a wake phrase or voice activity — **no button presses**).
2. The Meta Voice SDK (`AppDictationExperience`, powered by Wit.ai) transcribes speech to text on-device/via Wit.ai.
3. The transcript is POSTed to a **proxy backend** we control (serverless function).
4. The proxy forwards the request to the **Anthropic Claude Messages API** with `stream: true`, holding the API key server-side.
5. Streamed reply tokens flow back to the headset as Server-Sent Events.
6. The Meta Voice SDK **TTSService/TTSSpeaker** speaks the reply as sentences arrive (not waiting for the full response).
7. The app returns to listening — a continuous **listen → think → speak** loop with multi-turn conversation memory.

A minimal in-VR panel shows connection status, live transcript, and Claude's reply text.

### 1.2 Why this stack (decisions locked in)

| Decision | Choice | Rationale |
|---|---|---|
| Platform | Unity 6 LTS, native Android/Quest build | The Quest browser does **not** support the Web Speech API, so WebXR is unsuitable for voice. Native Unity is the correct path. |
| VR layer | Meta XR All-in-One SDK | Official, includes camera rig, interaction, permissions helpers. |
| STT | Meta Voice SDK — `AppDictationExperience` (Wit.ai) | Free, free-form dictation, best-in-class on Quest, strongest in English. |
| TTS | Meta Voice SDK — `TTSService` + `TTSSpeaker` | Free, low-latency, on-headset friendly. Optional upgrade path: ElevenLabs or OpenAI TTS via the proxy. |
| LLM | Claude Messages API, streaming SSE | `claude-haiku` class model recommended for speed/cost; configurable to Sonnet. |
| Security | **Proxy backend mandatory** | APKs can be decompiled. The Anthropic key never ships in the APK. The Quest app only ever talks to our proxy. |
| References | `github.com/danieloquelis/Unity-QuestConversationalAI` (speech-to-speech architecture) and `github.com/yagizeraslan/Claude-Unity` (Claude streaming layer for Unity/Android) | Fork/learn — do not blindly copy; verify licences before reuse. |

### 1.3 Architecture diagram (data flow)

```
┌─────────────────────────────── META QUEST 3 (Unity app) ───────────────────────────────┐
│                                                                                         │
│  Microphone ──▶ Voice SDK / AppDictationExperience ──▶ final transcript (text)          │
│      ▲                (Wit.ai STT, English)                     │                        │
│      │                                                          ▼                        │
│  Wake phrase / VAD gate                              ConversationManager                 │
│  (hands-free activation)                             (state machine + multi-turn         │
│      ▲                                                message history)                   │
│      │                                                          │                        │
│  TTSSpeaker ◀── sentence chunker ◀── SSE stream parser ◀── HTTPS POST /chat             │
│  (speaks reply; supports barge-in interrupt)                    │                        │
│                                                                 │                        │
│  In-VR UI panel: status (Listening / Thinking / Speaking), transcript, reply text        │
└─────────────────────────────────────────────────────────────────┼───────────────────────┘
                                                                  │  HTTPS + app auth token
                                                                  ▼
                                    ┌─────────────────────────────────────────┐
                                    │   PROXY BACKEND (Vercel / CF Workers)   │
                                    │  - validates app token                  │
                                    │  - holds ANTHROPIC_API_KEY (env var)    │
                                    │  - rate limits / caps tokens            │
                                    │  - forwards request, pipes SSE back     │
                                    └──────────────────┬──────────────────────┘
                                                       │  HTTPS, x-api-key header
                                                       ▼
                                    ┌─────────────────────────────────────────┐
                                    │  Anthropic Claude Messages API           │
                                    │  POST https://api.anthropic.com/v1/      │
                                    │       messages   (stream: true, SSE)     │
                                    └─────────────────────────────────────────┘

Separate path (not via proxy): Voice SDK ◀──▶ Wit.ai (STT) — handled by Meta's SDK
with the Wit.ai client token, which is low-sensitivity and scoped to your Wit app.
```

**Key property:** the Anthropic API key exists in exactly one place — the proxy's environment variables. The APK contains only the proxy URL and a revocable app token.

---

## 2. Prerequisites & Accounts

The **human** must complete these before Claude Code starts. Tick each off:

1. **Anthropic Console account** — console.anthropic.com. Create an API key; add a small amount of credit. Note: exact per-token prices change — confirm current Haiku/Sonnet pricing on Anthropic's pricing page before setting cost caps in §8.
2. **Meta Horizon developer account** — developer.meta.com (or the Meta Horizon developer portal). You must **verify** the account (Meta requires payment-card or two-factor verification) and create/join a developer **organisation** — this is required to enable Developer Mode on the headset.
3. **Wit.ai account** — wit.ai (free; sign in with a Meta/Facebook account). Create a new Wit app, language **English** (English is Wit's strongest language — good for a UK user; test with a British accent early). Copy the **Client Access Token** and **Server Access Token** from the app's Settings page.
4. **Unity account + Unity Hub** — unity.com. A Personal (free) licence is fine below the revenue threshold.
5. **Unity 6 LTS** — install via Unity Hub **with Android Build Support** (including OpenJDK and Android SDK/NDK modules). Exact point version changes over time — install the latest Unity 6 LTS and confirm it is on Meta's supported-versions list.
6. **A proxy hosting account** — Vercel **or** Cloudflare Workers (free tiers are sufficient for a single user). Pick one; this guide assumes either.
7. **Optional:** ElevenLabs or OpenAI account + API key, only if upgrading TTS voices later (§5.6). Skip for v1.
8. **Hardware:** Meta Quest 3, a USB-C data cable, the Meta Horizon companion app on your phone (needed to toggle Developer Mode), and Wi-Fi shared between PC and headset.

---

## 3. Environment Setup on the PC

Claude Code performs the file/config steps; the human performs the Editor-GUI and headset steps.

### 3.1 Unity project prerequisites

1. Install **Unity 6 LTS** via Unity Hub with modules: *Android Build Support*, *OpenJDK*, *Android SDK & NDK Tools*.
2. Confirm `adb` works: with the headset connected, `adb devices` should list it (after §3.4). Unity's bundled platform-tools or a standalone Android platform-tools install both work.

### 3.2 Meta XR All-in-One SDK

1. Add the **Meta XR All-in-One SDK** package to the project (via Unity Asset Store "My Assets" → Package Manager, or by name `com.meta.xr.sdk.all` in the Package Manager — confirm the current package identifier in Meta's documentation, as it has changed between releases).
2. After import, run the **Meta XR Project Setup Tool** (Edit → Project Settings → Meta XR) and click **Fix All / Apply All** on both checklist tabs. This configures: Android target, IL2CPP + ARM64, correct minimum API level, OpenXR/Oculus loader, stereo rendering, etc.
3. In **XR Plug-in Management**, ensure the Meta/Oculus (OpenXR) provider is enabled for **Android**.

### 3.3 Meta Voice SDK

1. Install the **Meta Voice SDK** (package `com.meta.voice.sdk` / "Voice SDK — Immersive Voice Commands" — confirm exact package name for your SDK version; in recent All-in-One SDK releases the Voice SDK ships alongside it or is added from the Meta registry).
2. Open **Meta → Voice SDK → Voice Hub / Settings** in the Editor and link your **Wit.ai app**: paste the **Server Access Token** — the SDK generates a Wit configuration asset (`WitConfiguration`) containing the Client Access Token. This asset is what `AppDictationExperience` and `TTSService` reference.
3. Note: the Wit **client** token does ship in the app. That is acceptable — it is scoped to your Wit app, does not incur meaningful cost, and is designed for client-side use. The **Anthropic** key is the one that must never ship.

### 3.4 Quest 3 Developer Mode + ADB

Human steps:

1. In the **Meta Horizon phone app**: Menu → Devices → select the Quest 3 → **Headset settings → Developer Mode → ON**. (This option appears only after your developer organisation from §2.2 exists.)
2. Connect the Quest to the PC via USB-C. Inside the headset, accept **"Allow USB debugging"** (tick *Always allow from this computer*).
3. On the PC: `adb devices` → the headset serial should show as `device` (not `unauthorized`).
4. Optional but recommended for iteration: enable Wi-Fi ADB — `adb tcpip 5555` then `adb connect <headset-ip>:5555`.

### 3.5 Project settings sanity list (Claude Code verifies)

- Build target: **Android**; Texture compression ASTC.
- Scripting backend: **IL2CPP**; Target architecture: **ARM64 only**.
- Minimum API level: as required by the current Meta XR SDK (the Setup Tool sets this — typically Android 10/API 29 or higher; confirm).
- Internet Access: **Require** (Player Settings) — the app needs the network.
- Package name: `com.<yourorg>.questclaude` (reverse-DNS, lowercase).
- Microphone: do **not** hand-edit the manifest for `RECORD_AUDIO` — Unity/Voice SDK auto-adds it because the Microphone API is referenced. Runtime permission handling is still required (§5.3).

---

## 4. The Proxy Backend

### 4.1 What it does and why

- Stores `ANTHROPIC_API_KEY` as a server-side **environment variable/secret** — never in the APK, never in the Unity project, never in git.
- Exposes a single chat endpoint the Quest app calls.
- Forwards the conversation to `POST https://api.anthropic.com/v1/messages` with `stream: true`, setting the `x-api-key` and `anthropic-version` headers server-side, and **pipes the SSE stream back verbatim** to the headset (do not buffer the whole response — that would destroy the latency win).
- Enforces guardrails: app authentication, rate limiting, `max_tokens` cap, model allow-list.

Host on **Vercel** (Edge Function/serverless with streaming response support) or **Cloudflare Workers** (natively streams). Either free tier is fine for personal use. Whichever is chosen, verify the platform's streaming/timeout behaviour: the function must support **streamed responses** and must not time out before ~60 s.

### 4.2 Endpoints

| Method & path | Purpose | Request body | Response |
|---|---|---|---|
| `POST /chat` | Main conversational call | `{ "messages": [ {"role":"user"/"assistant", "content":"..."} , ... ], "system": "<optional system prompt>", "model": "<optional, from allow-list>" }` | `text/event-stream` — Anthropic SSE events piped through (`message_start`, `content_block_delta` with text deltas, `message_stop`, etc.) |
| `GET /health` | Connectivity check for the headset's status indicator | — | `200 {"ok":true}` |

Proxy-side behaviour for `/chat`:

- Validate the app auth header (§4.4). Reject with `401` if absent/wrong.
- Validate body: `messages` non-empty, total serialised size under a sane cap (e.g. 32 KB), role values legal.
- Trim history server-side as a backstop (e.g. keep the last 20 messages) even though the app also trims.
- Construct the Anthropic request: default model `claude-haiku` (use the current Haiku model ID — **confirm the exact model string in Anthropic's docs at build time**, e.g. the latest `claude-haiku-*` release; allow `claude-sonnet-*` via the allow-list), `max_tokens` capped (e.g. 512 — spoken answers should be short), `stream: true`, and a default **system prompt** instructing Claude to answer conversationally and concisely for voice (e.g. "You are a spoken voice assistant. Reply in 1–3 short sentences unless asked for detail. No markdown, no lists, no emojis.").
- Set headers: `x-api-key: <env key>`, `anthropic-version: 2023-06-01` (confirm current recommended version), `content-type: application/json`.
- Stream the response through unmodified with `Content-Type: text/event-stream` and no buffering.
- On Anthropic errors (429, 529, 5xx), return a structured JSON error the app can speak ("I'm having trouble reaching my brain right now").

### 4.3 Key storage

- `ANTHROPIC_API_KEY` and `APP_AUTH_TOKEN` set as environment variables/secrets in the Vercel project settings or via `wrangler secret put` for Cloudflare Workers.
- `.env`/`.dev.vars` files are git-ignored. Add a `.env.example` with placeholder names only.
- The repo must contain a check (grep in CI or a pre-commit note) that no string starting `sk-ant-` appears anywhere in the Unity project or proxy source.

### 4.4 How the app authenticates to the proxy

Keep it simple and revocable:

- The proxy requires a header, e.g. `X-App-Token: <APP_AUTH_TOKEN>` — a long random string generated once and stored as a proxy env var.
- The Unity app stores this token and the proxy URL in a `ScriptableObject`/config asset. Yes, this token is technically extractable from the APK — that is acceptable *by design*: it protects your Anthropic key, and if the app token leaks you rotate it at the proxy in seconds without touching Anthropic. Pair with per-IP rate limiting (e.g. 30 requests/min) on the proxy.
- Do not attempt to embed the Anthropic key with "obfuscation" — it is not security.

---

## 5. App Build Steps (in order)

Claude Code should implement these as discrete, testable milestones. Study the two reference repos first: `danieloquelis/Unity-QuestConversationalAI` for the overall speech-to-speech loop and Voice SDK wiring, and `yagizeraslan/Claude-Unity` for a Unity-friendly SSE/streaming Claude client that works on Android (Unity's `UnityWebRequest` needs a custom `DownloadHandlerScript` to surface streamed bytes as they arrive — that repo demonstrates the pattern).

### 5.1 Project creation & scene

1. Create the Unity 6 project (3D URP or the Meta-recommended template), apply §3 setup.
2. Scene `Main.unity`: add the Meta XR **camera rig** (OVRCameraRig or the current Building Blocks equivalent — the SDK's "Building Blocks" panel can drop in a ready rig). No controllers/hand interaction is required for core UX, but leave hand tracking enabled for the optional wake gesture (§6.1).
3. Add an empty `Systems` GameObject to hold: `ConversationManager`, `ClaudeClient`, `VoiceInput`, `VoiceOutput`, `PermissionManager`, `UIController`.

### 5.2 Configuration asset

- A `ScriptableObject` (`AssistantConfig`) holding: proxy base URL, app token, model choice (haiku/sonnet), max history turns, silence timeout, wake phrase text, TTS voice preset. All tunables in one inspectable place; no secrets beyond the app token.

### 5.3 Microphone permission handling

- On first launch, check `Permission.HasUserAuthorizedPermission(Permission.Microphone)`; if not granted, request it via `Permission.RequestUserPermission` (Unity Android API) and handle the callback.
- The manifest entry `android.permission.RECORD_AUDIO` is auto-added by Unity/Voice SDK — verify it is present in the built manifest, don't duplicate it.
- UX: if denied, the UI panel must show a clear "Microphone permission needed — say nothing works without it" state with instructions to grant via Settings; the app must not crash or spin.
- Acceptance for this milestone: fresh install shows the Android permission dialog in-headset; granting proceeds to the listening state.

### 5.4 STT — wiring `AppDictationExperience`

1. Add an `AppDictationExperience` component (Voice SDK → Dictation), reference the Wit configuration asset from §3.3.
2. Subscribe to its events: partial transcription (live text for the UI), **full/final transcription** (triggers the Claude call), dictation session ended, and error events.
3. Configure dictation audio settings for conversational use: enable multi-phrase/continuous dictation if using VAD-style flow, and tune the **endpointing/silence timeout** (§6.3).
4. Route partial transcripts to the UI panel in real time so the user sees they are being heard.
5. Acceptance: speak in-headset → final transcript string appears in the log/UI with good accuracy in English (test with a UK accent; if accuracy is poor, add common phrasings as Wit utterances or adjust mic gain).

### 5.6 TTS — wiring `TTSService` + `TTSSpeaker`

(Ordered here because you can test TTS independently before the Claude call.)

1. Add a `TTSService` (Wit-backed, from the Voice SDK) referencing the same Wit configuration, and a `TTSSpeaker` on an audio-source GameObject near the camera.
2. Choose a voice preset; expose it in `AssistantConfig`.
3. Implement a **speech queue**: the speaker accepts sentence-sized chunks and plays them in order; expose `StopAll()` for barge-in (§6.2).
4. **Optional upgrade (later, not v1):** a `TTSProvider` interface with a second implementation calling ElevenLabs or OpenAI TTS **via the proxy** (add a `/tts` proxy endpoint so those keys also stay server-side). Keep Meta TTS as offline-ish fallback.
5. Acceptance: a hard-coded test string is spoken aloud in-headset with acceptable quality and <~1 s start delay for a short sentence.

### 5.5 Claude streaming client (via proxy)

1. Implement `ClaudeClient`: sends `POST {proxyUrl}/chat` with the JSON body from §4.2 and headers `Content-Type: application/json`, `X-App-Token`.
2. Implement SSE parsing on Android: use `UnityWebRequest` with a custom `DownloadHandlerScript` that receives bytes incrementally; buffer to lines; parse `event:`/`data:` pairs; extract text deltas from `content_block_delta` events; detect `message_stop`. (Follow the `Claude-Unity` repo's streaming approach — it validates this pattern on Android/IL2CPP.)
3. Expose events: `OnTextDelta(string)`, `OnComplete(string fullText)`, `OnError(code, message)`.
4. Implement a **sentence chunker** consuming deltas: accumulate text, and each time a sentence boundary (`.`, `?`, `!`, or ~120 chars) is reached, dispatch that chunk to the TTS queue immediately. This is what makes first-spoken-word latency low.
5. Timeouts: 10 s to first byte, 60 s overall; a `CancellationToken`/abort path for barge-in.
6. Acceptance: from the Editor (Play Mode on PC is fine for this milestone), a typed test prompt streams a reply from the proxy and fires deltas incrementally, not all at once.

### 5.7 The hands-free conversation loop

Implement `ConversationManager` as an explicit state machine:

```
        ┌────────────┐   wake phrase / VAD    ┌────────────┐  final transcript  ┌────────────┐
  ──▶   │   IDLE     │ ─────────────────────▶ │ LISTENING  │ ─────────────────▶ │  THINKING  │
        │ (passive   │                        │ (dictation │                    │ (streaming │
        │  wake ear) │ ◀── inactivity ─────── │  active)   │ ◀── barge-in ───── │  + TTS =   │
        └────────────┘        timeout         └────────────┘    (user speaks)   │  SPEAKING) │
              ▲                                     ▲                           └─────┬──────┘
              └────────────── reply finished ───────┴───── auto-relisten ─────────────┘
```

- **Multi-turn memory:** keep a `List<Message>` of `{role, content}` pairs; append user transcript and assistant full reply each turn; send the whole list (trimmed to the last N turns, N ≈ 10 user/assistant pairs, configurable) to the proxy each call. Provide a spoken/one-command "reset conversation" (e.g. saying "new conversation" clears history — intercept this phrase before sending to Claude).
- After Claude finishes speaking, **auto-return to LISTENING** for a follow-up window (e.g. 8 s); if silence, drop to IDLE awaiting the wake phrase. This gives natural back-and-forth without re-waking every turn.
- All state transitions logged and reflected in the UI.

### 5.8 Minimal in-VR UI

- A world-space Canvas panel, ~1 m in front of the user, lazily head-following (reposition when the user turns > ~30°, smooth-damped — never rigidly glued to the head, which is nauseating).
- Contents: **status chip** (Idle / Listening 🎤 / Thinking / Speaking — plus Offline/Error states), **live transcript line** (partial STT), **reply text** (streamed), and a small connectivity dot driven by periodic `/health` pings.
- Distinct audio earcons for wake-acknowledged, listening-started, and error — the user should not need to look at the panel.

---

## 6. Voice Control Specifics

### 6.1 Activation model (hands-free)

Implement in this priority order:

1. **Primary — wake phrase:** In IDLE, run a lightweight always-listening loop that starts short dictation sessions (or continuous dictation) and scans transcripts for a wake phrase, e.g. **"Hey Claude"** (make it configurable; fuzzy-match "hey Claude", "hey cloud", "a Claude" — Wit will mishear it). On match, play the earcon and enter LISTENING, discarding the wake phrase itself from the sent prompt. Note the trade-off honestly: this keeps the mic pipeline active in IDLE (battery + privacy implications — disclose in §8.6). Provide a settings toggle to disable always-listening, falling back to option 3.
2. **Within-conversation VAD:** after a reply, the auto-relisten window (§5.7) means no wake phrase is needed for follow-ups — the dictation session's own voice-activity detection picks up speech directly.
3. **Fallback — wake gesture:** an optional hand-tracked gesture (e.g. palm-up "look at watch" pose or pinch) via the Meta XR interaction SDK, for noisy environments or when always-listening is toggled off. This is a fallback, not the primary UX.

### 6.2 Barge-in / interrupt handling

- While SPEAKING, keep a dictation session (or VAD monitor) running at reduced sensitivity. If the user starts talking with sustained speech (> ~400 ms, to avoid the TTS output or coughs triggering it):
  1. `TTSSpeaker.StopAll()` — cut playback immediately;
  2. abort the in-flight SSE request (cancellation token);
  3. keep the partial assistant reply received so far in history, marked as the assistant turn (so context is preserved);
  4. transition to LISTENING and capture the new utterance.
- Guard against **self-hearing**: the headset mic may pick up the headset speakers. Mitigations, in order of preference: rely on the Quest's echo cancellation; reduce VAD sensitivity while speaking; ignore transcripts that closely match the last TTS chunk (string-similarity check). Test this explicitly — it is the most common failure mode in speech-to-speech apps.

### 6.3 Silence detection / endpointing

- LISTENING ends the utterance after **~1.0–1.5 s of trailing silence** (tune the `AppDictationExperience` endpointing settings; start at 1.2 s). Too short chops sentences; too long feels laggy.
- Hard cap an utterance at ~30 s.
- Empty/garbage transcripts (< 2 characters, or only the wake phrase) are discarded and the app quietly re-listens rather than calling Claude.

### 6.4 Latency budget — target **< 1 s to first spoken token**, < 2 s typical end of question → start of answer

| Stage | Target | How |
|---|---|---|
| STT endpoint + final transcript | ≤ 400 ms after user stops | tuned endpointing |
| Proxy hop + Claude first delta | ≤ 500 ms | Haiku model, streaming, small system prompt, trimmed history, proxy on edge (choose a UK/EU region if configurable) |
| First TTS audio out | ≤ 300 ms | dispatch the *first sentence fragment* to TTS as soon as it completes; pre-warm the TTS service with a silent request at app start |

Measure and log each stage per turn (§8.4). If total exceeds ~2.5 s consistently, the first suspects are: full-response buffering somewhere (proxy or download handler — must be incremental), oversized history, or waiting for the full reply before TTS.

---

## 7. Building the APK & Sideloading to Quest 3

### 7.1 Build

1. File → Build Settings → Android → Switch Platform (if not already).
2. Add `Main.unity` to Scenes in Build.
3. Player Settings final check: IL2CPP, ARM64, package name, version code incremented each build, Internet Access = Require.
4. **Build** → produces `QuestClaude.apk`. (Development Build + Script Debugging on for iteration builds; off for the "release" acceptance build.)

### 7.2 Sideload

Option A — command line (preferred for automation):

```
adb devices                     # confirm headset present
adb install -r QuestClaude.apk  # -r reinstalls, preserving data
```

Option B — **SideQuest** (GUI): install SideQuest on the PC, connect the headset, drag the APK on.

In the headset: **Library → filter/dropdown → "Unknown Sources"** → launch *QuestClaude*.

### 7.3 Iteration loop

- Fastest loop: Unity **Build And Run** deploys straight to the connected headset.
- Logs: `adb logcat -s Unity` (add your own log tag too). This is the primary debugging channel in-headset.
- Wi-Fi ADB (§3.4) removes the cable for repeated test sessions.
- Note: much of the voice pipeline (dictation, Claude call, TTS) can be tested in the **Editor on the PC** with a desktop mic before every headset build — use this to keep iteration tight; reserve headset builds for VR-specific behaviour (permissions, echo, UI comfort).

---

## 8. Production Hardening

### 8.1 Error handling (app)

- Every failure maps to a **spoken** message + UI state — never a silent hang: no network ("I can't reach the internet right now"), proxy 401 ("I'm not authorised — check the app token"), Anthropic 429/529 via proxy ("I'm a bit overloaded, try again in a moment"), STT error (earcon + re-listen), TTS error (show text reply on panel as fallback).
- The state machine must always return to IDLE or LISTENING — no dead ends. Watchdog: if THINKING exceeds 60 s, abort and recover.

### 8.2 Dropped network / retries

- `/health` ping every ~20 s drives the connectivity dot; when offline, wake phrase still responds but the app says it's offline rather than failing mid-answer.
- `/chat` failures: one automatic retry with short backoff (1–2 s) for transient errors (network fault, 5xx, 529); no retry on 4xx. If the stream drops **mid-reply**, keep and speak what was received, append "…I lost my connection there", keep the partial in history.

### 8.3 API cost controls

- Proxy enforces: `max_tokens` cap (~512), history-size cap, rate limit (e.g. 30 req/min/IP), model allow-list.
- Set a **spend limit in the Anthropic Console**. Haiku-class models are cheap per conversational turn, but *exact prices change — the human must confirm current pricing at console/docs and size the monthly limit accordingly.*
- Proxy logs per-request input/output token counts (available in the SSE `message_start`/`message_delta` usage fields) for a running cost view.

### 8.4 Logging

- App: state transitions, per-stage latency (STT ms / first-delta ms / first-audio ms), errors — to `adb logcat` and an in-app debug overlay toggle. **Never log full transcripts in release builds** (privacy).
- Proxy: request timestamps, token counts, status codes, latency. **Do not log message content** by default.

### 8.5 Key rotation

- Anthropic key: rotate in the Console → update the proxy env var → redeploy proxy. **Zero app rebuild required** — this is the payoff of the proxy design.
- App token: rotate the proxy env var; this *does* require a new APK (or make the token user-editable in an in-app settings field to avoid rebuilds).
- Rotate immediately if either value is ever pasted into a chat, log, or commit.

### 8.6 Privacy (mic data handling)

- Be explicit in a first-run notice: dictation audio is processed by **Wit.ai (Meta)** under its terms; transcripts are sent to **your proxy** and **Anthropic**. Review Wit.ai's data-retention settings for your app and Anthropic's data-usage policy (API inputs are not used for training by default — confirm the current policy).
- Always-listening wake mode is opt-out-able (§6.1); the LISTENING state is always visibly and audibly indicated.
- No audio is ever stored by the app; conversation history lives only in memory and clears on app exit or "new conversation".
- Proxy: HTTPS only (default on both platforms), no content logging, no third-party analytics.

---

## 9. Testing & Acceptance Criteria

The finished app must pass **all** of the following on a real Quest 3:

**Setup & permissions**
- [ ] 1. Fresh sideloaded install launches from Unknown Sources without crash.
- [ ] 2. First launch shows the Android mic permission dialog; granting leads to a working IDLE state; denying shows the guidance state without crashing.

**Core loop**
- [ ] 3. Saying the wake phrase ("Hey Claude") from IDLE triggers the listening earcon and LISTENING state — **no controller or hand input used at any point**.
- [ ] 4. Asking a spoken question (e.g. "What's the capital of Australia?") produces a correct **spoken** answer, with first audio within **~2 s** of the user finishing speaking (stretch target: first token < 1 s; measured by the latency log).
- [ ] 5. The reply is streamed: the panel text visibly grows during THINKING, and speech begins before the full reply has finished arriving.
- [ ] 6. A follow-up asked in the auto-relisten window **without re-waking** (e.g. "and what's its population?") is answered using prior context — proving multi-turn memory.
- [ ] 7. Saying "new conversation" clears context (verify: a subsequent "what did I just ask?" gets a no-memory answer).

**Voice control robustness**
- [ ] 8. Speaking over Claude mid-reply (barge-in) stops playback within ~0.5 s and the new utterance is handled.
- [ ] 9. Claude's own TTS output does not trigger a false barge-in or get transcribed as user input during a 5-turn conversation.
- [ ] 10. Sitting silent in LISTENING returns the app to IDLE after the timeout; background chatter of < 2 characters of content does not fire a Claude call.
- [ ] 11. STT accuracy is usable with the target user's UK accent across 10 varied test utterances (≥ 8/10 transcribed faithfully enough for correct answers).

**Failure & recovery**
- [ ] 12. With Wi-Fi disabled, the wake phrase yields a spoken "offline" message; re-enabling Wi-Fi restores service within one health-check interval, no restart needed.
- [ ] 13. Killing the proxy (or a forced 500) produces a spoken error and the app returns to a usable state.
- [ ] 14. A 10-minute continuous conversation session causes no crash, no runaway memory, and no audio drift.

**Security**
- [ ] 15. `grep -r "sk-ant"` over the Unity project, built APK (unzipped), and repo returns **nothing**.
- [ ] 16. Calling the proxy `/chat` without the `X-App-Token` header returns 401.
- [ ] 17. Rotating the Anthropic key at the proxy requires no app rebuild and the app keeps working.

**Comfort/UI**
- [ ] 18. The status panel is readable, follows the head smoothly without being glued to it, and status states match actual behaviour throughout a full conversation.

---

## 10. Ordered Task List for Claude Code

Execute in order; each task ends with its verification step.

1. **Repo scaffold:** create a mono-repo with `/unity-app` and `/proxy`; add `.gitignore` (Unity + node + `.env*`), `.env.example`, and a README stub pointing at this guide.
2. **Study references:** review `danieloquelis/Unity-QuestConversationalAI` and `yagizeraslan/Claude-Unity` — extract the SSE-on-Android pattern and the Voice SDK wiring approach; note licences.
3. **Build the proxy** (§4): `/chat` streaming pass-through + `/health`, env-var key handling, app-token auth, rate limit, model allow-list, `max_tokens` cap, voice-optimised default system prompt. Deploy to Vercel or Cloudflare Workers. *Verify:* `curl -N` against `/chat` with the app token streams SSE deltas; without the token returns 401.
4. **Unity project creation** (§3.1, §5.1): Unity 6 LTS project, Android target, Meta XR All-in-One SDK, Project Setup Tool fixes applied, camera rig in `Main.unity`. *Verify:* an empty scene builds to APK and runs on the headset (human confirms).
5. **Voice SDK setup** (§3.3): install Voice SDK, human links Wit.ai; commit the Wit configuration asset. *Verify:* SDK's built-in dictation demo transcribes in the Editor.
6. **`AssistantConfig` + `PermissionManager`** (§5.2, §5.3). *Verify:* fresh install shows and honours the mic permission dialog on-device.
7. **STT integration** (§5.4): `AppDictationExperience` wiring, partial/final transcript events, endpointing tuned. *Verify:* acceptance item 11 dry-run.
8. **TTS integration** (§5.6): `TTSService` + `TTSSpeaker`, sentence queue, `StopAll()`. *Verify:* test string spoken in-headset.
9. **Claude streaming client** (§5.5): proxy POST, incremental `DownloadHandlerScript` SSE parser, delta events, sentence chunker → TTS queue, timeouts, cancellation. *Verify:* Editor test prompt streams and speaks sentence-by-sentence.
10. **ConversationManager state machine** (§5.7): IDLE/LISTENING/THINKING-SPEAKING states, multi-turn history with trimming, "new conversation" phrase, auto-relisten window.
11. **Hands-free activation** (§6.1): wake-phrase loop with fuzzy matching, earcons, settings toggle; optional gesture fallback.
12. **Barge-in + self-hearing guards** (§6.2) and silence/garbage-transcript handling (§6.3).
13. **In-VR UI** (§5.8): status chip, transcript, streamed reply, connectivity dot, lazy head-follow.
14. **Hardening pass** (§8): error → spoken-message mapping, retry/offline logic, watchdog, latency logging, release-build log scrubbing.
15. **Release build + sideload** (§7); human runs the full §9 checklist on-device.
16. **Tune:** endpointing, VAD sensitivity, barge-in threshold, relisten window, history length — driven by checklist failures and latency logs.
17. **Security sweep:** acceptance items 15–17; confirm Anthropic spend limit is set.
18. **(Optional, post-v1):** premium TTS via a proxied `/tts` endpoint (ElevenLabs/OpenAI) behind the `TTSProvider` interface.

---

## Appendix: Things to confirm at build time (deliberately not hard-coded here)

- Current **Unity 6 LTS** point release and Meta's supported-version list.
- Exact **Meta XR All-in-One SDK** and **Voice SDK** package names/versions in the Package Manager.
- Current **Claude model IDs** for Haiku/Sonnet and the recommended `anthropic-version` header value (Anthropic docs).
- Current **Claude pricing** (for the spend cap in §8.3).
- Streaming-response and timeout limits of the chosen proxy platform's current free tier.
- Wit.ai data-retention settings and Anthropic API data-usage policy wording (for the §8.6 privacy notice).

*End of guide.*
