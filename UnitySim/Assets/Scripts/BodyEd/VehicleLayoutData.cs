using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// One placed part: what it is, where it sits, and how it is painted.
    ///
    /// <b><see cref="source"/> is the whole extensibility story</b> — a scheme-tagged
    /// string parsed by <see cref="PartSource"/> rather than an enum plus five
    /// optional id fields. A layout naming <c>shell:body_patrol/Police_PushBar</c>
    /// says exactly where that geometry came from, survives a build that has never
    /// heard of push bars, and can be read by a human looking at the JSON.
    ///
    /// Pose is in the BODY's local frame and in metres, which is the frame the
    /// gizmo drags in and the frame <c>VehicleFactory</c> mounts children in — so
    /// a prop placed in the studio lands in the same place on the track with no
    /// conversion anywhere.
    /// </summary>
    [System.Serializable]
    public class PropPlacement
    {
        /// <summary>Scheme-tagged source key; see <see cref="PartSource"/>.</summary>
        public string source = "";

        /// <summary>Display name. Free to be edited by the player and free to
        /// diverge from the source — it names this instance, not the part.</summary>
        public string label = "";

        public Vector3 localPos;
        public Vector3 euler;

        /// <summary>Uniform scale. <b>1, never 0</b>: an initialiser of 0 would
        /// make an absent JSON key mean "invisible", which is the one thing a
        /// missing key must never mean.
        ///
        /// Kept as the field a v2 layout writes and as the one-number summary a
        /// reader that has never heard of <see cref="scaleAxis"/> can still act on.
        /// Read <see cref="Scale"/>, not this.</summary>
        public float scale = 1f;

        /// <summary>
        /// Per-axis scale in the part's own frame. <b>All-zero means "not
        /// authored"</b> and <see cref="Scale"/> falls back to the uniform field —
        /// the same sentinel as <c>WheelSpec.linkage</c>, and for the same reason:
        /// it is what <c>JsonUtility</c> hands back for a key that is not in the
        /// file, so every v2 layout keeps meaning exactly what it meant. 0 cannot
        /// collide with a real value because a scale of 0 is not a part.
        /// </summary>
        public Vector3 scaleAxis;

        /// <summary>
        /// The scale to actually build with. The one accessor every pose site
        /// uses, so the sentinel is interpreted in one place rather than at five
        /// call sites that could drift apart.
        ///
        /// Writing it keeps <see cref="scale"/> in step as the mean of the three,
        /// so the legacy field stays a truthful summary rather than a stale one.
        /// </summary>
        public Vector3 Scale
        {
            get => scaleAxis.sqrMagnitude > 1e-8f
                ? scaleAxis
                : Vector3.one * (scale > 1e-4f ? scale : 1f);
            set
            {
                scaleAxis = value;
                scale = (value.x + value.y + value.z) / 3f;
            }
        }

        /// <summary>True when the three axes disagree — what the inspector asks
        /// before deciding whether one number or three is the honest readout.
        /// </summary>
        public bool IsUniformScale
        {
            get
            {
                Vector3 s = Scale;
                return Mathf.Abs(s.x - s.y) < 1e-4f && Mathf.Abs(s.y - s.z) < 1e-4f;
            }
        }

        /// <summary>Back to an unscaled part, both fields, so nothing is left
        /// behind for the sentinel to pick up later.</summary>
        public void ResetScale()
        {
            scale = 1f;
            scaleAxis = Vector3.zero;
        }

        /// <summary>Shared id linking a mirrored pair, −1 = unlinked. Same
        /// convention as every spec in <c>VehicleDesign</c>, so
        /// <c>SymmetryUtil</c>'s idea of a twin transfers unchanged.</summary>
        public int mirrorGroup = -1;

        /// <summary>Per-channel paint on this instance. Empty = the part's own
        /// authored materials, untouched.</summary>
        public FeatureTint[] tints;

        public PropPlacement Clone()
        {
            var c = (PropPlacement)MemberwiseClone();
            if (tints != null)
            {
                c.tints = new FeatureTint[tints.Length];
                for (int i = 0; i < tints.Length; i++) c.tints[i] = tints[i]?.Clone();
            }
            return c;
        }
    }

    /// <summary>
    /// A paint override for one renderer group.
    ///
    /// <b>The −1 sentinels are the point.</b> A tint that only changes colour must
    /// leave the authored metallic and smoothness alone, or every chrome part
    /// repainted red comes out plastic. 0 is a legal value for all three, so
    /// "unset" cannot be 0 — it is −1, and every apply site tests <c>&lt; 0</c>.
    /// </summary>
    [System.Serializable]
    public class FeatureTint
    {
        /// <summary>The renderer group this paints. For a token-named shell that
        /// is the token (<c>paint</c>, <c>chrome</c>, <c>em_tail</c>); for a
        /// manifest asset it is the object name (<c>Police_PushBar</c>). Read back
        /// off the built hierarchy rather than off either table — see
        /// <see cref="FeatureChannels"/>.</summary>
        public string channel = "";

        public Color color = Color.white;

        /// <summary>−1 = keep whatever the part was authored with.</summary>
        public float metallic = -1f;
        public float smoothness = -1f;

        /// <summary>Emission strength; −1 = keep, 0 = off, &gt;0 multiplies
        /// <see cref="color"/> into the emission channel.</summary>
        public float emission = -1f;

        /// <summary>Procedural texture key from <see cref="StudioTextures"/>;
        /// empty = flat colour.</summary>
        public string texture = "";

        /// <summary>Texture repeats across the part. 0 = the texture's own
        /// default, so an absent key is not a zero-scale UV.</summary>
        public float tiling = 0f;

        public FeatureTint Clone() => (FeatureTint)MemberwiseClone();
    }

    /// <summary>
    /// One point mass of the crash frame — the soft-body lattice wrapping the
    /// body. Position is in the VEHICLE frame and in metres, the frame
    /// <see cref="PropPlacement.localPos"/> uses, so a node means the same place
    /// in the studio and on the track.
    /// </summary>
    [System.Serializable]
    public class LatticeNode
    {
        public Vector3 localPos;

        /// <summary>Mass in kilograms. <b>0 = "derive the default"</b> — an equal
        /// share of the shell mass, resolved only by
        /// <c>LatticeBuilder.ResolveMass</c>. 0 kg is not a point mass, so 0 is
        /// free to be the sentinel. Generation writes the resolved value
        /// explicitly; the sentinel exists for hand-edited JSON.</summary>
        public float mass;
    }

    /// <summary>
    /// One spring-damper connecting two lattice nodes, by INDEX into the node
    /// array — <c>JsonUtility</c> cannot serialise an object graph, and indices
    /// diff legibly. Rest length is not stored: it is |pos[a] − pos[b]| at load,
    /// so dragging a node in the editor deliberately redefines the rest shape.
    /// </summary>
    [System.Serializable]
    public class LatticeBeam
    {
        /// <summary>Endpoint node indices. −1 (or any out-of-range value, or
        /// a == b) marks a beam <c>LatticeBuilder.Sanitize</c> drops at load
        /// rather than crashes on.</summary>
        public int a = -1, b = -1;

        /// <summary>Spring rate in N/m. 0 = derive the default (see
        /// <c>LatticeBuilder.ResolveSpring</c>).</summary>
        public float spring;

        /// <summary>Damping ratio ζ — the house convention (see
        /// <c>CarVehicle.MakeWheel</c>): the damper c = 2ζ√(k·μ) is derived, never
        /// stored. <b>−1 = derive the default</b>; 0 is legal (undamped), so 0
        /// cannot be the sentinel.</summary>
        public float dampingRatio = -1f;

        /// <summary>Strain |ΔL|/L₀ at which the beam snaps. Strain rather than
        /// force because it is scale-free — the same 0.35 means the same visual
        /// crumple on a stretched shell. −1 = derive the default.</summary>
        public float breakStrain = -1f;
    }

    /// <summary>
    /// Everything needed to rebuild a customised vehicle, and nothing that can be
    /// derived from it.
    ///
    /// <b>What is deliberately NOT in here: the mesh.</b> A layout names the base
    /// body, four morph weights and the handful of vertices somebody actually
    /// pulled. The morph targets themselves are regenerated by
    /// <see cref="BodyMorphs"/> from the base mesh at load, deterministically, so
    /// storing them would be storing a function of data already present — and a
    /// stale copy of it the first time a body FBX is re-exported. That is what
    /// keeps a heavily customised car down to a few kilobytes of readable JSON
    /// instead of a megabyte of float triples. The same reasoning covers props:
    /// a placed part stores a source key and a pose, never its geometry.
    ///
    /// <b>Sparse offsets.</b> A sculpt stroke touches a neighbourhood of a shell
    /// that has thousands of vertices; a dense array would serialise the
    /// overwhelming majority as zeroes. Two parallel arrays rather than a list of
    /// pairs because <c>JsonUtility</c> cannot serialise a tuple without a wrapper
    /// type anyway, and because two flat arrays diff more legibly.
    ///
    /// <b>Sentinels follow the house convention</b> (see <c>VehicleDesign</c>): an
    /// absent JSON key leaves the field at its initialiser, and every initialiser
    /// here IS the do-nothing value. A file with only <c>carBasePrefabID</c> in it
    /// loads as an undeformed body rather than as an error, and — the case the
    /// whole port rests on — a <see cref="VehicleLayoutData"/> nested inside a
    /// <c>VehicleDesign</c> that predates it deserialises to
    /// <see cref="IsEmpty"/>, which every apply site reads as "build the car the
    /// way it has always been built".
    /// </summary>
    [System.Serializable]
    public class VehicleLayoutData
    {
        /// <summary>Schema version. Bump only for a change an older reader would
        /// misinterpret rather than merely ignore.
        ///
        /// <b>2 adds props, tints and hidden channels — all of them additive</b>,
        /// so a v1 file loads into a v2 reader with empty arrays and means exactly
        /// what it meant, and a v2 file loaded by a v1 reader is a correctly
        /// deformed body with no decoration. Neither direction misreads anything,
        /// which is why the version is recorded rather than enforced.
        ///
        /// <b>3 adds per-axis prop scale</b>, which is additive in the same sense:
        /// the new key's absence is its own do-nothing sentinel, and a v3 file read
        /// by a v2 reader gives a part the mean of its three axes rather than an
        /// error.
        ///
        /// <b>4 adds the crash frame</b> — lattice nodes and beams. Absent keys =
        /// no lattice; a v4 file in a v3 reader is a car without one. Additive
        /// both ways again.</summary>
        public int version = 4;

        /// <summary>The catalogue body this was sculpted from —
        /// <c>BodyDef.id</c>, resolved through <c>BodyCatalog.ById</c>. Empty
        /// means "whatever is open", which is what makes a layout portable
        /// between shells at the caller's risk.</summary>
        public string carBasePrefabID = "";

        /// <summary>Axle-to-axle distance of the wheel markers, in metres. Not
        /// geometry — it is the context the body was shaped against, and it feeds
        /// the exposed-wheel term of the drag readout. 0 means "the editor's
        /// default", not "no wheelbase".</summary>
        public float wheelbaseLength;

        /// <summary>The design size the offsets were authored at, in metres. Kept
        /// so a future port can tell a layout sculpted on a stretched shell from
        /// one sculpted on a nominal shell.</summary>
        public Vector3 bodySize;

        /// <summary>Blendshape names, index-aligned with
        /// <see cref="blendShapeWeights"/>. Present so a load matches BY NAME:
        /// adding a fifth morph, or reordering the four, must not silently apply
        /// a roofline weight to a nose slider.</summary>
        public string[] blendShapeNames;

        /// <summary>Morph weights, 0..100, Unity's own scale.</summary>
        public float[] blendShapeWeights;

        /// <summary>Base-mesh vertex indices that were pulled. All members of a
        /// weld group appear, so a reload cannot half-move a seam.</summary>
        public int[] offsetIndex;

        /// <summary>Displacements in the mesh's AUTHOR units, index-aligned with
        /// <see cref="offsetIndex"/>. Author units rather than metres so a layout
        /// survives the body being resized.</summary>
        public Vector3[] offsetValue;

        /// <summary>Vertex count of the base mesh these indices refer to. A
        /// mismatch means the underlying FBX changed shape since the save, and the
        /// offsets are then meaningless rather than merely approximate — the
        /// loader drops them and says so.</summary>
        public int baseVertexCount;

        // ---- v2 ------------------------------------------------------------------

        /// <summary>Parts added to this vehicle, in the order they were placed.
        /// Null/empty on every v1 file.</summary>
        public PropPlacement[] props;

        /// <summary>Paint overrides on the BASE BODY's channels. A prop carries
        /// its own; these are kept separate rather than as a prop with an empty
        /// source, so "repaint the car" and "repaint that spoiler" stay two
        /// different edits in the file as well as in the UI.</summary>
        public FeatureTint[] tints;

        /// <summary>Channels of the base body that are switched off — the
        /// removal half of "geometry addition/removal". Names match
        /// <see cref="FeatureTint.channel"/>.
        ///
        /// Renderers are DISABLED, never destroyed: the mesh a channel refers to
        /// is shared catalogue geometry, and a body whose spoiler was deleted
        /// could not get it back without a full rebuild. It also means hiding
        /// nothing and hiding everything cost the same.</summary>
        public string[] hiddenChannels;

        // ---- v4 ------------------------------------------------------------------

        /// <summary>The crash frame's point masses. Null/empty on every earlier
        /// file. Derived data — rest lengths, vertex bindings, dampers — is
        /// recomputed at load by <c>LatticeBuilder</c>, never stored, for the
        /// same staleness reason morph targets are not stored.</summary>
        public LatticeNode[] latticeNodes;

        /// <summary>The springs between them, by node index.</summary>
        public LatticeBeam[] latticeBeams;

        /// <summary>Grid pitch the frame was generated at, in metres. 0 = never
        /// generated (a hand-built lattice). Context, not geometry — node
        /// positions are the truth.</summary>
        public float latticeSpacing;

        /// <summary>The FRAME tab's fidelity slider position — UI memory only,
        /// nothing downstream reads it. Starts at the middle per the tab's own
        /// default.</summary>
        public float latticeFidelity01 = 0.5f;

        /// <summary>The FRAME tab's damage slider — how hard the world dents
        /// this design. 0.5 (the initialiser, which is what an absent key
        /// deserialises to) is the neutral 1× response;
        /// <c>LatticeBuilder.DamageScale</c> is the one mapping site. Travels
        /// in the design JSON, so LAN peers dent a car by its owner's
        /// setting.</summary>
        public float latticeDamage01 = 0.5f;

        /// <summary>True when a crash frame exists. <b>THE runtime gate</b>: the
        /// driving path builds a soft-body solver exactly when this is true, so
        /// every design that predates the frame — including every physics-test
        /// and mission car — is unreachable-by-construction rather than
        /// excluded-by-flag.</summary>
        public bool HasLattice => latticeNodes != null && latticeNodes.Length > 0;

        /// <summary>
        /// True when this layout asks for nothing at all — no body, no
        /// deformation, no parts, no paint, no crash frame.
        ///
        /// <b>This is the compatibility contract for the whole port.</b> A
        /// <c>VehicleDesign</c> written before the studio existed has no
        /// <c>bodyLayout</c> key, <c>JsonUtility</c> hands the field a
        /// default-constructed instance rather than null, and every apply site
        /// asks this one question and takes the path it has always taken. The
        /// alternative — a bool somebody sets — is a bool somebody forgets.
        /// </summary>
        public bool IsEmpty =>
            string.IsNullOrEmpty(carBasePrefabID)
            && (blendShapeWeights == null || blendShapeWeights.Length == 0)
            && (offsetIndex == null || offsetIndex.Length == 0)
            && (props == null || props.Length == 0)
            && (tints == null || tints.Length == 0)
            && (hiddenChannels == null || hiddenChannels.Length == 0)
            && (latticeNodes == null || latticeNodes.Length == 0)
            && (latticeBeams == null || latticeBeams.Length == 0);

        /// <summary>True when any morph weight is non-zero or any vertex was
        /// pulled — i.e. the base mesh has to be rebuilt rather than
        /// instantiated.</summary>
        public bool HasDeformation
        {
            get
            {
                if (offsetIndex != null && offsetIndex.Length > 0) return true;
                if (blendShapeWeights == null) return false;
                foreach (float w in blendShapeWeights) if (Mathf.Abs(w) > 1e-4f) return true;
                return false;
            }
        }

        /// <summary>Deep copy, through the same JSON round trip
        /// <c>VehicleDesign.Clone</c> uses — which also means a clone can only
        /// carry what a save can carry.</summary>
        public VehicleLayoutData Clone() =>
            JsonUtility.FromJson<VehicleLayoutData>(JsonUtility.ToJson(this));
    }
}
