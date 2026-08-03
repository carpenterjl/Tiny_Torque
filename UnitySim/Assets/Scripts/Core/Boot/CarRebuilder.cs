using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core.Boot
{
    /// <summary>
    /// Replace a running car with one built from an edited design, in place,
    /// keeping its pose and its momentum.
    ///
    /// <b>Why a rebuild and not a setter.</b> Roughly half of what a
    /// <c>VehicleDesign</c> says is consumed exactly once — spring rates and
    /// travel become a <c>JointSpring</c>, mass and inertia become rigidbody
    /// state, body size becomes a <c>BoxCollider</c>, wheel positions become
    /// child transforms. <c>CarVehicle</c> has no re-apply path and giving it
    /// one would mean a second copy of <c>VehicleFactory</c> that has to agree
    /// with the first forever. Destroying and rebuilding is what
    /// <c>GarageBootstrap</c> already does for its preview, and it is honest:
    /// the car you are now driving was built by the same code as the one that
    /// spawned.
    ///
    /// <b>Refusing is a feature.</b> A car is not a lone object. It carries an
    /// <c>ArcadeRacer</c> or a <c>MatchRacer</c> that a director holds in a
    /// list; a LAN car is half of a pair on another machine; a firmware rig has
    /// a native controller whose integrators were sized for the car that is
    /// about to vanish. Rewiring one of those is not a checklist, it is a
    /// feature. So this refuses, loudly and by name, in every session that is
    /// not a plain solo human driving — which is exactly the session anyone
    /// tuning suspension is in.
    ///
    /// <b>The rewire list is the risk, and it is short only because of the
    /// refusals above.</b> Everything below either lives on the car (and dies
    /// with it) or is named here.
    /// </summary>
    public static class CarRebuilder
    {
        /// <summary>
        /// Can this rig be rebuilt? Returns false and explains, in a sentence
        /// naming the thing that would break, so the tuner can disable itself
        /// rather than fail at the moment somebody drags a slider.
        /// </summary>
        public static bool CanRebuild(PlayerRig rig, out string why)
        {
            why = null;
            if (rig == null || rig.car == null) { why = "there is no car"; return false; }
            if (rig.netSlot >= 0)
            {
                why = "this is a LAN session — the other machine's copy of this car would "
                      + "keep the old one, and nothing on the wire says a car was replaced";
                return false;
            }
            if (rig.arcade != null)
            {
                why = "the arcade director holds this car's ArcadeRacer in its own list, and "
                      + "that component dies with the car";
                return false;
            }
            if (rig.match != null)
            {
                why = "the mode director holds this car's MatchRacer (health, team, score) and "
                      + "that component dies with the car";
                return false;
            }
            if (rig.runner != null && rig.runner.loadControllerDll)
            {
                why = "a native controller is driving — its integrators were sized for the car "
                      + "that would be destroyed, and reloading it is a different operation";
                return false;
            }
            if (rig.slot != null && rig.slot.isBot)
            {
                why = "this is a bot, and its BotDriver holds the car it was constructed with";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Rebuild <paramref name="rig"/>'s car from <paramref name="design"/>.
        /// Returns the new car, or null if the rig was refused.
        ///
        /// <b>Momentum is preserved, the spawn point is not re-taken.</b> The
        /// new car inherits the old one's respawn pose rather than the pose it
        /// happened to be standing in — a slider dragged on the back straight
        /// must not move the start line.
        /// </summary>
        public static CarVehicle RebuildInPlace(PlayerRig rig, VehicleDesign design,
                                                PauseMenu pause = null)
        {
            if (!CanRebuild(rig, out string why))
            {
                Debug.LogWarning($"[REBUILD] refused: {why}. The live-tuning fields that need "
                                 + "a rebuild are inert in this session; the per-step ones "
                                 + "(grip, aero, brakes, assists) still work.");
                return null;
            }

            var old = rig.car;
            var body = old.GetComponent<Rigidbody>();

            // --- capture -----------------------------------------------------
            Vector3 pos = old.transform.position;
            Quaternion rot = old.transform.rotation;
            Vector3 vel = body != null ? body.linearVelocity : Vector3.zero;
            Vector3 spin = body != null ? body.angularVelocity : Vector3.zero;
            var (spawnPos, spawnRot) = old.Spawn;
            var assists = old.assists;

            var oldInput = rig.input;
            var source = oldInput != null ? oldInput.source : null;
            bool mouseSteer = oldInput != null && oldInput.enableMouseSteer;
            var chase = oldInput != null ? oldInput.chase : null;
            var lapTimer = oldInput != null ? oldInput.lapTimer : rig.lapTimer;
            int hornStyle = design.hornStyle;
            // Only restore what this rig actually had: the physics-debug and
            // test cars deliberately carry no HandlingFloor (it would re-assert
            // the arcade assist floor over a measurement), and adding one here
            // would silently turn traction control back on.
            bool hadFloor = old.GetComponent<HandlingFloor>() != null;

            // --- replace -----------------------------------------------------
            // Deactivate BEFORE Destroy, which is deferred to the end of the
            // frame: without this the old colliders and the new ones share a
            // frame in the same place, and PhysX resolves that overlap by
            // throwing both cars apart.
            old.gameObject.SetActive(false);
            Object.Destroy(old.gameObject);

            // Cloned: the caller keeps editing the object it handed in, and a
            // factory that ever normalises a field would otherwise write back
            // into a live Inspector panel and read as the value bouncing.
            var spawnDesign = design.Clone();
            if (rig.slot != null) rig.slot.design = spawnDesign;

            var built = VehicleFactory.Build(spawnDesign, pos, rot, previewKinematic: false);
            var car = built.car;
            car.SetSpawn(spawnPos, spawnRot);
            car.assists = assists;

            var input = car.gameObject.AddComponent<CarInput>();
            input.car = car;
            input.lapTimer = lapTimer;
            input.source = source;
            input.enableMouseSteer = mouseSteer;
            input.chase = chase;

            var audio = Audio.VehicleAudio.Attach(car.gameObject, car);
            if (audio != null) audio.hornStyle = hornStyle;

            // The slot now names the edited car, so a respawn or a saved
            // snapshot rebuilds what you tuned rather than what you started with.

            // --- rewire ------------------------------------------------------
            // PlayerRig is a class, so mutating it here reaches every holder —
            // the pause menu's list, the split-screen HUD, the mode HUDs. That
            // is load-bearing and worth knowing before anyone makes it a struct.
            rig.car = car;
            rig.input = input;
            rig.sensorRig = built.rig;

            if (rig.runner != null)
            {
                rig.runner.vehicleBehaviour = car;
                rig.runner.inputBehaviour = input;
                rig.runner.sensorRig = built.rig;
                // Field writes alone would leave the runner stepping a destroyed
                // component: Start cached three interface casts, a VehicleReset
                // subscription, the sensor manifest and the telemetry channels.
                rig.runner.Rebind(old);
            }

            if (chase != null) chase.target = car.transform;
            if (pause != null && pause.tunableBehaviour == null) pause.tunableBehaviour = car;

            // The handling floor holds the car directly and snapshots its
            // assists in Start, so it is rebuilt rather than repointed.
            if (hadFloor)
            {
                var floor = car.gameObject.AddComponent<HandlingFloor>();
                floor.car = car;
                floor.rig = rig;
            }

            // Momentum last: the factory builds inactive and CarVehicle.Awake
            // creates the Rigidbody on activation, so there is nothing to write
            // to before now.
            var newBody = car.GetComponent<Rigidbody>();
            if (newBody != null && !newBody.isKinematic)
            {
                newBody.linearVelocity = vel;
                newBody.angularVelocity = spin;
            }

            return car;
        }
    }
}
