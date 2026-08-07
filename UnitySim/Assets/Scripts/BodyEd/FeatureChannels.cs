using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// What a "feature" is in this editor, and how one gets painted.
    ///
    /// <b>A feature is a renderer group, named by the asset.</b> A material-binding
    /// dump of every shipped shell settles what that means in practice: the arcade
    /// bodies are joined per material, so <c>body_redline</c> arrives as
    /// <c>paint_1..8</c>, <c>dark_1..9</c>, <c>em_tail_1..2</c>, <c>glass_1</c>; the
    /// two manifest assets are named per part instead, so <c>body_patrol</c> arrives
    /// as <c>Police_PushBar</c>, <c>Police_HeadLights</c>, <c>_spotlens</c>. Both
    /// are groups of renderers with a shared name stem, and one rule —
    /// <see cref="NameOf"/> — reduces either to a channel.
    ///
    /// <b>Read back off the built hierarchy, never off a table.</b> Neither
    /// <c>PartVisualFactory.AccentTokens</c> nor a manifest can answer "what
    /// channels does this body actually have": the first is a token list that a
    /// given shell uses some of, the second exists for two assets out of thirteen.
    /// Asking the objects is the only method that is right for both, and it is the
    /// discipline <c>PartModelBindingDump</c> documents at the same seam — observe
    /// the result, never the mapping.
    ///
    /// <b>Hiding is not here, and deliberately.</b> A channel on the deformable
    /// body is a SUBMESH, and the honest way to remove one is to empty its
    /// triangles — which takes it out of the collider bake and the drag measurement
    /// too, because a spoiler somebody deleted should not still be making drag.
    /// That belongs to <see cref="DeformableBody"/>, which owns the mesh. A channel
    /// on a placed prop is a set of renderers, and hiding those is
    /// <see cref="Binding.SetHidden"/> below.
    /// </summary>
    public static class FeatureChannels
    {
        /// <summary>
        /// The channel an object name belongs to: the name with Unity's clone
        /// suffix, Blender's duplicate suffix and a trailing piece number removed.
        ///
        /// <c>paint_1</c> → <c>paint</c> · <c>Police_Star.001</c> →
        /// <c>Police_Star</c> · <c>body_shell(Clone)</c> → <c>body_shell</c> ·
        /// <c>Police_Strobe_blue</c> → unchanged, because <c>blue</c> is not a
        /// number and the group really is per-colour.
        /// </summary>
        public static string NameOf(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return BodyMeshSource.WholeBodyChannel;
            string s = objectName;

            int clone = s.IndexOf("(Clone)", System.StringComparison.Ordinal);
            if (clone >= 0) s = s.Substring(0, clone);

            s = TrimNumericSuffix(s, '.');
            s = TrimNumericSuffix(s, '_');

            s = s.Trim();
            return s.Length == 0 ? BodyMeshSource.WholeBodyChannel : s;
        }

        private static string TrimNumericSuffix(string s, char sep)
        {
            int i = s.LastIndexOf(sep);
            if (i <= 0 || i == s.Length - 1) return s;
            for (int k = i + 1; k < s.Length; k++)
                if (!char.IsDigit(s[k])) return s;
            return s.Substring(0, i);
        }

        /// <summary>A pretty label for a channel — the raw name is already the
        /// artist's word for it, so this only tidies the separators.</summary>
        public static string Label(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return "body";
            string s = channel.TrimStart('_').Replace('_', ' ');
            return s.Length == 0 ? channel : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // ==================== binding ====================

        /// <summary>
        /// The paintable channels of one built thing, and the materials behind
        /// them.
        ///
        /// Owns every material it creates and destroys them in
        /// <see cref="Dispose"/>. A channel with no tint keeps the material the
        /// asset was authored with — untouched, not copied — so an unpainted car
        /// batches exactly as it did before this existed.
        /// </summary>
        public sealed class Binding
        {
            private struct Slot
            {
                public Renderer renderer;
                public int index;          // material slot on that renderer
                public int channel;        // index into Names
                public Material authored;  // what the asset shipped with
            }

            private readonly List<Slot> _slots = new List<Slot>();
            private readonly List<string> _names = new List<string>();
            private readonly List<Material> _made = new List<Material>();
            private readonly List<int> _tris = new List<int>();

            public IReadOnlyList<string> Names => _names;

            /// <summary>Triangle count per channel — what the palette sorts by, so
            /// the substantial pieces of a body read before its badges.</summary>
            public IReadOnlyList<int> Triangles => _tris;

            public int IndexOf(string channel) => _names.IndexOf(channel);

            /// <summary>The colour a channel was authored with, for the paint
            /// panel's "reset" and for the initial swatch.</summary>
            public Color AuthoredColor(int channel)
            {
                foreach (Slot s in _slots)
                    if (s.channel == channel && s.authored != null && s.authored.HasProperty("_Color"))
                        return s.authored.color;
                return Color.white;
            }

            internal void Add(Renderer r, int index, string channel, int triangles)
            {
                if (r == null) return;
                int c = _names.IndexOf(channel);
                if (c < 0)
                {
                    c = _names.Count;
                    _names.Add(channel);
                    _tris.Add(0);
                }
                _tris[c] += triangles;

                Material[] mats = r.sharedMaterials;
                _slots.Add(new Slot
                {
                    renderer = r, index = index, channel = c,
                    authored = index < mats.Length ? mats[index] : null,
                });
            }

            /// <summary>
            /// Paint the channels named by <paramref name="tints"/> and restore
            /// every other one to what the asset shipped with.
            ///
            /// Rebuilds from the authored materials each time rather than editing
            /// in place: a tint that only sets colour has to be able to give back
            /// the authored metallic, and there is no way to do that from a
            /// material that has already been overwritten once.
            /// </summary>
            public void Apply(FeatureTint[] tints)
            {
                DestroyMade();

                var byChannel = new Material[_names.Count];
                if (tints != null)
                {
                    foreach (FeatureTint t in tints)
                    {
                        if (t == null) continue;
                        int c = _names.IndexOf(t.channel);
                        if (c < 0) continue;
                        Material src = AuthoredOf(c);
                        Material m = StudioPaint.Build(src, t);
                        if (m == null) continue;
                        byChannel[c] = m;
                        _made.Add(m);
                    }
                }

                foreach (Slot s in _slots)
                {
                    if (s.renderer == null) continue;
                    Material want = byChannel[s.channel] ?? s.authored;
                    Material[] mats = s.renderer.sharedMaterials;
                    if (s.index >= mats.Length) continue;
                    if (mats[s.index] == want) continue;
                    mats[s.index] = want;
                    s.renderer.sharedMaterials = mats;
                }
            }

            /// <summary>
            /// Switch renderers off by channel — the removal half of the parts
            /// tool, for a prop.
            ///
            /// Disabled, never destroyed: the geometry is shared catalogue data, so
            /// a part somebody hid has to be able to come back without rebuilding
            /// the vehicle. On a submesh binding this does nothing at all, because
            /// one renderer carries every channel — see the class note.
            /// </summary>
            public void SetHidden(string[] hidden)
            {
                foreach (Slot s in _slots)
                {
                    if (s.renderer == null) continue;
                    bool hide = hidden != null && System.Array.IndexOf(hidden, _names[s.channel]) >= 0;
                    if (s.renderer.enabled == !hide) continue;
                    s.renderer.enabled = !hide;
                }
            }

            private Material AuthoredOf(int channel)
            {
                foreach (Slot s in _slots)
                    if (s.channel == channel && s.authored != null) return s.authored;
                return null;
            }

            private void DestroyMade()
            {
                foreach (Material m in _made)
                {
                    if (m == null) continue;
                    if (Application.isPlaying) Object.Destroy(m);
                    else Object.DestroyImmediate(m);
                }
                _made.Clear();
            }

            /// <summary>Give back every material this created. The authored ones
            /// belong to the asset and are never touched.</summary>
            public void Dispose()
            {
                DestroyMade();
                _slots.Clear();
                _names.Clear();
                _tris.Clear();
            }
        }

        /// <summary>
        /// A binding over one renderer whose submeshes ARE the channels — the
        /// deformable body, whose flattened mesh keeps one submesh per group
        /// (<see cref="BodyMeshSource.ChannelsOf"/>).
        /// </summary>
        public static Binding ForSubmeshes(Renderer r, IReadOnlyList<string> channels)
        {
            var b = new Binding();
            if (r == null || channels == null) return b;

            Mesh mesh = MeshOf(r);
            for (int i = 0; i < channels.Count; i++)
            {
                int tris = mesh != null && i < mesh.subMeshCount
                    ? (int)(mesh.GetIndexCount(i) / 3) : 0;
                b.Add(r, i, channels[i], tris);
            }
            return b;
        }

        /// <summary>
        /// A binding over a built hierarchy, grouping its renderers by name — a
        /// placed prop, a harvested shell feature, a cosmetic.
        /// </summary>
        public static Binding ForHierarchy(Transform root)
        {
            var b = new Binding();
            if (root == null) return b;

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                string channel = NameOf(r.gameObject.name);
                Mesh mesh = MeshOf(r);
                int slots = Mathf.Max(1, r.sharedMaterials.Length);
                for (int i = 0; i < slots; i++)
                {
                    int tris = mesh != null && i < mesh.subMeshCount
                        ? (int)(mesh.GetIndexCount(i) / 3) : 0;
                    b.Add(r, i, channel, tris);
                }
            }
            return b;
        }

        private static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }
    }
}
