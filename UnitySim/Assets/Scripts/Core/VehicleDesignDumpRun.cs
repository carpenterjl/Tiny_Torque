using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Core
{
    /// <summary>
    /// Runtime half of the design dump: builds every vehicle design the project
    /// can produce and writes back the state the engine was actually handed —
    /// Rigidbody, chassis box and every WheelCollider field, plus what the
    /// design resolved to visually (mesh keys, render scale, measured extents,
    /// materials, aero coefficients — see <see cref="Appearance"/>).
    ///
    /// <b>Why this must run in play mode.</b> Everything a car IS happens in
    /// <see cref="CarVehicle.Awake"/>, and the editor does not call Awake outside
    /// play mode. Built in edit mode the cars come back carrying Unity's stock
    /// defaults — mass 1, angular damping 0.05 — so every design looks identical
    /// and the dump would cheerfully "prove" bit-identity across any change at
    /// all. That is a worse outcome than having no check, so it lives here,
    /// beside <see cref="MissionAutorun"/> and shaped like it: inert unless the
    /// editor half left a request file, and it CONSUMES that request before
    /// acting on it — one reader, one run.
    ///
    /// <b>Why it exists alongside the Opus mission.</b> The mission is the
    /// project's bit-identity gate and a better test of the integrated result
    /// than anything here, but it drives exactly ONE design. A new serialized
    /// field with a wrong initialiser would move the other nine presets, and
    /// every saved design in <c>UnitySim/Vehicles/</c>, without the mission
    /// shifting a micron. This closes that gap, and it is cheap — no physics
    /// steps, just build each car and read back what it was told.
    /// </summary>
    public static class VehicleDesignDumpRun
    {
        /// <summary>Where the editor half leaves the output path. A file rather
        /// than a static field because entering play mode triggers a domain
        /// reload that wipes statics.</summary>
        public static string RequestPath =>
            Path.Combine(Path.GetTempPath(), "aihwsim_design_dump_request.txt");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!File.Exists(RequestPath)) return;
            string outPath;
            try
            {
                outPath = File.ReadAllText(RequestPath).Trim();
                File.Delete(RequestPath);
            }
            catch { return; }
            if (string.IsNullOrEmpty(outPath)) return;

            int n = 0, failed = 0;
            try { Write(outPath, out n, out failed); }
            catch (System.Exception e)
            {
                Debug.LogError($"[DUMP] {e}");
                failed++;
            }
            Debug.Log($"[DUMP] {n} designs, {failed} failed -> {outPath}");

#if UNITY_EDITOR
            if (Application.isBatchMode)
                UnityEditor.EditorApplication.Exit(failed == 0 ? 0 : 2);
            else
                UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        private static void Write(string outPath, out int n, out int failed)
        {
            var sb = new StringBuilder();
            n = 0; failed = 0;
            foreach (var (label, design) in Designs())
            {
                sb.Append("=== ").Append(label).Append('\n');
                try { Dump(sb, design); n++; }
                catch (System.Exception e)
                {
                    sb.Append("  BUILD FAILED: ").Append(e.Message).Append('\n');
                    Debug.LogError($"[DUMP] {label}: {e.Message}");
                    failed++;
                }
            }
            File.WriteAllText(outPath, sb.ToString());
        }

        private static IEnumerable<(string, VehicleDesign)> Designs()
        {
            // The nine code presets...
            foreach (var (name, build) in VehiclePresets.All)
                yield return ("preset:" + name, build());

            // ...the fallback that is not in All but is reached everywhere...
            yield return ("default:Stock RC", VehicleDesign.Default());

            // ...and every design actually saved to disk. Read the directory
            // rather than VehicleLibrary.List(), which hides anything over 50 kg:
            // the point is to cover what is ON DISK, not what the UI will show.
            string dir = VehicleLibrary.Dir;
            if (!Directory.Exists(dir)) yield break;
            var files = Directory.GetFiles(dir, "*.json");
            System.Array.Sort(files, string.CompareOrdinal);
            foreach (string f in files)
            {
                VehicleDesign d = null;
                try { d = JsonUtility.FromJson<VehicleDesign>(File.ReadAllText(f)); }
                catch { }
                if (d != null) yield return ("saved:" + Path.GetFileName(f), d);
            }

            // ...and the full-scale reference car, which no picker and no save
            // file can reach. It is here for the APPEARANCE block: the Tiguan is
            // the one design that special-cases all three resolution sites at
            // once — scale 1 instead of the bodySize/authorSize divide, its own
            // token table, its own author radius — and those are exactly the
            // branches Phase 3 replaces. Without it the dump would cover the
            // migration everywhere except where it is hardest.
            //
            // Last in the enumeration on purpose: appended sections diff as pure
            // insertions against a dump taken before this line existed.
            yield return ("debug:VW Tiguan", DebugVehicles.VwTiguan());
        }

        private static void Dump(StringBuilder sb, VehicleDesign design)
        {
            var built = VehicleFactory.Build(design, Vector3.zero, Quaternion.identity,
                                             previewKinematic: false);
            var car = built.car;
            if (car == null) { sb.Append("  no car\n"); return; }
            try
            {
                var rb = car.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    Row(sb, "rb.mass", rb.mass);
                    Row(sb, "rb.centerOfMass", rb.centerOfMass);
                    Row(sb, "rb.inertiaTensor", rb.inertiaTensor);
                    Row(sb, "rb.inertiaTensorRotation", rb.inertiaTensorRotation);
                    Row(sb, "rb.linearDamping", rb.linearDamping);
                    Row(sb, "rb.angularDamping", rb.angularDamping);
                    Row(sb, "rb.maxAngularVelocity", rb.maxAngularVelocity);
                    Row(sb, "rb.maxDepenetrationVelocity", rb.maxDepenetrationVelocity);
                }

                var box = car.GetComponent<BoxCollider>();
                if (box != null)
                {
                    Row(sb, "box.size", box.size);
                    Row(sb, "box.center", box.center);
                    Row(sb, "box.contactOffset", box.contactOffset);
                }

                var wcs = car.GetComponentsInChildren<WheelCollider>(true);
                for (int i = 0; i < wcs.Length; i++)
                {
                    var w = wcs[i];
                    string p = $"wc[{i}]{w.name}.";
                    Row(sb, p + "localPosition", w.transform.localPosition);
                    Row(sb, p + "localRotation", w.transform.localRotation);
                    Row(sb, p + "mass", w.mass);
                    Row(sb, p + "radius", w.radius);
                    Row(sb, p + "suspensionDistance", w.suspensionDistance);
                    Row(sb, p + "center", w.center);
                    Row(sb, p + "forceAppPointDistance", w.forceAppPointDistance);
                    Row(sb, p + "wheelDampingRate", w.wheelDampingRate);
                    var s = w.suspensionSpring;
                    Row(sb, p + "spring.spring", s.spring);
                    Row(sb, p + "spring.damper", s.damper);
                    Row(sb, p + "spring.targetPosition", s.targetPosition);
                    Curve(sb, p + "fwd", w.forwardFriction);
                    Curve(sb, p + "side", w.sidewaysFriction);
                }

                // Spin inertia is integrated by CarVehicle itself on the brush
                // path, so it appears in no engine field — and it is one of the
                // numbers this dump exists to protect.
                for (int i = 0; i < car.WheelCount; i++)
                    Row(sb, $"car.spinInertia[{i}]", car.WheelSpinInertia(i));

                Appearance(sb, car);
            }
            finally
            {
                // Immediate, not deferred: otherwise all 21 cars coexist until
                // the end of frame and the later ones build inside the earlier
                // ones' colliders.
                if (car != null) Object.DestroyImmediate(car.gameObject);
            }
        }

        /// <summary>
        /// What the design turned into visually, read back off the built car.
        ///
        /// <b>Why it belongs in the physics dump rather than beside the binding
        /// dump.</b> <c>PartModelBindingDump</c> already covers every key and
        /// every wheel style exhaustively — what a material IS. The question it
        /// structurally cannot ask is which key a DESIGN resolves to, because it
        /// builds parts, not cars. That mapping — <c>bodyShape</c> → mesh key →
        /// scale, <c>wheelStyle</c> → mesh key → radius, shape → Cd — is exactly
        /// what Phase 3 replaces with string keys and a catalogue, and it lives
        /// nowhere else.
        ///
        /// <b>Measured, never recomputed.</b> Every row here is read off the
        /// instantiated hierarchy: the key comes from the instance's own name,
        /// the scale from its transform, the size from its renderer bounds, the
        /// coefficients from the car's own <see cref="CarVehicle.EffectiveAero"/>.
        /// A dumper that called <c>BodyMeshKey</c> itself would keep printing the
        /// right key for the rest of time, including after the car stopped asking
        /// it — which is the one failure this block exists to catch.
        ///
        /// Appended after the physics rows on purpose: the block is additive, so
        /// a diff against a pre-K0 dump shows insertions and nothing else.
        /// </summary>
        private static void Appearance(StringBuilder sb, CarVehicle car)
        {
            Row(sb, "body.shape", car.bodyShape.ToString());
            Transform inst = car.BodyMeshInstance;
            // "<primitive>" is a real answer, not a missing one: a shape with a
            // mesh key still lands here when the FBX did not ship.
            Row(sb, "body.key", inst != null ? StripClone(inst.name) : "<primitive>");

            var binding = inst != null ? inst.GetComponent<PartManifestBinding>() : null;
            Row(sb, "body.manifest", binding == null ? "-"
                : $"{binding.Key} mode={(binding.Manifest != null ? binding.Manifest.materialMode : "?")}" +
                  $" bound={binding.BoundSlots}/{binding.Parts.Count}" +
                  $" paintSlots={(binding.HasPaintSlots ? 1 : 0)}");

            Row(sb, "body.bodySize", car.bodySize);
            Vector3 scale = inst != null ? inst.localScale : Vector3.one;
            Row(sb, "body.renderScale", inst != null ? scale : Vector3.zero);
            // The divisor that was actually applied, recovered from the result.
            // BodyMeshAuthorSize is a nominal constant and the Tiguan bypasses it
            // entirely, so asking the car what it divided BY says more than
            // reading the constant back.
            Row(sb, "body.authorSizeImplied", inst != null
                ? new Vector3(Div(car.bodySize.x, scale.x), Div(car.bodySize.y, scale.y),
                              Div(car.bodySize.z, scale.z))
                : Vector3.zero);

            Transform frame = car.transform;
            Bounds bb = PartVisualFactory.LocalRendererBounds(frame, car.BodyVisual);
            Row(sb, "body.bounds.center", bb.center);
            Row(sb, "body.bounds.size", bb.size);
            Row(sb, "body.renderers", Renderers(car.BodyVisual));
            Row(sb, "body.paintRenderers",
                car.PaintRenderers.Count.ToString(CultureInfo.InvariantCulture));
            Materials(sb, "body", car.BodyVisual);

            car.EffectiveAero(out float cd, out float area, out float clA);
            Row(sb, "aero.cd", cd);
            Row(sb, "aero.frontalArea", area);
            Row(sb, "aero.cdA", cd * area);
            Row(sb, "aero.clA", clA);
            Row(sb, "aero.aeroMult", car.aeroMult);

            for (int i = 0; i < car.WheelCount; i++)
            {
                var col = car.GetWheel(i);
                Transform viz = car.GetWheelVisual(i);
                if (col == null || viz == null) continue;
                string p = $"viz[{i}]{col.name}.";

                Row(sb, p + "style", car.WheelStyle(i).ToString(CultureInfo.InvariantCulture));
                // The mesh instance, found the way TyreHalfWidth finds it — by
                // the "wheel_" name prefix — so the two cannot disagree about
                // which child is the tyre and which is the motor can.
                Transform mesh = MeshChild(viz);
                Row(sb, p + "key", mesh != null ? StripClone(mesh.name) : "<primitive>");
                Row(sb, p + "meshScale", mesh != null ? mesh.localScale.x : 0f);
                // The radius at which this mesh renders unscaled, recovered from
                // the result — AuthorRadiusFor's answer, as applied.
                Row(sb, p + "authorRadiusImplied",
                    mesh != null ? Div(col.radius, mesh.localScale.x) : 0f);
                Row(sb, p + "tyreHalfWidth", PartVisualFactory.TyreHalfWidth(viz, col.radius));
                Bounds wb = PartVisualFactory.LocalRendererBounds(viz, mesh != null ? mesh : viz);
                Row(sb, p + "bounds.size", wb.size);
                Row(sb, p + "renderers", Renderers(viz));
                Materials(sb, p.TrimEnd('.'), viz);
            }
        }

        /// <summary>Instantiate names its result "<c>key(Clone)</c>", so the
        /// instance carries the resolved key and nothing else has to.</summary>
        private static string StripClone(string n) =>
            n != null && n.EndsWith("(Clone)") ? n.Substring(0, n.Length - 7) : n;

        private static Transform MeshChild(Transform holder)
        {
            if (holder == null) return null;
            for (int i = 0; i < holder.childCount; i++)
                if (holder.GetChild(i).name.StartsWith("wheel_")) return holder.GetChild(i);
            return null;
        }

        private static float Div(float a, float b) => Mathf.Abs(b) < 1e-9f ? 0f : a / b;

        /// <summary>Renderer count, and how many are switched on — the second
        /// number is what a cosmetic rim or a hidden panel moves.</summary>
        private static string Renderers(Transform root)
        {
            if (root == null) return "0 on=0";
            int n = 0, on = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                n++;
                if (r.enabled) on++;
            }
            return n.ToString(CultureInfo.InvariantCulture) + " on=" +
                   on.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The distinct materials this part landed on, with how many submesh
        /// slots each took — the measurable consequence of which token table ran.
        ///
        /// Deliberately shorter than <c>PartModelBindingDump.Describe</c>: no
        /// shader, queue or keywords. That file's job is what a material is, and
        /// duplicating it here would double the noise for no extra coverage.
        /// What this needs is enough to tell one shared material from another and
        /// to show the design's own bodyColor arriving on the panels it paints.
        /// </summary>
        private static void Materials(StringBuilder sb, string prefix, Transform root)
        {
            if (root == null) return;
            var counts = new Dictionary<string, int>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                if (mats.Length == 0) Bump(counts, "<none>");
                else foreach (var m in mats) Bump(counts, Fingerprint(m));
            }
            var keys = new List<string>(counts.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (string k in keys)
                sb.Append("  ").Append(prefix).Append(".mat ").Append(k)
                  .Append(" n=").Append(counts[k].ToString(CultureInfo.InvariantCulture))
                  .Append('\n');
        }

        private static void Bump(Dictionary<string, int> d, string k) =>
            d[k] = d.TryGetValue(k, out int c) ? c + 1 : 1;

        private static string Fingerprint(Material m)
        {
            if (m == null) return "<null>";
            var tex = m.HasProperty("_MainTex") ? m.mainTexture : null;
            return "col=" + Col(m, "_Color") +
                   " met=" + Num(m, "_Metallic") +
                   " gls=" + Num(m, "_Glossiness") +
                   " emi=" + Col(m, "_EmissionColor") +
                   " tex=" + (tex != null ? tex.name : "-");
        }

        // "R" round-trip, like every other number here: a dump whose job is to
        // catch drift must not be able to round drift away.
        private static string Num(Material m, string prop) =>
            m.HasProperty(prop)
                ? m.GetFloat(prop).ToString("R", CultureInfo.InvariantCulture)
                : "-";

        private static string Col(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return "-";
            Color c = m.GetColor(prop);
            return c.r.ToString("R", CultureInfo.InvariantCulture) + "," +
                   c.g.ToString("R", CultureInfo.InvariantCulture) + "," +
                   c.b.ToString("R", CultureInfo.InvariantCulture) + "," +
                   c.a.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void Curve(StringBuilder sb, string p, WheelFrictionCurve c)
        {
            Row(sb, p + ".extremumSlip", c.extremumSlip);
            Row(sb, p + ".extremumValue", c.extremumValue);
            Row(sb, p + ".asymptoteSlip", c.asymptoteSlip);
            Row(sb, p + ".asymptoteValue", c.asymptoteValue);
            Row(sb, p + ".stiffness", c.stiffness);
        }

        private static void Row(StringBuilder sb, string k, string v) =>
            sb.Append("  ").Append(k).Append(' ').Append(v).Append('\n');

        private static void Row(StringBuilder sb, string k, float v) =>
            sb.Append("  ").Append(k).Append(' ')
              .Append(v.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

        private static void Row(StringBuilder sb, string k, Vector3 v) =>
            sb.Append("  ").Append(k).Append(' ')
              .Append(v.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(v.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(v.z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

        private static void Row(StringBuilder sb, string k, Quaternion q) =>
            sb.Append("  ").Append(k).Append(' ')
              .Append(q.x.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(q.y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(q.z.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
              .Append(q.w.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }
}
