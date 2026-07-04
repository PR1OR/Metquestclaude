using UnityEngine;
using UnityEngine.UI;

namespace QuestClaude
{
    /// <summary>
    /// Minimal in-VR status panel (guide §5.8): status chip, live transcript,
    /// streamed reply, connectivity dot. The panel follows the head *lazily* —
    /// it repositions only when the user turns far enough away, smooth-damped,
    /// never rigidly glued to the head (which is nauseating).
    /// </summary>
    public class UIController : MonoBehaviour
    {
        [Header("Panel (world-space canvas root)")]
        public Transform panel;
        public float followDistance = 1.2f;
        public float heightOffset = -0.1f;
        [Tooltip("Reposition when the panel drifts this many degrees off the view direction.")]
        public float repositionAngle = 30f;
        public float smoothTime = 0.35f;

        [Header("Widgets")]
        public Text statusText;
        public Text transcriptText;
        public Text replyText;
        public Image connectivityDot;

        public static readonly Color IdleColor = new Color(0.65f, 0.65f, 0.65f);
        public static readonly Color ListeningColor = new Color(0.35f, 0.85f, 0.35f);
        public static readonly Color ThinkingColor = new Color(0.95f, 0.8f, 0.25f);
        public static readonly Color SpeakingColor = new Color(0.35f, 0.7f, 0.95f);
        public static readonly Color ErrorColor = new Color(0.9f, 0.35f, 0.3f);

        private Transform _head;
        private Vector3 _velocity;
        private bool _moving;

        private void Start()
        {
            if (Camera.main != null) _head = Camera.main.transform;
            SnapToTarget();
        }

        public void SetStatus(string label, Color color)
        {
            if (statusText != null)
            {
                statusText.text = label;
                statusText.color = color;
            }
        }

        public void SetTranscript(string text)
        {
            if (transcriptText != null) transcriptText.text = text ?? "";
        }

        public void SetReply(string text)
        {
            if (replyText != null) replyText.text = text ?? "";
        }

        public void SetOnline(bool online)
        {
            if (connectivityDot != null)
                connectivityDot.color = online ? ListeningColor : ErrorColor;
        }

        private void LateUpdate()
        {
            if (_head == null || panel == null)
            {
                if (Camera.main != null) _head = Camera.main.transform;
                return;
            }

            Vector3 target = TargetPosition();
            Vector3 toPanel = panel.position - _head.position;
            Vector3 flatForward = YawForward();

            float angle = Vector3.Angle(flatForward, new Vector3(toPanel.x, 0f, toPanel.z));
            float drift = Vector3.Distance(panel.position, target);

            if (!_moving && (angle > repositionAngle || drift > followDistance))
                _moving = true;

            if (_moving)
            {
                panel.position = Vector3.SmoothDamp(panel.position, target, ref _velocity, smoothTime);
                if (Vector3.Distance(panel.position, target) < 0.03f) _moving = false;
            }

            // Always face the user (billboard, yaw only).
            Vector3 look = panel.position - _head.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.001f)
            {
                panel.rotation = Quaternion.Slerp(panel.rotation,
                    Quaternion.LookRotation(look), Time.deltaTime * 4f);
            }
        }

        private Vector3 YawForward()
        {
            Vector3 f = _head.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
        }

        private Vector3 TargetPosition()
        {
            return _head.position + YawForward() * followDistance + Vector3.up * heightOffset;
        }

        private void SnapToTarget()
        {
            if (_head == null || panel == null) return;
            panel.position = TargetPosition();
            Vector3 look = panel.position - _head.position;
            look.y = 0f;
            if (look.sqrMagnitude > 0.001f) panel.rotation = Quaternion.LookRotation(look);
        }
    }
}
