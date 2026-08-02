using System;
using System.Collections.Generic;
using System.Globalization;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Checks <see cref="BodyCatalog"/> and <see cref="WheelCatalog"/> against
    /// the switches they were transcribed from, <b>while those switches are
    /// still the live path</b>.
    ///
    /// That timing is the whole design. A catalogue is a hand-copy of six
    /// scattered <c>switch</c> statements, and a hand-copy is wrong at least
    /// once; if the consumers move over in the same commit, the first evidence
    /// of a wrong row is a car that renders with the wrong wheels and a drag
    /// coefficient nobody notices. Running this before anything reads the table
    /// turns that into a named failure in a log.
    ///
    /// It follows that this validator <b>gets less useful with every K
    /// milestone</b>, and is worth nothing after the last one: once
    /// <c>AeroDynamics.BodyCd</c> reads the catalogue, checking the catalogue
    /// against it proves only that it agrees with itself. K3 is expected to
    /// delete each check as it moves the consumer it guards; what should be left
    /// at the end is the internal consistency (unique keys, one row per legacy
    /// value, meshes that exist).
    ///
    /// Run with (editor must be closed):
    ///   Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.AssetKeyValidator.Report -logFile &lt;log&gt;
    /// then grep the log for "[AKEY] RESULT".
    /// </summary>
    public static class AssetKeyValidator
    {
        private const string Tag = "[AKEY]";

        [MenuItem("Tools/AIHWSim/Validate Asset Keys [AKEY]", priority = 60)]
        public static void Report()
        {
            int fail = 0;
            fail += Bodies();
            fail += Wheels();
            Debug.Log($"{Tag} RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")} " +
                      $"({BodyCatalog.All.Length} bodies, {WheelCatalog.All.Length} wheels)");
        }

        // ---- bodies ----------------------------------------------------------

        private static int Bodies()
        {
            int fail = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<BodyShape>();

            foreach (BodyDef d in BodyCatalog.All)
            {
                string why = "";

                if (!ids.Add(d.id)) why += $" duplicate id '{d.id}'";
                if (!seen.Add(d.legacy)) why += $" duplicate legacy {d.legacy}";
                if (BodyCatalog.ById(d.id) != d) why += " ById does not resolve to this row";
                if (BodyCatalog.ByLegacy(d.legacy) != d) why += " ByLegacy does not resolve to this row";

                // --- the transcriptions, against the still-live switches ---

                string liveKey = CarVehicle.BodyMeshKey(d.legacy);
                if (d.meshKey != liveKey) why += $" meshKey '{d.meshKey}' != BodyMeshKey '{liveKey}'";

                // The id IS the mesh key wherever there is a mesh. Checked rather
                // than assumed: it is what lets Asset Studio commit an asset
                // without inventing a second name for it.
                if (d.meshKey != null && d.id != d.meshKey)
                    why += $" id '{d.id}' should be the mesh key '{d.meshKey}'";

                float liveCd = AeroDynamics.BodyCd(d.legacy);
                if (!Same(d.cd, liveCd)) why += $" cd {N(d.cd)} != BodyCd {N(liveCd)}";
                float liveClA = AeroDynamics.BodyClA(d.legacy);
                if (!Same(d.clA, liveClA)) why += $" clA {N(d.clA)} != BodyClA {N(liveClA)}";

                // HasPaintableBody answers two questions at once — "does this
                // shape have a tintable channel" and "did the FBX ship" — so the
                // asset check is folded back in before comparing.
                bool present = d.meshKey == null || PartMeshLibrary.Has(d.meshKey);
                bool livePaint = CarVehicle.HasPaintableBody(d.legacy);
                if ((d.paintable && present) != livePaint)
                    why += $" paintable {d.paintable} (asset present {present})" +
                           $" != HasPaintableBody {livePaint}";

                BodyTokens liveTokens = TokensOf(CarVehicle.BodyAccentTable(d.legacy));
                if (d.tokens != liveTokens)
                    why += $" tokens {d.tokens} != BodyAccentTable {liveTokens}";

                // A probe size that would scale every axis, so "unscaled" cannot
                // pass by the design happening to be the author size.
                var probe = new Vector3(0.31f, 0.17f, 0.53f);
                bool liveUnscaled = CarVehicle.BodyRenderScale(d.legacy, probe) == Vector3.one;
                if (d.unscaled != liveUnscaled)
                    why += $" unscaled {d.unscaled} != BodyRenderScale {liveUnscaled}";

                bool liveFolded = CosmeticMounts.HasFoldedAppendages(d.legacy);
                if (d.foldedAppendages != liveFolded)
                    why += $" foldedAppendages {d.foldedAppendages} != HasFoldedAppendages {liveFolded}";

                // Nominal size: the constant for every arcade shell, and the
                // reference car's own published box for the one that bypasses it.
                Vector3 wantSize = d.legacy == BodyShape.Tiguan
                    ? DebugVehicles.VwTiguan().bodySize
                    : CarVehicle.BodyMeshAuthorSize;
                if ((d.nominalSize - wantSize).sqrMagnitude > 1e-8f)
                    why += $" nominalSize {V(d.nominalSize)} != {V(wantSize)}";

                // The picker prints shape.ToString() today, so the label is
                // pinned to the enum name until K4 makes it free to diverge.
                if (d.label != d.legacy.ToString() && d.legacy != BodyShape.Tiguan)
                    why += $" label '{d.label}' != enum name '{d.legacy}'";

                if (d.meshKey != null && !PartMeshLibrary.Has(d.meshKey))
                    why += $" no mesh at Resources/PartModels/{d.meshKey}";

                if (why.Length == 0) Debug.Log($"{Tag} PASS body:{d.id}");
                else { Debug.LogError($"{Tag} FAIL body:{d.id} -{why}"); fail++; }
            }

            // Every shape must have a row, or a save carrying it migrates to
            // nothing. Checked from the ENUM's side: the loop above can only see
            // the rows that exist.
            foreach (BodyShape s in Enum.GetValues(typeof(BodyShape)))
                if (BodyCatalog.ByLegacy(s) == null)
                {
                    Debug.LogError($"{Tag} FAIL body:<none> - BodyShape.{s} has no catalogue row");
                    fail++;
                }

            return fail;
        }

        /// <summary>Identify a token table by its content, not its reference —
        /// <c>AccentTokens</c> is a property that builds a fresh array on every
        /// call, so <c>==</c> would report every table as different.</summary>
        private static BodyTokens TokensOf((string, Material)[] table)
        {
            if (table == null) return BodyTokens.None;
            if (SameTokens(table, PartVisualFactory.AccentTokens)) return BodyTokens.Accent;
            if (SameTokens(table, PartVisualFactory.TiguanTokens)) return BodyTokens.Tiguan;
            return (BodyTokens)(-1);   // a table that is neither: never equal to a field
        }

        private static bool SameTokens((string, Material)[] a, (string, Material)[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!string.Equals(a[i].Item1, b[i].Item1, StringComparison.Ordinal)) return false;
            return true;
        }

        // ---- wheels ----------------------------------------------------------

        private static int Wheels()
        {
            int fail = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<int>();

            for (int i = 0; i < WheelCatalog.All.Length; i++)
            {
                WheelDef d = WheelCatalog.All[i];
                string why = "";

                if (!ids.Add(d.id)) why += $" duplicate id '{d.id}'";
                if (!seen.Add(d.legacy)) why += $" duplicate legacy {d.legacy}";
                // The int is an ARRAY INDEX everywhere it is persisted, so a gap
                // or a reorder silently repaints every saved car.
                if (d.legacy != i) why += $" legacy {d.legacy} is not its own index {i}";
                if (WheelCatalog.ById(d.id) != d) why += " ById does not resolve to this row";
                if (WheelCatalog.ByLegacy(d.legacy) != d) why += " ByLegacy does not resolve to this row";

                string liveKey = "wheel_" + PartVisualFactory.WheelStyleKey(d.legacy);
                if (d.meshKey != liveKey) why += $" meshKey '{d.meshKey}' != '{liveKey}'";

                float liveRadius = PartVisualFactory.AuthorRadiusFor(d.legacy);
                if (!Same(d.authorRadius, liveRadius))
                    why += $" authorRadius {N(d.authorRadius)} != AuthorRadiusFor {N(liveRadius)}";

                WheelFinish liveFinish = PartVisualFactory.FinishFor(d.legacy);
                if (d.finish != liveFinish) why += $" finish {d.finish} != FinishFor {liveFinish}";

                bool liveFull = PartVisualFactory.IsFullScale(d.legacy);
                if (d.fullScale != liveFull) why += $" fullScale {d.fullScale} != IsFullScale {liveFull}";

                // A finish is a re-tint of somebody else's mesh, and the two must
                // agree about which: a finish whose id claimed a mesh key, or a
                // mesh style carrying a finish, is the confusion this table exists
                // to end.
                if (d.finish != WheelFinish.None && d.id == d.meshKey)
                    why += $" finish style must not take the mesh key '{d.meshKey}' as its id";
                if (d.finish == WheelFinish.None && d.id != d.meshKey)
                    why += $" id '{d.id}' should be the mesh key '{d.meshKey}'";

                if (!PartMeshLibrary.Has(d.meshKey))
                    why += $" no mesh at Resources/PartModels/{d.meshKey}";

                if (why.Length == 0) Debug.Log($"{Tag} PASS wheel:{d.id}");
                else { Debug.LogError($"{Tag} FAIL wheel:{d.id} -{why}"); fail++; }
            }

            // WheelStyleKey maps anything it does not know to the slick, so it
            // cannot say where the styles stop. The table can, and this is the
            // check that catches a fifteenth style added to the switch and
            // forgotten here: one past the end must still be unknown to the
            // table AND must resolve to the slick.
            int past = WheelCatalog.All.Length;
            if (WheelCatalog.ByLegacy(past) != null)
            {
                Debug.LogError($"{Tag} FAIL wheel:<range> - style {past} has a row past the end");
                fail++;
            }
            if (PartVisualFactory.WheelStyleKey(past) != "slick")
            {
                Debug.LogError($"{Tag} FAIL wheel:<range> - style {past} is a real style in " +
                               "WheelStyleKey but has no catalogue row");
                fail++;
            }

            return fail;
        }

        // ---- helpers ---------------------------------------------------------

        private static bool Same(float a, float b) => Mathf.Abs(a - b) < 1e-6f;

        private static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string V(Vector3 v) =>
            $"({N(v.x)}, {N(v.y)}, {N(v.z)})";
    }
}
