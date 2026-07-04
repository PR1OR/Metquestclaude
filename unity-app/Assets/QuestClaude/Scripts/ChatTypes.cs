using System.Collections.Generic;
using System.Text;

namespace QuestClaude
{
    [System.Serializable]
    public class ChatMessage
    {
        public string role;    // "user" | "assistant"
        public string content;

        public ChatMessage(string role, string content)
        {
            this.role = role;
            this.content = content;
        }
    }

    /// <summary>
    /// Hand-rolled JSON for the proxy request body — JsonUtility can't skip optional
    /// fields, and the payload is trivial. The proxy treats an empty model string as
    /// "use the default".
    /// </summary>
    public static class ChatJson
    {
        public static string BuildChatRequest(IReadOnlyList<ChatMessage> messages, string model)
        {
            var sb = new StringBuilder(256);
            sb.Append("{\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":\"").Append(messages[i].role)
                  .Append("\",\"content\":\"").Append(Escape(messages[i].content)).Append("\"}");
            }
            sb.Append(']');
            if (!string.IsNullOrEmpty(model))
            {
                sb.Append(",\"model\":\"").Append(Escape(model)).Append('"');
            }
            sb.Append('}');
            return sb.ToString();
        }

        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // --- DTOs for parsing Anthropic SSE `data:` payloads with JsonUtility ---

    [System.Serializable]
    public class SseEventData
    {
        public string type;      // "content_block_delta", "message_stop", "error", ...
        public SseDelta delta;   // populated for content_block_delta
        public SseError error;   // populated for error events
    }

    [System.Serializable]
    public class SseDelta
    {
        public string type;      // "text_delta"
        public string text;
    }

    [System.Serializable]
    public class SseError
    {
        public string type;
        public string message;
    }

    // Proxy error body: { "error": { "code", "status", "message", "speak" } }
    [System.Serializable]
    public class ProxyErrorEnvelope
    {
        public ProxyError error;
    }

    [System.Serializable]
    public class ProxyError
    {
        public string code;
        public int status;
        public string message;
        public string speak;
    }
}
