using System.Collections.Generic;
using UnityEngine;

namespace QuestClaude
{
    /// <summary>
    /// All tunables in one inspectable place (guide §5.2). The only secret-ish value
    /// here is the app token, which is revocable at the proxy by design — the
    /// Anthropic API key never appears anywhere in the Unity project.
    /// </summary>
    [CreateAssetMenu(fileName = "AssistantConfig", menuName = "QuestClaude/Assistant Config")]
    public class AssistantConfig : ScriptableObject
    {
        [Header("Proxy")]
        [Tooltip("Base URL of the deployed proxy, e.g. https://questclaude-proxy.you.workers.dev")]
        public string proxyBaseUrl = "";
        [Tooltip("Matches APP_AUTH_TOKEN on the proxy. Extractable from the APK by design; rotate at the proxy if it leaks.")]
        public string appToken = "";
        [Tooltip("Leave empty for the proxy default (Haiku). Must be on the proxy's allow-list.")]
        public string model = "";

        [Header("Conversation")]
        [Range(1, 20)]
        [Tooltip("How many user/assistant pairs of history to send with each request.")]
        public int maxHistoryTurns = 10;
        [Tooltip("Seconds to keep listening for a follow-up after Claude finishes speaking.")]
        public float relistenWindowSeconds = 8f;
        [Tooltip("Abort and recover if a reply takes longer than this (guide §8.1 watchdog).")]
        public float thinkingWatchdogSeconds = 60f;

        [Header("Hands-free activation")]
        [Tooltip("Keep the mic pipeline active in IDLE to scan for the wake phrase. Battery/privacy trade-off — see §8.6.")]
        public bool alwaysListenForWakePhrase = true;
        public string wakePhrase = "hey claude";
        [Tooltip("Common Wit.ai mishearings that should also count as the wake phrase.")]
        public List<string> wakePhraseVariants = new List<string>
        {
            "hey claude", "hey cloud", "hey clod", "hey clawed", "hey clause", "a claude", "hey claud"
        };
        [Tooltip("Saying this clears the conversation history.")]
        public string resetPhrase = "new conversation";

        [Header("Barge-in (§6.2)")]
        [Tooltip("User speech must be sustained this many milliseconds while Claude is speaking before playback is cut.")]
        public int bargeInMinSpeechMs = 400;
        [Range(0f, 1f)]
        [Tooltip("Transcripts at least this similar to a recent TTS chunk are treated as self-hearing and ignored.")]
        public float selfHearingSimilarityThreshold = 0.8f;

        [Header("Network timeouts")]
        public float firstByteTimeoutSeconds = 10f;
        public float overallTimeoutSeconds = 60f;
        public float healthPingIntervalSeconds = 20f;

        [Header("TTS")]
        [Tooltip("Informational only — pick the voice preset on the TTSSpeaker component; record the choice here.")]
        public string ttsVoicePreset = "";
    }
}
