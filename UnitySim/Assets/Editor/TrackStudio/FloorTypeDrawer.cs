using AIHWSim.Track;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.TrackTools
{
    /// <summary>
    /// Draws any <c>[FloorType] int</c> as a dropdown of the real surface names.
    ///
    /// One drawer rather than a popup hand-rolled into each inspector, so the
    /// authoring component, the terrain table, the scene descriptor and
    /// <see cref="SurfaceTag"/> all offer the same list and cannot drift apart as
    /// floors are appended.
    /// </summary>
    [CustomPropertyDrawer(typeof(FloorTypeAttribute))]
    public sealed class FloorTypeDrawer : PropertyDrawer
    {
        private static GUIContent[] _names;

        /// <summary>Cached because Floors is a static append-only table; rebuilt on
        /// domain reload, which is the only time it can change.</summary>
        internal static GUIContent[] Names
        {
            get
            {
                if (_names == null)
                {
                    _names = new GUIContent[TrackCatalog.Floors.Length];
                    for (int i = 0; i < _names.Length; i++)
                    {
                        var f = TrackCatalog.Floors[i];
                        // Grip is shown because it is also the off-track test:
                        // anything below ArcadeConfig.OffTrackFrictionThreshold
                        // (0.90) counts as leaving the track.
                        _names[i] = new GUIContent(
                            $"{f.label}   (grip {f.frictionMult:0.00}){(f.frictionMult < 0.90f ? "  ·off-track" : "")}",
                            $"id {i} — {f.id}");
                    }
                }
                return _names;
            }
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            EditorGUI.BeginProperty(position, label, property);

            int cur = Mathf.Clamp(property.intValue, 0, Names.Length - 1);
            EditorGUI.BeginChangeCheck();
            int next = EditorGUI.Popup(position, label, cur, Names);
            if (EditorGUI.EndChangeCheck()) property.intValue = next;

            EditorGUI.EndProperty();
        }
    }
}
