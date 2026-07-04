using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace QuestClaude.EditorTools
{
    /// <summary>
    /// One-click scene scaffolding (guide §5.1 / §5.8). Creates the "QuestClaude
    /// Systems" object with every component wired together, plus the world-space
    /// status panel. The Meta XR camera rig and the Voice SDK components
    /// (AppDictationExperience, TTSService, TTSSpeaker) are added by the human
    /// afterwards — see unity-app/README.md for the Inspector wiring table.
    /// </summary>
    public static class SceneBootstrapper
    {
        private const string ConfigAssetPath = "Assets/QuestClaude/AssistantConfig.asset";

        [MenuItem("QuestClaude/1. Create Assistant Config")]
        public static void CreateConfig()
        {
            var existing = AssetDatabase.LoadAssetAtPath<AssistantConfig>(ConfigAssetPath);
            if (existing != null)
            {
                Selection.activeObject = existing;
                Debug.Log("[QuestClaude] AssistantConfig already exists — selected it.");
                return;
            }

            var config = ScriptableObject.CreateInstance<AssistantConfig>();
            AssetDatabase.CreateAsset(config, ConfigAssetPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = config;
            Debug.Log("[QuestClaude] Created AssistantConfig. Fill in the proxy URL and app token.");
        }

        [MenuItem("QuestClaude/2. Set Up Scene Objects")]
        public static void SetUpScene()
        {
            var config = AssetDatabase.LoadAssetAtPath<AssistantConfig>(ConfigAssetPath);
            if (config == null)
            {
                Debug.LogWarning("[QuestClaude] No AssistantConfig asset — run 'Create Assistant Config' first.");
                CreateConfig();
                config = AssetDatabase.LoadAssetAtPath<AssistantConfig>(ConfigAssetPath);
            }

            if (GameObject.Find("QuestClaude Systems") != null)
            {
                Debug.LogWarning("[QuestClaude] 'QuestClaude Systems' already exists in the scene.");
                return;
            }

            // ---- Systems object with all components ----
            var systems = new GameObject("QuestClaude Systems");
            Undo.RegisterCreatedObjectUndo(systems, "Create QuestClaude Systems");

            var claude = systems.AddComponent<ClaudeClient>();
            var voiceIn = systems.AddComponent<VoiceInput>();
            var voiceOut = systems.AddComponent<VoiceOutput>();
            var permissions = systems.AddComponent<PermissionManager>();
            var health = systems.AddComponent<HealthMonitor>();
            var earcons = systems.AddComponent<Earcons>();
            var manager = systems.AddComponent<ConversationManager>();

            claude.config = config;
            health.config = config;

            // ---- World-space UI panel ----
            var ui = BuildUi(systems.transform);

            manager.config = config;
            manager.claudeClient = claude;
            manager.voiceInput = voiceIn;
            manager.voiceOutput = voiceOut;
            manager.permissionManager = permissions;
            manager.healthMonitor = health;
            manager.ui = ui;
            manager.earcons = earcons;

            EditorUtility.SetDirty(systems);
            Selection.activeGameObject = systems;
            Debug.Log("[QuestClaude] Scene objects created. Next: add the Meta XR camera rig, " +
                      "an AppDictationExperience and a TTSSpeaker, then follow the wiring table in unity-app/README.md.");
        }

        private static UIController BuildUi(Transform parent)
        {
            var canvasGo = new GameObject("Assistant UI", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(parent, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var canvasRect = canvasGo.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(800, 460);
            canvasGo.transform.localScale = Vector3.one * 0.001f; // 0.8 m wide panel
            canvasGo.transform.position = new Vector3(0f, 1.5f, 1.2f);

            var panel = CreateImage(canvasGo.transform, "Panel", new Color(0.08f, 0.08f, 0.1f, 0.82f));
            Stretch(panel.rectTransform, Vector2.zero, Vector2.one, 0, 0, 0, 0);

            var status = CreateText(panel.transform, "StatusText", 34, TextAnchor.MiddleLeft);
            Place(status.rectTransform, new Vector2(0, 1), new Vector2(1, 1), 24, -76, -90, -12);

            var dot = CreateImage(panel.transform, "ConnectivityDot", UIController.ErrorColor);
            var dotRect = dot.rectTransform;
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(1, 1);
            dotRect.sizeDelta = new Vector2(26, 26);
            dotRect.anchoredPosition = new Vector2(-36, -40);

            var transcript = CreateText(panel.transform, "TranscriptText", 26, TextAnchor.UpperLeft);
            transcript.color = new Color(0.75f, 0.9f, 0.75f);
            Place(transcript.rectTransform, new Vector2(0, 1), new Vector2(1, 1), 24, -170, -24, -84);

            var reply = CreateText(panel.transform, "ReplyText", 26, TextAnchor.UpperLeft);
            Place(reply.rectTransform, new Vector2(0, 0), new Vector2(1, 1), 24, 16, -24, -178);

            var controller = canvasGo.AddComponent<UIController>();
            controller.panel = canvasGo.transform;
            controller.statusText = status;
            controller.transcriptText = transcript;
            controller.replyText = reply;
            controller.connectivityDot = dot;
            return controller;
        }

        private static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            return img;
        }

        private static Text CreateText(Transform parent, string name, int size, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.alignment = anchor;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            float left, float bottom, float right, float top)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(right, top);
        }

        private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            float left, float bottom, float right, float top)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(right, top);
        }
    }
}
