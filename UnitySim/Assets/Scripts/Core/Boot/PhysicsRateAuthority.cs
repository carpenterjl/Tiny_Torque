using UnityEngine;

namespace AIHWSim.Core.Boot
{
    /// <summary>
    /// Who wrote <c>Time.fixedDeltaTime</c>, and did anyone disagree.
    ///
    /// <c>SimulationRunner.ConfigureRates</c> writes the physics step, and the
    /// step is a property of the PROCESS, not of a runner. A scene with more
    /// than one runner therefore runs at whichever rate started last, and the
    /// loser is silently re-timed — every dt-dependent term in its controller
    /// (integrators, derivatives, odometry) scaled by the ratio, with nothing on
    /// screen to say so. <c>DebugVehicleSpawner</c> documents this and works
    /// around it by adopting the scene's existing rate; a race with eight bots
    /// is fine because every rig is handed the same two numbers.
    ///
    /// <b>This changes nothing.</b> The write happens exactly as it always did,
    /// at the same moment, with the same value. All that is added is a warning
    /// the first time two live requests differ — turning a comment into
    /// something the log will actually tell you.
    ///
    /// <b>Why a warning rather than a fix.</b> There is no correct arbitration
    /// here: refusing the second write breaks the P9 and A7 timestep sweeps,
    /// which change the step ON PURPOSE and are the tests that prove the
    /// integrator is converged. Preferring the first breaks a debug car dropped
    /// into a running scene. The genuine fix is that each scene names one rate —
    /// which is what <see cref="DrivingSceneDescriptor"/> now lets it do — and
    /// until every scene does, the useful thing is to be told.
    ///
    /// <b>It keys on WHO asks, not on what changed</b>, and the first run of
    /// this proved why: <c>ConfigureRates</c> deliberately runs twice on every
    /// runner — once in <c>Awake</c> on the component's defaults and again in
    /// <c>Start</c> on the values the builder assigned in between — so a check
    /// that only compared rates called every single rig in the project a
    /// conflict (500 then 400, twenty times in one [PHYS] run). A rate is a
    /// conflict when a DIFFERENT object wants a different one.
    ///
    /// Timestep sweeps stay silent for the same reason rather than by
    /// exception: P9 and A7 change the step repeatedly on one runner, which is
    /// one object changing its mind, not two disagreeing.
    /// </summary>
    public static class PhysicsRateAuthority
    {
        private static int _rate;
        private static int _ownerId;
        private static string _owner;
        private static bool _warned;

        /// <summary>
        /// Record a request and apply it. Returns the step that is now in force,
        /// which is always the requested one — see the class note for why this
        /// does not arbitrate.
        /// </summary>
        public static float Apply(int physicsRateHz, Object requester)
        {
            string who = requester != null ? requester.name : "(unnamed)";
            int id = requester != null ? requester.GetInstanceID() : 0;
            if (_rate != 0 && _rate != physicsRateHz && id != _ownerId && !_warned)
            {
                _warned = true;
                Debug.LogWarning(
                    $"[RATE] two live physics rates in one session: '{_owner}' asked for "
                    + $"{_rate} Hz and '{who}' asks for {physicsRateHz} Hz. "
                    + "Time.fixedDeltaTime is global, so the second one wins and the first "
                    + "rig is now being stepped at a rate its control period was not "
                    + "derived from. If that is deliberate (a timestep sweep) ignore this; "
                    + "otherwise give the scene one PhysicsSettings asset and let every "
                    + "rig read it.");
            }
            _rate = physicsRateHz;
            _owner = who;
            _ownerId = id;

            Time.fixedDeltaTime = 1f / physicsRateHz;
            return Time.fixedDeltaTime;
        }

        /// <summary>Forget the session's rate. Called when play mode starts, so
        /// the warning is about THIS session rather than about a rate left over
        /// from the last one — statics survive a play-mode exit in the editor,
        /// and a false positive on the first frame would train anyone to ignore
        /// the real one.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            _rate = 0;
            _ownerId = 0;
            _owner = null;
            _warned = false;
        }
    }
}
