using AIHWSim.Track;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Inspector for track markers, with the one operation that matters: snap to the
    /// road.
    ///
    /// A gate spans its own local X and cars travel through its local +Z, so a
    /// marker rotated by eye is the most common way to author a track that looks
    /// right and never completes a lap. Snapping takes the heading from the corridor
    /// tangent instead of from the author's wrist.
    /// </summary>
    [CustomEditor(typeof(TrackMarker), editorForChildClasses: true)]
    [CanEditMultipleObjects]
    public sealed class TrackMarkerEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var d = FindFirstObjectByType<SceneTrackDescriptor>();
            EditorGUILayout.Space();

            if (d == null)
            {
                EditorGUILayout.HelpBox("No SceneTrackDescriptor in this scene.",
                    MessageType.Warning);
                return;
            }
            if (!d.HasCorridor)
            {
                EditorGUILayout.HelpBox(
                    "No baked corridor to snap to. Bake the ribbon first " +
                    "(Track Studio > Bake ribbon + corridor).", MessageType.Info);
                return;
            }

            if (GUILayout.Button("Snap to road (position + heading)", GUILayout.Height(24f)))
                foreach (var t in targets)
                    Snap((TrackMarker)t, d);

            if (GUILayout.Button("Snap heading only"))
                foreach (var t in targets)
                    Snap((TrackMarker)t, d, headingOnly: true);
        }

        /// <summary>
        /// Move the marker onto the nearest corridor node and face it along the
        /// local tangent. Position lands on the centreline rather than the marker's
        /// projection onto it: a gate centred on the road covers the road, which is
        /// exactly the property the validator checks.
        /// </summary>
        private static void Snap(TrackMarker m, SceneTrackDescriptor d, bool headingOnly = false)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            var pos = m.transform.position;
            for (int i = 0; i < d.centerline.Length; i++)
            {
                float sq = (d.centerline[i] - pos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = i; }
            }
            if (best < 0) return;

            int n = d.centerline.Length;
            int next = d.corridorClosed ? (best + 1) % n : Mathf.Min(n - 1, best + 1);
            int prev = d.corridorClosed ? (best + n - 1) % n : Mathf.Max(0, best - 1);
            var tan = d.centerline[next] - d.centerline[prev];
            tan.y = 0f;

            Undo.RecordObject(m.transform, "Snap marker to road");
            if (!headingOnly) m.transform.position = d.centerline[best];
            if (tan.sqrMagnitude > 1e-6f)
                m.transform.rotation = Quaternion.LookRotation(tan.normalized, Vector3.up);

            // A gate must span the road it sits on, or cars pass beside it.
            if (m is TrackFinishMarker f)
            {
                Undo.RecordObject(f, "Snap marker to road");
                f.gateWidth = Mathf.Max(f.gateWidth, d.halfWidths[best] * 2f);
            }
            else if (m is TrackCheckpointMarker c)
            {
                Undo.RecordObject(c, "Snap marker to road");
                c.gateWidth = Mathf.Max(c.gateWidth, d.halfWidths[best] * 2f);
            }

            EditorUtility.SetDirty(m);
            SceneTrackSetup.MarkSceneDirty();
        }
    }
}
