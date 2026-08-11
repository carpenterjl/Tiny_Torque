using UnityEngine;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// One step of a driving tutorial, authored as a child of the scene's
    /// Tutorial root. Sibling order IS step order.
    ///
    /// Steps are scene objects rather than a ScriptableObject list or a table in
    /// code because these scenes are meant to be hand-edited into custom maps.
    /// Authored here, reordering a lesson is dragging a child up the hierarchy
    /// and moving an objective is dragging its collider — neither needs a code
    /// edit, and the step sits in the scene view next to the place it is talking
    /// about. It is the same marker-in-the-scene split the track markers use.
    /// </summary>
    public sealed class TutorialStep : MonoBehaviour
    {
        [Tooltip("Heading on the objective panel.")]
        public string title = "";

        [TextArea(2, 6)]
        [Tooltip("The explanation. {throttle} {brake} {steer} {handbrake} " +
                 "{respawn} {horn} {jump} {boost} {item} {pause} expand to " +
                 "whatever the player's controls are actually bound to.")]
        public string body = "";

        [Tooltip("Big centre-screen flash on completion. Blank for none.")]
        public string banner = "";

        public TutorialCondition condition = TutorialCondition.Continue;

        [Tooltip("TriggerVolume: the volume to reach.")]
        public TutorialTrigger trigger;

        [Tooltip("InputHeld: which control.")]
        public TutorialInput input = TutorialInput.Throttle;

        [Tooltip("InputHeld: axis level 0..1. SpeedReached: speed in m/s.")]
        public float amount = 0.5f;

        [Tooltip("InputHeld: how long to hold. Timer: how long to wait. " +
                 "Everything else: a minimum time on screen before the step may " +
                 "pass, so an objective that is already true still gets read.")]
        public float seconds = 1f;

        [Tooltip("Signal: the token. ScreenReached: the screen id. " +
                 "TelemetryObserved: the channel name.")]
        public string token = "";

        public TutorialStepData ToData() => new TutorialStepData
        {
            title = title,
            body = body,
            banner = banner,
            condition = condition,
            trigger = trigger,
            input = input,
            amount = amount,
            seconds = seconds,
            token = token,
        };

        /// <summary>Draw a line to the volume this step points at, so a scene
        /// full of steps reads as a route rather than a pile of empties.</summary>
        private void OnDrawGizmosSelected()
        {
            if (trigger == null) return;
            Gizmos.color = new Color(0.30f, 0.80f, 1f, 0.9f);
            Gizmos.DrawLine(transform.position, trigger.transform.position);
        }
    }
}
