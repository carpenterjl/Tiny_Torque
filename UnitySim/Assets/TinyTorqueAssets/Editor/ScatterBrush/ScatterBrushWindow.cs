using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.Pack
{
    /// <summary>
    /// Scene-view scatter brush for the pack's prefabs.
    ///
    /// Open it, pick a preset, then hold the paint modifier and drag in the Scene
    /// view. Each stamp raycasts straight DOWN from above the cursor onto scene
    /// colliders, so it lands on whatever is actually there — the sandbox ground
    /// plane, a floor tile, the deck of a ramp — rather than on a maths plane that
    /// the geometry then intersects. Nothing is placed where nothing was hit, which
    /// is what stops a drag off the edge of the world from filling the sky.
    ///
    /// Every placement and erase goes through Undo, individually, so Ctrl+Z walks
    /// back through a pass one stamp at a time and the whole pass is one delete of
    /// the <c>Scatter/&lt;preset&gt;</c> parent.
    /// </summary>
    public sealed class ScatterBrushWindow : EditorWindow
    {
        private ScatterPreset _preset;
        private bool _active;
        private int _seed = 12345;
        private System.Random _rng;
        private Vector3 _lastStamp = new Vector3(float.MaxValue, 0f, 0f);
        private int _placed;

        /// <summary>Height the probe ray starts above the cursor point. Generous:
        /// this world is 1:10, but arena tiles reach 4 m and the town's water tower
        /// is taller still.</summary>
        private const float ProbeUp = 20f;
        private const float ProbeLen = 60f;

        [MenuItem("Tools/TinyTorque Assets/Scatter Brush", priority = 40)]
        public static void Open()
        {
            var w = GetWindow<ScatterBrushWindow>(false, "Scatter Brush", true);
            w.minSize = new Vector2(280f, 300f);
            w.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
            _rng = new System.Random(_seed);
        }

        private void OnDisable() => SceneView.duringSceneGui -= OnSceneGui;

        // -------------------------------------------------------------------
        // window
        // -------------------------------------------------------------------

        private void OnGUI()
        {
            EditorGUILayout.Space();
            _preset = (ScatterPreset)EditorGUILayout.ObjectField(
                "Preset", _preset, typeof(ScatterPreset), false);

            if (_preset == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick a preset. The pack ships several under " +
                    PackPaths.BrushesRoot + ".", MessageType.Info);
                if (GUILayout.Button("Select first pack preset")) SelectFirstPreset();
                return;
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.LabelField("Palette", _preset.entries.Count + " prefabs");

            _preset.radius = EditorGUILayout.Slider("Radius (m)", _preset.radius, 0.05f, 8f);
            _preset.density = EditorGUILayout.IntSlider("Density", _preset.density, 1, 24);
            _preset.randomYaw = EditorGUILayout.Toggle("Random yaw", _preset.randomYaw);
            EditorGUILayout.BeginHorizontal();
            _preset.scaleMin = EditorGUILayout.FloatField("Scale min", _preset.scaleMin);
            _preset.scaleMax = EditorGUILayout.FloatField("max", _preset.scaleMax);
            EditorGUILayout.EndHorizontal();
            _preset.alignToSurface = EditorGUILayout.Toggle("Align to surface", _preset.alignToSurface);
            _preset.avoidRadius = EditorGUILayout.Slider("Avoid radius", _preset.avoidRadius, 0f, 2f);
            _preset.surfaceMask = LayerMaskField("Surface layers", _preset.surfaceMask);

            EditorGUILayout.Space();
            _seed = EditorGUILayout.IntField("Seed", _seed);
            if (GUILayout.Button("Reseed")) _rng = new System.Random(_seed);

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
                "Drag in the Scene view to paint. Hold Shift to erase.\n" +
                "Stamps land on colliders below the cursor — the Sandbox scene's " +
                "ground plane has one.", MessageType.None);
            EditorGUILayout.LabelField("Placed this session", _placed.ToString());

            if (GUI.changed) EditorUtility.SetDirty(_preset);
        }

        private void SelectFirstPreset()
        {
            var guids = AssetDatabase.FindAssets("t:ScatterPreset", new[] { PackPaths.BrushesRoot });
            if (guids.Length == 0)
            {
                PackPaths.Warn("no scatter presets found — run 'Rebuild everything' first.");
                return;
            }
            _preset = AssetDatabase.LoadAssetAtPath<ScatterPreset>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
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
            if (!_active || _preset == null) return;

            int control = GUIUtility.GetControlID(FocusType.Passive);
            var ev = Event.current;

            if (ev.type == EventType.Layout)
            {
                // Take the default control so a drag paints instead of
                // rubber-band-selecting.
                HandleUtility.AddDefaultControl(control);
                return;
            }

            var ray = HandleUtility.GUIPointToWorldRay(ev.mousePosition);
            if (!Physics.Raycast(ray, out var aim, 1000f, _preset.surfaceMask)) return;

            // Brush disc.
            Handles.color = ev.shift ? new Color(1f, 0.4f, 0.3f, 0.9f)
                                     : new Color(0.4f, 0.9f, 1f, 0.9f);
            Handles.DrawWireDisc(aim.point, aim.normal, _preset.radius);
            Handles.DrawWireDisc(aim.point, aim.normal, _preset.radius * 0.02f);
            view.Repaint();

            bool paint = (ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag)
                         && ev.button == 0 && !ev.alt;
            if (!paint) return;

            if (ev.shift) Erase(aim.point);
            else Stamp(aim.point);

            ev.Use();
        }

        private void Stamp(Vector3 centre)
        {
            var parent = EnsureParent();
            var placed = new List<Vector3>();
            foreach (Transform t in parent) placed.Add(t.position);

            for (int i = 0; i < _preset.density; i++)
            {
                var prefab = _preset.Pick(_rng);
                if (prefab == null) return;         // empty palette: nothing to do

                // Uniform over the disc — sqrt, or every pass clumps at the centre.
                double a = _rng.NextDouble() * System.Math.PI * 2.0;
                double r = System.Math.Sqrt(_rng.NextDouble()) * _preset.radius;
                var probe = centre + new Vector3((float)(System.Math.Cos(a) * r), 0f,
                                                 (float)(System.Math.Sin(a) * r));

                if (!Physics.Raycast(probe + Vector3.up * ProbeUp, Vector3.down,
                                     out var hit, ProbeLen, _preset.surfaceMask))
                    continue;                        // no ground under it — skip

                if (_preset.avoidRadius > 0f)
                {
                    bool crowded = false;
                    foreach (var p in placed)
                        if ((p - hit.point).sqrMagnitude <
                            _preset.avoidRadius * _preset.avoidRadius) { crowded = true; break; }
                    if (crowded) continue;
                }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                if (go == null) continue;
                go.transform.position = hit.point;

                var up = _preset.alignToSurface ? hit.normal : Vector3.up;
                float yaw = _preset.randomYaw ? (float)(_rng.NextDouble() * 360.0) : 0f;
                go.transform.rotation = Quaternion.AngleAxis(yaw, up) *
                                        Quaternion.FromToRotation(Vector3.up, up);

                float s = Mathf.Lerp(_preset.scaleMin, _preset.scaleMax,
                                     (float)_rng.NextDouble());
                go.transform.localScale = Vector3.one * s;

                Undo.RegisterCreatedObjectUndo(go, "Scatter");
                placed.Add(hit.point);
                _placed++;
            }
            Repaint();
        }

        private void Erase(Vector3 centre)
        {
            var parent = FindParent();
            if (parent == null) return;
            float r2 = _preset.radius * _preset.radius;

            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                if ((child.position - centre).sqrMagnitude <= r2)
                    Undo.DestroyObjectImmediate(child.gameObject);
            }
        }

        private Transform FindParent()
        {
            string group = string.IsNullOrEmpty(_preset.parentName) ? "Scatter" : _preset.parentName;
            var rootGo = GameObject.Find(group);
            if (rootGo == null) return null;
            return rootGo.transform.Find(_preset.name);
        }

        private Transform EnsureParent()
        {
            string group = string.IsNullOrEmpty(_preset.parentName) ? "Scatter" : _preset.parentName;
            var rootGo = GameObject.Find(group);
            if (rootGo == null)
            {
                rootGo = new GameObject(group);
                Undo.RegisterCreatedObjectUndo(rootGo, "Scatter root");
            }
            var sub = rootGo.transform.Find(_preset.name);
            if (sub == null)
            {
                var go = new GameObject(_preset.name);
                go.transform.SetParent(rootGo.transform, false);
                Undo.RegisterCreatedObjectUndo(go, "Scatter group");
                sub = go.transform;
            }
            return sub;
        }
    }
}
