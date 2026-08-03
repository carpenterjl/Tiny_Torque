using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Drives a full-scale debug vehicle in <b>any</b> scene. Drop it on an empty
    /// GameObject (or use <c>Tools ▸ AIHWSim ▸ Debug Vehicle ▸ Add Spawner to Open
    /// Scene</c>) and press Play.
    ///
    /// <b>It is a tool, not content.</b> Nothing else in the game references it,
    /// and it is the only thing besides the physics-debug scene that can reach
    /// <see cref="DebugVehicles"/> — which stays out of
    /// <see cref="VehiclePresets.All"/>, so no picker, garage or save path can
    /// see the Tiguan. This component leaves that property intact by restoring
    /// whatever design it displaced when the scene unloads (see
    /// <see cref="OnDestroy"/>): without that, walking from a track back to the
    /// garage would carry a 1500 kg car into the vehicle editor.
    ///
    /// <b>Two modes, and the first one is the interesting one.</b>
    ///
    /// <see cref="Mode.ReplacePlayerCar"/> does not spawn anything. It runs
    /// before the scene's own bootstrap — hence
    /// <c>[DefaultExecutionOrder(-5000)]</c>, which is what makes it work at all —
    /// and swaps the design the bootstrap is about to build. The scene then
    /// composes the Tiguan through its own rig path, so the camera, HUD, lap
    /// timer, respawn, race director and pause menu all bind to it exactly as
    /// they would to an RC car. There is no parallel code path to keep in step,
    /// which is the entire argument for doing it this way rather than spawning a
    /// second car and fighting the first one for input.
    ///
    /// <see cref="Mode.SpawnAlongside"/> builds its own rig via
    /// <see cref="DebugVehicleRig"/> and takes the camera and the keyboard off
    /// whatever was being driven. Use it when the scene's car has to stay where
    /// it is, or in a scene with no bootstrap at all.
    ///
    /// <b>Scale is your problem, not the tool's.</b> Every shipped map is built
    /// for a 0.42 m RC car: the classic oval is 12 m across with a 2.5 m road, and
    /// a 4.49 m Tiguan on it is roughly a bus in a go-kart track. The circuits
    /// (Interlagos, Monza, Spa) are the maps where this is worth doing, because
    /// they were surveyed at full scale. Nothing here rescales a track — that
    /// would make the measurement meaningless, which is the whole reason the car
    /// is 1:1 in the first place.
    ///
    /// <b>What this cannot make honest.</b> A scene other than the physics-debug
    /// one has ground friction, terrain colliders, its own physics rate and a
    /// global 2 mm contact offset from <c>PhysicsTuning</c>. Any number measured
    /// here is a driving impression, not a measurement — the validated figures
    /// come from <c>PhysicsDebugScene</c>, whose ground is frictionless precisely
    /// so that all horizontal force comes from the tyre model.
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class DebugVehicleSpawner : MonoBehaviour
    {
        public enum Mode
        {
            /// <summary>Substitute the debug car for the player's, before the
            /// scene's bootstrap builds it. Keeps one rig and one camera.</summary>
            ReplacePlayerCar = 0,
            /// <summary>Build a second, self-contained rig and take over the
            /// camera and input.</summary>
            SpawnAlongside = 1,
        }

        /// <summary>Which reference vehicle. One entry today; the enum exists so
        /// adding a second full-scale car is an append rather than a rewrite.</summary>
        public enum Vehicle { VwTiguan = 0 }

        [Header("What to drive")]
        public Vehicle vehicle = Vehicle.VwTiguan;
        public Mode mode = Mode.ReplacePlayerCar;

        [Header("Spawn (SpawnAlongside only)")]
        [Tooltip("Offset from the car already in the scene — or from the world "
                 + "origin if there isn't one. +Z is ahead of it.")]
        public Vector3 spawnOffset = new Vector3(0f, 0f, 8f);
        [Tooltip("Point the main camera at the debug car and disable the other "
                 + "car's input, so one set of keys drives one car.")]
        public bool takeOverCamera = true;

        [Header("Rig (SpawnAlongside / no-bootstrap scenes)")]
        [Tooltip("Ignored when the scene already runs a SimulationRunner — its "
                 + "rate wins, because fixedDeltaTime is global.")]
        public int physicsRateHz = 400;
        public int controlRateHz = 100;
        public bool logCsv = false;

        [Header("Guards")]
        [Tooltip("Hide the pause menu's Tune sliders. CarVehicle.GetTunables "
                 + "clamps brake torque to [0.1, 3] N.m, so one nudge would take "
                 + "a 2400 N.m brake to 3 and silently ruin the car.")]
        public bool hideTuningSliders = true;
        public bool showHud = true;

        private CarVehicle _car;
        private VehicleDesign _displacedDesign;
        private PlayerSlot _displacedSlot;
        private VehicleDesign _slotOriginalDesign;
        private bool _armed;

        private VehicleDesign Design() => vehicle switch
        {
            _ => DebugVehicles.VwTiguan(),
        };

        // ==================== substitution, before the bootstrap ==============

        private void Awake()
        {
            if (mode != Mode.ReplacePlayerCar) return;

            // LAN composes its roster from the wire: every car's design arrives
            // as JSON from the machine that owns it, and the host adjudicates
            // against copies of cars it did not build. Substituting locally would
            // put a 1500 kg body on a car every other machine still thinks is an
            // RC toy — a desync, not a debug aid. Refuse rather than half-work.
            if (SessionConfig.Mode == SessionMode.LanHost ||
                SessionConfig.Mode == SessionMode.LanClient)
            {
                Debug.LogWarning("[DebugVehicleSpawner] ReplacePlayerCar is not "
                    + "available in a LAN session (designs come from the wire). "
                    + "Use SpawnAlongside, or drive it offline.");
                return;
            }

            var design = Design();

            // Two carriers, because the scene may use either. An empty roster is
            // synthesised from GameFlow.ActiveDesign by SessionConfig.ResolvePlayers;
            // a roster set by the menu is honoured verbatim and ignores it.
            _displacedDesign = GameFlow.ActiveDesign;
            GameFlow.ActiveDesign = design;

            // Only the first LOCAL HUMAN is displaced. Bots keep their cars, so a
            // race against the AI becomes "the Tiguan against the field" rather
            // than a grid of identical full-scale SUVs — which is both more useful
            // and much cheaper to render.
            foreach (var slot in SessionConfig.Players)
            {
                if (slot == null || slot.isBot || !slot.isLocal) continue;
                _displacedSlot = slot;
                _slotOriginalDesign = slot.design;
                slot.design = design;
                break;
            }

            _armed = true;
        }

        // ==================== after the bootstrap has built the scene =========

        private void Start()
        {
            // Runs before every other Start (execution order -5000), which is what
            // lets the PauseMenu guard below land before PauseMenu.Start caches
            // its ITunable.
            if (mode == Mode.ReplacePlayerCar)
            {
                _car = FindDebugCar();
                if (_car != null)
                {
                    // The scene built it through its own rig path, so it carries
                    // that path's assists and possibly a HandlingFloor.
                    DebugVehicleRig.ForceAssistsOff(_car);
                    GuardPauseMenu();
                    return;
                }

                // No bootstrap in this scene, or one that builds no car. Fall
                // through and stand up a rig ourselves rather than reporting
                // success and leaving an empty scene — "any scene" has to include
                // a scene with nothing in it.
                if (_armed)
                    Debug.Log("[DebugVehicleSpawner] No vehicle was built by this "
                              + "scene; building a self-contained rig instead.");
            }

            BuildStandalone();
            GuardPauseMenu();
        }

        private CarVehicle FindDebugCar()
        {
            foreach (var c in FindObjectsByType<CarVehicle>(FindObjectsSortMode.None))
                if (c.bodyShape == BodyShape.Tiguan) return c;
            return null;
        }

        /// <summary>
        /// A rig of our own: car, input, camera and simulation loop, from the
        /// same builder the physics-debug scene uses.
        /// </summary>
        private void BuildStandalone()
        {
            var reference = FindReferenceCar();
            var (pos, rot) = SpawnPose(reference);

            var rig = DebugVehicleRig.BuildCar(Design(), pos, rot);
            _car = rig.car;

            Camera cam = takeOverCamera || Camera.main == null ? TakeCamera() : null;
            var graph = cam != null
                ? (cam.GetComponent<Telemetry.GraphOverlay>()
                   ?? cam.gameObject.AddComponent<Telemetry.GraphOverlay>())
                : null;

            // Adopt the scene's rates if it already has a runner.
            // SimulationRunner writes the GLOBAL Time.fixedDeltaTime, so two
            // runners at different rates means whichever ran last decides — and
            // the loser is the scene that was already working. Nothing here is
            // worth re-timing someone else's integrator for.
            int phys = physicsRateHz, ctrl = controlRateHz;
            var existing = FindFirstObjectByType<SimulationRunner>();
            if (existing != null && existing.physicsRateHz > 0)
            {
                phys = existing.physicsRateHz;
                ctrl = existing.controlRateHz;
            }
            DebugVehicleRig.AttachRunner(ref rig, graph, phys, ctrl, logCsv);

            if (takeOverCamera) SilenceOtherInputs();
        }

        private CarVehicle FindReferenceCar()
        {
            foreach (var c in FindObjectsByType<CarVehicle>(FindObjectsSortMode.None))
                if (c != _car) return c;
            return null;
        }

        /// <summary>
        /// Where to put it. The offset is applied in the reference car's frame so
        /// "+8 ahead" follows the track rather than world +Z, and the height is
        /// then found by RAYCAST rather than assumed: every map this runs in has
        /// its own ground level, and a car spawned below a terrain is a car that
        /// explodes out of it on the first step.
        /// </summary>
        private (Vector3, Quaternion) SpawnPose(CarVehicle reference)
        {
            Vector3 basePos = reference != null ? reference.transform.position : transform.position;
            Quaternion rot = reference != null ? reference.transform.rotation : transform.rotation;
            Vector3 pos = basePos + rot * spawnOffset;

            // Ignore triggers: item boxes and checkpoints are not ground.
            if (Physics.Raycast(pos + Vector3.up * 60f, Vector3.down, out var hit, 200f,
                                ~0, QueryTriggerInteraction.Ignore))
                pos.y = hit.point.y;
            pos.y += DebugVehicles.TiguanChassisRestY;
            return (pos, rot);
        }

        private Camera TakeCamera()
        {
            Camera cam = Boot.SceneRig.CameraOrCreate();
            // 3 km: the circuits are long, and the chase distance below is sized
            // for a 4.5 m car rather than a 0.42 m one.
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, 1500f);
            var follow = cam.GetComponent<ChaseCamera>() ?? cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = _car.transform;
            follow.offset = new Vector3(0f, 3.2f, -9f);
            return cam;
        }

        /// <summary>
        /// One set of keys, one car. Disabling the other CarInput leaves that car
        /// standing rather than destroying it — a bot field, a lap timer's
        /// registered car and a race director's roster all keep working, and the
        /// original is still there to look at for scale.
        /// </summary>
        private void SilenceOtherInputs()
        {
            foreach (var input in FindObjectsByType<CarInput>(FindObjectsSortMode.None))
                if (input.car != _car) input.enabled = false;
        }

        /// <summary>
        /// The Tune panel would clamp this car's brakes into uselessness — see
        /// <see cref="hideTuningSliders"/>. Nulling the reference (rather than
        /// deleting the menu) keeps Resume, Restart, Settings and Quit working.
        /// </summary>
        private void GuardPauseMenu()
        {
            if (!hideTuningSliders) return;
            foreach (var pause in FindObjectsByType<PauseMenu>(FindObjectsSortMode.None))
                if (pause.tunableBehaviour is CarVehicle c && c == _car)
                    pause.tunableBehaviour = null;
        }

        /// <summary>
        /// Put back what we displaced. <see cref="GameFlow.ActiveDesign"/> and the
        /// roster both outlive the scene, so leaving the Tiguan in either would
        /// carry it into the garage — the one place the whole design says it must
        /// never reach.
        /// </summary>
        private void OnDestroy()
        {
            if (!_armed) return;
            GameFlow.ActiveDesign = _displacedDesign;
            if (_displacedSlot != null) _displacedSlot.design = _slotOriginalDesign;
        }

        private void OnGUI()
        {
            if (!showHud || _car == null) return;
            const float w = 300f, h = 74f;
            GUILayout.BeginArea(new Rect(Screen.width - w - 10f, 10f, w, h), GUI.skin.box);
            GUILayout.Label("DEBUG VEHICLE — VW Tiguan 1.4 TSI (1:1)");
            var rb = _car.GetComponent<Rigidbody>();
            float v = rb != null ? rb.linearVelocity.magnitude : 0f;
            GUILayout.Label($"speed {v:0.00} m/s   ({v * 3.6f:0.0} km/h)   "
                            + $"mass {(rb != null ? rb.mass : 0f):0} kg");
            GUILayout.Label("assists OFF · this scene's surfaces, not the probe's");
            GUILayout.EndArea();
        }
    }
}
