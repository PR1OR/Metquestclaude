using System;
using UnityEngine;
using UnityEngine.Events;

namespace QuestClaude
{
    /// <summary>
    /// Bridge between the app and the Meta Voice SDK's AppDictationExperience
    /// (guide §5.4). Deliberately has NO compile-time dependency on the SDK —
    /// wiring happens in the Inspector so the project compiles before the SDK is
    /// imported and survives SDK API renames:
    ///
    ///   VoiceInput.onActivateDictation    → AppDictationExperience.Activate()
    ///   VoiceInput.onDeactivateDictation  → AppDictationExperience.Deactivate()
    ///
    ///   AppDictationExperience Dictation Events (Inspector):
    ///     OnPartialTranscription (string) → VoiceInput.HandlePartialTranscription
    ///     OnFullTranscription   (string)  → VoiceInput.HandleFullTranscription
    ///     OnDictationSessionStopped       → VoiceInput.HandleDictationSessionStopped
    ///     OnError                         → VoiceInput.HandleErrorMessage
    /// </summary>
    public class VoiceInput : MonoBehaviour
    {
        [Header("Wire to the Voice SDK (see unity-app/README.md wiring table)")]
        public UnityEvent onActivateDictation;
        public UnityEvent onDeactivateDictation;

        public event Action<string> PartialTranscription;
        public event Action<string> FinalTranscription;
        public event Action<string> DictationError;
        public event Action DictationStopped;

        /// <summary>True while we have asked the SDK to listen.</summary>
        public bool IsActive { get; private set; }

        public void Activate()
        {
            if (IsActive) return;
            IsActive = true;
            onActivateDictation?.Invoke();
        }

        public void Deactivate()
        {
            if (!IsActive) return;
            IsActive = false;
            onDeactivateDictation?.Invoke();
        }

        // ---- Entry points invoked BY the Voice SDK via Inspector-wired events ----

        public void HandlePartialTranscription(string text)
        {
            if (!string.IsNullOrEmpty(text)) PartialTranscription?.Invoke(text);
        }

        public void HandleFullTranscription(string text)
        {
            if (!string.IsNullOrEmpty(text)) FinalTranscription?.Invoke(text);
        }

        public void HandleDictationSessionStopped()
        {
            IsActive = false;
            DictationStopped?.Invoke();
        }

        public void HandleErrorMessage(string message)
        {
            IsActive = false;
            DictationError?.Invoke(message ?? "dictation error");
        }
    }
}
