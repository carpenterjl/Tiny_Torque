using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Binds an instantiated asset's renderers from its manifest — <b>by object
    /// name and submesh slot index, exactly, or not at all</b>.
    ///
    /// This is the replacement for the case-insensitive first-match substring
    /// matcher the shipped assets still use. That matcher has produced two bugs
    /// this project can name (the Redline's flank flash came out the Coupe's gold;
    /// the Highwing's wing came out generic white trim), and both were invisible:
    /// the compiler sees a valid table either way and a swallowed token renders as
    /// a plausible WRONG material rather than as an error. Here there is nothing to
    /// swallow. "Police_Door_L" slot 1 is "M_Police_Paint" because the manifest says
    /// so, and a name that does not match is a named warning rather than a
    /// plausible guess.
    ///
    /// <b>Nothing falls back to the token tables.</b> A manifest asset with an
    /// unlisted object keeps whatever the import left in that slot and says so, and
    /// the piece renders as Unity's default white — visibly, deliberately wrong.
    /// The alternative, quietly binding it to the body material, is precisely the
    /// behaviour the substring matcher has and the reason a renamed export can ship
    /// looking almost right.
    ///
    /// <b>Diagnostics are per KEY, not per instance.</b> Every cross-check here
    /// compares a manifest against a prefab, and both are fixed for the life of the
    /// session, so the answer cannot differ between the first car on the grid and
    /// the eighth. Reporting it eight times would only teach people to stop reading
    /// the console.
    /// </summary>
    public static class AssetManifestBinder
    {
        private static readonly HashSet<string> _diagnosed = new HashSet<string>();

        /// <summary>Let every manifest report itself again — for the commit
        /// pipeline, which has just rewritten the file the complaints were about.
        /// Called from <see cref="AssetManifests.ResetCache"/>.</summary>
        internal static void ResetDiagnostics() => _diagnosed.Clear();

        /// <summary>
        /// Record on <paramref name="inst"/> which asset it is, when that asset
        /// ships a manifest; returns the component, or null for the ordinary case
        /// of an asset that has none.
        ///
        /// The null return is load-bearing: it is what makes the manifest path cost
        /// the 207 shipped assets one dictionary lookup and one <c>GetComponent</c>
        /// that finds nothing.
        /// </summary>
        public static PartManifestBinding TryStamp(GameObject inst, string key, string root)
        {
            if (inst == null) return null;
            AssetManifest m = AssetManifests.Load(key, root);
            if (m == null) return null;

            var b = inst.AddComponent<PartManifestBinding>();
            b.Key = key;
            b.Root = root;
            b.Manifest = m;
            return b;
        }

        /// <summary>
        /// Bind every renderer under <paramref name="b"/> from its manifest and
        /// fill in the identity table.
        ///
        /// <paramref name="paintMat"/> is the caller's tintable material — the
        /// car's own <c>_bodyMat</c>, the object the design's colour, the garage
        /// painter and <c>SetBodyMaterial</c> all act on. A material the manifest
        /// marks <c>paintChannel</c> does not get built here at all; it gets
        /// CONFIGURED onto that one, so a manifest can hand a car livery maps and
        /// a metal/gloss without taking its colour away. Callers with no paint
        /// concept (wheels, cosmetics, props) pass null, and a paint-channel
        /// material there is just an ordinary shared material.
        ///
        /// Returns the <see cref="MaterialBindings"/> projection of the same walk,
        /// so this is a drop-in for the two token binders whose signature it has to
        /// match. The component's table is the richer one and is what anything
        /// downstream should read.
        /// </summary>
        public static MaterialBindings Bind(PartManifestBinding b, Material paintMat,
                                            ICollection<MeshRenderer> paintRenderers)
        {
            var bindings = new MaterialBindings();
            if (b == null || b.Manifest == null) return bindings;

            AssetManifest man = b.Manifest;
            b.Clear();
            bool report = _diagnosed.Add(b.Root + b.Key);
            bool verbatim = man.IsVerbatim;

            // One paint channel per asset, and the FIRST one wins. A second is a
            // manifest that cannot be satisfied — there is exactly one _bodyMat and
            // it can only be one material — so it is the validator's to reject at
            // authoring time, not something to invent an answer for here.
            AssetMaterialDef paintDef = null;
            if (paintMat != null && !verbatim)
            {
                foreach (AssetMaterialDef d in man.materials)
                    if (d != null && d.paintChannel) { paintDef = d; break; }
                if (paintDef != null) ConfigurePaint(paintMat, paintDef);
            }

            var seen = new HashSet<string>();
            foreach (Renderer r in b.GetComponentsInChildren<Renderer>(true))
            {
                string name = r.gameObject.name;
                seen.Add(name);
                Material[] mats = r.sharedMaterials;
                AssetObjectDef od = man.ObjectDef(name);

                if (od == null)
                {
                    if (report)
                        Debug.LogWarning($"[AssetManifests] {b.Key}: mesh object \"{name}\" " +
                                         $"has no manifest entry — its {mats.Length} slot(s) keep " +
                                         "the material the import left. Re-sync the asset in " +
                                         "Asset Studio.");
                    for (int i = 0; i < mats.Length; i++)
                        Record(bindings, b, r, name, i, null, null, null, BindSource.Unbound);
                    continue;
                }

                if (report && od.SlotCount != mats.Length)
                    Debug.LogWarning($"[AssetManifests] {b.Key}: object \"{name}\" declares " +
                                     $"{od.SlotCount} slot(s), the mesh has {mats.Length}. " +
                                     "Re-sync the asset — slot counts come from the import, " +
                                     "not from the export.");

                bool wrote = false;
                bool listed = false;   // this renderer is already in paintRenderers
                for (int i = 0; i < mats.Length; i++)
                {
                    string matName = od.SlotAt(i);

                    // Verbatim: the FBX's own .mat assets ARE the answer, so the
                    // walk records identity and touches nothing. Inert until R4
                    // stops the importer stripping those materials; until then a
                    // verbatim manifest is a well-formed statement about an asset
                    // that still arrives with its materials removed.
                    if (verbatim)
                    {
                        Record(bindings, b, r, name, i, matName, od, mats[i], BindSource.Verbatim);
                        continue;
                    }

                    // An empty slot entry means "leave this one as imported" — a
                    // statement, not a gap, and the only way an author can say it.
                    if (string.IsNullOrEmpty(matName))
                    {
                        Record(bindings, b, r, name, i, null, od, null, BindSource.Unbound);
                        continue;
                    }

                    AssetMaterialDef md = man.MaterialDef(matName);
                    if (md == null)
                    {
                        if (report)
                            Debug.LogWarning($"[AssetManifests] {b.Key}: object \"{name}\" slot " +
                                             $"{i} names material \"{matName}\", which the " +
                                             "manifest does not define.");
                        Record(bindings, b, r, name, i, matName, od, null, BindSource.Unbound);
                        continue;
                    }

                    Material chosen;
                    BindSource src;
                    if (md.paintChannel && paintMat != null)
                    {
                        chosen = paintMat;
                        src = BindSource.PaintChannel;
                        if (!listed && r is MeshRenderer mr) { paintRenderers?.Add(mr); listed = true; }
                    }
                    else
                    {
                        // Shared across every instance of this asset: eight police
                        // cars want one chrome, and only the paint channel differs
                        // between them.
                        chosen = man.Shared(matName);
                        src = BindSource.Manifest;
                    }

                    if (chosen != null) { mats[i] = chosen; wrote = true; }
                    Record(bindings, b, r, name, i, matName, od, chosen, src);
                }
                if (wrote) r.sharedMaterials = mats;
            }

            // The other direction, and the one a rename shows up in: the manifest
            // names a piece the mesh does not have. Silent otherwise — the walk
            // above only ever visits renderers that exist.
            if (report)
                foreach (AssetObjectDef od in man.objects)
                    if (od != null && !string.IsNullOrEmpty(od.name) && !seen.Contains(od.name))
                        Debug.LogWarning($"[AssetManifests] {b.Key}: the manifest names object " +
                                         $"\"{od.name}\", which this mesh does not have. Its " +
                                         "materials and damage tags bind to nothing — the object " +
                                         "was renamed in Blender, or the manifest is stale.");

            return bindings;
        }

        /// <summary>
        /// Write the paint-channel design onto the caller's material, keeping what
        /// the caller had already decided.
        ///
        /// <b>The design outranks the asset.</b> The colour is whatever the car put
        /// there (its <c>bodyColor</c>), and an albedo texture already present is a
        /// decoded livery or a garage paint stroke — a player painted THIS car, and
        /// the manifest's own baked artwork must not take that back. The manifest
        /// still supplies everything the design has no opinion about: the
        /// metallic/smoothness map, the normal map, emission.
        /// </summary>
        private static void ConfigurePaint(Material paintMat, AssetMaterialDef d)
        {
            Color tint = paintMat.color;
            Texture2D livery = paintMat.mainTexture as Texture2D;
            AssetManifests.Configure(paintMat, d, tint, null);
            if (livery != null) paintMat.mainTexture = livery;
        }

        private static void Record(MaterialBindings bindings, PartManifestBinding b,
                                   Renderer r, string name, int slot, string matName,
                                   AssetObjectDef od, Material mat, BindSource src)
        {
            bindings.Add(r, slot, mat, null, src);
            b.Add(new PartIdentity(r, name, slot, matName, mat,
                                   od != null ? od.role : "",
                                   od != null ? od.healthHp : 0f,
                                   od != null ? od.group : "",
                                   src == BindSource.PaintChannel, src));
        }
    }
}
