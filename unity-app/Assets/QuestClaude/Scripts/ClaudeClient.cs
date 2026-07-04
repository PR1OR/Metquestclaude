using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestClaude
{
    /// <summary>
    /// Streams a chat completion from the proxy (guide §5.5). Fires OnTextDelta for
    /// each streamed text fragment, OnComplete with the full reply, and OnError with
    /// a speakable message. Supports abort (barge-in) and first-byte/overall timeouts.
    /// </summary>
    public class ClaudeClient : MonoBehaviour
    {
        public AssistantConfig config;

        public event Action<string> OnTextDelta;
        public event Action<string> OnComplete;          // full reply text
        public event Action<long, string> OnError;       // (httpStatus or 0, speakable message)

        public bool IsBusy => _request != null;

        private UnityWebRequest _request;
        private Coroutine _routine;
        private StringBuilder _fullText;
        private bool _messageStopped;

        public void SendChat(IReadOnlyList<ChatMessage> messages)
        {
            Abort();
            _routine = StartCoroutine(SendRoutine(messages));
        }

        /// <summary>Cancel the in-flight request (barge-in / watchdog). No events fire.</summary>
        public void Abort()
        {
            if (_routine != null)
            {
                StopCoroutine(_routine);
                _routine = null;
            }
            if (_request != null)
            {
                var req = _request;
                _request = null;
                try { req.Abort(); } catch { /* already finished */ }
                req.Dispose();
            }
        }

        private IEnumerator SendRoutine(IReadOnlyList<ChatMessage> messages)
        {
            _fullText = new StringBuilder();
            _messageStopped = false;

            string url = config.proxyBaseUrl.TrimEnd('/') + "/chat";
            string json = ChatJson.BuildChatRequest(messages, config.model);

            var handler = new SseDownloadHandler(HandleSseEvent);
            bool gotFirstByte = false;
            handler.OnFirstBytes += () => gotFirstByte = true;

            var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
                downloadHandler = handler,
            };
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "text/event-stream");
            req.SetRequestHeader("X-App-Token", config.appToken);
            _request = req;

            var op = req.SendWebRequest();
            float start = Time.realtimeSinceStartup;
            bool timedOut = false;
            string timeoutMessage = "";

            while (!op.isDone)
            {
                float elapsed = Time.realtimeSinceStartup - start;
                if (!gotFirstByte && elapsed > config.firstByteTimeoutSeconds)
                {
                    timedOut = true;
                    timeoutMessage = "I didn't get a response in time.";
                    break;
                }
                if (elapsed > config.overallTimeoutSeconds)
                {
                    timedOut = true;
                    timeoutMessage = "That answer took too long, sorry.";
                    break;
                }
                yield return null;
            }

            _routine = null;
            if (_request != req)
            {
                // Aborted while yielding — a newer request owns the events now.
                req.Dispose();
                yield break;
            }
            _request = null;

            if (timedOut)
            {
                try { req.Abort(); } catch { }
                FireError(0, timeoutMessage, req);
                yield break;
            }

            long status = req.responseCode;
            bool transportError =
#if UNITY_2020_1_OR_NEWER
                req.result != UnityWebRequest.Result.Success;
#else
                req.isNetworkError || req.isHttpError;
#endif

            if (status >= 400)
            {
                // Proxy returned a JSON error envelope rather than a stream.
                string speak = "I'm having trouble reaching my brain right now.";
                try
                {
                    var env = JsonUtility.FromJson<ProxyErrorEnvelope>(handler.RawText);
                    if (env?.error != null && !string.IsNullOrEmpty(env.error.speak)) speak = env.error.speak;
                }
                catch { /* keep generic line */ }
                FireError(status, speak, req);
                yield break;
            }

            if (transportError && !_messageStopped)
            {
                // Connection dropped mid-stream. The manager keeps any partial text.
                FireError(0, "I lost my connection there.", req);
                yield break;
            }

            req.Dispose();
            OnComplete?.Invoke(_fullText.ToString());
        }

        private void FireError(long status, string speak, UnityWebRequest req)
        {
            req.Dispose();
            Debug.LogWarning($"[QuestClaude] ClaudeClient error status={status}: {speak}");
            OnError?.Invoke(status, speak);
        }

        private void HandleSseEvent(string eventName, string data)
        {
            if (string.IsNullOrEmpty(data)) return;

            SseEventData parsed;
            try { parsed = JsonUtility.FromJson<SseEventData>(data); }
            catch { return; }
            if (parsed == null) return;

            string type = string.IsNullOrEmpty(parsed.type) ? eventName : parsed.type;
            switch (type)
            {
                case "content_block_delta":
                    if (parsed.delta != null && parsed.delta.type == "text_delta"
                        && !string.IsNullOrEmpty(parsed.delta.text))
                    {
                        _fullText.Append(parsed.delta.text);
                        OnTextDelta?.Invoke(parsed.delta.text);
                    }
                    break;

                case "message_stop":
                    _messageStopped = true;
                    break;

                case "error":
                    string msg = parsed.error != null && !string.IsNullOrEmpty(parsed.error.message)
                        ? parsed.error.message : "stream error";
                    Debug.LogWarning($"[QuestClaude] SSE error event: {msg}");
                    break;

                // message_start / content_block_start / content_block_stop /
                // message_delta / ping — nothing to do.
            }
        }

        private void OnDestroy() => Abort();
    }
}
