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

        [MenuItem(TrackStudio.Menu + "Surface Brush", priority = TrackStudio.PrioBrush)]
        public static void Open()
        {
            var w = GetWindow<SurfaceBrushWindow>(false, "Surface Brush", true);
            w.minSize = new Vector2(300f, 320f);
            w.Show();
        }

        private void OnEnable() => SceneView.duringSceneGui += OnSceneGui;
        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGui;

        // -------------------------------------------------------------------
        // window
        // -------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.Space();

            var names = new string[TrackCatalog.Floors.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var f = TrackCatalog.Floors[i];
                names[i] = $"{i}  {f.label}   (grip {f.frictionMult:0.00})";
            }
            _floorType = EditorGUILayout.Popup("Surface", _floorType, names);

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
                "collider gets a SurfaceTag component.", MessageType.None);
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
            Handles.color = terrain
                ? new Color(0.6f, 1f, 0.4f, 0.9f)
                : new Color(0.4f, 0.9f, 1f, 0.9f);
            Handles.DrawWireDisc(aim.point, aim.normal, _radius);
            Handles.DrawWireDisc(aim.point, aim.normal, _radius * 0.02f);
            view.Repaint();

            bool paint = (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag)
                         && ev.button == 0 && !ev.alt;
            if (!paint) return;

            if (terrain) PaintTerrain(aim);
            else PaintCollider(aim.collider);

            ev.Use();
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
            int layer = LayerForFloor(td, table, _floorType);
            if (layer < 0)
            {
                TrackStudio.Warn($"no TerrainLayer on '{terrain.name}' maps to floor " +
                    $"{_floorType} ({TrackCatalog.Floors[_floorType].label}). Add a row to " +
                    $"'{table.name}' and the matching layer to the terrain.");
                return;
            }

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
        }

        /// <summary>
        /// The terrain's own layer index whose asset maps to this floor type.
        /// Resolved per terrain because terrainLayers is per-terrain: layer 2 may be
        /// grass on one tile and gravel on the next.
        /// </summary>
        private static int LayerForFloor(TerrainData td, TerrainFloorTable table, int floorType)
        {
            var layers = td.terrainLayers;
            if (layers == null) return -1;
            for (int i = 0; i < layers.Length; i++)
                if (layers[i] != null && table.FloorFor(layers[i]) == floorType) return i;
            return -1;
        }
    }
}
