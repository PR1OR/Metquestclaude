using System;

namespace QuestClaude
{
    /// <summary>
    /// Decides when user speech during TTS playback counts as a real interruption
    /// (guide §6.2): the speech must be sustained (default ≥ 400 ms), non-trivial,
    /// and must NOT sound like the assistant hearing its own voice. Pure C#.
    /// </summary>
    public class BargeInDetector
    {
        private readonly int _minSpeechMs;
        private float _firstPartialTime = -1f;

        public BargeInDetector(int minSpeechMs)
        {
            _minSpeechMs = minSpeechMs;
        }

        public void Reset() => _firstPartialTime = -1f;

        /// <summary>
        /// Feed each partial transcript received while SPEAKING.
        /// <paramref name="soundsLikeOwnTts"/> is the self-hearing check supplied by
        /// the caller. Returns true once the speech qualifies as a barge-in.
        /// </summary>
        public bool OnPartial(string partial, float nowSeconds, Func<string, bool> soundsLikeOwnTts)
        {
            string norm = TextSimilarity.Normalize(partial);
            if (norm.Length < 3) return false;              // coughs, noise
            if (soundsLikeOwnTts != null && soundsLikeOwnTts(norm))
            {
                // Echo of our own speaker — don't let it accumulate toward a barge-in.
                Reset();
                return false;
            }

            if (_firstPartialTime < 0f)
            {
                _firstPartialTime = nowSeconds;
                return false;
            }

            return (nowSeconds - _firstPartialTime) * 1000f >= _minSpeechMs;
        }
    }
}
