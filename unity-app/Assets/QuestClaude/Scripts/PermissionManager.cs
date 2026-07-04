using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace QuestClaude
{
    /// <summary>
    /// Runtime microphone permission handling (guide §5.3). The RECORD_AUDIO manifest
    /// entry is auto-added by Unity/the Voice SDK — do not hand-edit the manifest.
    /// On denial the app must show guidance and keep running, never crash or spin.
    /// </summary>
    public class PermissionManager : MonoBehaviour
    {
        public event Action Granted;
        public event Action Denied;

        public bool HasMicrophonePermission { get; private set; }

        public void RequestMicrophonePermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                HasMicrophonePermission = true;
                Granted?.Invoke();
                return;
            }

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ =>
            {
                HasMicrophonePermission = true;
                Granted?.Invoke();
            };
            callbacks.PermissionDenied += _ =>
            {
                HasMicrophonePermission = false;
                Denied?.Invoke();
            };
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
#else
            // Editor / non-Android: desktop mic needs no runtime permission.
            HasMicrophonePermission = true;
            Granted?.Invoke();
#endif
        }
    }
}
