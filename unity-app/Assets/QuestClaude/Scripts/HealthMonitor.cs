using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestClaude
{
    /// <summary>
    /// Periodic GET /health ping driving the connectivity dot (guide §8.2). When the
    /// proxy is unreachable the wake phrase still responds — the assistant just says
    /// it's offline instead of failing mid-answer.
    /// </summary>
    public class HealthMonitor : MonoBehaviour
    {
        public AssistantConfig config;

        public bool IsOnline { get; private set; }
        public event Action<bool> OnlineChanged;

        private Coroutine _loop;

        public void Begin()
        {
            if (_loop == null) _loop = StartCoroutine(PingLoop());
        }

        private IEnumerator PingLoop()
        {
            while (true)
            {
                yield return PingOnce();
                yield return new WaitForSecondsRealtime(Mathf.Max(5f, config.healthPingIntervalSeconds));
            }
        }

        private IEnumerator PingOnce()
        {
            if (string.IsNullOrEmpty(config.proxyBaseUrl))
            {
                SetOnline(false);
                yield break;
            }

            using (var req = UnityWebRequest.Get(config.proxyBaseUrl.TrimEnd('/') + "/health"))
            {
                req.timeout = 5;
                yield return req.SendWebRequest();
#if UNITY_2020_1_OR_NEWER
                bool ok = req.result == UnityWebRequest.Result.Success && req.responseCode == 200;
#else
                bool ok = !req.isNetworkError && !req.isHttpError && req.responseCode == 200;
#endif
                SetOnline(ok);
            }
        }

        private void SetOnline(bool online)
        {
            if (online == IsOnline) return;
            IsOnline = online;
            Debug.Log($"[QuestClaude] Connectivity: {(online ? "online" : "OFFLINE")}");
            OnlineChanged?.Invoke(online);
        }
    }
}
