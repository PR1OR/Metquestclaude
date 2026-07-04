# Quest Claude — Unity App

The native Meta Quest 3 app: a hands-free **listen → think → speak** loop with
Claude. All C# for the pipeline is in `Assets/QuestClaude/` and compiles **before
any Meta SDK is imported** — the two SDK touch points (dictation in, TTS out) are
bridged through Inspector-wired UnityEvents, so the code survives Voice SDK API
renames and the human wiring stays a 5-minute checklist.

## What's here

| File | Guide § | Purpose |
|---|---|---|
| `Scripts/AssistantConfig.cs` | 5.2 | ScriptableObject with every tunable (proxy URL, app token, wake phrase, timeouts…) |
| `Scripts/PermissionManager.cs` | 5.3 | Runtime mic permission; denial shows guidance, never crashes |
| `Scripts/VoiceInput.cs` | 5.4 | Bridge to `AppDictationExperience` (partial/final transcripts, session lifecycle) |
| `Scripts/VoiceOutput.cs` | 5.6 | Sentence queue bridge to `TTSSpeaker`, `StopAll()` for barge-in, self-hearing memory |
| `Scripts/ClaudeClient.cs` + `SseDownloadHandler.cs` + `SseParser.cs` | 5.5 | Streaming SSE client via `UnityWebRequest` + custom `DownloadHandlerScript`; 10 s first-byte / 60 s overall timeouts; abort support |
| `Scripts/SentenceChunker.cs` | 5.5 | Emits sentence-sized chunks to TTS as deltas arrive → low first-word latency |
| `Scripts/ConversationManager.cs` | 5.7 | IDLE/LISTENING/THINKING/SPEAKING state machine, multi-turn history + trimming, "new conversation", auto-relisten, watchdog, retry, offline + error → spoken lines |
| `Scripts/WakeWordDetector.cs` | 6.1 | Fuzzy "Hey Claude" matching (variants + edit distance) |
| `Scripts/BargeInDetector.cs` + `TextSimilarity.cs` | 6.2 | Sustained-speech barge-in with self-hearing guard |
| `Scripts/UIController.cs` | 5.8 | Status chip / transcript / streamed reply / connectivity dot; lazy head-follow |
| `Scripts/Earcons.cs` | 5.8 | Wake/listen/error audio cues (procedurally generated beeps if no clips assigned) |
| `Scripts/HealthMonitor.cs` | 8.2 | `/health` ping every 20 s |
| `Scripts/LatencyTracker.cs` | 6.4 | Per-turn stage timings → `adb logcat -s Unity` (`[QuestClaude][Latency]`) |
| `Editor/SceneBootstrapper.cs` | — | Menu: **QuestClaude → Create Assistant Config / Set Up Scene Objects** |

## Human setup, in order

Prerequisites from the guide §2–§3 (Meta developer account + verified org, Wit.ai
app, Unity 6 LTS with Android Build Support, headset in Developer Mode) are assumed.

### 1. Create the Unity project

1. Unity Hub → New Project → **Universal 3D (URP)**, Unity 6 LTS.
2. Close Unity. Copy this repo's `unity-app/Assets/QuestClaude` folder into the
   new project's `Assets/`. (Or open this `unity-app/` folder directly via
   Hub → *Add project from disk* — Unity will generate `Library/`, `Packages/`
   and `ProjectSettings/` with defaults.)
3. File → Build Settings → **Android** → Switch Platform. Texture compression **ASTC**.

### 2. Meta SDKs

1. Package Manager: add **Meta XR All-in-One SDK** (`com.meta.xr.sdk.all`) and the
   **Meta Voice SDK** — confirm current package identifiers against Meta's docs
   (they have changed between releases; see guide appendix).
2. **Edit → Project Settings → Meta XR** → run the Project Setup Tool → **Fix All /
   Apply All** on both tabs (sets IL2CPP, ARM64, min API level, XR loader, etc.).
3. Verify Player Settings: IL2CPP, ARM64 only, Internet Access **Require**,
   package name `com.<yourorg>.questclaude`.
4. **Meta → Voice SDK → Voice Hub** → link your Wit.ai app with the **Server Access
   Token** → a `WitConfiguration` asset is generated. (The Wit *client* token ships
   in the app by design and is low-sensitivity; the Anthropic key never does.)

### 3. Scene

1. Open/create `Main.unity`, add it to Scenes in Build.
2. Add the Meta XR **camera rig** (Building Blocks → Camera Rig, or `OVRCameraRig`).
3. Menu **QuestClaude → 1. Create Assistant Config** → fill in `proxyBaseUrl` and
   `appToken` (from the proxy deploy).
4. Menu **QuestClaude → 2. Set Up Scene Objects** → creates `QuestClaude Systems`
   (all components pre-wired) + the world-space `Assistant UI` panel.
5. Add the Voice SDK components to the scene:
   - `AppDictationExperience` (Voice SDK → Dictation), referencing the Wit config.
     Enable multi-phrase/continuous dictation if available and start endpointing
     around **1.2 s** trailing silence (§6.3).
   - `TTSService` (Wit) + a `TTSSpeaker` on a child GameObject near the camera.
     Pick a voice preset; note it in `AssistantConfig.ttsVoicePreset`.

### 4. Inspector wiring table (the only manual wiring)

On **VoiceInput** (QuestClaude Systems):

| VoiceInput field | Wire to |
|---|---|
| `onActivateDictation` | `AppDictationExperience.Activate()` |
| `onDeactivateDictation` | `AppDictationExperience.Deactivate()` |

On **AppDictationExperience** (Dictation Events section):

| Voice SDK event | Wire to |
|---|---|
| On Partial Transcription (string) | `VoiceInput.HandlePartialTranscription` *(dynamic string)* |
| On Full Transcription (string) | `VoiceInput.HandleFullTranscription` *(dynamic string)* |
| On Dictation Session Stopped / On Stopped Listening | `VoiceInput.HandleDictationSessionStopped` |
| On Error | `VoiceInput.HandleErrorMessage` |

On **VoiceOutput** (QuestClaude Systems):

| VoiceOutput field | Wire to |
|---|---|
| `onSpeakChunk` (string) | `TTSSpeaker.SpeakQueued` *(dynamic string — queues; do **not** use `Speak`, which interrupts)* |
| `onStopSpeaking` | `TTSSpeaker.Stop()` |

On **TTSSpeaker** (Events section):

| Voice SDK event | Wire to |
|---|---|
| On Playback Complete (per clip) | `VoiceOutput.HandlePlaybackComplete` |

> Event names differ slightly across Voice SDK versions — pick the closest match
> (e.g. "On Finished Speaking"). If playback-complete can't be wired, the app
> still works: `VoiceOutput` has a watchdog fallback, just with a laggier return
> to listening.

### 5. Editor smoke test (before any headset build)

Play Mode on the PC with a desktop mic exercises almost the whole pipeline
(guide §7.3): say the wake phrase, watch state transitions in the Console
(`[QuestClaude]` tag), confirm streamed text appears and TTS speaks
sentence-by-sentence, check the `[QuestClaude][Latency]` lines.

### 6. Build & sideload (guide §7)

```sh
adb devices                      # headset present, authorized
# Unity: File → Build Settings → Build → QuestClaude.apk  (or Build And Run)
adb install -r QuestClaude.apk
adb logcat -s Unity              # primary in-headset debugging channel
```

Launch from **Library → Unknown Sources**. Then run the acceptance checklist in
`docs/BUILD_GUIDE.md` §9.

## Tuning knobs (guide §16)

All in the `AssistantConfig` asset: relisten window, wake variants, barge-in
threshold, self-hearing similarity, watchdog/timeouts, history length — plus
dictation endpointing on the `AppDictationExperience` component itself.

## Privacy notes baked in (§8.6)

- Conversation history lives only in memory; cleared on exit or "new conversation".
- No audio is stored; transcripts are never written to disk.
- Latency logs contain timings only, never transcript content.
- Always-listening wake mode is a config toggle (`alwaysListenForWakePhrase`).
