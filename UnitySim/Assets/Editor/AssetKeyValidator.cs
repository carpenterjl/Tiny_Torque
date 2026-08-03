using System;
using System.Collections.Generic;
using System.Globalization;
using AIHWSim.Garage;
using AIHWSim.Persistence;
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
            fail += Presets();
            fail += Loadouts();
            Debug.Log($"{Tag} RESULT {(fail == 0 ? "ALL PASS" : fail + " FAILED")} " +
                      $"({BodyCatalog.All.Length} bodies, {WheelCatalog.All.Length} wheels)");
        }

        // ---- bodies ----------------------------------------------------------

        private static int Bodies()
        {
            int fail = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<BodyShape>();

            foreach (BodyDef d in BodyCatalog.Seed)
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

                // cd/clA were compared against AeroDynamics until K3b, which now
                // reads them; deleted rather than kept as self-agreement. What
                // remains is the one thing a table can still be wrong about on
                // its own: a drag coefficient outside anything a car body can be.
                // 0.15 is a teardrop, 1.2 is a flat plate broadside.
                if (d.cd < 0.15f || d.cd > 1.2f) why += $" cd {N(d.cd)} is not a car";
                if (d.clA < 0f || d.clA > 0.05f) why += $" clA {N(d.clA)} is not a shell";

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

                // foldedAppendages lost its second source at K3d. Only the
                // internal rule is left, and it is a real one: a body with no
                // mesh has no wings or booms to fold out of the mount box.
                if (d.foldedAppendages && d.meshKey == null)
                    why += " foldedAppendages on a primitive body";

                // Nominal size: the constant for every arcade shell, and the
                // reference car's own published box for the one that bypasses it.
                Vector3 wantSize = d.legacy == BodyShape.Tiguan
                    ? DebugVehicles.VwTiguan().bodySize
                    : CarVehicle.BodyMeshAuthorSize;
                if ((d.nominalSize - wantSize).sqrMagnitude > 1e-8f)
                    why += $" nominalSize {V(d.nominalSize)} != {V(wantSize)}";

                // The label was pinned to the enum name until K4, because the
                // picker printed shape.ToString() and the two had to agree.
                // The picker prints THIS now, so the pin is gone — a body Asset
                // Studio commits has no enum name to be held to. What replaces
                // it is what a printed string has to be: there, and its own.
                if (string.IsNullOrWhiteSpace(d.label)) why += " no label";
                else if (!labels.Add(d.label)) why += $" duplicate label '{d.label}'";

                // debugOnly is what the garage picker filters on, and this is
                // what K4 replaced `s != BodyShape.Tiguan` with: a body is
                // content exactly when the garage's own size sliders can reach
                // the size it is authored for. Two independent numbers — the
                // slider range and the shell's nominal size — so a row that
                // hid itself for no reason, or offered a shell nobody can size,
                // is a named failure rather than a puzzling picker.
                if (d.debugOnly == BodyCatalog.Buildable(d))
                    why += $" debugOnly {d.debugOnly} disagrees with nominalSize " +
                           $"{V(d.nominalSize)} against the size sliders";

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
            var labels = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<int>();

            for (int i = 0; i < WheelCatalog.Seed.Length; i++)
            {
                WheelDef d = WheelCatalog.Seed[i];
                string why = "";

                if (!ids.Add(d.id)) why += $" duplicate id '{d.id}'";
                if (!seen.Add(d.legacy)) why += $" duplicate legacy {d.legacy}";
                // The int is an ARRAY INDEX everywhere it is persisted, so a gap
                // or a reorder silently repaints every saved car.
                if (d.legacy != i) why += $" legacy {d.legacy} is not its own index {i}";
                if (WheelCatalog.ById(d.id) != d) why += " ById does not resolve to this row";
                if (WheelCatalog.ByLegacy(d.legacy) != d) why += " ByLegacy does not resolve to this row";

                // K3c retired the four transcription checks here — meshKey,
                // authorRadius, finish and fullScale were compared against
                // WheelStyleKey, AuthorRadiusFor, FinishFor and IsFullScale, all
                // four of which are now this table. What is left is the internal
                // consistency, which is where the real rules always lived.

                // Two author radii exist and no third is meaningful: 33 mm is
                // what the exporter rescales every arcade tyre to, 0.349 is the
                // Tiguan's loaded centre height. A row inventing a number would
                // render a wheel at some other size with no other symptom.
                bool knownRadius = Same(d.authorRadius, PartVisualFactory.WheelAuthorRadius)
                                || Same(d.authorRadius, PartVisualFactory.TiguanWheelAuthorRadius);
                if (!knownRadius) why += $" authorRadius {N(d.authorRadius)} is neither author radius";

                // fullScale IS "authored 1:1", so it has to be the one that
                // takes the full-scale radius. This is the check that used to be
                // three copies of `style == 13 || style == 14` agreeing.
                if (d.fullScale != Same(d.authorRadius, PartVisualFactory.TiguanWheelAuthorRadius))
                    why += $" fullScale {d.fullScale} disagrees with authorRadius {N(d.authorRadius)}";

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

                // K4 made the three label arrays one. A label is now printed
                // rather than compared, so what it has to be is there and its
                // own — the garage and the showroom can no longer disagree about
                // what style 10 is called, but they can both print "".
                if (string.IsNullOrWhiteSpace(d.label)) why += " no label";
                else if (!labels.Add(d.label)) why += $" duplicate label '{d.label}'";

                // Two picker filters, checkable for the first time now that the
                // pickers read them.
                //
                // debugOnly IS "authored 1:1": the reference car's wheels are
                // the ones that came from the other pipeline, and nothing else
                // should be hiding from both pickers.
                if (d.debugOnly != d.fullScale)
                    why += $" debugOnly {d.debugOnly} disagrees with fullScale {d.fullScale}";

                // The garage offers meshes. A finish is unlocked in the showroom,
                // not designed in the garage; a reference wheel is not content.
                if (d.garageOffered != (d.finish == WheelFinish.None && !d.debugOnly))
                    why += $" garageOffered {d.garageOffered} disagrees with finish " +
                           $"{d.finish} / debugOnly {d.debugOnly}";

                // K4's contiguity check lived here: the non-debugOnly rows had
                // to be the FRONT of the table with no gap, because ShowroomUI
                // indexed its cycle by wheelStyle. C1b removed that consumer —
                // the showroom looks its position up from the row it resolves to,
                // since a committed wheel has no int for a position to equal — so
                // per K1's own rule the check went with it. legacy == index above
                // still says everything it said about the seeds.

                if (why.Length == 0) Debug.Log($"{Tag} PASS wheel:{d.id}");
                else { Debug.LogError($"{Tag} FAIL wheel:{d.id} -{why}"); fail++; }
            }

            // One past the end must be unknown to the table and must still
            // RESOLVE, to the slick. The second half is the live rule now that
            // WheelStyleKey is gone: a corrupt save renders rather than throwing.
            int past = WheelCatalog.Seed.Length;
            if (WheelCatalog.ByLegacy(past) != null)
            {
                Debug.LogError($"{Tag} FAIL wheel:<range> - style {past} has a row past the end");
                fail++;
            }
            if (WheelCatalog.Resolve("", past) != WheelCatalog.Default)
            {
                Debug.LogError($"{Tag} FAIL wheel:<range> - style {past} does not resolve to the slick");
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

            // Over the SEED table, because this whole section is about the
            // legacy int and a committed body does not have one — its legacy is
            // Box, so "a legacy-only design resolves to this row" is false for it
            // by design, and that IS the downgrade this format cannot fix. What
            // still holds for a committed row is that the key wins, which is
            // checked where it can be checked against a real asset: [AST], C2.
            foreach (BodyDef d in BodyCatalog.Seed)
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

            foreach (WheelDef d in WheelCatalog.Seed)
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

        // ---- presets ---------------------------------------------------------

        /// <summary>
        /// Every built-in design, as the pickers hand it out. K5 moved the
        /// presets onto keys — <c>bodyKey = "body_coupe"</c> rather than
        /// <c>bodyShape = BodyShape.Coupe</c> — and the pair is made to agree by
        /// a wrapper in the <c>All</c> table, which is one place to forget.
        /// So the check is on the OUTPUT: whatever route a preset took, the key
        /// it carries and the int beside it must name the same row.
        ///
        /// This is not the transcription check the deleted switches had. It is
        /// the thing that actually goes wrong: a design handed to
        /// <c>CosmeticProbe</c> or the attract loop, both of which read
        /// <c>bodyShape</c> raw, while the renderer reads the key.
        /// </summary>
        private static int Presets()
        {
            int fail = 0;
            foreach (var p in VehiclePresets.All)
            {
                string why = "";
                VehicleDesign d = p.build();

                if (string.IsNullOrEmpty(d.bodyKey)) why += " no bodyKey";
                else if (BodyCatalog.ById(d.bodyKey) == null)
                    why += $" bodyKey '{d.bodyKey}' is not a catalogue row";
                else if (d.Body.legacy != d.bodyShape)
                    why += $" bodyKey '{d.bodyKey}' and bodyShape {d.bodyShape} disagree";

                for (int i = 0; d.wheels != null && i < d.wheels.Count; i++)
                {
                    WheelSpec w = d.wheels[i];
                    if (string.IsNullOrEmpty(w.wheelKey)) { why += $" wheel {i} has no wheelKey"; continue; }
                    if (WheelCatalog.ById(w.wheelKey) == null)
                    { why += $" wheel {i} key '{w.wheelKey}' is not a catalogue row"; continue; }
                    if (w.Wheel.legacy != w.wheelStyle)
                        why += $" wheel {i} key '{w.wheelKey}' and style {w.wheelStyle} disagree";
                }

                // A preset must survive the round trip its own Save would do.
                // Migrate is idempotent by K2's check; this is the other half —
                // that the preset was already in the state Save would leave it.
                VehicleDesign again = Round(d);
                again.Migrate();
                if (again.bodyKey != d.bodyKey || again.bodyShape != d.bodyShape)
                    why += " a save would change the body pair";

                if (why.Length == 0) Debug.Log($"{Tag} PASS preset:{p.name}");
                else { Debug.LogError($"{Tag} FAIL preset:{p.name} -{why}"); fail++; }
            }
            return fail;
        }

        // ---- loadouts --------------------------------------------------------

        /// <summary>
        /// What a <c>progress.json</c> means. This is the section the plan owes
        /// K5: <b>an existing progress.json with <c>wheelStyle: 7</c> still
        /// yields gold wheels</b> — which the design dump structurally cannot
        /// witness, because no design in its enumeration uses styles 6-8.
        ///
        /// Built as TEXT and parsed, not constructed in memory, for the same
        /// reason K2's migration section is: what a field does to a save file is
        /// the whole subject, and a pre-K5 file has no <c>wheelKey</c> member at
        /// all rather than an empty one.
        /// </summary>
        private static int Loadouts()
        {
            int fail = 0;

            // 1. The pre-K5 file, verbatim: an int and nothing else.
            Check(ref fail, "old-int",
                "{\"vehicleName\":\"TT Coupe\",\"wheelStyle\":7}", "slick_gold", 7);

            // 2. What this build writes: both halves, agreeing.
            Check(ref fail, "both",
                "{\"vehicleName\":\"TT Coupe\",\"wheelStyle\":7,\"wheelKey\":\"slick_gold\"}",
                "slick_gold", 7);

            // 3. Disagreeing, the downgrade-then-upgrade case: an old build read
            //    the file, wrote the int it understood, and a newer build reads
            //    both. The KEY wins, exactly as it does on a design.
            Check(ref fail, "key-wins",
                "{\"vehicleName\":\"TT Coupe\",\"wheelStyle\":3,\"wheelKey\":\"slick_gold\"}",
                "slick_gold", 7);

            // 4. Key only, no int — a wheel with no legacy value, which is what
            //    Asset Studio will commit. The int sentinel is still -1 here, so
            //    an override keyed on `wheelStyle >= 0` alone would do nothing.
            Check(ref fail, "key-only",
                "{\"vehicleName\":\"TT Coupe\",\"wheelKey\":\"slick_gold\"}", "slick_gold", 7);

            // 5. Untouched: neither half set, and the design keeps what it
            //    authored. The Coupe's own wheel, not the slick.
            {
                string why = "";
                var l = JsonUtility.FromJson<VehicleLoadout>("{\"vehicleName\":\"TT Coupe\"}");
                var d = VehiclePresets.Resolve("TT Coupe");
                string before = d.wheels[0].wheelKey;
                Progression.ApplyLoadout(d, l);
                if (l.wheelStyle != -1 || l.wheelKey != "") why += " sentinels are not -1/\"\"";
                if (d.wheels[0].wheelKey != before)
                    why += $" an untouched loadout changed the wheel to '{d.wheels[0].wheelKey}'";
                if (why.Length == 0) Debug.Log($"{Tag} PASS loadout:untouched");
                else { Debug.LogError($"{Tag} FAIL loadout:untouched -{why}"); fail++; }
            }

            // 6. The three showroom finishes each have an unlock that names them
            //    by KEY. This replaced `v >= 6`, so what has to be true is that
            //    every locked wheel is found and no free one is.
            {
                string why = "";
                foreach (WheelDef d in WheelCatalog.All)
                {
                    var item = UnlockCatalog.ByWheelKey(d.id);
                    bool shouldLock = d.finish != WheelFinish.None;
                    if (shouldLock && item == null) why += $" {d.id} has no unlock row";
                    if (!shouldLock && item != null) why += $" {d.id} is locked by '{item.id}'";
                    if (item != null && item.payload != d.legacy)
                        why += $" {item.id} payload {item.payload} != style {d.legacy}";
                }
                if (why.Length == 0) Debug.Log($"{Tag} PASS loadout:unlocks");
                else { Debug.LogError($"{Tag} FAIL loadout:unlocks -{why}"); fail++; }
            }

            return fail;
        }

        /// <summary>Parse a loadout as written, apply it to a real preset, and
        /// assert every wheel came out as the named row.</summary>
        private static void Check(ref int fail, string label, string json,
            string wantKey, int wantStyle)
        {
            string why = "";
            var l = JsonUtility.FromJson<VehicleLoadout>(json);
            var d = VehiclePresets.Resolve("TT Coupe");
            Progression.ApplyLoadout(d, l);
            foreach (var w in d.wheels)
            {
                if (w.wheelKey != wantKey)
                    { why += $" wheelKey '{w.wheelKey}' != '{wantKey}'"; break; }
                if (w.wheelStyle != wantStyle)
                    { why += $" wheelStyle {w.wheelStyle} != {wantStyle}"; break; }
                if (w.Wheel.finish != WheelCatalog.ById(wantKey).finish)
                    { why += " resolved to a different finish"; break; }
            }
            if (why.Length == 0) Debug.Log($"{Tag} PASS loadout:{label}");
            else { Debug.LogError($"{Tag} FAIL loadout:{label} -{why}"); fail++; }
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
