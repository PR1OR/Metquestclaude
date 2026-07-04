using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace QuestClaude
{
    [Serializable]
    public class StringUnityEvent : UnityEvent<string> { }

    /// <summary>
    /// Sentence-queue TTS bridge (guide §5.6). Like VoiceInput, it has no
    /// compile-time dependency on the Meta Voice SDK — wire in the Inspector:
    ///
    ///   VoiceOutput.onSpeakChunk (string) → TTSSpeaker.SpeakQueued(string)
    ///   VoiceOutput.onStopSpeaking        → TTSSpeaker.Stop()
    ///
    ///   TTSSpeaker Events (Inspector):
    ///     On Playback Complete → VoiceOutput.HandlePlaybackComplete()
    ///
    /// Chunks are dispatched to the speaker immediately (the TTSSpeaker queues
    /// internally); this class tracks how many are outstanding so the conversation
    /// manager knows when the reply has finished being spoken.
    /// </summary>
    public class VoiceOutput : MonoBehaviour
    {
        [Header("Wire to a TTSSpeaker (see unity-app/README.md wiring table)")]
        public StringUnityEvent onSpeakChunk;
        public UnityEvent onStopSpeaking;

        /// <summary>Fires once the stream is complete AND every queued chunk has played.</summary>
        public event Action AllSpeechFinished;

        public bool IsSpeaking => _pending > 0;

        private int _pending;
        private bool _streamComplete;
        private float _lastActivityTime;
        private const float PlaybackWatchdogSeconds = 20f;
        private const int RecentChunkMemory = 6;
        private readonly List<string> _recentChunks = new List<string>();

        /// <summary>Call before the first chunk of a new reply.</summary>
        public void BeginUtterance()
        {
            _pending = 0;
            _streamComplete = false;
            _lastActivityTime = Time.realtimeSinceStartup;
        }

        public void EnqueueChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            _pending++;
            _lastActivityTime = Time.realtimeSinceStartup;

            _recentChunks.Add(TextSimilarity.Normalize(chunk));
            while (_recentChunks.Count > RecentChunkMemory) _recentChunks.RemoveAt(0);

            onSpeakChunk?.Invoke(chunk);
        }

        /// <summary>Call when the SSE stream has delivered everything.</summary>
        public void NotifyStreamComplete()
        {
            _streamComplete = true;
            CheckFinished();
        }

        /// <summary>Immediate stop for barge-in (guide §6.2).</summary>
        public void StopAll()
        {
            _pending = 0;
            _streamComplete = false;
            onStopSpeaking?.Invoke();
        }

        /// <summary>Self-hearing guard: does this transcript closely match something
        /// we just sent to the speaker?</summary>
        public bool SoundsLikeRecentTts(string transcript, float threshold)
        {
            string norm = TextSimilarity.Normalize(transcript);
            if (norm.Length == 0) return false;
            foreach (var chunk in _recentChunks)
            {
                if (TextSimilarity.Similarity(norm, chunk) >= threshold) return true;
                // Partial transcripts are prefixes — also compare against the chunk head.
                if (chunk.Length > norm.Length)
                {
                    string head = chunk.Substring(0, norm.Length);
                    if (TextSimilarity.Similarity(norm, head) >= threshold) return true;
                }
            }
            return false;
        }

        // ---- Invoked BY the TTSSpeaker via Inspector-wired event ----
        public void HandlePlaybackComplete()
        {
            if (_pending > 0) _pending--;
            _lastActivityTime = Time.realtimeSinceStartup;
            CheckFinished();
        }

        private void Update()
        {
            // Safety net: if playback-complete events were never wired (or a clip
            // failed), don't leave the state machine stuck in SPEAKING forever.
            if (_streamComplete && _pending > 0 &&
                Time.realtimeSinceStartup - _lastActivityTime > PlaybackWatchdogSeconds)
            {
                Debug.LogWarning("[QuestClaude] VoiceOutput watchdog: forcing speech-finished. " +
                                 "Is TTSSpeaker's playback-complete event wired to HandlePlaybackComplete?");
                _pending = 0;
                CheckFinished();
            }
        }

        private void CheckFinished()
        {
            if (_streamComplete && _pending <= 0)
            {
                _streamComplete = false;
                AllSpeechFinished?.Invoke();
            }
        }
    }
}
