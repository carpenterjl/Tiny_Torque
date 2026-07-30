using System.Collections.Generic;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Paints <c>TrackCatalog.Floors</c> surface types onto a scene track — onto
    /// Unity Terrain through its alphamap, and onto mesh colliders through a
    /// <see cref="SurfaceTag"/>.
    ///
    /// Two targets, one brush, because the author is doing one thing: deciding what
    /// a patch of ground is made of. Which mechanism carries that decision is an
    /// implementation detail of the thing under the cursor, and asking the user to
    /// track it would be asking them to think about SurfaceMap's resolution order.
    ///
    /// <b>Nothing is unpaintable.</b> A terrain with no TerrainLayer for the chosen
    /// floor gets one built (see <see cref="TerrainLayerLibrary"/>), so the only way
    /// a stroke can do nothing is a target the author switched off in the window's
    /// own list — and that case draws the brush red and says so under the cursor.
    /// The earlier behaviour, refusing with a console warning per stamp, made a
    /// setup problem look like a broken tool.
    ///
    /// Scene-view interaction follows ScatterBrushWindow exactly — control ID taken
    /// first, AddDefaultControl only during Layout, alt/right-drag left to the
    /// camera, ev.Use() only on an actual paint.
    /// </summary>
    public sealed class SurfaceBrushWindow : EditorWindow
    {
        private int _floorType = 1;          // asphalt
        private float _radius = 1.5f;
        private float _strength = 1f;
        private bool _active;
        private LayerMask _mask = ~0;

        /// <summary>True between MouseDown and MouseUp. Undo is registered once per
        /// STROKE rather than once per stamp: a terrain alphamap undo entry copies
        /// the whole map, so per-stamp registration would make a two-second drag
        /// allocate tens of megabytes and give the user forty Ctrl+Z presses to get
        /// back where they started.</summary>
        private bool _stroking;
        private TerrainData _strokeUndoTarget;

        /// <summary>
        /// One paintable thing in the scene, as the brush window lists it.
        ///
        ///
        /// Grouped by scene root rather than per collider: a scene with a few hundred
        /// props would give a few hundred rows, and nobody wants to hunt for
        /// "Barrel_037" in a list. A root is also the unit an author already thinks
        /// in — "don't paint the buildings" is a sentence about a root.
        /// </summary>
        private sealed class Target
        {
            public GameObject root;
            public int terrains, roads, meshes;
            public string Key => root.name;
            public int Total => terrains + roads + meshes;
        }

        /// <summary>Rebuilt from the scene, never serialised. Keyed by root NAME so a
        /// toggle survives a domain reload, which instance ids do not. Two roots with
        /// the same name therefore share one toggle — rename one if that matters.</summary>
        private List<Target> _targets;
        private readonly Dictionary<string, bool> _off = new Dictionary<string, bool>();
        private Vector2 _scroll;

        [MenuItem(TrackStudio.Menu + "Surface Brush", priority = TrackStudio.PrioBrush)]
        public static void Open()
        {
            var w = GetWindow<SurfaceBrushWindow>(false, "Surface Brush", true);
            w.minSize = new Vector2(320f, 420f);
            w.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.hierarchyChanged += Invalidate;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
            EditorApplication.hierarchyChanged -= Invalidate;
        }

        private void Invalidate() { _targets = null; Repaint(); }

        // -------------------------------------------------------------------
        // window
        // -------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.Space();

            // The same list every other floor field shows, so "Asphalt" reads the
            // same in the brush, the inspector and the terrain table.
            var names = FloorTypeDrawer.Names;
            _floorType = EditorGUILayout.Popup(
                new GUIContent("Surface"), _floorType, names);

            var def = TrackCatalog.Floors[Mathf.Clamp(_floorType, 0, names.Length - 1)];
            // The off-track threshold is a property of frictionMult, not a separate
            // flag, so it is worth saying out loud while painting rather than
            // discovering it as "why do track limits never trigger here".
            if (def.frictionMult <= 0.90f)
                EditorGUILayout.HelpBox(
                    $"grip {def.frictionMult:0.00} is at or below the arcade off-track " +
                    "threshold (0.90) — driving here counts as leaving the track.",
                    MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    $"grip {def.frictionMult:0.00} is above the off-track threshold " +
                    "(0.90) — this counts as on-track.", MessageType.None);

            EditorGUILayout.Space();
            _radius = EditorGUILayout.Slider("Radius (m)", _radius, 0.1f, 20f);
            _strength = EditorGUILayout.Slider("Strength", _strength, 0.05f, 1f);
            _mask = LayerMaskField("Paintable layers", _mask);

            EditorGUILayout.Space();
            var d = FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null)
                EditorGUILayout.HelpBox(
                    "No SceneTrackDescriptor in this scene. Mesh painting still works; " +
                    "terrain painting needs the descriptor's TerrainFloorTable to know " +
                    "which TerrainLayer means which surface.", MessageType.Warning);
            else if (d.terrainFloors == null)
                EditorGUILayout.HelpBox(
                    "The descriptor has no TerrainFloorTable — terrain painting is " +
                    "disabled until one is assigned.", MessageType.Warning);
            else
                EditorGUILayout.LabelField("Terrain table", d.terrainFloors.name);

            DrawTargets();

            EditorGUILayout.Space();
            GUI.backgroundColor = _active ? new Color(0.5f, 1f, 0.5f) : Color.white;
            if (GUILayout.Button(_active ? "Painting — click to stop" : "Start painting",
                                 GUILayout.Height(30f)))
            {
                _active = !_active;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.HelpBox(
                "Drag in the Scene view to paint.\n" +
                "Terrain under the cursor is painted into its alphamap; any other " +
                "collider gets a SurfaceTag component. A terrain missing the layer " +
                "for this surface has one created for it.", MessageType.None);
        }

        // -------------------------------------------------------------------
        // targets
        // -------------------------------------------------------------------

        private void DrawTargets()
        {
            if (_targets == null) Rebuild();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Paint targets ({_targets.Count})",
                                           EditorStyles.boldLabel);
                if (GUILayout.Button("All", GUILayout.Width(44f))) _off.Clear();
                if (GUILayout.Button("None", GUILayout.Width(52f)))
                    foreach (var t in _targets) _off[t.Key] = true;
                if (GUILayout.Button("Refresh", GUILayout.Width(66f))) Rebuild();
            }

            if (_targets.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing in this scene has a collider, so there is nothing to " +
                    "paint on.", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(190f));
            foreach (var t in _targets)
            {
                if (t.root == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool on = !_off.ContainsKey(t.Key);
                    bool next = EditorGUILayout.ToggleLeft(
                        new GUIContent(t.root.name, Describe(t)), on,
                        GUILayout.MinWidth(120f));
                    if (next != on)
                    {
                        if (next) _off.Remove(t.Key); else _off[t.Key] = true;
                        SceneView.RepaintAll();
                    }
                    GUILayout.Label(Describe(t), EditorStyles.miniLabel,
                                    GUILayout.Width(120f));
                    if (GUILayout.Button("Select", EditorStyles.miniButton,
                                         GUILayout.Width(52f)))
                        Selection.activeGameObject = t.root;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private static string Describe(Target t)
        {
            var parts = new List<string>(3);
            if (t.terrains > 0) parts.Add($"{t.terrains} terrain");
            if (t.roads > 0) parts.Add($"{t.roads} road");
            if (t.meshes > 0) parts.Add($"{t.meshes} mesh");
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Walk the scene once and group every collider under its root. Cached rather
        /// than done per repaint — this is a full-scene <c>GetComponentsInChildren</c>
        /// and the window repaints continuously while the brush is live.
        /// </summary>
        private void Rebuild()
        {
            _targets = new List<Target>();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (!scene.IsValid()) return;

            foreach (var root in scene.GetRootGameObjects())
            {
                var cols = root.GetComponentsInChildren<Collider>(true);
                if (cols.Length == 0) continue;

                var t = new Target { root = root };
                foreach (var c in cols)
                {
                    if (c is TerrainCollider) t.terrains++;
                    else if (c.GetComponentInParent<TrackSplineAuthoring>() != null) t.roads++;
                    else t.meshes++;
                }
                if (t.Total > 0) _targets.Add(t);
            }
            _targets.Sort((a, b) => string.CompareOrdinal(a.root.name, b.root.name));
        }

        /// <summary>The list row a collider belongs to, or null if its root is gone.</summary>
        private static string RootKey(Collider col)
            => col != null ? col.transform.root.name : null;

        private bool Enabled(Collider col)
        {
            string key = RootKey(col);
            return key != null && !_off.ContainsKey(key);
        }

        private static LayerMask LayerMaskField(string label, LayerMask mask)
        {
            var names = new System.Collections.Generic.List<string>();
            var ids = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 32; i++)
            {
                string n = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(n)) { names.Add(n); ids.Add(i); }
            }
            int shown = 0;
            for (int i = 0; i < ids.Count; i++)
                if ((mask.value & (1 << ids[i])) != 0) shown |= 1 << i;
            shown = EditorGUILayout.MaskField(label, shown, names.ToArray());
            int outv = 0;
            for (int i = 0; i < ids.Count; i++)
                if ((shown & (1 << i)) != 0) outv |= 1 << ids[i];
            return outv;
        }

        // -------------------------------------------------------------------
        // scene view
        // -------------------------------------------------------------------

        private void OnSceneGui(SceneView view)
        {
            if (!_active) return;

            int control = GUIUtility.GetControlID(FocusType.Passive);
            var ev = Event.current;

            if (ev.type == EventType.Layout)
            {
                // Take the default control so a drag paints instead of
                // rubber-band-selecting.
                HandleUtility.AddDefaultControl(control);
                return;
            }

            if (ev.type == EventType.MouseUp && ev.button == 0) EndStroke();

            var ray = HandleUtility.GUIPointToWorldRay(ev.mousePosition);
            if (!Physics.Raycast(ray, out var aim, 2000f, _mask,
                                 QueryTriggerInteraction.Ignore))
                return;

            bool terrain = aim.collider is TerrainCollider;
            bool allowed = Enabled(aim.collider);
            DrawBrush(aim, terrain, allowed);
            view.Repaint();

            bool paint = (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag)
                         && ev.button == 0 && !ev.alt;
            if (!paint) return;

            // The only refusal left. Everything else the brush needs — the layer, the
            // table row, the terrain slot — it makes for itself, so "nothing happened"
            // always means "you switched this off", which is a thing you can see.
            if (!allowed)
            {
                ev.Use();
                return;
            }

            if (terrain) PaintTerrain(aim);
            else PaintCollider(aim.collider);

            ev.Use();
        }

        /// <summary>
        /// The brush footprint, filled and tinted with the floor being painted.
        ///
        /// A wire ring says where the brush is; it does not say what the brush will
        /// do. Filling it in the surface's own colour makes the answer to "is this
        /// working" visible before the click as well as after it — and a target you
        /// switched off goes red, which is now the only way a stroke can do nothing.
        /// </summary>
        private void DrawBrush(RaycastHit aim, bool terrain, bool allowed)
        {
            var def = TrackCatalog.Floors[Mathf.Clamp(_floorType, 0,
                                                      TrackCatalog.Floors.Length - 1)];
            Color tint = allowed ? SurfaceColor(def.frictionMult)
                                 : new Color(0.95f, 0.3f, 0.25f);

            // Lifted off the surface, or z-fighting with the ground makes the disc
            // strobe as the camera moves.
            Vector3 at = aim.point + aim.normal * 0.02f;

            Handles.color = new Color(tint.r, tint.g, tint.b, 0.22f);
            Handles.DrawSolidDisc(at, aim.normal, _radius);
            Handles.color = new Color(tint.r, tint.g, tint.b, 0.95f);
            Handles.DrawWireDisc(at, aim.normal, _radius);
            Handles.DrawWireDisc(at, aim.normal, _radius * 0.02f);

            Handles.Label(at + aim.normal * 0.05f,
                allowed ? $"{def.label} → {(terrain ? "terrain" : "SurfaceTag")}"
                        : $"{RootKey(aim.collider)} — turned off in Paint targets");
        }

        /// <summary>Same grip-to-colour rule the spline channel handles use, so a
        /// surface looks the same wherever you meet it.</summary>
        private static Color SurfaceColor(float grip)
        {
            if (grip < 0.90f) return new Color(0.95f, 0.55f, 0.25f);
            float t = Mathf.InverseLerp(0.90f, 1.10f, grip);
            return Color.Lerp(new Color(0.35f, 0.6f, 1f), new Color(0.4f, 1f, 0.5f), t);
        }

        private void EndStroke()
        {
            _stroking = false;
            _strokeUndoTarget = null;
        }

        // -------------------------------------------------------------------
        // mesh target
        // -------------------------------------------------------------------

        /// <summary>
        /// Stamp a SurfaceTag onto a mesh collider. SurfaceMap caches the tag per
        /// collider, and a tagged collider wins over every other resolution — so
        /// this is the precise, local override, and it is what a spline ribbon
        /// already uses.
        /// </summary>
        private void PaintCollider(Collider col)
        {
            if (col == null) return;
            var tag = col.GetComponent<SurfaceTag>();
            if (tag == null)
            {
                tag = Undo.AddComponent<SurfaceTag>(col.gameObject);
            }
            else
            {
                if (tag.floorType == _floorType) return;   // no-op, no undo entry
                // RecordObject, not RegisterCreatedObjectUndo: this mutates an
                // existing component rather than creating one, and the scatter
                // brush never needed this API because it only ever adds and removes
                // whole objects.
                Undo.RecordObject(tag, "Paint Surface");
            }
            tag.floorType = _floorType;
            EditorUtility.SetDirty(tag);
        }

        // -------------------------------------------------------------------
        // terrain target
        // -------------------------------------------------------------------

        private void PaintTerrain(RaycastHit aim)
        {
            var terrain = aim.collider.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null) return;

            var d = FindFirstObjectByType<SceneTrackDescriptor>();
            var table = d != null ? d.terrainFloors : null;
            if (table == null)
            {
                TrackStudio.Warn("no TerrainFloorTable on the scene descriptor — " +
                                 "cannot decide which TerrainLayer means this surface.");
                return;
            }

            var td = terrain.terrainData;

            // Provision rather than refuse: the layer asset, the table row and the
            // terrain's own layer slot are all created on demand. This is why a
            // stroke on a never-painted terrain works the first time.
            int layer = TerrainLayerLibrary.EnsureLayer(td, table, _floorType);
            if (layer < 0) return;   // EnsureLayer has already said why

            if (!_stroking || _strokeUndoTarget != td)
            {
                // One undo entry per stroke per terrain. A full alphamap copy is
                // expensive enough that per-stamp registration is not an option.
                Undo.RegisterCompleteObjectUndo(td, "Paint Terrain Surface");
                _stroking = true;
                _strokeUndoTarget = td;
            }

            // World -> normalized terrain -> alphamap texel.
            Vector3 local = aim.point - terrain.transform.position;
            var size = td.size;
            float nx = Mathf.Clamp01(local.x / Mathf.Max(0.001f, size.x));
            float nz = Mathf.Clamp01(local.z / Mathf.Max(0.001f, size.z));

            int w = td.alphamapWidth, h = td.alphamapHeight;
            // Texels per metre differs per axis when a terrain is not square.
            int rx = Mathf.Max(1, Mathf.CeilToInt(_radius / Mathf.Max(0.001f, size.x) * w));
            int rz = Mathf.Max(1, Mathf.CeilToInt(_radius / Mathf.Max(0.001f, size.z) * h));
            int cx = Mathf.RoundToInt(nx * (w - 1));
            int cz = Mathf.RoundToInt(nz * (h - 1));

            int x0 = Mathf.Clamp(cx - rx, 0, w - 1);
            int z0 = Mathf.Clamp(cz - rz, 0, h - 1);
            int x1 = Mathf.Clamp(cx + rx, 0, w - 1);
            int z1 = Mathf.Clamp(cz + rz, 0, h - 1);
            int bw = x1 - x0 + 1, bh = z1 - z0 + 1;

            // Only the dirty sub-rect is read and written. Writing the whole map per
            // stamp would stall the drag on any terrain worth painting.
            float[,,] maps = td.GetAlphamaps(x0, z0, bw, bh);   // [z, x, layer]
            int layers = maps.GetLength(2);

            for (int z = 0; z < bh; z++)
            {
                for (int x = 0; x < bw; x++)
                {
                    float dx = (x0 + x - cx) / (float)rx;
                    float dz = (z0 + z - cz) / (float)rz;
                    float dd = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dd > 1f) continue;

                    // Soft edge, so overlapping strokes blend instead of stepping.
                    float fall = Mathf.SmoothStep(1f, 0f, dd) * Mathf.Clamp01(_strength);
                    if (fall <= 0f) continue;

                    float target = maps[z, x, layer];
                    float want = Mathf.Lerp(target, 1f, fall);
                    float rest = 1f - want;
                    float others = 0f;
                    for (int l = 0; l < layers; l++) if (l != layer) others += maps[z, x, l];

                    // Renormalise: weights must sum to 1 or the terrain shader and
                    // the SurfaceMap bake disagree about what is painted here.
                    if (others > 1e-5f)
                    {
                        float k = rest / others;
                        for (int l = 0; l < layers; l++)
                            if (l != layer) maps[z, x, l] *= k;
                    }
                    else
                    {
                        for (int l = 0; l < layers; l++) if (l != layer) maps[z, x, l] = 0f;
                        want = 1f;
                    }
                    maps[z, x, layer] = want;
                }
            }

            td.SetAlphamaps(x0, z0, maps);

            // Push the new weights into the terrain's rendering data now. Without
            // this the splat textures can lag a stroke by a repaint or more, which
            // makes painting feel like it is not working — you are looking at the
            // old ground while the data underneath has already changed.
            terrain.Flush();
            SceneView.RepaintAll();
        }

    }
}
