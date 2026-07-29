using System.Collections.Generic;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Measures what a cosmetic rim actually does to a built car, because the
    /// showroom is the only place a human ever sees one and "it looks wrong" is
    /// not a fact you can fix. Builds every vehicle preset wearing every rim and
    /// reports, per wheel: whether the cosmetic mounted at all, how many stock
    /// renderers it hid, and where its geometry landed relative to the tyre.
    ///
    /// **Runs in PLAY MODE**, unlike <see cref="PartModelValidator"/>. The editor
    /// does not call Awake on a MonoBehaviour in edit mode, and
    /// <c>CarVehicle.Awake</c> is what builds the wheel visual holders that
    /// <c>CosmeticMounts.ApplyRims</c> parents to — so an edit-mode probe would
    /// measure a car with no wheels and report a fault that only it has.
    ///
    /// Run with (editor must be closed; NO -quit, for the same reason
    /// OpusMissionRunner documents — play mode has to keep the process alive):
    ///
    ///   Unity.exe -batchmode -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.CosmeticProbe.Report -logFile &lt;log&gt;
    ///
    /// then grep the log for "[COS] RESULT".
    /// </summary>
    public static class CosmeticProbe
    {
        private const string PendingKey = "AIHWSim.CosmeticProbe.Pending";
        private const string BatchKey = "AIHWSim.CosmeticProbe.Batch";

        /// <summary>Cars are built here, far from anything else in the scene.</summary>
        private static readonly Vector3 Origin = new Vector3(0f, -900f, 0f);

        /// <summary>Authored rim disc diameter at the author radius (0.033 m), from
        /// build_cosmetics.py's report: every rim measures 0.0409-0.0411 across its
        /// face. A ±15 % window catches a re-export that drifts either way without
        /// tripping on the one rim with a raised centre cap.</summary>
        private const float RimFaceMin = 0.0348f, RimFaceMax = 0.0472f;

        [MenuItem("Tools/AIHWSim/Probe cosmetic rims")]
        public static void RunFromMenu() => Begin(batch: false);

        public static void Report() => Begin(batch: true);

        private static void Begin(bool batch)
        {
            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(BatchKey, batch);
            // An empty scene: the probe needs nothing but a light-less void to
            // build cars in, and loading a real scene would drag its bootstrap in.
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Debug.Log("[COS] entering play mode");
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void Tick()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            if (!EditorApplication.isPlaying) return;
            // Let the scene settle a frame or two; Resources loads and the first
            // material creation both want a live player loop.
            if (Time.frameCount < 3) return;

            SessionState.SetBool(PendingKey, false);
            int fail = Probe();
            Debug.Log($"[COS] RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")}");
            SessionState.SetInt("AIHWSim.CosmeticProbe.Fail", fail);
            EditorApplication.ExitPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            if (!SessionState.GetBool(BatchKey, false)) return;
            SessionState.SetBool(BatchKey, false);
            int fail = SessionState.GetInt("AIHWSim.CosmeticProbe.Fail", 0);
            // Deferred: exiting from inside the state callback trips over the
            // editor's own teardown (OpusMissionRunner hit the same thing).
            EditorApplication.delayCall += () => EditorApplication.Exit(fail == 0 ? 0 : 1);
        }

        // ---- the probe itself ----------------------------------------------

        private static int Probe()
        {
            var rims = CosmeticCatalog.InSlot(CosmeticSlot.Rim);
            if (rims == null || rims.Count == 0)
            {
                Debug.LogError("[COS] FAIL: the catalog lists no rims");
                return 1;
            }

            var cars = new List<(string name, VehicleDesign design)>();
            cars.Add(("Stock Default", VehicleDesign.Default()));
            foreach (var p in VehiclePresets.All) cars.Add((p.name, p.build()));

            int fail = 0;
            foreach (var (carName, template) in cars)
            {
                foreach (var rim in rims)
                {
                    var design = template.Clone();
                    design.cosRim = rim.id;
                    fail += ProbeOne(carName, rim.id, design);
                }
                fail += ProbeTopperMount(carName, template);
            }
            return fail;
        }

        /// <summary>
        /// The topper must sit ON the body, not float over it. Written for the
        /// Legendary cars, whose whole-subtree bounds are dominated by folded
        /// appendages (Highwing's stalk wing, Rattletrap's boom) — the
        /// coupe-fraction mount put the hat metres of nothing above the roof
        /// until CosmeticMounts learned to measure the SHELL. Proved by
        /// raycast: body meshes are CPU-readable (the garage painter's
        /// contract), so temp MeshColliders are cooked and a ray dropped from
        /// above the mount must meet body geometry within a hat's-brim of it.
        /// Asserted only for authored mesh bodies; the primitive box/wedge
        /// stand-ins approximate a car too loosely to hold to millimetres.
        /// </summary>
        private static int ProbeTopperMount(string carName, VehicleDesign template)
        {
            var toppers = CosmeticCatalog.InSlot(CosmeticSlot.Topper);
            if (toppers == null || toppers.Count == 0) return 0;

            var design = template.Clone();
            design.cosTopper = toppers[0].id;
            var built = VehicleFactory.Build(design, Origin, Quaternion.identity,
                previewKinematic: true);
            var cols = new List<MeshCollider>();
            try
            {
                var root = built.root.transform;
                var holder = root.Find("BodyMesh");
                var mount = root.Find("cos_topper");
                if (holder == null || mount == null) return 0;   // no body / no asset

                bool authored = false;
                foreach (var mf in holder.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    string mn = mf.sharedMesh.name;
                    if (mn != "Cube" && mn != "Cylinder" && mn != "Sphere" && mn != "Capsule")
                        authored = true;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    cols.Add(mc);
                }
                if (!authored || cols.Count == 0) return 0;
                Physics.SyncTransforms();

                Vector3 from = mount.position + Vector3.up * 0.3f;
                float best = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(from, Vector3.down, 1f))
                {
                    if (!(hit.collider is MeshCollider mc) || !cols.Contains(mc)) continue;
                    best = Mathf.Min(best, mount.position.y - hit.point.y);
                }
                if (best == float.MaxValue)
                {
                    Debug.LogError($"[COS] FAIL mount {carName}: topper ray met no body geometry");
                    return 1;
                }
                // Sunk a touch into the roof is authored (TopperSink); floating
                // more than a brim above it is the defect. Open-top shapes are
                // report-only: the Baja has no roof to sit on, the ray drops
                // between the cage bars into the cockpit, and perching the hat
                // at cage height IS the best available placement.
                bool openTop = template.bodyShape == Vehicles.BodyShape.Baja ||
                               template.bodyShape == Vehicles.BodyShape.Buggy;
                if (!openTop && (best < -0.02f || best > 0.03f))
                {
                    Debug.LogError($"[COS] FAIL mount {carName}: topper sits " +
                                   $"{best * 1000f:0.0} mm above the surface below it");
                    return 1;
                }
                Debug.Log($"[COS] MOUNT {carName}: topper gap {best * 1000f:0.0} mm" +
                          (openTop ? " (open-top: report only)" : ""));
                return 0;
            }
            finally
            {
                if (built.root != null) Object.DestroyImmediate(built.root);
            }
        }

        private static int ProbeOne(string carName, string rimId, VehicleDesign design)
        {
            var built = VehicleFactory.Build(design, Origin, Quaternion.identity,
                previewKinematic: true);
            int fail = 0;
            try
            {
                var car = built.car;
                if (car == null || car.WheelCount == 0)
                {
                    Debug.LogError($"[COS] FAIL {carName} + {rimId}: car has no wheels");
                    return 1;
                }

                var sb = new StringBuilder();
                for (int i = 0; i < car.WheelCount; i++)
                {
                    var holder = car.GetWheelVisual(i);
                    if (holder == null)
                    {
                        sb.Append($" w{i}=NO-HOLDER");
                        fail++;
                        continue;
                    }

                    var rim = holder.Find("cos_rim");
                    if (rim == null)
                    {
                        sb.Append($" w{i}=NOT-MOUNTED[{Children(holder)}]");
                        fail++;
                        continue;
                    }

                    int on = 0, off = 0;
                    foreach (var r in rim.GetComponentsInChildren<Renderer>(true))
                        if (r.enabled) on++; else off++;

                    // Everything is expressed in the holder's frame: local X is
                    // the axle, so the rim's face is its X extreme on the
                    // outboard side. The tyre is measured through the same helper
                    // the mount uses, which reads the wheel mesh alone — a naive
                    // "everything under the holder" box would report the MOTOR CAN
                    // on a powered wheel, which sticks out three times as far.
                    Bounds rimB = LocalBounds(holder, rim);
                    int hidden = HiddenCount(holder, rim);
                    float radius = RadiusOf(design, i);
                    float tyreFace = PartVisualFactory.TyreHalfWidth(holder, radius);
                    float face = Mathf.Max(Mathf.Abs(rimB.min.x), Mathf.Abs(rimB.max.x));
                    float disc = Mathf.Max(rimB.size.y, rimB.size.z);
                    float norm = disc * PartVisualFactory.WheelAuthorRadius / radius;

                    sb.Append($" w{i}=[rend {on}on/{off}off hid {hidden} " +
                              $"disc {disc:0.0000} norm {norm:0.0000} " +
                              $"face {face:0.0000} tyre {tyreFace:0.0000} " +
                              $"proud {(face - tyreFace) * 1000f:0.0}mm]");

                    if (on == 0) { sb.Append("!NO-VISIBLE-RENDERER"); fail++; }
                    if (norm < RimFaceMin || norm > RimFaceMax)
                    { sb.Append("!DISC-SIZE"); fail++; }
                    // The defect this probe was written for: a rim seated at the
                    // hub plane falls inside the tyre's aperture and vanishes
                    // behind the sidewall. It has to end up proud of the tyre.
                    if (face < tyreFace) { sb.Append("!RECESSED"); fail++; }
                    // HideStockRim must never take tyre geometry with it. It
                    // did: the Autopia's whitewall — the tyre's own sidewall
                    // band, split off only because it is its own material —
                    // was switched off by the keep-list with every cosmetic
                    // rim, leaving plain black tyres with a groove. Asserted
                    // by NAME, not structure, because the wheel families split
                    // their tyres differently and the next tyre-side material
                    // is exactly what this must catch.
                    string hiddenTyres = HiddenTyreNames(holder, rim);
                    if (hiddenTyres.Length > 0)
                    { sb.Append($"!TYRE-HIDDEN[{hiddenTyres}]"); fail++; }
                }

                string line = $"{carName} + {rimId}:{sb}";
                if (fail == 0) Debug.Log($"[COS] PASS {line}");
                else Debug.LogError($"[COS] FAIL {line}");
                return fail;
            }
            finally
            {
                if (built.root != null) Object.DestroyImmediate(built.root);
            }
        }

        /// <summary>Wheel i's radius, which sets both the rim's scale and the
        /// primitive tyre's width — so the probe can normalise a measured disc
        /// back to its authored size and know what it is comparing against.</summary>
        private static float RadiusOf(VehicleDesign d, int i)
        {
            if (d.wheels == null || i >= d.wheels.Count) return PartVisualFactory.WheelAuthorRadius;
            return Mathf.Max(0.01f, d.wheels[i].radius);
        }

        private static string Children(Transform holder)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < holder.childCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(holder.GetChild(i).name);
            }
            return sb.Length == 0 ? "empty" : sb.ToString();
        }

        /// <summary>How many renderers under the holder, outside the cosmetic, are
        /// switched off — i.e. what HideStockRim actually achieved.</summary>
        private static int HiddenCount(Transform holder, Transform except)
        {
            int n = 0;
            foreach (var r in holder.GetComponentsInChildren<Renderer>(true))
            {
                if (except != null && r.transform.IsChildOf(except)) continue;
                if (!r.enabled) n++;
            }
            return n;
        }

        /// <summary>Disabled renderers under the holder (outside the cosmetic)
        /// whose names read as tyre-family — which HideStockRim must never
        /// touch. Empty string = healthy.</summary>
        private static string HiddenTyreNames(Transform holder, Transform except)
        {
            var sb = new StringBuilder();
            foreach (var r in holder.GetComponentsInChildren<Renderer>(true))
            {
                if (except != null && r.transform.IsChildOf(except)) continue;
                if (r.enabled) continue;
                string n = r.gameObject.name.ToLowerInvariant();
                if (n.Contains("tire") || n.Contains("tyre") || n.Contains("whitewall"))
                {
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(r.gameObject.name);
                }
            }
            return sb.ToString();
        }

        private static Bounds LocalBounds(Transform frame, Transform subtree) =>
            PartVisualFactory.LocalRendererBounds(frame, subtree);
    }
}
