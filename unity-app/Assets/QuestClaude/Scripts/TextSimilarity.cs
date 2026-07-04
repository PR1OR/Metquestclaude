using System;
using System.Text;

namespace QuestClaude
{
    /// <summary>Normalised string similarity for wake-phrase fuzzy matching and the
    /// self-hearing guard (guide §6.1 / §6.2). Pure C#.</summary>
    public static class TextSimilarity
    {
        /// <summary>Lowercase, strip punctuation, collapse whitespace.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool lastWasSpace = true;
            foreach (char raw in s)
            {
                char c = char.ToLowerInvariant(raw);
                if (char.IsLetterOrDigit(c) || c == '\'')
                {
                    sb.Append(c);
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>1.0 = identical after normalisation, 0.0 = nothing in common.</summary>
        public static float Similarity(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a.Length == 0 && b.Length == 0) return 1f;
            int maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 1f;
            return 1f - (float)Levenshtein(a, b) / maxLen;
        }

        public static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[b.Length];
        }
    }
}
