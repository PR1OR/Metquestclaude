using UnityEngine;

namespace QuestClaude
{
    /// <summary>
    /// Audio cues for wake-acknowledged, listening-started, and error (guide §5.8) —
    /// the user should never need to look at the panel. Assign clips in the
    /// Inspector, or leave empty and short sine beeps are generated at runtime so
    /// the app needs no audio assets.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class Earcons : MonoBehaviour
    {
        public AudioClip wakeClip;
        public AudioClip listeningClip;
        public AudioClip errorClip;

        [Range(0f, 1f)] public float volume = 0.6f;

        private AudioSource _source;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f; // UI sound, not spatialised

            if (wakeClip == null) wakeClip = GenerateBeep("earcon_wake", 880f, 0.12f);
            if (listeningClip == null) listeningClip = GenerateBeep("earcon_listen", 660f, 0.10f);
            if (errorClip == null) errorClip = GenerateBeep("earcon_error", 220f, 0.25f);
        }

        public void PlayWakeAcknowledged() => Play(wakeClip);
        public void PlayListening() => Play(listeningClip);
        public void PlayError() => Play(errorClip);

        private void Play(AudioClip clip)
        {
            if (clip != null) _source.PlayOneShot(clip, volume);
        }

        private static AudioClip GenerateBeep(string name, float frequency, float duration)
        {
            const int sampleRate = 44100;
            int sampleCount = Mathf.RoundToInt(sampleRate * duration);
            var samples = new float[sampleCount];
            int fade = Mathf.Min(sampleCount / 4, sampleRate / 100); // ~10 ms fades, no clicks

            for (int i = 0; i < sampleCount; i++)
            {
                float envelope = 1f;
                if (i < fade) envelope = (float)i / fade;
                else if (i > sampleCount - fade) envelope = (float)(sampleCount - i) / fade;
                samples[i] = Mathf.Sin(2f * Mathf.PI * frequency * i / sampleRate) * envelope * 0.5f;
            }

            var clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
