using System;
using System.Text;

namespace QuestClaude
{
    /// <summary>
    /// Incremental Server-Sent Events parser. Bytes go in as they arrive off the
    /// wire (any chunk boundaries, multi-byte UTF-8 safe); complete events come
    /// out as (eventName, data) pairs. Pure C# — no Unity dependencies — so it can
    /// be exercised in Edit Mode tests.
    /// </summary>
    public class SseParser
    {
        public event Action<string, string> OnEvent;

        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new StringBuilder(256);
        private readonly StringBuilder _data = new StringBuilder(512);
        private string _eventName = "";

        public void Feed(byte[] bytes, int length)
        {
            if (bytes == null || length <= 0) return;
            int charCount = _decoder.GetCharCount(bytes, 0, length);
            if (charCount == 0) return;
            var chars = new char[charCount];
            _decoder.GetChars(bytes, 0, length, chars, 0);

            foreach (char c in chars)
            {
                if (c == '\n') EndLine();
                else if (c != '\r') _line.Append(c);
            }
        }

        private void EndLine()
        {
            string line = _line.ToString();
            _line.Length = 0;

            if (line.Length == 0)
            {
                // Blank line terminates an event.
                if (_data.Length > 0 || _eventName.Length > 0)
                {
                    string name = _eventName.Length > 0 ? _eventName : "message";
                    string data = _data.ToString();
                    _eventName = "";
                    _data.Length = 0;
                    OnEvent?.Invoke(name, data);
                }
                return;
            }

            if (line[0] == ':') return; // comment / keep-alive

            string field, value;
            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                field = line;
                value = "";
            }
            else
            {
                field = line.Substring(0, colon);
                value = line.Substring(colon + 1);
                if (value.StartsWith(" ")) value = value.Substring(1);
            }

            switch (field)
            {
                case "event":
                    _eventName = value;
                    break;
                case "data":
                    if (_data.Length > 0) _data.Append('\n');
                    _data.Append(value);
                    break;
                // "id" and "retry" are irrelevant here.
            }
        }
    }
}
