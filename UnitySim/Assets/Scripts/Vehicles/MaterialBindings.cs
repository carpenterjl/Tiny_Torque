using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>Why a renderer ended up with the material it did.</summary>
    public enum BindSource
    {
        /// <summary>An object-name token matched a row of the table. The winning
        /// token is recorded, which is the only way to see WHICH row won on a
        /// first-match-substring matcher where several could have.</summary>
        Token,

        /// <summary>The object name starts with "paint" — the tintable channel,
        /// claimed before the token table is consulted at all. Only
        /// <see cref="PartVisualFactory.BindByToken"/> produces this;
        /// <see cref="PartMeshLibrary.AssignByName"/> has no paint concept.</summary>
        PaintChannel,

        /// <summary>Nothing matched, so the caller's fallback was used. On a body
        /// that fallback IS the paint material, which is why this is a separate
        /// value from <see cref="PaintChannel"/> even though both can land on the
        /// same material: a renamed export lands here, and the two look identical
        /// on screen while meaning completely different things.</summary>
        Fallback,
    }

    /// <summary>One renderer, one submesh slot, and what the binder put in it.</summary>
    public readonly struct MaterialBinding
    {
        public readonly Renderer Renderer;

        /// <summary>The GameObject name AS AUTHORED — not lower-cased, and not
        /// <c>sharedMesh.name</c>. Those differ: the POLICE export's mesh
        /// datablocks are "Police_Body_baked_baked_…" while Unity names the
        /// GameObject after the Model, "Police_Body", and it is the GameObject
        /// name that both the token matcher and export.json speak.</summary>
        public readonly string Name;

        /// <summary>Submesh slot. Always 0 today — both binders write
        /// <c>sharedMaterial</c>, which is slot 0 and nothing else, so a
        /// two-material object like Police_Body has its second slot left
        /// untouched. The field exists because R3 binds per slot.</summary>
        public readonly int Slot;

        /// <summary>What the binder DECIDED, which is not always what the
        /// renderer now holds: <c>AssignByName</c> declines to write when the
        /// chosen material is null (a null fallback and no token match), leaving
        /// the imported material in place. A null here means exactly that.</summary>
        public readonly Material Material;

        /// <summary>The token that won, or null when none did.</summary>
        public readonly string Token;

        public readonly BindSource Source;

        public MaterialBinding(Renderer renderer, string name, int slot,
                               Material material, string token, BindSource source)
        {
            Renderer = renderer; Name = name; Slot = slot;
            Material = material; Token = token; Source = source;
        }
    }

    /// <summary>
    /// The renderer→material mapping a token binder just computed.
    ///
    /// <b>Nothing consumes this yet, deliberately.</b> It is the per-mesh
    /// identity seam the manifest path needs: R3 binds by object name and
    /// explicit submesh slot and hangs damage identity (role, health, group) off
    /// the same rows, and there is no way to join a manifest's objects to the
    /// live hierarchy without the binder saying which renderer it touched.
    /// Landing it inert, with a dump that diffs empty, means the seam is proved
    /// before anything depends on it.
    ///
    /// The mapping is a by-product of a walk the binders already do, so it costs
    /// no extra traversal — only the list. That allocation is unconditional
    /// rather than opt-in because every caller is already mid-<c>Instantiate</c>
    /// of a prefab hierarchy, which allocates GameObjects, Transforms and
    /// Renderers; a list of a dozen structs beside that is not a cost worth an
    /// opt-in flag that call sites would then get wrong.
    /// </summary>
    public sealed class MaterialBindings
    {
        private readonly List<MaterialBinding> _items = new List<MaterialBinding>();

        public int Count => _items.Count;
        public MaterialBinding this[int i] => _items[i];
        public IReadOnlyList<MaterialBinding> Items => _items;

        /// <summary>Assembly-internal: only the binders may write rows, so a
        /// mapping can never claim a binding that did not happen.</summary>
        internal void Add(Renderer r, int slot, Material mat, string token, BindSource source) =>
            _items.Add(new MaterialBinding(r, r != null ? r.gameObject.name : "", slot,
                                           mat, token, source));

        /// <summary>The rows for one renderer (all its slots), or an empty
        /// sequence. Linear — these lists are a few dozen rows at most.</summary>
        public IEnumerable<MaterialBinding> For(Renderer r)
        {
            foreach (var b in _items)
                if (b.Renderer == r) yield return b;
        }
    }
}
