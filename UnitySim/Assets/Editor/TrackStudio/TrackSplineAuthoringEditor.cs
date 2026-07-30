using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Inspector for <see cref="TrackSplineAuthoring"/>, whose whole reason to exist
    /// is the three <c>SplineData&lt;float&gt;</c> channels.
    ///
    /// Unity's default drawer renders them as a raw <c>m_DataPoints</c> list of
    /// index/value pairs with no idea what the numbers mean — an unlabelled float is
    /// a poor way to author "this corner is 3.2 m wide and made of kerb". This draws
    /// each channel as a keyed list: position along the curve, then a value editor
    /// that matches the channel (metres, degrees, or a floor-type popup).
    ///
    /// Edits go through <c>SetDataPoint</c> rather than <c>SerializedProperty</c>:
    /// SplineData keeps its points sorted by index and that method is what maintains
    /// the invariant. Poking the backing list through serialization would let an
    /// author drag a key past its neighbour and leave the channel unsorted, which
    /// evaluates to nonsense rather than failing.
    /// </summary>
    [CustomEditor(typeof(TrackSplineAuthoring))]
    public sealed class TrackSplineAuthoringEditor : Editor
    {
        private static string[] _floorNames;

        private static string[] FloorNames
        {
            get
            {
                if (_floorNames == null)
                {
                    _floorNames = new string[TrackCatalog.Floors.Length];
                    for (int i = 0; i < _floorNames.Length; i++)
                    {
                        var f = TrackCatalog.Floors[i];
                        _floorNames[i] = $"{i}  {f.label}  (grip {f.frictionMult:0.00})";
                    }
                }
                return _floorNames;
            }
        }

        public override void OnInspectorGUI()
        {
            var a = (TrackSplineAuthoring)target;

            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script",
                "widthChannel", "rollChannel", "surfaceChannel");
            serializedObject.ApplyModifiedProperties();

            // ---- the curve itself ----
            EditorGUILayout.Space();
            var container = a.Container;
            int knots = container != null && container.Splines.Count > a.splineIndex &&
                        container.Splines[a.splineIndex] != null
                ? container.Splines[a.splineIndex].Count : 0;

            if (container == null)
                EditorGUILayout.HelpBox("No SplineContainer on this object.", MessageType.Error);
            else if (knots < 2)
                EditorGUILayout.HelpBox(
                    $"Spline {a.splineIndex} has {knots} knot(s). A road needs at least 2 — " +
                    "draw the curve with Unity's Spline tool first.", MessageType.Warning);
            else
                EditorGUILayout.LabelField("Knots", knots.ToString());

            // ---- channels ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Channels", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Empty channel = the default above, for the whole road. Add keys to vary " +
                "it along the curve. Position is 0 at the start and 1 at the end.",
                MessageType.None);

            DrawChannel(a, a.widthChannel, "Width (m)", ChannelKind.Width, a.defaultWidth);
            DrawChannel(a, a.rollChannel, "Banking (deg, +ve drops the RIGHT edge)",
                        ChannelKind.Roll, 0f);
            DrawChannel(a, a.surfaceChannel, "Surface", ChannelKind.Surface, a.defaultSurface);

            // ---- bake ----
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(knots < 2))
                if (GUILayout.Button("Bake this road", GUILayout.Height(24f)))
                {
                    Undo.SetCurrentGroupName("Bake spline road");
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
                "Baking replaces this spline's own ribbon only. Track Studio's " +
                "\"2. Bake ribbon + corridor\" does every road in the scene and also " +
                "refreshes the corridor bots drive.", MessageType.None);
        }

        private enum ChannelKind { Width, Roll, Surface }

        private void DrawChannel(TrackSplineAuthoring a, SplineData<float> data,
                                 string label, ChannelKind kind, float seed)
        {
            if (data == null) return;

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                if (GUILayout.Button("Add key", GUILayout.Width(70f)))
                {
                    Undo.RecordObject(a, "Add channel key");
                    // Append past the last key so a new one never lands on top of an
                    // existing one, where it would be invisible and unselectable.
                    float t = data.Count == 0
                        ? 0f
                        : Mathf.Min(1f, data[data.Count - 1].Index + 0.1f);
                    float v = data.Count == 0 ? seed : data[data.Count - 1].Value;
                    data.Add(t, v);
                    EditorUtility.SetDirty(a);
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
                        if (kind == ChannelKind.Surface)
                        {
                            int cur = Mathf.Clamp(Mathf.RoundToInt(dp.Value),
                                                  0, FloorNames.Length - 1);
                            v = EditorGUILayout.Popup(cur, FloorNames, GUILayout.Width(190f));
                        }
                        else
                        {
                            v = EditorGUILayout.FloatField(dp.Value, GUILayout.Width(60f));
                            if (kind == ChannelKind.Width) v = Mathf.Max(0.1f, v);
                        }

                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(a, "Edit channel key");
                            // SetDataPoint re-sorts, so dragging a key past its
                            // neighbour reorders the list instead of corrupting it.
                            data.SetDataPoint(i, new DataPoint<float>(t, v));
                            EditorUtility.SetDirty(a);
                        }

                        if (GUILayout.Button("-", GUILayout.Width(22f))) removeAt = i;
                    }
                }

                if (removeAt >= 0)
                {
                    Undo.RecordObject(a, "Remove channel key");
                    data.RemoveAt(removeAt);
                    EditorUtility.SetDirty(a);
                }
            }
        }
    }
}
