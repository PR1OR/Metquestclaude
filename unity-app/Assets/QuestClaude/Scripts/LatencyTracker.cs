using UnityEngine;

namespace QuestClaude
{
    /// <summary>
    /// Per-turn latency instrumentation (guide §6.4 / §8.4). Stages:
    ///   utterance end (final transcript) → request sent → first delta → first audio dispatch.
    /// Logged to adb logcat with the [QuestClaude][Latency] tag. Never logs content.
    /// </summary>
    public class LatencyTracker
    {
        private float _utteranceEnd = -1f;
        private float _requestSent = -1f;
        private float _firstDelta = -1f;
        private float _firstAudio = -1f;

        public void BeginTurn()
        {
            _utteranceEnd = Time.realtimeSinceStartup;
            _requestSent = _firstDelta = _firstAudio = -1f;
        }

        public void MarkRequestSent() => _requestSent = Time.realtimeSinceStartup;

        public void MarkFirstDelta()
        {
            if (_firstDelta < 0f) _firstDelta = Time.realtimeSinceStartup;
        }

        public void MarkFirstAudio()
        {
            if (_firstAudio < 0f) _firstAudio = Time.realtimeSinceStartup;
        }

        public void LogTurn()
        {
            if (_utteranceEnd < 0f) return;
            Debug.Log("[QuestClaude][Latency] " +
                      $"utterance→request: {Ms(_utteranceEnd, _requestSent)} | " +
                      $"request→firstDelta: {Ms(_requestSent, _firstDelta)} | " +
                      $"firstDelta→firstAudio: {Ms(_firstDelta, _firstAudio)} | " +
                      $"utterance→firstAudio TOTAL: {Ms(_utteranceEnd, _firstAudio)}");
        }

        private static string Ms(float from, float to)
        {
            if (from < 0f || to < 0f) return "n/a";
            return $"{Mathf.RoundToInt((to - from) * 1000f)}ms";
        }
    }
}
