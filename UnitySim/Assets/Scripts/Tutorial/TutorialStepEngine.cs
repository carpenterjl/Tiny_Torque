using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Tutorial
{
    /// <summary>
    /// Runs a list of steps: show one, watch for its condition, flash its banner,
    /// move on. The whole state machine, shared by the driving director and the
    /// menu overlay so the two kinds of tutorial cannot drift apart in how a step
    /// behaves.
    ///
    /// Plain C# rather than a MonoBehaviour — it has no scene presence of its
    /// own, and both hosts already have an Update to drive it from. That also
    /// means it can be stepped by a test without a scene.
    ///
    /// <b>Its clock is unscaled-but-pausable</b>: accumulated from
    /// <c>Time.deltaTime</c> by the host, so a paused game freezes the banner and
    /// the hold timers rather than eating them. That is the property
    /// <c>ArcadeFeedback</c> gets from <c>ArcadeDirector.Clock</c>, and the
    /// reason neither reads <c>Time.time</c>.
    ///
    /// <b>Conditions are polled, never subscribed.</b> Every one of them is a
    /// field read or a latched bool, so a poll costs nothing and there is no
    /// unsubscribe to forget when a step ends early — which matters because a
    /// skip can end any step at any moment.
    /// </summary>
    public sealed class TutorialStepEngine
    {
        /// <summary>How long a completion banner stays up.</summary>
        public const float BannerSeconds = 2.2f;

        private readonly List<TutorialStepData> _steps = new List<TutorialStepData>();

        /// <summary>Who is being taught. Null until the host binds a car, which
        /// is normal for an overlay tutorial and momentary for a driving one.</summary>
        private CarVehicle _car;
        private CarInput _input;

        private int _index;
        private float _clock;          // seconds since this step began
        private float _hold;           // seconds the InputHeld axis has been down
        private float _bannerUntil;    // engine-clock deadline
        private string _bannerText = "";
        private bool _continuePressed;
        private bool _finished;

        public IReadOnlyList<TutorialStepData> Steps => _steps;
        public int Index => _index;
        public int Count => _steps.Count;
        public bool Finished => _finished;

        /// <summary>The step on screen, or null when the run is over.</summary>
        public TutorialStepData Current =>
            !_finished && _index >= 0 && _index < _steps.Count ? _steps[_index] : null;

        /// <summary>Does the current step want a Continue button drawn?</summary>
        public bool WantsContinue =>
            Current != null && Current.condition == TutorialCondition.Continue;

        /// <summary>Banner text to flash, or "" for none.</summary>
        public string Banner => _clockNow < _bannerUntil ? _bannerText : "";

        /// <summary>0..1 through the banner's life, for the fade.</summary>
        public float BannerAlpha
        {
            get
            {
                float left = _bannerUntil - _clockNow;
                if (left <= 0f) return 0f;
                return Mathf.Clamp01(left / (BannerSeconds * 0.4f));
            }
        }

        /// <summary>Raised as each step completes, with the index of the step
        /// that just finished. The host saves the resume point from this.</summary>
        public event System.Action<int> StepCompleted;

        /// <summary>Raised once, when the last step completes.</summary>
        public event System.Action Completed;

        private float _clockNow;   // engine clock, monotone across steps

        // ---- setup ------------------------------------------------------------

        public void SetSteps(IEnumerable<TutorialStepData> steps)
        {
            _steps.Clear();
            if (steps != null) foreach (var s in steps) if (s != null) _steps.Add(s);
            _index = 0;
            _finished = _steps.Count == 0;
            BeginStep();
        }

        /// <summary>Bind the car the conditions read. Also tells every trigger
        /// volume whose crossings count.</summary>
        public void Bind(CarVehicle car, CarInput input)
        {
            _car = car;
            _input = input;
            foreach (var s in _steps)
                if (s.trigger != null) s.trigger.Watch(car);
        }

        /// <summary>
        /// Jump straight to a step, marking the ones before it done without
        /// running their conditions. This is how resume works: a player who quit
        /// at step 5 comes back to step 5, not to the gate they already drove
        /// through. Out-of-range indices clamp, so a progress file naming a step
        /// this build no longer has starts the tutorial over rather than
        /// finishing it instantly.
        /// </summary>
        public void FastForwardTo(int index)
        {
            if (_steps.Count == 0) return;
            _index = Mathf.Clamp(index, 0, _steps.Count - 1);
            _finished = false;
            BeginStep();
        }

        /// <summary>The Continue button was clicked. An explicit call rather than
        /// the engine reading GUI state, because the button lives in the host's
        /// layout and only the host knows when it is safe to say so.</summary>
        public void PressContinue() => _continuePressed = true;

        // ---- the loop -----------------------------------------------------------

        /// <summary>
        /// One tick. <paramref name="dt"/> is the host's delta — scaled, so pause
        /// holds everything.
        /// </summary>
        public void Tick(float dt)
        {
            _clockNow += dt;
            if (_finished) return;

            var step = Current;
            if (step == null) { _finished = true; Completed?.Invoke(); return; }

            _clock += dt;
            AccumulateHold(step, dt);

            // Every condition also has to wait out `seconds` as a minimum dwell,
            // so an objective that was already satisfied when the step opened
            // still gets long enough on screen to be read. Timer and InputHeld
            // consume the field themselves and are exempt.
            bool dwellDone = step.condition == TutorialCondition.Timer
                          || step.condition == TutorialCondition.InputHeld
                          || _clock >= Mathf.Min(step.seconds, MaxDwell);

            if (dwellDone && IsSatisfied(step)) CompleteStep(step);
        }

        /// <summary>Cap on the implicit dwell, so a step that borrowed `seconds`
        /// for a big hold time cannot also become a long silent wait.</summary>
        private const float MaxDwell = 2.5f;

        private void AccumulateHold(TutorialStepData step, float dt)
        {
            if (step.condition != TutorialCondition.InputHeld) return;
            float level = AxisLevel(step.input);
            if (level >= Mathf.Max(0.05f, step.amount)) _hold += dt;
            else _hold = 0f;      // the hold has to be continuous, or it is not a hold
        }

        private void CompleteStep(TutorialStepData step)
        {
            if (!string.IsNullOrEmpty(step.banner))
            {
                _bannerText = step.banner;
                _bannerUntil = _clockNow + BannerSeconds;
            }
            int done = _index;
            _index++;
            StepCompleted?.Invoke(done);

            if (_index >= _steps.Count)
            {
                _finished = true;
                Completed?.Invoke();
                return;
            }
            BeginStep();
        }

        private void BeginStep()
        {
            _clock = 0f;
            _hold = 0f;
            _continuePressed = false;
            // Latched signals reach back only to the start of the current step —
            // otherwise "paint the car" would be satisfied by a paint job from
            // three steps ago.
            TutorialSignals.Clear();
            var step = Current;
            if (step?.trigger != null) step.trigger.ResetLatch();
        }

        // ---- conditions ---------------------------------------------------------

        private bool IsSatisfied(TutorialStepData step)
        {
            switch (step.condition)
            {
                case TutorialCondition.TriggerVolume:
                    // A step pointed at nothing would block the tutorial forever;
                    // treat it as satisfied and let the [TUT] gate be the thing
                    // that complains about it.
                    return step.trigger == null || step.trigger.Entered;

                case TutorialCondition.InputHeld:
                    return _hold >= Mathf.Max(0.05f, step.seconds);

                case TutorialCondition.SpeedReached:
                    return _car != null && Mathf.Abs(_car.ForwardSpeed) >= step.amount;

                case TutorialCondition.Timer:
                    return _clock >= step.seconds;

                case TutorialCondition.Continue:
                    return _continuePressed;

                case TutorialCondition.Signal:
                    return TutorialSignals.WasRaised(step.token);

                case TutorialCondition.ScreenReached:
                    return TutorialSignals.OnScreen(step.token);

                case TutorialCondition.LobbyHosted:
                    return TutorialProbes.HostingLobby();

                case TutorialCondition.IpcConnected:
                    return TutorialProbes.IpcClientConnected();

                case TutorialCondition.TelemetryObserved:
                    return TutorialProbes.TelemetryLive(step.token);

                default:
                    return true;
            }
        }

        /// <summary>
        /// The 0..1 level of a named control, read through the driver input
        /// source rather than the keyboard — so a step asking for throttle is
        /// satisfied by a trigger, a key or a rebound key alike, and a tutorial
        /// never has to know what the player is holding.
        ///
        /// The three edge reads (respawn, jump, use-item) are safe to poll
        /// alongside the real consumer: <c>PlayerInputSource</c> answers them
        /// from <c>InputReader</c>'s GetKeyDown-shaped checks, which every caller
        /// in a frame sees. A LATCHING source would be a different story — but a
        /// tutorial always drives a local human, which is the one that reads
        /// through.
        /// </summary>
        private float AxisLevel(TutorialInput which)
        {
            var src = _input?.source;
            if (src == null) return 0f;
            switch (which)
            {
                case TutorialInput.Throttle: return Mathf.Max(0f, src.Throttle());
                case TutorialInput.Brake: return src.Brake();
                case TutorialInput.SteerLeft: return Mathf.Max(0f, -src.Steer());
                case TutorialInput.SteerRight: return Mathf.Max(0f, src.Steer());
                case TutorialInput.SteerEither: return Mathf.Abs(src.Steer());
                case TutorialInput.Handbrake: return src.Handbrake() ? 1f : 0f;
                case TutorialInput.Respawn: return src.RespawnPressed() ? 1f : 0f;
                case TutorialInput.Horn: return src.HornHeld() ? 1f : 0f;
                case TutorialInput.Jump: return src.JumpPressed() ? 1f : 0f;
                case TutorialInput.Boost: return src.BoostHeld() ? 1f : 0f;
                case TutorialInput.UseItem: return src.UseItemPressed() ? 1f : 0f;
                case TutorialInput.LookBack: return src.LookBackHeld() ? 1f : 0f;
                default: return 0f;
            }
        }
    }
}
