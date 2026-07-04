using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace QuestClaude
{
    public enum AssistantState
    {
        Booting,
        PermissionDenied,
        Idle,        // passive wake ear
        Listening,   // dictation active, capturing an utterance
        Thinking,    // request in flight, no audio yet
        Speaking,    // reply streaming and/or TTS playing
    }

    /// <summary>
    /// The hands-free conversation loop (guide §5.7):
    ///
    ///   IDLE --wake phrase--> LISTENING --final transcript--> THINKING --first chunk--> SPEAKING
    ///     ^                        ^                                                      |
    ///     |---- relisten timeout --+------------------ auto-relisten ---------------------+
    ///
    /// Also owns: multi-turn history with trimming, the "new conversation" reset
    /// phrase, barge-in, self-hearing guard, offline handling, error→spoken-message
    /// mapping, retry with backoff, and the THINKING watchdog. The state machine
    /// always returns to IDLE or LISTENING — no dead ends (guide §8.1).
    /// </summary>
    public class ConversationManager : MonoBehaviour
    {
        [Header("Components")]
        public AssistantConfig config;
        public ClaudeClient claudeClient;
        public VoiceInput voiceInput;
        public VoiceOutput voiceOutput;
        public PermissionManager permissionManager;
        public HealthMonitor healthMonitor;
        public UIController ui;
        public Earcons earcons;

        public AssistantState State { get; private set; } = AssistantState.Booting;

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        private readonly StringBuilder _assistantBuffer = new StringBuilder();
        private readonly LatencyTracker _latency = new LatencyTracker();

        private SentenceChunker _chunker;
        private WakeWordDetector _wakeDetector;
        private BargeInDetector _bargeIn;

        private Coroutine _relistenTimer;
        private Coroutine _thinkingWatchdog;
        private bool _retriedThisTurn;
        private bool _speakingSystemLine;          // offline/error lines return to IDLE, not relisten
        private bool _sawSpeechInRelistenWindow;
        private List<ChatMessage> _lastRequestSnapshot;

        private void Awake()
        {
            _chunker = new SentenceChunker();
            _chunker.OnChunk += HandleChunk;
            _wakeDetector = new WakeWordDetector(config.wakePhraseVariants);
            _bargeIn = new BargeInDetector(config.bargeInMinSpeechMs);
        }

        private void Start()
        {
            claudeClient.OnTextDelta += HandleDelta;
            claudeClient.OnComplete += HandleComplete;
            claudeClient.OnError += HandleClaudeError;

            voiceInput.PartialTranscription += HandlePartial;
            voiceInput.FinalTranscription += HandleFinal;
            voiceInput.DictationStopped += HandleDictationStopped;
            voiceInput.DictationError += HandleDictationError;

            voiceOutput.AllSpeechFinished += HandleAllSpeechFinished;

            healthMonitor.OnlineChanged += online => ui.SetOnline(online);

            permissionManager.Granted += HandlePermissionGranted;
            permissionManager.Denied += HandlePermissionDenied;

            SetStatus("Requesting microphone permission…", UIController.IdleColor);
            permissionManager.RequestMicrophonePermission();
        }

        // ------------------------------------------------------------------ setup

        private void HandlePermissionGranted()
        {
            healthMonitor.Begin();
            EnterIdle();
        }

        private void HandlePermissionDenied()
        {
            State = AssistantState.PermissionDenied;
            SetStatus("Microphone permission needed — nothing works without it.\n" +
                      "Grant it in Settings → Apps → QuestClaude → Permissions.",
                      UIController.ErrorColor);
        }

        // ------------------------------------------------------------- transitions

        private void EnterIdle()
        {
            CancelRelistenTimer();
            State = AssistantState.Idle;
            SetStatus($"Idle — say \"{config.wakePhrase}\"", UIController.IdleColor);
            ui.SetTranscript("");

            if (config.alwaysListenForWakePhrase) voiceInput.Activate();
            else voiceInput.Deactivate();
        }

        private void EnterListening(bool fromRelisten = false)
        {
            State = AssistantState.Listening;
            SetStatus("Listening 🎤", UIController.ListeningColor);
            earcons.PlayListening();
            voiceInput.Activate();

            CancelRelistenTimer();
            if (fromRelisten)
            {
                _sawSpeechInRelistenWindow = false;
                _relistenTimer = StartCoroutine(RelistenCountdown());
            }
        }

        private IEnumerator RelistenCountdown()
        {
            yield return new WaitForSecondsRealtime(config.relistenWindowSeconds);
            if (State == AssistantState.Listening && !_sawSpeechInRelistenWindow)
                EnterIdle();
            _relistenTimer = null;
        }

        private void CancelRelistenTimer()
        {
            if (_relistenTimer != null)
            {
                StopCoroutine(_relistenTimer);
                _relistenTimer = null;
            }
        }

        // ------------------------------------------------------------- voice input

        private void HandlePartial(string text)
        {
            ui.SetTranscript(text);

            switch (State)
            {
                case AssistantState.Listening:
                    _sawSpeechInRelistenWindow = true;
                    break;

                case AssistantState.Speaking:
                    // Barge-in check: sustained, non-self speech interrupts playback.
                    bool interrupt = _bargeIn.OnPartial(
                        text,
                        Time.realtimeSinceStartup,
                        t => voiceOutput.SoundsLikeRecentTts(t, config.selfHearingSimilarityThreshold));
                    if (interrupt) DoBargeIn();
                    break;
            }
        }

        private void HandleFinal(string text)
        {
            string norm = TextSimilarity.Normalize(text);
            if (norm.Length < 2) return; // garbage / noise → quietly re-listen (§6.3)

            switch (State)
            {
                case AssistantState.Idle:
                    HandleWakeAttempt(text);
                    break;

                case AssistantState.Listening:
                    // A trailing echo of our own TTS can arrive as a final right
                    // after SPEAKING→LISTENING — ignore it and keep listening (§9.9).
                    if (voiceOutput.SoundsLikeRecentTts(norm, config.selfHearingSimilarityThreshold))
                        return;
                    HandleUtterance(text, norm);
                    break;

                case AssistantState.Speaking:
                    // A final transcript mid-speech that survived the self-hearing
                    // guard is a barge-in whose speech already completed.
                    if (!voiceOutput.SoundsLikeRecentTts(norm, config.selfHearingSimilarityThreshold))
                    {
                        DoBargeIn();
                        HandleUtterance(text, norm);
                    }
                    break;

                // Thinking: ignore — the mic shouldn't steer an in-flight request.
            }
        }

        private void HandleWakeAttempt(string text)
        {
            if (!_wakeDetector.TryMatch(text, out string remainder)) return;

            earcons.PlayWakeAcknowledged();

            if (!healthMonitor.IsOnline)
            {
                SpeakSystemLine("I can't reach the internet right now.");
                return;
            }

            // The wake phrase itself is never sent to Claude (§6.1). If the user
            // said "hey claude <question>" in one breath, answer the question.
            if (TextSimilarity.Normalize(remainder).Length >= 2)
                HandleUtterance(remainder, TextSimilarity.Normalize(remainder));
            else
                EnterListening();
        }

        private void HandleUtterance(string text, string norm)
        {
            if (norm == TextSimilarity.Normalize(config.resetPhrase))
            {
                _history.Clear();
                SpeakSystemLine("Okay, starting a new conversation.");
                return;
            }

            // Utterances that are only the wake phrase carry no content (§6.3).
            if (_wakeDetector.TryMatch(text, out string remainder) &&
                TextSimilarity.Normalize(remainder).Length < 2)
            {
                EnterListening();
                return;
            }

            SendToClaude(text.Trim());
        }

        private void HandleDictationStopped()
        {
            // Dictation sessions self-terminate after endpointing; keep the ear open
            // when the state calls for it. Deferred a frame to avoid a tight loop.
            if (State == AssistantState.Idle && config.alwaysListenForWakePhrase)
                StartCoroutine(ReactivateNextFrame());
            else if (State == AssistantState.Listening || State == AssistantState.Speaking)
                StartCoroutine(ReactivateNextFrame());
        }

        private IEnumerator ReactivateNextFrame()
        {
            yield return null;
            if (State == AssistantState.Idle && !config.alwaysListenForWakePhrase) yield break;
            if (State == AssistantState.Thinking || State == AssistantState.PermissionDenied) yield break;
            voiceInput.Activate();
        }

        private void HandleDictationError(string message)
        {
            Debug.LogWarning($"[QuestClaude] Dictation error: {message}");
            earcons.PlayError();
            // Recover by re-listening (§8.1: STT error → earcon + re-listen).
            if (State == AssistantState.Listening || State == AssistantState.Idle)
                StartCoroutine(ReactivateNextFrame());
        }

        // ---------------------------------------------------------------- request

        private void SendToClaude(string userText)
        {
            CancelRelistenTimer();
            _history.Add(new ChatMessage("user", userText));
            TrimHistory();

            _assistantBuffer.Length = 0;
            _chunker.Reset();
            _retriedThisTurn = false;
            _speakingSystemLine = false;
            voiceOutput.BeginUtterance();
            _bargeIn.Reset();

            State = AssistantState.Thinking;
            SetStatus("Thinking…", UIController.ThinkingColor);
            ui.SetReply("");

            _latency.BeginTurn();
            _lastRequestSnapshot = new List<ChatMessage>(_history);
            _latency.MarkRequestSent();
            claudeClient.SendChat(_lastRequestSnapshot);

            RestartThinkingWatchdog();
        }

        private void TrimHistory()
        {
            int maxMessages = config.maxHistoryTurns * 2;
            while (_history.Count > maxMessages) _history.RemoveAt(0);
            while (_history.Count > 0 && _history[0].role != "user") _history.RemoveAt(0);
        }

        private void RestartThinkingWatchdog()
        {
            if (_thinkingWatchdog != null) StopCoroutine(_thinkingWatchdog);
            _thinkingWatchdog = StartCoroutine(ThinkingWatchdog());
        }

        private IEnumerator ThinkingWatchdog()
        {
            yield return new WaitForSecondsRealtime(config.thinkingWatchdogSeconds);
            if (State == AssistantState.Thinking)
            {
                Debug.LogWarning("[QuestClaude] THINKING watchdog fired — aborting request.");
                claudeClient.Abort();
                earcons.PlayError();
                SpeakSystemLine("Sorry, that took too long. Ask me again?");
            }
            _thinkingWatchdog = null;
        }

        private void StopThinkingWatchdog()
        {
            if (_thinkingWatchdog != null)
            {
                StopCoroutine(_thinkingWatchdog);
                _thinkingWatchdog = null;
            }
        }

        // ------------------------------------------------------------ reply stream

        private void HandleDelta(string delta)
        {
            _latency.MarkFirstDelta();
            _assistantBuffer.Append(delta);
            ui.SetReply(_assistantBuffer.ToString());
            _chunker.Add(delta);
        }

        private void HandleChunk(string chunk)
        {
            if (State == AssistantState.Thinking)
            {
                State = AssistantState.Speaking;
                SetStatus("Speaking", UIController.SpeakingColor);
                StopThinkingWatchdog();
                _latency.MarkFirstAudio();
                // Keep a dictation session running so barge-in can interrupt us
                // (§6.2). The self-hearing guard filters our own TTS back out.
                _bargeIn.Reset();
                voiceInput.Activate();
            }
            voiceOutput.EnqueueChunk(chunk);
        }

        private void HandleComplete(string fullText)
        {
            StopThinkingWatchdog();
            _chunker.Flush();

            if (_assistantBuffer.Length > 0)
                _history.Add(new ChatMessage("assistant", _assistantBuffer.ToString()));

            _latency.LogTurn();

            if (State == AssistantState.Thinking)
            {
                // Empty reply — nothing to speak; go straight back to listening.
                EnterListening(fromRelisten: true);
                return;
            }

            voiceOutput.NotifyStreamComplete();
        }

        private void HandleClaudeError(long status, string speakable)
        {
            StopThinkingWatchdog();

            bool transient = status == 0 || status == 429 || status >= 500;
            bool hasPartial = _assistantBuffer.Length > 0;

            // One automatic retry with a short backoff for transient failures,
            // but only if nothing has been spoken yet (§8.2).
            if (transient && !hasPartial && !_retriedThisTurn && _lastRequestSnapshot != null)
            {
                _retriedThisTurn = true;
                StartCoroutine(RetryAfterBackoff());
                return;
            }

            earcons.PlayError();

            if (hasPartial)
            {
                // Stream dropped mid-reply: keep and speak what we have, note the
                // drop, and keep the partial in history (§8.2).
                _chunker.Flush();
                _history.Add(new ChatMessage("assistant", _assistantBuffer.ToString()));
                if (State == AssistantState.Thinking)
                {
                    State = AssistantState.Speaking;
                    SetStatus("Speaking", UIController.SpeakingColor);
                }
                voiceOutput.EnqueueChunk("… I lost my connection there.");
                voiceOutput.NotifyStreamComplete();
            }
            else
            {
                SpeakSystemLine(speakable);
            }
        }

        private IEnumerator RetryAfterBackoff()
        {
            SetStatus("Retrying…", UIController.ThinkingColor);
            yield return new WaitForSecondsRealtime(1.5f);
            if (State != AssistantState.Thinking) yield break;
            _latency.MarkRequestSent();
            claudeClient.SendChat(_lastRequestSnapshot);
            RestartThinkingWatchdog();
        }

        // ----------------------------------------------------------- speech output

        private void HandleAllSpeechFinished()
        {
            _bargeIn.Reset();

            if (_speakingSystemLine)
            {
                _speakingSystemLine = false;
                EnterIdle();
                return;
            }

            // Natural back-and-forth: reopen the mic for a follow-up window (§5.7).
            EnterListening(fromRelisten: true);
        }

        /// <summary>Speak a status/error line that is NOT part of the conversation;
        /// afterwards the assistant returns to IDLE.</summary>
        private void SpeakSystemLine(string line)
        {
            _speakingSystemLine = true;
            State = AssistantState.Speaking;
            SetStatus("Speaking", UIController.SpeakingColor);
            ui.SetReply(line);
            voiceOutput.BeginUtterance();
            voiceOutput.EnqueueChunk(line);
            voiceOutput.NotifyStreamComplete();
        }

        // --------------------------------------------------------------- barge-in

        private void DoBargeIn()
        {
            Debug.Log("[QuestClaude] Barge-in: stopping playback and aborting stream.");
            voiceOutput.StopAll();          // 1. cut playback immediately
            claudeClient.Abort();           // 2. abort the in-flight SSE request
            StopThinkingWatchdog();

            // 3. keep the partial reply in history so context is preserved (§6.2)
            if (_assistantBuffer.Length > 0)
            {
                _history.Add(new ChatMessage("assistant", _assistantBuffer.ToString()));
                _assistantBuffer.Length = 0;
            }

            _bargeIn.Reset();
            // 4. capture the new utterance
            EnterListening();
        }

        // ------------------------------------------------------------------- misc

        private void SetStatus(string label, Color color)
        {
            ui.SetStatus(label, color);
            Debug.Log($"[QuestClaude] State → {State}: {label.Replace('\n', ' ')}");
        }
    }
}
