using System;
using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// One child mesh's submesh slot, joined to everything the manifest says
    /// about it — what material it took, and what the piece IS.
    ///
    /// <b>The join is the point.</b> A renderer on its own is anonymous: forty-one
    /// meshes called Police_Door_L through Police_Wheel_Arch_R, and nothing in the
    /// scene that can say which of them is a door that comes off at 40 hp and which
    /// is a wheel arch that never does. The manifest knows, the hierarchy knows
    /// where, and this row is where the two meet.
    ///
    /// <see cref="Role"/>, <see cref="HealthHp"/> and <see cref="Group"/> are read
    /// by nothing yet, exactly as they are authored by Asset Studio and consumed by
    /// nothing yet. They are here because they are cheap to carry through a walk
    /// that is already happening and expensive to re-derive from an anonymous
    /// hierarchy later.
    /// </summary>
    public readonly struct PartIdentity
    {
        public readonly Renderer Renderer;

        /// <summary>The GameObject name as authored — the join key, and NOT
        /// <c>sharedMesh.name</c>, which on a POLICE export reads
        /// "Police_Body_baked_baked_baked" while the object reads "Police_Body".</summary>
        public readonly string ObjectName;

        public readonly int Slot;

        /// <summary>The manifest material name this slot asked for, or null when
        /// the manifest had nothing to say about it.</summary>
        public readonly string MaterialName;

        /// <summary>What the binder put in the slot. Null means it declined to
        /// write and the imported material is still there.</summary>
        public readonly Material Material;

        public readonly string Role;
        public readonly float HealthHp;
        public readonly string Group;

        /// <summary>This slot is the car's tintable paint channel. It is a slot
        /// property and not a renderer one: <c>Police_Body</c> is dark trim in
        /// slot 0 and paint in slot 1, and a repaint that wrote slot 0 would erase
        /// the trim and leave the paint alone.</summary>
        public readonly bool PaintChannel;

        public readonly BindSource Source;

        public PartIdentity(Renderer renderer, string objectName, int slot,
                            string materialName, Material material,
                            string role, float healthHp, string group,
                            bool paintChannel, BindSource source)
        {
            Renderer = renderer; ObjectName = objectName; Slot = slot;
            MaterialName = materialName; Material = material;
            Role = role; HealthHp = healthHp; Group = group;
            PaintChannel = paintChannel; Source = source;
        }
    }

    /// <summary>
    /// The manifest an instantiated asset came with, and the joined table of what
    /// its binder did — one component on the instance root.
    ///
    /// Stamped by <see cref="PartMeshLibrary.TryInstantiate"/>, and <b>only when
    /// the asset actually ships a manifest</b>. That is what makes the whole
    /// manifest path invisible to the 207 shipped assets: they carry no component,
    /// so the two token binders find nothing to consult and run exactly the code
    /// they always did. It is also the seam itself — the instantiating call knows
    /// the key and nothing else does, while the binding call knows the materials
    /// and nothing else does, so the key is written down where it is known and read
    /// where it is needed rather than threaded through eight call sites that could
    /// each forget.
    ///
    /// Every field is deliberately unserialized. These instances are built at
    /// runtime under a car or a prop that is itself built at runtime, so there is
    /// no prefab or scene for the table to survive into, and a
    /// <c>[Serializable] AssetManifest</c> left serializable here would be deep
    /// copied into the component on every <c>Instantiate</c> for nobody.
    /// </summary>
    public sealed class PartManifestBinding : MonoBehaviour
    {
        /// <summary>The asset key, e.g. "body_police" — for diagnostics, which are
        /// worthless without it.</summary>
        [NonSerialized] public string Key;

        /// <summary>The Resources root it was loaded from
        /// (<see cref="PartMeshLibrary.PartRoot"/> and friends).</summary>
        [NonSerialized] public string Root;

        [NonSerialized] public AssetManifest Manifest;

        [NonSerialized] private readonly List<PartIdentity> _parts = new List<PartIdentity>();

        public IReadOnlyList<PartIdentity> Parts => _parts;

        /// <summary>Slots the manifest actually decided — as opposed to slots it
        /// had nothing to say about, which keep the imported material.</summary>
        public int BoundSlots
        {
            get
            {
                int n = 0;
                foreach (PartIdentity p in _parts)
                    if (p.Source != BindSource.Unbound) n++;
                return n;
            }
        }

        public int UnboundSlots => _parts.Count - BoundSlots;

        public bool HasPaintSlots
        {
            get
            {
                foreach (PartIdentity p in _parts)
                    if (p.PaintChannel) return true;
                return false;
            }
        }

        /// <summary>Assembly-internal: only the binder may write rows, so a table
        /// can never claim an identity that was never bound.</summary>
        internal void Add(PartIdentity p) => _parts.Add(p);

        internal void Clear() => _parts.Clear();

        /// <summary>
        /// Put <paramref name="mat"/> in every paint slot.
        ///
        /// <b>Per slot, which is the whole reason this exists.</b>
        /// <c>CarVehicle.SetBodyMaterial</c> has always written
        /// <c>renderer.sharedMaterial</c>, and that is slot 0 and nothing else —
        /// correct for every legacy shell, where the token binder could only ever
        /// bind slot 0 anyway, and destructive on a manifest asset whose paint
        /// channel is slot 1 of a two-material object. The array is re-assigned per
        /// row rather than per renderer because these tables are a few dozen rows
        /// and a repaint happens when a scene is built, not per frame.
        /// </summary>
        public void ApplyPaint(Material mat)
        {
            if (mat == null) return;
            foreach (PartIdentity p in _parts)
            {
                if (!p.PaintChannel || p.Renderer == null) continue;
                Material[] mats = p.Renderer.sharedMaterials;
                if (p.Slot < 0 || p.Slot >= mats.Length) continue;
                mats[p.Slot] = mat;
                p.Renderer.sharedMaterials = mats;
            }
        }
    }
}
