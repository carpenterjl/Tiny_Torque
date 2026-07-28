using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AIHWSim.UI
{
    /// <summary>
    /// Full-screen fade for scene loads and menu page changes, replacing the
    /// hard cuts. Lives on the persistent TinyTorqueRuntime GO so it keeps
    /// drawing straight through a synchronous SceneManager.LoadScene. Driven
    /// by unscaled time (the pause menu runs at timeScale 0) and drawn at
    /// GUI.depth -1000 so it covers every other OnGUI in the frame.
    /// </summary>
    public sealed class ScreenFade : MonoBehaviour
    {
        private static ScreenFade _instance;

        private float _alpha;         // current black overlay strength
        private bool _busy;

        private void Awake() { _instance = this; }
        private void OnDestroy() { if (_instance == this) _instance = null; }

        /// <summary>
        /// Fade to black over <paramref name="outSec"/>, run <paramref name="mid"/>
        /// (typically a GameFlow.Load*), fade back in. If a fade is already
        /// running, or there is no runtime object (batch mode), the action just
        /// runs immediately — a fade is polish, never a gate.
        /// </summary>
        public static void To(Action mid, float outSec = 0.25f, float inSec = 0.3f)
        {
            UiRuntime.Ensure();
            if (_instance == null || _instance._busy) { mid?.Invoke(); return; }
            _instance.StartCoroutine(_instance.Run(mid, outSec, inSec));
        }

        /// <summary>Quick dip for same-scene page changes.</summary>
        public static void Dip(Action mid) => To(mid, 0.10f, 0.14f);

        private IEnumerator Run(Action mid, float outSec, float inSec)
        {
            _busy = true;
            for (float t = 0f; t < outSec; t += Time.unscaledDeltaTime)
            {
                _alpha = Mathf.Clamp01(t / outSec);
                yield return null;
            }
            _alpha = 1f;

            // Even if the action throws, the fade-in below must still run —
            // a stuck black screen is worse than whatever just failed.
            try { mid?.Invoke(); }
            catch (Exception e) { Debug.LogException(e); }

            // One frame so a freshly loaded scene gets to draw beneath the
            // black before it starts lifting.
            yield return null;

            for (float t = 0f; t < inSec; t += Time.unscaledDeltaTime)
            {
                _alpha = 1f - Mathf.Clamp01(t / inSec);
                yield return null;
            }
            _alpha = 0f;
            _busy = false;
        }

        private void OnGUI()
        {
            if (_alpha <= 0f) return;
            GUI.depth = -1000;
            var c = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, _alpha);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = c;
        }
    }
}
