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
            fail += Migration();
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
                //
                // K3a retired four of these: meshKey, tokens, unscaled and
                // paintable were compared against BodyMeshKey, BodyAccentTable,
                // BodyRenderScale's Tiguan test and HasPaintableBody's five-name
                // list. Those switches are gone — the catalogue IS the live path
                // now, so the checks would have proved only that it agrees with
                // itself. What is left below is the transcription that still has
                // a second source (cd/clA at K3b, foldedAppendages at K3d) plus
                // the internal consistency, which never expires.

                // The id IS the mesh key wherever there is a mesh. Checked rather
                // than assumed: it is what lets Asset Studio commit an asset
                // without inventing a second name for it.
                if (d.meshKey != null && d.id != d.meshKey)
                    why += $" id '{d.id}' should be the mesh key '{d.meshKey}'";

                float liveCd = AeroDynamics.BodyCd(d.legacy);
                if (!Same(d.cd, liveCd)) why += $" cd {N(d.cd)} != BodyCd {N(liveCd)}";
                float liveClA = AeroDynamics.BodyClA(d.legacy);
                if (!Same(d.clA, liveClA)) why += $" clA {N(d.clA)} != BodyClA {N(liveClA)}";

                // Now that the catalogue decides, "paintable" can only be checked
                // for INTERNAL sense: a body with no mesh has nothing to paint,
                // and one whose manifest is verbatim keeps the FBX's materials.
                // Neither is a transcription — both are conditions the table must
                // not contradict, whoever wrote the row.
                if (d.paintable && d.meshKey == null)
                    why += " paintable with no mesh key";
                if (d.paintable && !CarVehicle.HasPaintableBody(d) &&
                    d.meshKey != null && PartMeshLibrary.Has(d.meshKey))
                    why += " paintable but HasPaintableBody says no with the asset present";

                // The token table must resolve to something BindByToken can use.
                // Not a comparison any more — a reachability check: an unnamed
                // BodyTokens value would silently flatten every renderer onto the
                // body material, which renders as a correctly shaped grey car.
                if (d.tokens != BodyTokens.None && CarVehicle.BodyAccentTable(d) == null)
                    why += $" tokens {d.tokens} resolves to no table";

                // No "unscaled" check at all after K3a. BodyRenderScale now READS
                // this field, so comparing the two is the definition of a check
                // that proves nothing; deleting it is the honest move, not
                // rewriting it into something that still passes.

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

        // TokensOf/SameTokens lived here until K3a. They identified a token table
        // by its CONTENT because AccentTokens builds a fresh array per call — a
        // real problem, worth remembering if anything ever needs to compare two
        // tables again, and no longer one this file has: BodyAccentTable is now
        // a switch on the field it used to be compared against.

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

        // ---- migration (K2) --------------------------------------------------

        /// <summary>
        /// The K2 checks, and the only evidence K2 has: the design dump cannot
        /// see this milestone at all, because nothing reads the new properties
        /// yet, so an empty diff there proves only that K2 did not break K1.
        ///
        /// What is actually being claimed is a round trip — that a design written
        /// before the keys existed still means the same car, that the key wins
        /// when the pair disagrees, and that an OLD build reading a NEW file
        /// still gets the right shape out of the int beside it. All three are
        /// exercised through <c>JsonUtility</c> rather than in memory, because
        /// what a field does to a save file is the entire subject.
        ///
        /// Unlike the sections above, these checks do <b>not</b> expire at K3:
        /// they compare the catalogue against the save format, not against a
        /// switch that is about to be deleted.
        /// </summary>
        private static int Migration()
        {
            int fail = 0;

            foreach (BodyDef d in BodyCatalog.All)
            {
                string why = "";

                // 1. A design written before K2: the int, no key. Through JSON,
                //    because "absent" and "empty string" have to be the same
                //    thing here and only JsonUtility can say whether they are.
                var old = Round(new VehicleDesign { bodyShape = d.legacy, bodyKey = "" });
                if (old.Body != d) why += $" legacy-only design resolves to '{old.BodyKey}'";

                // 2. The key wins. The wrong int is a real other shape, so this
                //    cannot pass by both sides happening to say the same thing.
                BodyShape wrong = d.legacy == BodyShape.Wedge ? BodyShape.Box : BodyShape.Wedge;
                var mixed = Round(new VehicleDesign { bodyShape = wrong, bodyKey = d.id });
                if (mixed.Body != d) why += $" key '{d.id}' lost to bodyShape {wrong}";

                // 3. Migrate fills the key in and leaves the int alone — the
                //    no-op half, which is every design that exists today.
                old.Migrate();
                if (old.bodyKey != d.id) why += $" Migrate wrote bodyKey '{old.bodyKey}'";
                if (old.bodyShape != d.legacy)
                    why += $" Migrate moved bodyShape to {old.bodyShape}";

                // 4. ...and derives the int back from the key — the half that
                //    stops a saved file's two readers seeing two different cars.
                mixed.Migrate();
                if (mixed.bodyShape != d.legacy)
                    why += $" Migrate left bodyShape {mixed.bodyShape}, not {d.legacy}";

                // 5. Idempotent: saving twice must not walk.
                string once = JsonUtility.ToJson(mixed);
                mixed.Migrate();
                if (JsonUtility.ToJson(mixed) != once) why += " Migrate is not idempotent";

                // 6. The downgrade. An old build has no bodyKey member, so
                //    JsonUtility drops it; what is left must still be this body.
                var downgraded = JsonUtility.FromJson<VehicleDesign>(Strip(once, "bodyKey"));
                if (downgraded.bodyKey.Length != 0) why += " strip did not remove bodyKey";
                else if (downgraded.Body != d)
                    why += $" old build reads this design as '{downgraded.BodyKey}'";

                if (why.Length == 0) Debug.Log($"{Tag} PASS migrate:{d.id}");
                else { Debug.LogError($"{Tag} FAIL migrate:{d.id} -{why}"); fail++; }
            }

            foreach (WheelDef d in WheelCatalog.All)
            {
                string why = "";

                var old = Round(OneWheel(d.legacy, ""));
                if (old.wheels[0].Wheel != d)
                    why += $" legacy-only wheel resolves to '{old.wheels[0].WheelKey}'";

                int wrong = d.legacy == 1 ? 2 : 1;
                var mixed = Round(OneWheel(wrong, d.id));
                if (mixed.wheels[0].Wheel != d) why += $" key '{d.id}' lost to wheelStyle {wrong}";

                old.Migrate();
                if (old.wheels[0].wheelKey != d.id)
                    why += $" Migrate wrote wheelKey '{old.wheels[0].wheelKey}'";
                if (old.wheels[0].wheelStyle != d.legacy)
                    why += $" Migrate moved wheelStyle to {old.wheels[0].wheelStyle}";

                mixed.Migrate();
                if (mixed.wheels[0].wheelStyle != d.legacy)
                    why += $" Migrate left wheelStyle {mixed.wheels[0].wheelStyle}, not {d.legacy}";

                string once = JsonUtility.ToJson(mixed);
                mixed.Migrate();
                if (JsonUtility.ToJson(mixed) != once) why += " Migrate is not idempotent";

                var downgraded = JsonUtility.FromJson<VehicleDesign>(Strip(once, "wheelKey"));
                if (downgraded.wheels[0].wheelKey.Length != 0) why += " strip did not remove wheelKey";
                else if (downgraded.wheels[0].Wheel != d)
                    why += $" old build reads this wheel as '{downgraded.wheels[0].WheelKey}'";

                if (why.Length == 0) Debug.Log($"{Tag} PASS migrate:{d.id}");
                else { Debug.LogError($"{Tag} FAIL migrate:{d.id} -{why}"); fail++; }
            }

            fail += Fallbacks();
            return fail;
        }

        /// <summary>
        /// What happens when neither half of the pair is a thing this build
        /// knows. Resolution must never return null and must never throw: a
        /// design that cannot be read is still a design somebody has to drive.
        ///
        /// The two LogWarnings this provokes are expected — they are the point of
        /// the unknown-key rows.
        /// </summary>
        private static int Fallbacks()
        {
            int fail = 0;
            string why = "";

            // Unknown key, good int: the downgrade case. The int is the answer.
            if (BodyCatalog.Resolve("body_from_the_future", BodyShape.Coupe) !=
                BodyCatalog.ByLegacy(BodyShape.Coupe)) why += " unknown body key ignored the int";
            if (WheelCatalog.Resolve("wheel_from_the_future", 3) != WheelCatalog.ByLegacy(3))
                why += " unknown wheel key ignored the int";

            // Nothing usable at all. These match what the live switches have
            // always built for an out-of-range value: BodyMeshKey's `_ => null`
            // is the primitive box, WheelStyleKey's `_ => "slick"` is the slick.
            if (BodyCatalog.Resolve("", (BodyShape)999) != BodyCatalog.ById("box"))
                why += " out-of-range bodyShape does not fall back to the box";
            if (WheelCatalog.Resolve("", 47) != WheelCatalog.ById("wheel_slick"))
                why += " out-of-range wheelStyle does not fall back to the slick";
            if (BodyCatalog.Resolve(null, BodyShape.Box) == null) why += " null body key threw or returned null";
            if (WheelCatalog.Resolve(null, 0) == null) why += " null wheel key threw or returned null";

            // And a corrupt int is REWRITTEN, not preserved. 47 has always
            // rendered as the slick; after a save the file says so.
            var corrupt = OneWheel(47, "");
            corrupt.Migrate();
            if (corrupt.wheels[0].wheelStyle != 0 || corrupt.wheels[0].wheelKey != "wheel_slick")
                why += $" corrupt wheelStyle 47 migrated to {corrupt.wheels[0].wheelStyle}" +
                       $"/'{corrupt.wheels[0].wheelKey}'";

            if (why.Length == 0) Debug.Log($"{Tag} PASS migrate:<fallbacks>");
            else { Debug.LogError($"{Tag} FAIL migrate:<fallbacks> -{why}"); fail++; }
            return fail;
        }

        private static VehicleDesign Round(VehicleDesign d) =>
            JsonUtility.FromJson<VehicleDesign>(JsonUtility.ToJson(d));

        private static VehicleDesign OneWheel(int style, string key)
        {
            var d = new VehicleDesign();
            d.wheels.Add(new WheelSpec { wheelStyle = style, wheelKey = key });
            return d;
        }

        /// <summary>Delete a string member from compact JSON, the way an older
        /// build's <c>JsonUtility</c> would: it has no member to bind it to, so
        /// the field is simply gone on the next write. Neither key is the last
        /// field of its object, so the trailing comma always exists.</summary>
        private static string Strip(string json, string field) =>
            System.Text.RegularExpressions.Regex.Replace(
                json, "\"" + field + "\":\"[^\"]*\",", "");

        // ---- helpers ---------------------------------------------------------

        private static bool Same(float a, float b) => Mathf.Abs(a - b) < 1e-6f;

        private static string N(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private static string V(Vector3 v) =>
            $"({N(v.x)}, {N(v.y)}, {N(v.z)})";
    }
}
