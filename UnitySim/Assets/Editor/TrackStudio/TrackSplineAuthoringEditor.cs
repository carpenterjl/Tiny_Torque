using AIHWSim.Track;
using AIHWSim.TrackEd;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.Splines;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Inspector and Scene-view tooling for <see cref="TrackSplineAuthoring"/>,
    /// whose whole reason to exist is the three <c>SplineData&lt;float&gt;</c>
    /// channels.
    ///
    /// <b>The channels are authored in the Scene view, not in a list.</b> A key is a
    /// place on the road — "this corner is wide", "this bit is dirt" — and picking a
    /// number out of a list while looking at a curve somewhere else is the wrong way
    /// round. Each key draws as a road cross-section at the point it applies, sized
    /// by the width channel and tilted by the banking channel, so a channel is
    /// something you look at rather than something you compute. The inspector list
    /// stays as the numeric fallback for a value you want exact.
    ///
    /// Keys are dragged along the curve by the package's own
    /// <c>DataPointHandles</c>, which also adds a key on click and removes one on
    /// right-click, so the interaction matches every other SplineData in Unity.
    /// </summary>
    [CustomEditor(typeof(TrackSplineAuthoring))]
    public sealed class TrackSplineAuthoringEditor : Editor
    {
        private enum Channel { None, Width, Banking, Surface }

        /// <summary>Which channel the Scene-view handles edit. Editing all three at
        /// once would stack three sets of handles on the same points, where the one
        /// you grab is whichever happens to be on top.</summary>
        private Channel _active = Channel.Width;

        private const float BarHalfLenFallback = 1.5f;

        /// <summary>Set by every edit, cleared by the rebuild. One flag rather than a
        /// queue: two edits before the next tick still want exactly one rebake.</summary>
        private bool _rebuildQueued;

        // -------------------------------------------------------------------
        // live rebuild
        // -------------------------------------------------------------------

        private void OnEnable()
        {
            // Moving a knot is an edit to the road as much as moving a width key is,
            // and it happens through Unity's own spline tool, which knows nothing
            // about this component. The package fires this once per frame when any
            // spline changes, which is also the debounce.
            EditorSplineUtility.AfterSplineWasModified += OnSplineModified;

            // Undo restores the channel data but knows nothing about the generated
            // meshes, so without this a Ctrl-Z leaves the road showing the edit it
            // just took back.
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        private void OnDisable()
        {
            EditorSplineUtility.AfterSplineWasModified -= OnSplineModified;
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        private void OnUndoRedo() => QueueRebuild(target as TrackSplineAuthoring);

        private void OnSplineModified(Spline modified)
        {
            var a = target as TrackSplineAuthoring;
            if (a == null || modified != Spline(a)) return;
            QueueRebuild(a);
        }

        /// <summary>
        /// Ask for the ribbon to catch up with the data.
        ///
        /// Deferred rather than immediate: <c>Bake</c> destroys and recreates
        /// GameObjects, which is not something to do underneath an <c>OnGUI</c> or a
        /// Scene-view repaint, and a drag would otherwise re-cook a MeshCollider on
        /// every mouse-move. <c>delayCall</c> runs it on the next editor tick, and the
        /// hot-control check pushes it past the end of the drag.
        /// </summary>
        private void QueueRebuild(TrackSplineAuthoring a)
        {
            if (a == null || !a.liveRebuild || !a.HasBaked) return;
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            EditorApplication.delayCall += () => Rebuild(a);
        }

        private void Rebuild(TrackSplineAuthoring a)
        {
            _rebuildQueued = false;
            if (a == null) return;                       // deleted since it was queued
            if (!a.liveRebuild || !a.HasBaked) return;   // or cleared, or switched off

            if (GUIUtility.hotControl != 0)              // still dragging — look again
            {
                QueueRebuild(a);
                return;
            }

            a.Bake();
            EditorUtility.SetDirty(a);
            SceneView.RepaintAll();
        }

        // -------------------------------------------------------------------
        // inspector
        // -------------------------------------------------------------------

        public override void OnInspectorGUI()
        {
            var a = (TrackSplineAuthoring)target;

            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script",
                "widthChannel", "rollChannel", "surfaceChannel");
            // Width, surface, spacing and the edge toggles all change the mesh, so
            // the plain fields feed the live rebuild too.
            if (serializedObject.ApplyModifiedProperties()) QueueRebuild(a);

            var spline = Spline(a);
            int knots = spline?.Count ?? 0;

            EditorGUILayout.Space();
            if (a.Container == null)
                EditorGUILayout.HelpBox("No SplineContainer on this object.", MessageType.Error);
            else if (knots < 2)
                EditorGUILayout.HelpBox(
                    $"Spline {a.splineIndex} has {knots} knot(s). A road needs at least 2 — " +
                    "draw the curve with Unity's Spline tool first.", MessageType.Warning);
            else
                EditorGUILayout.LabelField("Knots", knots.ToString());

            NormalizeUnits(a, spline);

            // ---- channels ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Channels", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Empty channel = the default above, for the whole road.\n\n" +
                "In the Scene view: click the road line to ADD a key, drag a key to move " +
                "it along the curve, right-click to delete. Drag the blue edge handle to " +
                "set width; drag the arc to set banking.", MessageType.None);

            _active = (Channel)EditorGUILayout.EnumPopup(
                new GUIContent("Edit in Scene view",
                    "Which channel the Scene-view handles act on."), _active);

            DrawChannel(a, a.widthChannel, "Width (m)", Channel.Width, a.defaultWidth);
            DrawChannel(a, a.rollChannel, "Banking (deg, +ve drops the RIGHT edge)",
                        Channel.Banking, 0f);
            DrawChannel(a, a.surfaceChannel, "Surface", Channel.Surface, a.defaultSurface);

            // ---- bake ----
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(knots < 2))
                if (GUILayout.Button("Bake this road", GUILayout.Height(24f)))
                {
                    a.Bake();
                    EditorUtility.SetDirty(a);
                    TrackStudio.Log($"BAKE '{a.name}' ribbon rebuilt.");
                }

            if (GUILayout.Button("Clear baked geometry"))
            {
                a.ClearBaked();
                EditorUtility.SetDirty(a);
            }

            EditorGUILayout.HelpBox(
                a.liveRebuild
                    ? "Live rebuild is on: the ribbon follows the curve and the " +
                      "channels as you edit them. Bake once here to create it, then " +
                      "you can forget the button. The corridor bots drive is NOT " +
                      "live — refresh it with Track Studio's \"2. Bake ribbon + " +
                      "corridor\" when the road is where you want it."
                    : "Baking replaces this spline's own ribbon only. Track Studio's " +
                      "\"2. Bake ribbon + corridor\" does every road in the scene and " +
                      "also refreshes the corridor bots drive.", MessageType.None);
        }

        /// <summary>
        /// Force the channels onto <c>PathIndexUnit.Normalized</c>.
        ///
        /// <c>SplineData</c> defaults to <c>Knot</c>, so a DataPoint index is a knot
        /// number, not a 0-1 position — which makes "halfway along the road" mean
        /// different things on two splines with different knot counts, and moves
        /// every key when a knot is inserted. Normalized is what
        /// <c>UnitySplineSampler</c> evaluates against and what the handles below
        /// assume. <c>ConvertPathUnit</c> re-expresses existing keys rather than
        /// reinterpreting them, so a channel authored before this fix keeps its
        /// positions.
        /// </summary>
        private static void NormalizeUnits(TrackSplineAuthoring a, Spline spline)
        {
            if (spline == null || spline.Count < 2) return;
            Convert(a, a.widthChannel, spline);
            Convert(a, a.rollChannel, spline);
            Convert(a, a.surfaceChannel, spline);
        }

        private static void Convert(TrackSplineAuthoring a, SplineData<float> d, Spline spline)
        {
            if (d == null || d.PathIndexUnit == PathIndexUnit.Normalized) return;
            Undo.RecordObject(a, "Normalize channel units");
            d.ConvertPathUnit(spline, PathIndexUnit.Normalized);
            EditorUtility.SetDirty(a);
        }

        private void DrawChannel(TrackSplineAuthoring a, SplineData<float> data,
                                 string label, Channel kind, float seed)
        {
            if (data == null) return;

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var style = kind == _active ? EditorStyles.boldLabel : EditorStyles.label;
                EditorGUILayout.LabelField(kind == _active ? "▸ " + label : label, style);
                if (GUILayout.Button("Add key", GUILayout.Width(70f)))
                {
                    Undo.RecordObject(a, "Add channel key");
                    float t = data.Count == 0
                        ? 0f
                        : Mathf.Min(1f, data[data.Count - 1].Index + 0.1f);
                    float v = data.Count == 0 ? seed : data[data.Count - 1].Value;
                    data.Add(t, v);
                    EditorUtility.SetDirty(a);
                    QueueRebuild(a);
                }
            }

            if (data.Count == 0)
            {
                using (new EditorGUI.IndentLevelScope())
                    EditorGUILayout.LabelField("(constant — using the default)");
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                int removeAt = -1;
                for (int i = 0; i < data.Count; i++)
                {
                    var dp = data[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();

                        float t = EditorGUILayout.Slider(dp.Index, 0f, 1f);

                        float v;
                        if (kind == Channel.Surface)
                        {
                            int cur = Mathf.Clamp(Mathf.RoundToInt(dp.Value),
                                                  0, FloorTypeDrawer.Names.Length - 1);
                            v = EditorGUILayout.Popup(cur, FloorTypeDrawer.Names,
                                                      GUILayout.Width(200f));
                        }
                        else
                        {
                            v = EditorGUILayout.FloatField(dp.Value, GUILayout.Width(60f));
                            if (kind == Channel.Width) v = Mathf.Max(0.1f, v);
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(a, "Edit channel key");
                            // SetDataPoint re-sorts, so dragging a key past its
                            // neighbour reorders the list instead of corrupting it.
                            data.SetDataPoint(i, new DataPoint<float>(t, v));
                            EditorUtility.SetDirty(a);
                            QueueRebuild(a);
                        }

                        if (GUILayout.Button("-", GUILayout.Width(22f))) removeAt = i;
                    }
                }

                if (removeAt >= 0)
                {
                    Undo.RecordObject(a, "Remove channel key");
                    data.RemoveAt(removeAt);
                    EditorUtility.SetDirty(a);
                    QueueRebuild(a);
                }
            }
        }

        // -------------------------------------------------------------------
        // scene view
        // -------------------------------------------------------------------

        private void OnSceneGUI()
        {
            var a = (TrackSplineAuthoring)target;
            var spline = Spline(a);
            if (spline == null || spline.Count < 2) return;

            // The spline is in the container's local space; so are the handles.
            using (new Handles.DrawingScope(a.Container.transform.localToWorldMatrix))
            {
                DrawKeyBars(a, spline);

                var data = ChannelData(a, _active);
                if (data == null) return;

                // Record before the handles run: they mutate the SplineData in place,
                // so recording afterwards would capture the already-changed state and
                // undo would be a no-op.
                if (Event.current.type == EventType.MouseDown || GUIUtility.hotControl != 0)
                    Undo.RecordObject(a, "Edit spline channel");

                EditorGUI.BeginChangeCheck();
                spline.DataPointHandles(data, false, a.splineIndex);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(a);
                    QueueRebuild(a);
                }

                DrawValueHandles(a, spline, data);
            }
        }

        /// <summary>
        /// A road cross-section at every key of every channel: the geometry an author
        /// is actually reasoning about. Width sets the bar's length, banking its tilt,
        /// surface its colour — so "this corner is 4.5 m of kerb, leaning 8 degrees"
        /// is a thing you can see rather than three numbers to cross-reference.
        /// </summary>
        private void DrawKeyBars(TrackSplineAuthoring a, Spline spline)
        {
            DrawBarsFor(a, spline, a.widthChannel, _active == Channel.Width);
            DrawBarsFor(a, spline, a.rollChannel, _active == Channel.Banking);
            DrawBarsFor(a, spline, a.surfaceChannel, _active == Channel.Surface);
        }

        private void DrawBarsFor(TrackSplineAuthoring a, Spline spline,
                                 SplineData<float> data, bool highlight)
        {
            if (data == null) return;

            // Draw over geometry. These bars sit ON the road surface, so with the
            // default depth test the half that falls behind a kerb, a rise or the
            // ribbon itself simply vanishes — which reads as "the bar is one-sided"
            // rather than "the bar is occluded", and makes a centred cross-section
            // look like it starts at the spline and goes one way.
            var prevZ = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

            for (int i = 0; i < data.Count; i++)
            {
                float t = Mathf.Clamp01(data[i].Index);
                Frame(a, spline, t, out var pos, out var right, out var up);

                float half = Mathf.Max(0.1f, EvalWidth(a, spline, t)) * 0.5f;
                float roll = EvalRoll(a, spline, t);
                var axis = math.normalize(spline.EvaluateTangent(t));
                right = Quaternion.AngleAxis(roll, axis) * right;

                var c = SurfaceColor(Mathf.RoundToInt(EvalSurface(a, spline, t)));
                Handles.color = highlight ? c : c * new Color(1f, 1f, 1f, 0.35f);
                Handles.DrawAAPolyLine(highlight ? 5f : 2.5f,
                    (Vector3)pos - (Vector3)right * half,
                    (Vector3)pos + (Vector3)right * half);
            }

            Handles.zTest = prevZ;
        }

        /// <summary>
        /// The handle that edits the active channel's value in place: an edge you drag
        /// outward for width, an arc you drag round for banking. Surface has no
        /// meaningful geometric handle, so it gets a label naming the floor instead of
        /// a control that would only be a disguised number field.
        /// </summary>
        private void DrawValueHandles(TrackSplineAuthoring a, Spline spline,
                                      SplineData<float> data)
        {
            for (int i = 0; i < data.Count; i++)
            {
                var dp = data[i];
                float t = Mathf.Clamp01(dp.Index);
                Frame(a, spline, t, out var pos, out var right, out var up);
                var axis = math.normalize(spline.EvaluateTangent(t));

                float roll = EvalRoll(a, spline, t);
                var rolled = (Vector3)(Quaternion.AngleAxis(roll, axis) * right);
                float half = Mathf.Max(0.1f, EvalWidth(a, spline, t)) * 0.5f;
                float size = HandleUtility.GetHandleSize(pos) * 0.09f;

                if (_active == Channel.Width)
                {
                    Handles.color = new Color(0.4f, 0.7f, 1f);
                    Vector3 edge = (Vector3)pos + rolled * half;

                    EditorGUI.BeginChangeCheck();
                    Vector3 moved = Handles.Slider(edge, rolled, size, Handles.DotHandleCap, 0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(a, "Drag road width");
                        float newHalf = Mathf.Max(0.05f,
                            Vector3.Dot(moved - (Vector3)pos, rolled));
                        data.SetDataPoint(i, new DataPoint<float>(t, newHalf * 2f));
                        EditorUtility.SetDirty(a);
                        QueueRebuild(a);
                    }
                }
                else if (_active == Channel.Banking)
                {
                    Handles.color = new Color(1f, 0.75f, 0.3f);

                    EditorGUI.BeginChangeCheck();
                    var rot = Handles.Disc(Quaternion.identity, pos, axis,
                                           half, false, 0f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(a, "Drag road banking");
                        // Disc returns the accumulated rotation; project it onto the
                        // tangent so a drag reads as degrees of lean.
                        rot.ToAngleAxis(out float ang, out Vector3 ax);
                        if (Vector3.Dot(ax, (Vector3)axis) < 0f) ang = -ang;
                        data.SetDataPoint(i, new DataPoint<float>(t, dp.Value + ang));
                        EditorUtility.SetDirty(a);
                        QueueRebuild(a);
                    }
                }
                else if (_active == Channel.Surface)
                {
                    int id = Mathf.Clamp(Mathf.RoundToInt(dp.Value),
                                         0, TrackCatalog.Floors.Length - 1);
                    Handles.color = SurfaceColor(id);
                    Handles.Label((Vector3)pos + Vector3.up * 0.25f,
                                  TrackCatalog.Floors[id].label);
                }
            }
        }

        // -------------------------------------------------------------------
        // helpers
        // -------------------------------------------------------------------

        private static Spline Spline(TrackSplineAuthoring a)
        {
            var c = a.Container;
            if (c == null || a.splineIndex < 0 || a.splineIndex >= c.Splines.Count) return null;
            return c.Splines[a.splineIndex];
        }

        private static SplineData<float> ChannelData(TrackSplineAuthoring a, Channel k) => k switch
        {
            Channel.Width => a.widthChannel,
            Channel.Banking => a.rollChannel,
            Channel.Surface => a.surfaceChannel,
            _ => null,
        };

        /// <summary>Position and a road-plane right vector at normalized t.</summary>
        private static void Frame(TrackSplineAuthoring a, Spline spline, float t,
                                  out float3 pos, out float3 right, out float3 up)
        {
            pos = spline.EvaluatePosition(t);
            var tan = spline.EvaluateTangent(t);
            up = math.up();
            if (math.lengthsq(tan) < 1e-8f) { right = math.right(); return; }
            tan = math.normalize(tan);
            var r = math.cross(tan, up);
            right = math.lengthsq(r) < 1e-8f ? math.right() : math.normalize(r);
        }

        private static float EvalWidth(TrackSplineAuthoring a, Spline spline, float t) =>
            a.widthChannel != null && a.widthChannel.Count > 0
                ? a.widthChannel.Evaluate(spline, t, PathIndexUnit.Normalized,
                                          new UnityEngine.Splines.Interpolators.LerpFloat())
                : a.defaultWidth;

        private static float EvalRoll(TrackSplineAuthoring a, Spline spline, float t) =>
            a.rollChannel != null && a.rollChannel.Count > 0
                ? a.rollChannel.Evaluate(spline, t, PathIndexUnit.Normalized,
                                         new UnityEngine.Splines.Interpolators.LerpFloat())
                : 0f;

        private static float EvalSurface(TrackSplineAuthoring a, Spline spline, float t) =>
            a.surfaceChannel != null && a.surfaceChannel.Count > 0
                ? a.surfaceChannel.Evaluate(spline, t, PathIndexUnit.Normalized,
                                            new UnityEngine.Splines.Interpolators.LerpFloat())
                : a.defaultSurface;

        /// <summary>Rough visual identity per floor, driven by grip so the off-track
        /// threshold (0.90) reads as a colour change rather than a number.</summary>
        private static Color SurfaceColor(int id)
        {
            if (id < 0 || id >= TrackCatalog.Floors.Length) return Color.grey;
            float g = TrackCatalog.Floors[id].frictionMult;
            if (g < 0.90f) return new Color(0.95f, 0.55f, 0.25f);   // off-track
            return Color.Lerp(new Color(0.55f, 0.85f, 1f),
                              new Color(0.45f, 1f, 0.55f),
                              Mathf.InverseLerp(0.90f, 1.10f, g));
        }
    }
}
