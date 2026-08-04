using UnityEngine;

namespace AIHWSim.Modes
{
    /// <summary>
    /// Holds world gravity at <see cref="ModeConfig.ArenaGravityScale"/> for as
    /// long as an arena match is running, and puts it back afterwards.
    ///
    /// <b>Why a component and not two lines in the director.</b> Gravity is a
    /// global, and a global that a mode changes is a global some other scene
    /// inherits — a moon-gravity derby followed by a circuit race would silently
    /// retune every car on that circuit. A component has an <c>OnDestroy</c>, and
    /// putting the restore there means the value is handed back when the match
    /// object dies for ANY reason: the mode ending, the scene unloading, a
    /// domain reload in the editor, or the player quitting to the menu. The
    /// directors are also free to add their own <c>OnDestroy</c> without
    /// shadowing a base-class one, which is the trap this avoids.
    ///
    /// <b>Scale 1 writes nothing.</b> Not "writes the same value" — writes
    /// nothing, and never captures a baseline. So a project that leaves this
    /// alone (which is every project until somebody drags the slider) cannot be
    /// affected by this file at all, and the physics gates stay bit-identical by
    /// construction rather than by measurement.
    ///
    /// <b>The cars are authored at 1 g.</b> Their springs, ride heights and tyre
    /// loads all assume it, so a low-gravity arena gives light, skating,
    /// long-flying cars. That is the effect people want from the knob; it is
    /// worth knowing it is not a free parameter of the vehicle model.
    /// </summary>
    public sealed class ArenaGravity : MonoBehaviour
    {
        private Vector3 _original;
        private bool _held;

        /// <summary>Put one on <paramref name="host"/> if it has none. Idempotent
        /// so a director can call it from every match start.</summary>
        public static ArenaGravity Ensure(GameObject host)
        {
            if (host == null) return null;
            return host.GetComponent<ArenaGravity>() ?? host.AddComponent<ArenaGravity>();
        }

        private void OnEnable()
        {
            Core.Config.TuningBus.Changed += OnTuningChanged;
            Apply();
        }

        private void OnDisable()
        {
            Core.Config.TuningBus.Changed -= OnTuningChanged;
            Release();
        }

        private void OnTuningChanged(ScriptableObject _) => Apply();

        /// <summary>Re-applied from the ORIGINAL each time, never from the
        /// current value: scaling what is already scaled compounds, and a slider
        /// dragged across a range would walk gravity off to nothing.</summary>
        private void Apply()
        {
            float scale = ModeConfig.ArenaGravityScale;
            if (Mathf.Approximately(scale, 1f)) { Release(); return; }
            if (!_held) { _original = Physics.gravity; _held = true; }
            Physics.gravity = _original * scale;
        }

        private void Release()
        {
            if (!_held) return;
            Physics.gravity = _original;
            _held = false;
        }
    }
}
