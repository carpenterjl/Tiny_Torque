using UnityEngine;
using AIHWSim.Core;

namespace AIHWSim.UI
{
    /// <summary>
    /// The one persistent runtime object ("TinyTorqueRuntime"). Before this,
    /// the only DontDestroyOnLoad object was NetSession, which doesn't exist
    /// offline — so nothing could run a coroutine across a scene load, keep a
    /// fade drawing through one, or keep music playing over one. This GO
    /// hosts <see cref="ScreenFade"/>, <see cref="CursorAutoHide"/> and (from
    /// the music milestone) MusicDirector.
    ///
    /// It deliberately carries NO AudioListener — every scene's camera owns
    /// that, and two listeners is a Unity warning storm. Skipped entirely in
    /// batch mode: the Opus regression and the validators want a world with
    /// nothing extra in it.
    /// </summary>
    public sealed class UiRuntime : MonoBehaviour
    {
        private static UiRuntime _instance;

        public static UiRuntime Ensure()
        {
            if (_instance != null) return _instance;
            if (Application.isBatchMode) return null;
            var go = new GameObject("TinyTorqueRuntime");
            Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<UiRuntime>();
            go.AddComponent<ScreenFade>();
            go.AddComponent<CursorAutoHide>();
            Audio.MusicDirector.Attach(go);
            return _instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureOnBoot() => Ensure();
    }

    /// <summary>
    /// Hides the OS cursor while the player is driving the UI from a gamepad,
    /// brings it back the moment the mouse moves. Also the source of truth for
    /// "is the pad the current input device" — MenuNav only draws its focus
    /// ring while that is true, so mouse users never see a phantom highlight.
    /// </summary>
    public sealed class CursorAutoHide : MonoBehaviour
    {
        public static bool PadIsLastInput { get; private set; }

        private void Update()
        {
            if (InputReader.AnyPadActivity())
            {
                PadIsLastInput = true;
                Cursor.visible = false;
            }
            else if (InputReader.MouseDelta().sqrMagnitude > 4f
                     || InputReader.LeftMouseHeld() || InputReader.RightMouseHeld())
            {
                PadIsLastInput = false;
                Cursor.visible = true;
            }
        }

        private void OnDestroy()
        {
            Cursor.visible = true;
        }
    }
}
