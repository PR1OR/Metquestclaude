using System;
using System.Text;
using UnityEngine.Networking;

namespace QuestClaude
{
    /// <summary>
    /// Custom DownloadHandlerScript that surfaces streamed bytes as they arrive —
    /// the pattern required to get incremental SSE on Android/IL2CPP (guide §5.5).
    /// Unity invokes ReceiveData on the main thread, so callbacks are safe to use
    /// directly from game code.
    /// </summary>
    public class SseDownloadHandler : DownloadHandlerScript
    {
        public event Action OnFirstBytes;

        private readonly SseParser _parser = new SseParser();
        private readonly StringBuilder _rawCapture = new StringBuilder();
        private const int RawCaptureCap = 4096;
        private bool _gotBytes;

        public SseDownloadHandler(Action<string, string> onEvent) : base(new byte[16 * 1024])
        {
            _parser.OnEvent += onEvent;
        }

        /// <summary>First few KB of the raw body — used to parse the proxy's JSON error
        /// envelope when the response turns out not to be an SSE stream (HTTP >= 400).</summary>
        public string RawText => _rawCapture.ToString();

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return false;

            if (!_gotBytes)
            {
                _gotBytes = true;
                OnFirstBytes?.Invoke();
            }

            if (_rawCapture.Length < RawCaptureCap)
            {
                int take = Math.Min(dataLength, RawCaptureCap - _rawCapture.Length);
                _rawCapture.Append(Encoding.UTF8.GetString(data, 0, take));
            }

            _parser.Feed(data, dataLength);
            return true;
        }

        protected override byte[] GetData() => null;
    }
}
