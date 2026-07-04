using System;
using System.Text;

namespace QuestClaude
{
    /// <summary>
    /// Accumulates streamed text deltas and emits sentence-sized chunks the moment a
    /// boundary is reached (guide §5.5 step 4). Dispatching the first sentence to TTS
    /// before the reply has finished arriving is what makes first-spoken-word latency low.
    /// Pure C#.
    /// </summary>
    public class SentenceChunker
    {
        public event Action<string> OnChunk;

        private readonly StringBuilder _buf = new StringBuilder(160);
        private readonly int _maxChunkChars;

        public SentenceChunker(int maxChunkChars = 120)
        {
            _maxChunkChars = maxChunkChars;
        }

        public void Add(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            foreach (char c in delta)
            {
                _buf.Append(c);

                if (IsBoundary(c))
                {
                    Emit();
                }
                else if (_buf.Length >= _maxChunkChars && c == ' ')
                {
                    // No sentence boundary in sight — cut at a word break.
                    Emit();
                }
            }
        }

        /// <summary>Emit whatever remains (call when the stream completes).</summary>
        public void Flush() => Emit();

        public void Reset() => _buf.Length = 0;

        private bool IsBoundary(char c)
        {
            if (c == '?' || c == '!' || c == '\n') return true;
            if (c != '.') return false;
            // Don't split decimals like "3.14" or abbreviations glued to digits.
            if (_buf.Length >= 2 && char.IsDigit(_buf[_buf.Length - 2])) return false;
            return true;
        }

        private void Emit()
        {
            string chunk = _buf.ToString().Trim();
            _buf.Length = 0;
            if (chunk.Length > 0) OnChunk?.Invoke(chunk);
        }
    }
}
