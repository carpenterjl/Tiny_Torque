using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIHWSim.Garage;
using AIHWSim.TrackEd;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Writes down what every token binder in the project actually produced —
    /// per part, per renderer, per submesh slot, with the material's name,
    /// shader, colour, metallic, smoothness, texture, keywords and render queue.
    ///
    /// <b>The gate is `git diff`, not a RESULT line.</b> Take a dump before
    /// touching the binding code, take another after, and an empty diff is the
    /// claim "nothing changed what renders", proved rather than asserted. That
    /// is why it lives beside <see cref="PartModelValidator"/> rather than
    /// inside it: the validator's contract is one pass/fail line, and a dump has
    /// no opinion at all about whether the numbers are right — only about
    /// whether they moved.
    ///
    /// <b>It observes the result, never the mapping.</b> Every row is read back
    /// off the built hierarchy with <c>sharedMaterials</c>. Building the dump
    /// out of the <see cref="MaterialBindings"/> the binders now return would
    /// only prove the mapping agrees with itself — the same trap
    /// <see cref="PartModelValidator"/>'s spec table documents, one level up.
    ///
    /// <b>It calls the game's own builders.</b> Wheels go through
    /// <c>BuildWheelViz</c>, bodies through <c>CarVehicle.BindBodyMesh</c>,
    /// cosmetics through <c>CosmeticCatalog.Build</c>, props through each
    /// <c>ItemDef.build</c>. A dumper with its own copy of a call site would
    /// keep passing after that call site changed.
    ///
    /// Edit mode is enough — binding needs no <c>Awake</c>, unlike the physics
    /// design dump next door. Run with (editor closed):
    ///   Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///     -executeMethod AIHWSim.EditorTools.PartModelBindingDump.Run
    ///     -logFile &lt;log&gt; -bindingDump &lt;out.txt&gt;
    /// </summary>
    public static class PartModelBindingDump
    {
        /// <summary>Wheel styles BuildWheelViz understands: 0-12 arcade, 13/14
        /// the Tiguan's front and rear. Hard-coded rather than derived because
        /// the range IS the contract — a new style must be added here on
        /// purpose, so the dump cannot silently stop covering one.</summary>
        private const int WheelStyles = 15;

        [MenuItem("Tools/AIHWSim/Dump Material Bindings")]
        public static void RunFromMenu() => Write(DefaultPath());

        public static void Run() => Write(ArgValue("-bindingDump") ?? DefaultPath());

        private static string DefaultPath() =>
            Path.Combine(Path.GetTempPath(), "binding_dump.txt");

        private static void Write(string outPath)
        {
            var sb = new StringBuilder();
            int rows = 0;

            // Everything is built under one parent that is destroyed at the end,
            // so a dump run leaves the open scene exactly as it found it. The
            // objects are HideAndDontSave for the same reason: an interrupted
            // run must not be able to save a car body into someone's scene.
            var root = new GameObject("~bindingdump") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                rows += Bodies(sb, root.transform);
                rows += Wheels(sb, root.transform);
                rows += Parts(sb, root.transform);
                rows += Cosmetics(sb, root.transform);
                rows += Props(sb, root.transform);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            File.WriteAllText(outPath, sb.ToString());
            Debug.Log($"[BIND] {rows} rows -> {outPath}");
        }

        // ---- sections --------------------------------------------------------

        /// <summary>
        /// Every <see cref="BodyShape"/> that has an authored shell, bound the
        /// way <see cref="CarVehicle"/> binds it.
        ///
        /// The paint material is a stand-in, not a real car's: <c>_bodyMat</c>
        /// carries the design's bodyColor and livery, which are not what this
        /// dump is about. What matters is WHICH renderers land on it, and the
        /// per-row paint flag records exactly that — the same set the garage
        /// painter cooks its stroke colliders from.
        /// </summary>
        private static int Bodies(StringBuilder sb, Transform parent)
        {
            var paintMat = new Material(Shader.Find("Standard"))
            {
                name = "<paint>",
                hideFlags = HideFlags.HideAndDontSave,
            };
            int rows = 0;
            try
            {
                // Still enumerated from the ENUM, not from BodyCatalog, and after
                // K3a that is the point: it is what makes "every shape a saved
                // design can carry is covered" true rather than "every row the
                // table happens to have". The catalogue is looked up per shape,
                // which is exactly what a car does.
                foreach (BodyShape shape in Enum.GetValues(typeof(BodyShape)))
                {
                    BodyDef def = BodyCatalog.ByLegacy(shape);
                    string key = def?.meshKey;
                    if (key == null) continue;      // Box/Wedge are pure primitives
                    var holder = Holder(parent, "body");
                    var paint = new HashSet<MeshRenderer>();
                    var inst = PartMeshLibrary.TryInstantiate(key, holder, holder.gameObject.layer);
                    if (inst != null)
                        CarVehicle.BindBodyMesh(inst, def, paintMat, paint);
                    rows += Emit(sb, $"body:{shape} key={key}", holder, paint);
                    UnityEngine.Object.DestroyImmediate(holder.gameObject);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(paintMat);
            }
            return rows;
        }

        /// <summary>
        /// Every wheel style through <c>BuildWheelViz</c>, which is the only way
        /// to cover <c>ApplyWheelFinish</c> — styles 6-8 are finishes over the
        /// slick mesh, so binding the mesh alone would miss three of them
        /// entirely and pass with the other twelve.
        ///
        /// Powered, because the motor can is a sibling under the same holder and
        /// its materials are part of what a wheel looks like. Inboard sign -1 so
        /// the mesh keeps its authored rotation; the +1 case is a 180° turn about
        /// Y, which moves no material.
        /// </summary>
        private static int Wheels(StringBuilder sb, Transform parent)
        {
            int rows = 0;
            for (int style = 0; style < WheelStyles; style++)
            {
                var holder = Holder(parent, "wheel");
                PartVisualFactory.BuildWheelViz(holder, PartVisualFactory.AuthorRadiusFor(style),
                                                powered: true, inboardSign: -1f, style: style);
                rows += Emit(sb, $"wheel:style{style}", holder, null);
                UnityEngine.Object.DestroyImmediate(holder.gameObject);
            }
            return rows;
        }

        /// <summary>Battery, antennas and light clusters — the three remaining
        /// <c>AssignByName</c> call sites inside PartVisualFactory, each with its
        /// own inline token table.</summary>
        private static int Parts(StringBuilder sb, Transform parent)
        {
            int rows = 0;

            var b = Holder(parent, "battery");
            PartVisualFactory.BuildBatteryViz(b);
            rows += Emit(sb, "battery", b, null);
            UnityEngine.Object.DestroyImmediate(b.gameObject);

            for (int style = 0; style < 4; style++)
            {
                var a = Holder(parent, "antenna");
                PartVisualFactory.BuildAntennaViz(a, 0f, 1f, style);
                rows += Emit(sb, $"antenna:style{style}", a, null);
                UnityEngine.Object.DestroyImmediate(a.gameObject);
            }

            for (int style = 0; style < 2; style++)
            {
                var l = Holder(parent, "light");
                PartVisualFactory.BuildLightViz(l, style, 1f);
                rows += Emit(sb, $"light:style{style}", l, null);
                UnityEngine.Object.DestroyImmediate(l.gameObject);
            }

            return rows;
        }

        /// <summary>The 47 unlockables and the four crates, through
        /// <c>CosmeticCatalog.Build</c> and its longest-token-first table.</summary>
        private static int Cosmetics(StringBuilder sb, Transform parent)
        {
            int rows = 0;
            var keys = new List<string>();
            foreach (var item in CosmeticCatalog.All) keys.Add(item.id);
            foreach (var crate in CosmeticCatalog.Crates) keys.Add(crate.id);
            keys.Sort(StringComparer.Ordinal);

            foreach (string key in keys)
            {
                var holder = Holder(parent, "cosmetic");
                CosmeticCatalog.Build(holder, key);
                rows += Emit(sb, $"cosmetic:{key}", holder, null);
                UnityEngine.Object.DestroyImmediate(holder.gameObject);
            }
            return rows;
        }

        /// <summary>
        /// Every placeable track item through its own <c>ItemDef.build</c>.
        ///
        /// That includes the ones with no mesh at all — the primitive walls,
        /// ramps and markers. They cost a few rows each and they are the only
        /// coverage the primitive fallback path has, which is the path every
        /// prop takes on a machine where the FBX did not ship.
        /// </summary>
        private static int Props(StringBuilder sb, Transform parent)
        {
            var items = new List<ItemDef>(TrackCatalog.Items);
            items.Sort((x, y) => string.CompareOrdinal(x.id, y.id));

            int rows = 0;
            foreach (var it in items)
            {
                if (it.build == null) continue;
                var holder = Holder(parent, "prop");
                try { it.build(holder); }
                catch (Exception e)
                {
                    sb.Append("=== prop:").Append(it.id).Append('\n')
                      .Append("  BUILD FAILED: ").Append(e.Message).Append('\n');
                    Debug.LogError($"[BIND] {it.id}: {e.Message}");
                    UnityEngine.Object.DestroyImmediate(holder.gameObject);
                    continue;
                }
                rows += Emit(sb, $"prop:{it.id}", holder, null);
                UnityEngine.Object.DestroyImmediate(holder.gameObject);
            }
            return rows;
        }

        // ---- emit ------------------------------------------------------------

        private static Transform Holder(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>
        /// One line per renderer per submesh slot, sorted so the file is a
        /// function of what was built and not of hierarchy walk order.
        ///
        /// Slots are spelled out rather than collapsed to slot 0 because "the
        /// binders write <c>sharedMaterial</c>, which is slot 0 and nothing
        /// else" is a fact this dump should state out loud: a two-material
        /// object shows its second slot sitting at whatever the import left,
        /// and that is precisely what R3's per-slot binding changes.
        /// </summary>
        private static int Emit(StringBuilder sb, string label, Transform root,
                                HashSet<MeshRenderer> paint)
        {
            var lines = new List<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                string path = PathOf(root, r.transform);
                bool isPaint = paint != null && r is MeshRenderer mr && paint.Contains(mr);
                string tail = $" | on={(r.enabled ? 1 : 0)} paint={(isPaint ? 1 : 0)}";
                var mats = r.sharedMaterials;
                if (mats.Length == 0)
                {
                    lines.Add($"  {path} | slot=-/0 | mat=<none>{tail}");
                    continue;
                }
                for (int i = 0; i < mats.Length; i++)
                    lines.Add($"  {path} | slot={i}/{mats.Length} | {Describe(mats[i])}{tail}");
            }
            lines.Sort(StringComparer.Ordinal);

            sb.Append("=== ").Append(label).Append('\n');
            foreach (string l in lines) sb.Append(l).Append('\n');
            return lines.Count;
        }

        /// <summary>Hierarchy path below <paramref name="root"/>. Full path, not
        /// the bare object name: names repeat inside a prop (five "Cylinder"
        /// studs), and a sort over bare names would shuffle identical keys
        /// against each other between runs.</summary>
        private static string PathOf(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null && cur != root; cur = cur.parent)
                parts.Add(cur.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Describe(Material m)
        {
            if (m == null) return "mat=<null>";

            var kw = m.shaderKeywords;
            Array.Sort(kw, StringComparer.Ordinal);

            var tex = m.HasProperty("_MainTex") ? m.mainTexture : null;
            return "mat=" + m.name +
                   " sh=" + (m.shader != null ? m.shader.name : "<null>") +
                   " col=" + Col(m, "_Color") +
                   " met=" + Num(m, "_Metallic") +
                   " gls=" + Num(m, "_Glossiness") +
                   " emi=" + Col(m, "_EmissionColor") +
                   " mode=" + Num(m, "_Mode") +
                   " tex=" + (tex != null ? tex.name : "-") +
                   " q=" + m.renderQueue.ToString(CultureInfo.InvariantCulture) +
                   " kw=[" + string.Join(";", kw) + "]";
        }

        // "R" round-trip, not a rounded format: a dump whose job is to catch
        // drift must not be able to round drift away.
        private static string Num(Material m, string prop) =>
            m.HasProperty(prop)
                ? m.GetFloat(prop).ToString("R", CultureInfo.InvariantCulture)
                : "-";

        private static string Col(Material m, string prop)
        {
            if (!m.HasProperty(prop)) return "-";
            var c = m.GetColor(prop);
            return c.r.ToString("R", CultureInfo.InvariantCulture) + "," +
                   c.g.ToString("R", CultureInfo.InvariantCulture) + "," +
                   c.b.ToString("R", CultureInfo.InvariantCulture) + "," +
                   c.a.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string ArgValue(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
