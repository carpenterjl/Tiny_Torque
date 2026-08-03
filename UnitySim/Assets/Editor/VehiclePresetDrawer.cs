using System.Collections.Generic;
using AIHWSim.Garage;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// Draws any <c>[VehiclePreset] string</c> as a dropdown of the real cars.
    ///
    /// The stored value is still the plain preset name — nothing downstream learns
    /// that this field is drawn differently, and an asset written before this drawer
    /// existed reads back unchanged. What goes away is the typo: every name in the
    /// list is one <see cref="VehiclePresets.Resolve"/> answers.
    ///
    /// <b>A name it does not recognise is shown, not replaced.</b> An unresolvable
    /// value gets its own entry at the bottom, marked, and stays selected until an
    /// author picks something else. Silently rewriting it to the first preset would
    /// destroy the only evidence of what was meant — and a field that quietly
    /// disagrees with the asset on disk is the bug this drawer exists to prevent,
    /// not a fix for it.
    /// </summary>
    [CustomPropertyDrawer(typeof(VehiclePresetAttribute))]
    public sealed class VehiclePresetDrawer : PropertyDrawer
    {
        private static GUIContent[] _presets;

        /// <summary>Cached because <c>VehiclePresets.All</c> is a static readonly
        /// table; it is rebuilt on domain reload, which is the only time a preset
        /// can be added.</summary>
        private static GUIContent[] Presets
        {
            get
            {
                if (_presets == null)
                {
                    _presets = new GUIContent[VehiclePresets.All.Length];
                    for (int i = 0; i < _presets.Length; i++)
                        _presets[i] = new GUIContent(VehiclePresets.All[i].name);
                }
                return _presets;
            }
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            var attr = (VehiclePresetAttribute)attribute;
            string cur = property.stringValue ?? "";
            // Resolve is prefix-tolerant, so a value saved from a picker with the
            // ★ still matches its row here.
            string bare = cur.StartsWith(VehiclePresets.Prefix)
                ? cur.Substring(VehiclePresets.Prefix.Length) : cur;

            var options = new List<GUIContent>(Presets.Length + 2);
            var values = new List<string>(Presets.Length + 2);
            if (attr.allowEmpty)
            {
                options.Add(new GUIContent(attr.emptyLabel));
                values.Add("");
            }
            foreach (var p in Presets) { options.Add(p); values.Add(p.text); }

            int index = values.IndexOf(bare);
            if (index < 0)
            {
                // Not a preset — including empty on a field that does not offer it.
                options.Add(new GUIContent(
                    string.IsNullOrEmpty(bare) ? "(empty — no car named)" : $"{bare}   ⚠ not a preset",
                    "This name resolves to nothing, so the bootstrap falls back to its "
                    + "own default. Pick a car above to fix it."));
                values.Add(bare);
                index = values.Count - 1;
            }

            EditorGUI.BeginProperty(position, label, property);
            EditorGUI.BeginChangeCheck();
            int next = EditorGUI.Popup(position, label, index, options.ToArray());
            if (EditorGUI.EndChangeCheck()) property.stringValue = values[next];
            EditorGUI.EndProperty();
        }
    }
}
