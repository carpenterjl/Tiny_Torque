using System;
using UnityEngine;

namespace AIHWSim.Core.Config
{
    /// <summary>
    /// "A tuning asset was just edited" — one static event, raised by the
    /// override ScriptableObjects from <c>OnValidate</c>.
    ///
    /// <b>Why anything at all, when the accessors are already live.</b> Most
    /// tuned numbers are read at the moment they are used — a damage figure is
    /// read when a car is hit, a drift torque every physics step — so turning a
    /// <c>const</c> into a property is by itself enough to make dragging the
    /// slider land immediately. This exists for the minority that are read
    /// ONCE: the ball's mass is handed to a Rigidbody when the ball is created,
    /// the derby's health bar is a number copied into each racer at match
    /// start. Those consumers subscribe here and re-apply.
    ///
    /// So the rule for anyone adding a field to a tuning asset is short: if the
    /// value is consumed at the point of use, do nothing — it is already live.
    /// If it is copied into engine state, subscribe and copy it again.
    ///
    /// <b>Editor-only, and deliberately so.</b> <c>OnValidate</c> does not exist
    /// in a player build, so nothing raises this there and every subscriber's
    /// handler is dead weight of one delegate. The subscribers are written to be
    /// correct either way rather than compiled out, because a re-apply that runs
    /// only in the editor is a difference between what you tune and what you
    /// ship.
    ///
    /// Subscribers must unsubscribe in <c>OnDisable</c>/<c>OnDestroy</c>: this
    /// is a static event and a MonoBehaviour it holds is a leak that also throws
    /// on the next raise.
    /// </summary>
    public static class TuningBus
    {
        /// <summary>Raised after any tuning asset is edited. Carries the asset
        /// so a handler can ignore edits to something it does not read, though
        /// most handlers simply re-apply — re-applying the same numbers is a
        /// no-op and cheaper than the bookkeeping to avoid it.</summary>
        public static event Action<ScriptableObject> Changed;

        public static void Raise(ScriptableObject asset)
        {
            var handler = Changed;
            if (handler == null) return;
            // Walked one delegate at a time rather than invoked as a whole: a
            // subscriber that throws must not stop the ones behind it in the
            // list from hearing the edit, and must not leave the Inspector
            // looking as though the value did not take.
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<ScriptableObject>)d)(asset); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
