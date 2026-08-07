using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// What the deformed body would actually drag like, measured off the shape on
    /// screen rather than read out of the catalogue.
    ///
    /// <b>Why this belongs in the editor at all.</b> The whole argument for
    /// geometry-derived drag is that a shape's coefficient follows from the shape
    /// — so an editor that changes the shape and says nothing about the drag is
    /// hiding the one consequence the player is actually authoring. Chopping a
    /// tail should be visible as a number falling.
    ///
    /// <b>Read-only, and not wired to any car.</b> Nothing here feeds physics.
    /// <c>CarVehicle.EffectiveAero</c> is untouched, and a driving car still gets
    /// its drag from <c>DragEstimator.TryEstimate</c> against the catalogue row —
    /// whose silhouette cache is keyed by body key and knows nothing about
    /// deformation. Closing that gap is the port, not this.
    ///
    /// <b>Measured from the collider's bake, not from a second pass over the
    /// mesh.</b> <c>BakeMesh</c> already resolved morphs and pulled vertices into
    /// one set of metre-space positions; measuring anything else would risk the
    /// readout and the collision describing different cars.
    /// </summary>
    public sealed class BodyDragReadout
    {
        private readonly List<Vector3> _soup = new List<Vector3>(24576);

        /// <summary>The deformed body's own measurement. Valid once
        /// <see cref="HasMeasured"/> is true.</summary>
        public DragEstimator.Result Latest;
        public bool HasMeasured;

        /// <summary>What the undeformed catalogue row measures, for the
        /// side-by-side. Computed once per body.</summary>
        public DragEstimator.Result Baseline;
        public bool HasBaseline;

        /// <summary>How many triangles the last measurement saw — the diagnostic
        /// that tells a body whose geometry failed to reach here from one that is
        /// genuinely that slippery.</summary>
        public int Triangles;

        private string _bodyId = "";

        /// <summary>
        /// Take the undeformed reference for a body, at its nominal size and with
        /// the editor's own wheel discs so the comparison is like-for-like.
        /// </summary>
        public void SetBody(BodyDef def, IReadOnlyList<WheelDisc> wheels)
        {
            _bodyId = def != null ? def.id : "?";
            HasBaseline = false;
            HasMeasured = false;
            if (def == null) return;

            Vector3 size = def.nominalSize.sqrMagnitude > 1e-9f
                ? def.nominalSize : CarVehicle.BodyMeshAuthorSize;
            HasBaseline = DragEstimator.TryEstimate(def, size, wheels, out Baseline);
        }

        /// <summary>
        /// Re-measure from the body's freshly baked geometry. Called on commit —
        /// once per edit, never per frame; the rasteriser walks every triangle
        /// across sixteen stations and is not something to run at sixty hertz.
        /// </summary>
        public void Remeasure(DeformableBody body)
        {
            HasMeasured = false;
            if (body == null || body.Collision == null) return;

            Mesh baked = body.Collision.BakedMesh;
            Vector3[] verts = body.Collision.BakedVertices;
            if (baked == null || verts == null || verts.Length == 0) return;

            int[] idx = baked.triangles;
            _soup.Clear();
            for (int i = 0; i < idx.Length; i++)
            {
                int v = idx[i];
                if (v < 0 || v >= verts.Length) return;   // a mesh mid-rewrite: skip this one
                _soup.Add(verts[v]);
            }
            Triangles = _soup.Count / 3;

            HasMeasured = DragEstimator.TryEstimateSoup(_soup, body.Wheels,
                                                        "deformed:" + _bodyId, out Latest);
            if (!HasMeasured) return;

            string baseline = HasBaseline
                ? $" (catalogue {Baseline.cd:0.000} / {Baseline.frontalArea:0.00000} m²)"
                : "";
            Debug.Log($"[BodyDragReadout] {_bodyId}: Cd {Latest.cd:0.000}, " +
                      $"frontal area {Latest.frontalArea:0.00000} m², " +
                      $"Cd·A {Latest.cd * Latest.frontalArea:0.00000} m²{baseline}");
        }

        /// <summary>Percent change in Cd·A against the undeformed row, or 0 when
        /// there is nothing to compare against.</summary>
        public float CdaChangePercent
        {
            get
            {
                if (!HasMeasured || !HasBaseline) return 0f;
                float b = Baseline.cd * Baseline.frontalArea;
                if (b <= 1e-9f) return 0f;
                return (Latest.cd * Latest.frontalArea / b - 1f) * 100f;
            }
        }
    }
}
