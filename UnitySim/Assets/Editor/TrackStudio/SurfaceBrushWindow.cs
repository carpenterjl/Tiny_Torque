using System.Collections.Generic;
using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Paints <c>TrackCatalog.Floors</c> surface types onto a scene track — onto
    /// Unity Terrain through its alphamap, onto a spline road through its surface
    /// channel, and onto anything else through a <see cref="SurfaceTag"/>.
    ///
    /// Three targets, one brush, because the author is doing one thing: deciding what
    /// a patch of ground is made of. Which mechanism carries that decision is an
    /// implementation detail of the thing under the cursor, and asking the user to
    /// track it would be asking them to think about SurfaceMap's resolution order.
    ///
    /// <b>A road is painted into the spline, not onto the ribbon.</b> The ribbon's
    /// colliders are destroyed and rebuilt by every <c>TrackSplineAuthoring.Bake</c>,
    /// which now runs live on every knot drag — so a SurfaceTag stamped on a ribbon
    /// collider survives until the next edit and then silently vanishes. Writing keys
    /// into <c>surfaceChannel</c> instead makes the paint part of the road's own data:
    /// it survives a rebuild, it moves with the curve, and it is the same channel the
    /// inspector and the Scene-view handles edit.
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
        /// <summary>Brush footprint. Circle is what a surface brush usually wants;
        /// Square exists because a rotated square is the only way to paint a straight
        /// edge — a run-off strip, the lip of a pit lane — without stair-stepping it
        /// out of overlapping discs.</summary>
        private enum Shape { Circle, Square }

        // ---- palette ----
        [SerializeField] private int _floorType = 1;          // asphalt

        // ---- brush ----
        [SerializeField] private Shape _shape = Shape.Circle;
        [SerializeField] private float _radius = 1.5f;
        [SerializeField] private float _rotation;             // degrees about the surface normal
        [SerializeField] private float _hardness = 0.35f;     // solid core as a fraction of the radius
        [SerializeField] private float _strength = 1f;

        // ---- stroke shaping ----
        [SerializeField] private float _spacing;              // stamp gap, in brush DIAMETERS
        [SerializeField] private float _scatter;              // random offset, in brush radii
        [SerializeField] private float _sizeJitter;
        [SerializeField] private float _rotationJitter;
        [SerializeField] private float _strengthJitter;

        [SerializeField] private LayerMask _mask = ~0;
        [SerializeField] private bool _showBrush = true;
        [SerializeField] private bool _showStroke = true;

        private bool _active;

        /// <summary>Jitter source. A private <c>System.Random</c> rather than
        /// <c>UnityEngine.Random</c>, which is global state shared with anything else
        /// the editor happens to be running.</summary>
        private readonly System.Random _rng = new System.Random();

        /// <summary>
        /// Live between MouseDown and MouseUp. Undo is registered once per STROKE per
        /// object rather than once per stamp: a terrain alphamap undo entry copies the
        /// whole map, so per-stamp registration would make a two-second drag allocate
        /// tens of megabytes and give the user forty Ctrl+Z presses to get back where
        /// they started. The same holds for a road — one drag is one edit.
        /// </summary>
        private readonly HashSet<Object> _recorded = new HashSet<Object>();

        /// <summary>Roads touched by the current stroke, rebuilt when it ends. Not
        /// per stamp: a rebake destroys and recreates GameObjects and re-cooks a
        /// MeshCollider, which at drag rate turns a stroke into a slideshow.</summary>
        private readonly List<TrackSplineAuthoring> _dirtyRoads =
            new List<TrackSplineAuthoring>();

        private Vector3 _lastStamp;
        private bool _hasLastStamp;

        /// <summary>
        /// One paintable thing in the scene, as the brush window lists it.
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
            w.minSize = new Vector2(340f, 460f);
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
            EndStroke();
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

            DrawBrushSettings();
            DrawStrokeSettings();

            EditorGUILayout.Space();
            _mask = LayerMaskField("Paintable layers", _mask);

            EditorGUILayout.Space();
            var d = FindFirstObjectByType<SceneTrackDescriptor>();
            if (d == null)
                EditorGUILayout.HelpBox(
                    "No SceneTrackDescriptor in this scene. Mesh and road painting " +
                    "still work; terrain painting needs the descriptor's " +
                    "TerrainFloorTable to know which TerrainLayer means which surface.",
                    MessageType.Warning);
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
                "• Terrain goes into the alphamap — a missing layer is created.\n" +
                "• A spline road becomes keys in its surface channel, so the paint " +
                "survives the next ribbon rebuild.\n" +
                "• Anything else gets a SurfaceTag component.", MessageType.None);
        }

        private void DrawBrushSettings()
        {
            EditorGUILayout.Space();
            _showBrush = EditorGUILayout.Foldout(_showBrush, "Brush", true,
                                                 EditorStyles.foldoutHeader);
            if (!_showBrush) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _shape = (Shape)EditorGUILayout.EnumPopup(
                    new GUIContent("Shape",
                        "Square is the only way to paint a straight edge without " +
                        "stair-stepping it out of overlapping discs."), _shape);
                _radius = EditorGUILayout.Slider(
                    new GUIContent("Size (m radius)",
                        "On a road this is half the length of the painted run along " +
                        "the curve. On a mesh it does nothing — a SurfaceTag applies " +
                        "to the whole collider."), _radius, 0.1f, 20f);
                _rotation = EditorGUILayout.Slider(
                    new GUIContent("Rotation (deg)",
                        "Turns the footprint about the surface normal. Only visible " +
                        "on a square brush — a circle is rotationally symmetric."),
                    _rotation, 0f, 360f);
                _hardness = EditorGUILayout.Slider(
                    new GUIContent("Hardness",
                        "Fraction of the radius painted at full strength before the " +
                        "edge falls off. 1 is a hard cut."), _hardness, 0f, 1f);
                _strength = EditorGUILayout.Slider(
                    new GUIContent("Strength",
                        "Alphamap weight laid down per stamp. Terrain only."),
                    _strength, 0.05f, 1f);
            }
        }

        private void DrawStrokeSettings()
        {
            EditorGUILayout.Space();
            _showStroke = EditorGUILayout.Foldout(_showStroke, "Stroke and jitter", true,
                                                  EditorStyles.foldoutHeader);
            if (!_showStroke) return;

            using (new EditorGUI.IndentLevelScope())
            {
                _spacing = EditorGUILayout.Slider(
                    new GUIContent("Spacing (diameters)",
                        "Minimum gap between stamps along the drag. 0 stamps on every " +
                        "mouse event, which is dense and smooth; raise it to break a " +
                        "stroke into separate marks."), _spacing, 0f, 2f);
                _scatter = EditorGUILayout.Slider(
                    new GUIContent("Scatter (radii)",
                        "Random offset from the cursor, in the surface plane. Frays " +
                        "the edge of a patch so it does not read as a stencil."),
                    _scatter, 0f, 2f);

                EditorGUILayout.LabelField("Jitter", EditorStyles.miniBoldLabel);
                _sizeJitter = EditorGUILayout.Slider("Size", _sizeJitter, 0f, 1f);
                _rotationJitter = EditorGUILayout.Slider("Rotation", _rotationJitter, 0f, 1f);
                _strengthJitter = EditorGUILayout.Slider("Strength", _strengthJitter, 0f, 1f);

                if (_strengthJitter > 0f || _strength < 1f || _hardness < 1f)
                    EditorGUILayout.HelpBox(
                        "Strength and hardness blend alphamap weights, so they shape " +
                        "terrain only. A floor id is a discrete value: a road run and " +
                        "a SurfaceTag are either painted or not.", MessageType.None);
            }
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
                    else if (RoadFor(c) != null) t.roads++;
                    else t.meshes++;
                }
                if (t.Total > 0) _targets.Add(t);
            }
            _targets.Sort((a, b) => string.CompareOrdinal(a.root.name, b.root.name));
        }

        /// <summary>The spline road a collider belongs to, or null. A baked ribbon's
        /// colliders live under the authoring component, so one walk up answers it.</summary>
        private static TrackSplineAuthoring RoadFor(Collider col)
            => col != null ? col.GetComponentInParent<TrackSplineAuthoring>() : null;

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
            var names = new List<string>();
            var ids = new List<int>();
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
            var road = terrain ? null : RoadFor(aim.collider);
            bool allowed = Enabled(aim.collider);

            DrawBrushGizmo(aim, terrain, road, allowed);
            if (road != null) DrawRoadSurface(road);
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

            if (ev.type == EventType.MouseDown) _hasLastStamp = false;

            // Spacing is measured on the CURSOR, not on the scattered stamp — else a
            // high scatter would keep clearing the gate and spacing would do nothing.
            if (!SpacingOk(aim.point)) { ev.Use(); return; }
            _lastStamp = aim.point;
            _hasLastStamp = true;

            var stamp = BuildStamp(aim, terrain);

            if (terrain) PaintTerrain(aim, stamp);
            else if (road != null) PaintRoad(road, stamp);
            else PaintCollider(aim.collider);

            ev.Use();
        }

        // -------------------------------------------------------------------
        // stroke shaping
        // -------------------------------------------------------------------

        /// <summary>One placed brush impression, after jitter and scatter.</summary>
        private struct Stamp
        {
            public Vector3 point;
            public Vector3 normal;
            public float radius;
            public float rotation;
            public float strength;
        }

        /// <summary>Symmetric random in ±<paramref name="amount"/>.</summary>
        private float Rand(float amount)
            => (float)(_rng.NextDouble() * 2.0 - 1.0) * amount;

        private bool SpacingOk(Vector3 p)
        {
            if (_spacing <= 0f || !_hasLastStamp) return true;
            return Vector3.Distance(p, _lastStamp) >= _spacing * _radius * 2f;
        }

        private Stamp BuildStamp(RaycastHit aim, bool terrain)
        {
            // Terrain is painted in world XZ, so its footprint is oriented by world
            // up whatever the slope — using the surface normal there would squash the
            // stamp on a hillside relative to the texels it actually writes.
            Vector3 n = terrain ? Vector3.up : aim.normal;

            var s = new Stamp
            {
                point = aim.point,
                normal = n,
                radius = Mathf.Max(0.02f, _radius * (1f + Rand(_sizeJitter))),
                rotation = _rotation + Rand(_rotationJitter) * 180f,
                strength = Mathf.Clamp01(_strength * (1f + Rand(_strengthJitter))),
            };

            if (_scatter > 0f)
            {
                Basis(n, _rotation, out var u, out var v);
                double ang = _rng.NextDouble() * System.Math.PI * 2.0;
                // sqrt of a uniform draw, or the offsets bunch towards the centre and
                // the scatter reads as a blur rather than a spray.
                float mag = _scatter * s.radius * Mathf.Sqrt((float)_rng.NextDouble());
                s.point += (u * Mathf.Cos((float)ang) + v * Mathf.Sin((float)ang)) * mag;
            }
            return s;
        }

        /// <summary>An orthonormal pair in the plane of <paramref name="n"/>, turned
        /// by <paramref name="rotDeg"/> about it.</summary>
        private static void Basis(Vector3 n, float rotDeg, out Vector3 u, out Vector3 v)
        {
            Vector3 seed = Mathf.Abs(n.y) > 0.9f ? Vector3.right : Vector3.up;
            u = Vector3.Normalize(Vector3.Cross(n, seed));
            v = Vector3.Cross(n, u);
            var q = Quaternion.AngleAxis(rotDeg, n);
            u = q * u;
            v = q * v;
        }

        /// <summary>
        /// Brush weight at a normalized distance from the centre. <c>_hardness</c> is
        /// the fraction painted flat before the edge starts to fall away, so 1 is a
        /// stencil and 0 is a smooth dome.
        /// </summary>
        private float Falloff(float d)
        {
            if (d >= 1f) return 0f;
            if (d <= _hardness) return 1f;
            return Mathf.SmoothStep(1f, 0f, (d - _hardness) / Mathf.Max(1e-4f, 1f - _hardness));
        }

        /// <summary>Normalized distance under the current shape: a circle measures
        /// radially, a square by the larger axis, which is what makes its edge straight.</summary>
        private float ShapeDistance(float u, float v, float radius)
        {
            float r = Mathf.Max(1e-4f, radius);
            return _shape == Shape.Square
                ? Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)) / r
                : Mathf.Sqrt(u * u + v * v) / r;
        }

        // -------------------------------------------------------------------
        // gizmos
        // -------------------------------------------------------------------

        /// <summary>
        /// The brush footprint, filled and tinted with the floor being painted.
        ///
        /// A wire ring says where the brush is; it does not say what the brush will
        /// do. Filling it in the surface's own colour makes the answer to "is this
        /// working" visible before the click as well as after it — and a target you
        /// switched off goes red, which is now the only way a stroke can do nothing.
        /// </summary>
        private void DrawBrushGizmo(RaycastHit aim, bool terrain,
                                    TrackSplineAuthoring road, bool allowed)
        {
            var def = TrackCatalog.Floors[Mathf.Clamp(_floorType, 0,
                                                      TrackCatalog.Floors.Length - 1)];
            Color tint = allowed ? SurfaceColor(def.frictionMult)
                                 : new Color(0.95f, 0.3f, 0.25f);

            Vector3 n = terrain ? Vector3.up : aim.normal;
            // Lifted off the surface, or z-fighting with the ground makes the disc
            // strobe as the camera moves.
            Vector3 at = aim.point + aim.normal * 0.02f;

            var fill = new Color(tint.r, tint.g, tint.b, 0.22f);
            var line = new Color(tint.r, tint.g, tint.b, 0.95f);

            if (_shape == Shape.Square)
            {
                Basis(n, _rotation, out var u, out var v);
                var c = new[]
                {
                    at + ( u + v) * _radius,
                    at + ( u - v) * _radius,
                    at + (-u - v) * _radius,
                    at + (-u + v) * _radius,
                };
                Handles.DrawSolidRectangleWithOutline(c, fill, line);
            }
            else
            {
                Handles.color = fill;
                Handles.DrawSolidDisc(at, n, _radius);
                Handles.color = line;
                Handles.DrawWireDisc(at, n, _radius);
            }

            // The hardness ring: where full strength ends. Worth seeing, because on a
            // soft brush the visible footprint is much wider than the part that
            // actually reaches weight 1.
            if (_hardness > 0.01f && _hardness < 0.99f && _shape == Shape.Circle)
            {
                Handles.color = new Color(tint.r, tint.g, tint.b, 0.5f);
                Handles.DrawWireDisc(at, n, _radius * _hardness);
            }

            Handles.color = line;
            Handles.DrawWireDisc(at, n, _radius * 0.02f);

            if (_scatter > 0f)
            {
                Handles.color = new Color(tint.r, tint.g, tint.b, 0.35f);
                Handles.DrawWireDisc(at, n, _radius * (1f + _scatter));
            }

            string what = terrain ? "terrain"
                        : road != null ? $"{road.name} surface channel"
                        : "SurfaceTag";
            Handles.Label(at + n * 0.05f,
                allowed ? $"{def.label} → {what}"
                        : $"{RootKey(aim.collider)} — turned off in Paint targets");
        }

        /// <summary>
        /// The road's surface channel drawn along its own centreline, so a road you
        /// are about to paint already shows what it is made of. Without this the only
        /// feedback for a road stroke is the rebuild at the end of it, which is far
        /// too late to tell whether the brush is landing where you meant.
        /// </summary>
        private static void DrawRoadSurface(TrackSplineAuthoring a)
        {
            var container = a.Container;
            var spline = RoadSurfacePainter.SplineOf(a);
            if (spline == null || container == null) return;

            var l2w = container.transform.localToWorldMatrix;
            const int Steps = 160;
            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

            Vector3 prev = l2w.MultiplyPoint3x4((Vector3)spline.EvaluatePosition(0f));
            for (int i = 1; i <= Steps; i++)
            {
                float t = i / (float)Steps;
                Vector3 p = l2w.MultiplyPoint3x4((Vector3)spline.EvaluatePosition(t));
                int id = Mathf.Clamp(Mathf.RoundToInt(
                    RoadSurfacePainter.Sample(a.surfaceChannel, spline,
                                              t - 0.5f / Steps, a.defaultSurface)),
                    0, TrackCatalog.Floors.Length - 1);
                Handles.color = SurfaceColor(TrackCatalog.Floors[id].frictionMult);
                Handles.DrawAAPolyLine(6f, prev + Vector3.up * 0.03f, p + Vector3.up * 0.03f);
                prev = p;
            }

            Handles.zTest = prevZ;
        }

        /// <summary>Same grip-to-colour rule the spline channel handles use, so a
        /// surface looks the same wherever you meet it.</summary>
        private static Color SurfaceColor(float grip)
        {
            if (grip < 0.90f) return new Color(0.95f, 0.55f, 0.25f);
            float t = Mathf.InverseLerp(0.90f, 1.10f, grip);
            return Color.Lerp(new Color(0.35f, 0.6f, 1f), new Color(0.4f, 1f, 0.5f), t);
        }

        // -------------------------------------------------------------------
        // stroke lifetime
        // -------------------------------------------------------------------

        private void EndStroke()
        {
            _hasLastStamp = false;
            _recorded.Clear();

            // One rebuild per road per stroke. Deferred to here rather than done per
            // stamp because Bake destroys and recreates the ribbon and re-cooks its
            // MeshColliders — at drag rate that is a slideshow, and the Scene-view
            // centreline overlay already gave the feedback in the meantime.
            for (int i = 0; i < _dirtyRoads.Count; i++)
            {
                var a = _dirtyRoads[i];
                if (a == null) continue;
                if (a.HasBaked) a.Bake();
                EditorUtility.SetDirty(a);
            }
            if (_dirtyRoads.Count > 0)
            {
                _dirtyRoads.Clear();
                SceneView.RepaintAll();
            }
        }

        /// <summary>Register one undo entry for an object per stroke. Returns false if
        /// it was already registered, which is the common case inside a drag.</summary>
        private bool RecordOnce(Object o, string label, bool complete)
        {
            if (o == null || !_recorded.Add(o)) return false;
            if (complete) Undo.RegisterCompleteObjectUndo(o, label);
            else Undo.RecordObject(o, label);
            return true;
        }

        // -------------------------------------------------------------------
        // mesh target
        // -------------------------------------------------------------------

        /// <summary>
        /// Stamp a SurfaceTag onto a mesh collider. SurfaceMap caches the tag per
        /// collider, and a tagged collider wins over every other resolution — so
        /// this is the precise, local override.
        ///
        /// The tag applies to the WHOLE collider, so brush size, spacing and scatter
        /// have nothing to act on here; splitting a mesh into painted regions would
        /// mean splitting the mesh, which is a modelling decision, not a brush one.
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
                // existing component rather than creating one.
                Undo.RecordObject(tag, "Paint Surface");
            }
            tag.floorType = _floorType;
            EditorUtility.SetDirty(tag);
        }

        // -------------------------------------------------------------------
        // road target
        // -------------------------------------------------------------------

        /// <summary>
        /// Paint a run of road. The channel work is <see cref="RoadSurfacePainter"/>'s;
        /// what belongs here is the once-per-stroke undo entry and the note that this
        /// road owes a rebuild when the stroke ends.
        /// </summary>
        private void PaintRoad(TrackSplineAuthoring a, Stamp stamp)
        {
            RecordOnce(a, "Paint Road Surface", complete: false);
            if (!RoadSurfacePainter.Paint(a, stamp.point, stamp.radius, _floorType)) return;

            EditorUtility.SetDirty(a);
            if (!_dirtyRoads.Contains(a)) _dirtyRoads.Add(a);
        }

        // -------------------------------------------------------------------
        // terrain target
        // -------------------------------------------------------------------

        private void PaintTerrain(RaycastHit aim, Stamp stamp)
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

            // One full-map undo entry per terrain per stroke.
            RecordOnce(td, "Paint Terrain Surface", complete: true);

            // World -> normalized terrain -> alphamap texel.
            Vector3 local = stamp.point - terrain.transform.position;
            var size = td.size;
            float nx = Mathf.Clamp01(local.x / Mathf.Max(0.001f, size.x));
            float nz = Mathf.Clamp01(local.z / Mathf.Max(0.001f, size.z));

            int w = td.alphamapWidth, h = td.alphamapHeight;
            float mPerX = size.x / Mathf.Max(1, w - 1);
            float mPerZ = size.z / Mathf.Max(1, h - 1);

            // A square reaches its corner at radius*sqrt(2), and a rotated one can
            // put that corner on either axis — so the sampled rect covers the
            // circumscribing circle whatever the rotation.
            float ext = stamp.radius * (_shape == Shape.Square ? 1.4143f : 1f);
            int rx = Mathf.Max(1, Mathf.CeilToInt(ext / mPerX));
            int rz = Mathf.Max(1, Mathf.CeilToInt(ext / mPerZ));

            float fx = nx * (w - 1);
            float fz = nz * (h - 1);
            int cx = Mathf.RoundToInt(fx);
            int cz = Mathf.RoundToInt(fz);

            int x0 = Mathf.Clamp(cx - rx, 0, w - 1);
            int z0 = Mathf.Clamp(cz - rz, 0, h - 1);
            int x1 = Mathf.Clamp(cx + rx, 0, w - 1);
            int z1 = Mathf.Clamp(cz + rz, 0, h - 1);
            int bw = x1 - x0 + 1, bh = z1 - z0 + 1;

            // Rotate the sample point into brush space rather than rotating the
            // footprint — one sin/cos for the whole stamp instead of per texel.
            float rad = -stamp.rotation * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rad), sinR = Mathf.Sin(rad);

            // Only the dirty sub-rect is read and written. Writing the whole map per
            // stamp would stall the drag on any terrain worth painting.
            float[,,] maps = td.GetAlphamaps(x0, z0, bw, bh);   // [z, x, layer]
            int layers = maps.GetLength(2);

            for (int z = 0; z < bh; z++)
            {
                for (int x = 0; x < bw; x++)
                {
                    float wx = (x0 + x - fx) * mPerX;
                    float wz = (z0 + z - fz) * mPerZ;
                    float u = wx * cosR - wz * sinR;
                    float v = wx * sinR + wz * cosR;

                    float fall = Falloff(ShapeDistance(u, v, stamp.radius)) * stamp.strength;
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
