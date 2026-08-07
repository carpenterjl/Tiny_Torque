using AIHWSim.Core;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// What the studio hands to a test drive, and what it takes back afterwards.
    ///
    /// <b>A static, deliberately, and the same one <c>GameFlow</c> is.</b> These
    /// are plain managed objects, so they survive <c>SceneManager.LoadScene</c>
    /// with no <c>DontDestroyOnLoad</c> and no serialisation. The alternative —
    /// writing the layout to disk on the way out and reading it back on the way in
    /// — would make an unsaved edit vanish on the round trip, which is exactly the
    /// thing "keeps the same car between scenes" is asking for.
    ///
    /// <b>The design is built once, on the way out.</b> A studio layout is not a
    /// vehicle: it has no wheels, no motor, no sensors. <see cref="TestDrive"/>
    /// takes whatever design the studio is working on (the stock RC by default),
    /// attaches the layout, and hands it to <c>GameFlow</c> — so the thing that
    /// drives is a real design that the track, the LAN and the snapshot system all
    /// already understand, wearing a body the studio shaped.
    /// </summary>
    public static class StudioSession
    {
        /// <summary>The layout to reopen when the studio next starts. Null means
        /// "start fresh".</summary>
        public static VehicleLayoutData Pending;

        /// <summary>The design the studio is dressing. Null = the stock RC.</summary>
        public static VehicleDesign Design;

        /// <summary>
        /// True while a test drive launched from the studio is in progress —
        /// consumed by the studio's bootstrap when it comes back, so a return
        /// reopens the car and a cold start does not.
        /// </summary>
        public static bool Returning;

        /// <summary>
        /// Statics outlive a Play session when domain reload is off, which would
        /// make the second Play of a session think it was returning from a drive.
        /// Same guard, and the same reason, as <c>GameFlow.ResetLaunchState</c>.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Pending = null;
            Design = null;
            Returning = false;
        }

        /// <summary>
        /// The design a layout should be worn by: the studio's own if it has one,
        /// otherwise the stock RC. Cloned, so the studio's copy and the one that
        /// goes racing cannot drift into each other.
        /// </summary>
        public static VehicleDesign DesignFor(VehicleLayoutData layout)
        {
            VehicleDesign d = (Design ?? VehicleDesign.Default()).Clone();
            d.bodyLayout = layout;

            // The body the layout was sculpted on becomes the design's body. Both
            // halves of the key pair, because VehicleDesign.Migrate writes the int
            // back from the string and a design whose two disagree is a file whose
            // two readers see different cars.
            if (layout != null && !string.IsNullOrEmpty(layout.carBasePrefabID))
            {
                Vehicles.BodyDef def = Vehicles.BodyCatalog.ById(layout.carBasePrefabID);
                if (def != null)
                {
                    d.bodyKey = def.id;
                    d.bodyShape = def.legacy;
                }
            }
            return d;
        }

        /// <summary>
        /// Leave for the track wearing this layout, and arrange to come back here.
        ///
        /// The track's pause menu already has a "garage" door that keeps
        /// <c>ActiveDesign</c>; <c>GameFlow.ReturnScene</c> only redirects where
        /// that door leads. So the whole round trip is two statics and no new
        /// plumbing on the driving side.
        /// </summary>
        public static void TestDrive(VehicleLayoutData layout)
        {
            Pending = layout;
            Returning = true;

            VehicleDesign d = DesignFor(layout);
            GameFlow.ActiveDesign = d;
            GameFlow.ReturnScene = GameFlow.BodyEditorSceneName;

            Debug.Log($"[StudioSession] Test drive: '{d.name}' on body " +
                      $"'{d.BodyKey}' with {(layout?.props?.Length ?? 0)} parts. " +
                      "Pause ▸ Garage comes back here.");
            GameFlow.LoadTrack();
        }

        /// <summary>
        /// Save this layout as a real vehicle — one the garage's Load list offers,
        /// the menu can race, and a LAN peer can be handed.
        ///
        /// <b>It goes to <c>&lt;project&gt;/UnitySim/Vehicles/</c>, not into the
        /// Unity Assets folder.</b> That is where every vehicle in this game lives:
        /// it is what <c>VehicleLibrary</c> reads, it is hand-editable and
        /// diffable, and it works in a shipped build — where there is no Assets
        /// folder and no AssetDatabase to import into. A studio creation that
        /// should become permanent CONTENT (a catalogue body other players can
        /// pick) is a different job with a different tool: the Blender export goes
        /// through Asset Studio, which is also editor-only, and is documented under
        /// asset authoring.
        ///
        /// Returns the path written, or null.
        /// </summary>
        public static string SaveAsVehicle(string vehicleName, VehicleLayoutData layout)
        {
            VehicleDesign d = DesignFor(layout);
            d.name = string.IsNullOrWhiteSpace(vehicleName) ? "Studio Vehicle" : vehicleName.Trim();
            try
            {
                string path = VehicleLibrary.Save(d);
                Design = d;
                Debug.Log($"[StudioSession] Saved vehicle '{d.name}' — body '{d.BodyKey}', " +
                          $"{(layout?.props?.Length ?? 0)} parts, " +
                          $"{(layout?.offsetIndex?.Length ?? 0)} sculpted vertices → {path}");
                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[StudioSession] Could not save vehicle '{vehicleName}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The layout the studio should open with, or null for a fresh start.
        /// Consumes <see cref="Returning"/>: asking twice is a cold start the
        /// second time.
        /// </summary>
        public static VehicleLayoutData TakePending()
        {
            if (!Returning) return null;
            Returning = false;

            // Prefer the design that has just been driven — a lap may have gone
            // through the garage, and whatever the player did there is the more
            // recent truth about this car.
            if (GameFlow.ActiveDesign != null)
            {
                Design = GameFlow.ActiveDesign;
                if (GameFlow.ActiveDesign.HasBodyLayout) Pending = GameFlow.ActiveDesign.bodyLayout;
            }
            return Pending;
        }
    }
}
