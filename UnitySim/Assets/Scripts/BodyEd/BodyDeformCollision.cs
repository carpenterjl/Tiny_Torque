using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Keeps a <see cref="MeshCollider"/> in step with a deformed body — once per
    /// edit, never per frame.
    ///
    /// <b>Why the timing is the whole design.</b> Assigning a MeshCollider's
    /// <c>sharedMesh</c> makes PhysX cook an acceleration structure for it, which
    /// costs milliseconds on a few-thousand-triangle shell. Doing that while the
    /// mouse is down — sixty times a second through a drag — turns sculpting into
    /// a slideshow, which is the same conclusion the track tools reached about
    /// re-cooking a road surface per brush stroke. So the mesh updates live and
    /// the collider updates on release, driven by
    /// <c>DeformableBody.DeformCommitted</c>. Between those two moments the
    /// collider is knowingly stale, and nothing reads it: the sculpt raycast
    /// happens at the START of a stroke, and the drag readout only ever measures a
    /// freshly baked mesh.
    ///
    /// <b>This lives on an UNSCALED sibling of the renderer, and applies the
    /// render scale by hand.</b> The obvious route is
    /// <c>BakeMesh(mesh, useScale: true)</c>, which is documented to return
    /// vertices with the renderer's lossy scale already applied. It does not do
    /// that for a renderer with no bones: <c>[BDEF]</c> doubled the renderer's
    /// scale and the baked geometry did not move. Left alone that would have given
    /// <c>body_patrol</c> — whose mesh arrives 12.573× oversized and is rendered
    /// down by <c>authorScale</c> — a collision shape twelve times the size of the
    /// car, with nothing anywhere to say so. So the bake is taken in author units,
    /// where the answer is unambiguous, and scaled here in one pass that runs once
    /// per edit.
    /// </summary>
    [RequireComponent(typeof(MeshCollider))]
    public sealed class BodyDeformCollision : MonoBehaviour
    {
        private MeshCollider _col;
        private Mesh _baked;
        private Vector3[] _bakedVerts;

        /// <summary>The last baked geometry, in metres, in this object's local
        /// space. Null until the first bake.</summary>
        public Mesh BakedMesh => _baked;

        /// <summary>The baked vertex positions, in metres, cached so a sculpt pick
        /// does not re-marshal the whole array out of the mesh. Index-aligned with
        /// the source mesh — <c>BakeMesh</c> preserves vertex order, which is what
        /// lets a hit in metres address a vertex in author units.</summary>
        public Vector3[] BakedVertices => _bakedVerts;

        public MeshCollider Collider => EnsureCollider();

        /// <summary><c>Time.realtimeSinceStartup</c> of the last bake, for the
        /// UI's "last bake" line. Negative until the first one.</summary>
        public float LastBakeTime { get; private set; } = -1f;

        private void Awake() => EnsureCollider();

        /// <summary>
        /// Find or make the collider, on demand rather than only in
        /// <c>Awake</c> — <c>[BDEF]</c> builds one of these rigs without entering
        /// play mode, and Unity does not call Awake on a component added at edit
        /// time unless the script asks to run there.
        /// </summary>
        private MeshCollider EnsureCollider()
        {
            if (_col != null) return _col;
            _col = GetComponent<MeshCollider>();
            if (_col == null) _col = gameObject.AddComponent<MeshCollider>();
            // Non-convex: nothing dynamic collides with this body, the collider is
            // here so the sculpt brush has a surface to hit. A convex hull would
            // fill in exactly the concavities somebody is most likely to sculpt.
            _col.convex = false;
            return _col;
        }

        /// <summary>
        /// Capture the body's current shape — morphs and pulled vertices together
        /// — and hand it to the physics engine.
        /// </summary>
        public void Rebake(DeformableBody body)
        {
            if (body == null || body.Smr == null || body.WorkMesh == null) return;
            MeshCollider col = EnsureCollider();
            if (col == null) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (_baked == null)
                _baked = new Mesh { name = "bodyed_baked", hideFlags = HideFlags.HideAndDontSave };
            else
                _baked.Clear();
            _baked.indexFormat = body.WorkMesh.indexFormat;

            // The one call that resolves morph weights and pulled vertices into a
            // single set of positions. Everything downstream — the collider here,
            // the drag readout — reads THIS, so the two can never disagree about
            // what the car currently is.
            //
            // useScale: false, and then scaled below. See the class note: the
            // true branch is a no-op on a bone-less renderer, so trusting it would
            // silently hand an author-scaled body the wrong collision size.
            body.Smr.BakeMesh(_baked, false);

            Vector3 s = body.RenderScale;
            Vector3[] v = _baked.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = Vector3.Scale(v[i], s);
            _baked.vertices = v;

            // BakeMesh writes vertices and leaves the bounds where Clear() put
            // them — at zero. Everything that asks a collider where it is starts
            // from those bounds, so without this the body is a correctly shaped
            // object that the broadphase believes is a point at the origin.
            _baked.RecalculateBounds();

            // Null first, then the mesh. Re-assigning the same Mesh reference is a
            // no-op to PhysX even when its contents have changed underneath, so
            // without the clear the collider would keep the shape it was cooked
            // with and the brush would hit a car that is no longer there.
            col.sharedMesh = null;
            col.sharedMesh = _baked;

            // Autosync is off in this project: without this the collider's own
            // transform is still at last frame's pose when the next raycast runs.
            Physics.SyncTransforms();

            _bakedVerts = v;
            LastBakeTime = Time.realtimeSinceStartup;

            sw.Stop();
            Debug.Log($"[BodyDeformCollision] Baked collider for '{body.name}': " +
                      $"{_baked.vertexCount} verts, {_baked.triangles.Length / 3} tris, " +
                      $"{sw.Elapsed.TotalMilliseconds:0.0} ms.");
        }

        private void OnDestroy()
        {
            if (_col != null) _col.sharedMesh = null;
            BodyMeshSource.DestroyMesh(_baked);
            _baked = null;
            _bakedVerts = null;
        }
    }
}
