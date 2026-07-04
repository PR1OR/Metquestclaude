using System.Collections.Generic;

namespace QuestClaude
{
    /// <summary>
    /// Fuzzy wake-phrase matching over dictation transcripts (guide §6.1). Matches
    /// exact variants ("hey claude", "hey cloud", ...) anywhere in the transcript,
    /// plus a Levenshtein fallback over sliding token windows so near-misses like
    /// "hey clode" still wake the assistant. Pure C#.
    /// </summary>
    public class WakeWordDetector
    {
        private readonly List<string> _variants = new List<string>();
        private const int MaxEditDistance = 2;

        public WakeWordDetector(IEnumerable<string> variants)
        {
            foreach (var v in variants)
            {
                string norm = TextSimilarity.Normalize(v);
                if (norm.Length > 0) _variants.Add(norm);
            }
        }

        /// <summary>
        /// True if the transcript contains the wake phrase. <paramref name="remainder"/>
        /// is whatever the user said after it (normalised) — often the actual question,
        /// e.g. "hey claude what's the capital of australia".
        /// </summary>
        public bool TryMatch(string transcript, out string remainder)
        {
            remainder = "";
            string norm = TextSimilarity.Normalize(transcript);
            if (norm.Length == 0) return false;

            // 1. Direct substring match on any variant.
            foreach (var variant in _variants)
            {
                int idx = norm.IndexOf(variant, System.StringComparison.Ordinal);
                if (idx >= 0)
                {
                    remainder = norm.Substring(idx + variant.Length).Trim();
                    return true;
                }
            }

            // 2. Fuzzy match: slide a window of N tokens over the transcript and
            //    compare to each variant with a small edit-distance budget.
            string[] tokens = norm.Split(' ');
            foreach (var variant in _variants)
            {
                int windowSize = variant.Split(' ').Length;
                for (int i = 0; i + windowSize <= tokens.Length; i++)
                {
                    string window = string.Join(" ", tokens, i, windowSize);
                    if (TextSimilarity.Levenshtein(window, variant) <= MaxEditDistance)
                    {
                        remainder = string.Join(" ", tokens, i + windowSize,
                            tokens.Length - i - windowSize).Trim();
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
