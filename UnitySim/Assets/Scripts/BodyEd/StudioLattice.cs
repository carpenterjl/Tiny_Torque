using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The crash frame under construction: the editable node/beam lists, their
    /// overlay, and nothing else — generation and every derivation live in
    /// <see cref="LatticeBuilder"/>, so this component is bookkeeping around a
    /// document the way <see cref="StudioVehicle"/> is around props.
    ///
    /// <b>The overlay is two meshes and one pooled sphere, not two thousand
    /// GameObjects.</b> Beams are a single <c>MeshTopology.Lines</c> mesh and
    /// nodes a single combined octahedron mesh, each one draw call however fine
    /// the fidelity; the selected node and beam get their own tiny objects on
    /// top. Nothing in the overlay has a collider — picking is
    /// <see cref="LatticeBuilder"/>'s ray math, so the sculpt brush, the prop
    /// picker and the gizmo raycasts never meet a thousand trigger spheres.
    ///
    /// <b>Stale, not clobbered.</b> Sculpting the body after the frame was
    /// generated sets <see cref="Stale"/> and the tab says so; regenerating is
    /// the user's call because it discards their manual edits. The one commit a
    /// layout load itself causes is consumed, not misread as sculpting.
    /// </summary>
    public sealed class StudioLattice : MonoBehaviour
    {
        private readonly List<LatticeNode> _nodes = new List<LatticeNode>();
        private readonly List<LatticeBeam> _beams = new List<LatticeBeam>();

        public IReadOnlyList<LatticeNode> Nodes => _nodes;
        public IReadOnlyList<LatticeBeam> Beams => _beams;
        public bool HasLattice => _nodes.Count > 0;

        /// <summary>Slider memory, persisted with the layout.</summary>
        public float Fidelity01 = 0.5f;

        /// <summary>The damage-amount slider, persisted with the layout — the
        /// value the driving path scales its hit response by, and the value
        /// the crash test below previews. One number, same feel both places.</summary>
        public float Damage01 = 0.5f;

        /// <summary>Grid pitch of the last generation, m. 0 = hand-built.</summary>
        public float SpacingM { get; private set; }

        /// <summary>The body was sculpted after this frame was made.</summary>
        public bool Stale { get; private set; }

        private StudioVehicle _vehicle;
        private Transform _beamGo, _nodeGo, _hotBeamGo, _hotNodeGo;
        private Mesh _beamMesh, _nodeMesh, _hotBeamMesh;
        private int _hotNode = -1, _hotBeam = -1;
        private bool _dirty, _visible;
        private int _consumeCommits;

        /// <summary>Node sphere radius, m — big enough to click, small enough
        /// not to read as geometry. Floored for hand-built frames (spacing 0).</summary>
        public float NodeRadiusM => Mathf.Max(0.003f, SpacingM * 0.07f);

        /// <summary>Beam pick radius — thinner than a node so the node wins
        /// where they overlap at an endpoint.</summary>
        public float BeamRadiusM => Mathf.Max(0.002f, SpacingM * 0.04f);

        // ==================== lifecycle ====================

        public void Init(StudioVehicle vehicle)
        {
            _vehicle = vehicle;
            vehicle.DeformCommitted += OnDeformCommitted;

            _beamGo = Child("beams");
            _nodeGo = Child("nodes");
            _hotBeamGo = Child("hotbeam");
            _hotNodeGo = MakeHotNode();

            _beamMesh = NewMesh("BodyEd_BeamLines");
            _nodeMesh = NewMesh("BodyEd_NodeMarks");
            _hotBeamMesh = NewMesh("BodyEd_HotBeam");

            Bind(_beamGo, _beamMesh, BodyEdMaterials.Beam());
            Bind(_nodeGo, _nodeMesh, BodyEdMaterials.Node());
            Bind(_hotBeamGo, _hotBeamMesh, BodyEdMaterials.BeamHot());
            SetVisible(false);
        }

        private Transform Child(string name)
        {
            var go = new GameObject(name) { layer = Vehicles.PartVisualFactory.VizLayer };
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        private static Mesh NewMesh(string name) => new Mesh
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave,
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
        };

        private static void Bind(Transform t, Mesh mesh, Material mat)
        {
            t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            t.gameObject.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private Transform MakeHotNode()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "hotnode";
            go.layer = Vehicles.PartVisualFactory.VizLayer;
            Kill(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.GetComponent<MeshRenderer>().sharedMaterial = BodyEdMaterials.NodeHot();
            go.SetActive(false);
            return go.transform;
        }

        private void OnDestroy()
        {
            if (_vehicle != null) _vehicle.DeformCommitted -= OnDeformCommitted;
            _preview?.Dispose();       // the mesh is gone; the native memory is not
            _preview = null;
            Kill(_beamMesh); Kill(_nodeMesh); Kill(_hotBeamMesh);
        }

        private void OnDeformCommitted(DeformableBody body)
        {
            // A commit rebuilds the body's mesh — the preview's cached vertices
            // no longer describe anything. Drop it BEFORE the consume check:
            // even a load's own commit invalidates the cache.
            RepairPreview();
            if (_consumeCommits > 0) { _consumeCommits--; return; }
            if (HasLattice) Stale = true;
        }

        // ==================== the document ====================

        /// <summary>Regenerate from the body's current baked shape. False when
        /// the body has no geometry or the fidelity would exceed the beam
        /// budget — the caller shows the status either way.</summary>
        public bool Generate(DeformableBody body, float fidelity01)
        {
            if (body == null || body.Collision == null || body.Collision.BakedMesh == null)
                return false;

            Mesh baked = body.Collision.BakedMesh;
            float length = Mathf.Max(baked.bounds.size.z, 0.05f);
            float spacing = LatticeBuilder.SpacingFor(fidelity01, length);

            if (!LatticeBuilder.Generate(baked.vertices, baked.triangles, spacing,
                                         out LatticeNode[] nodes, out LatticeBeam[] beams))
                return false;

            RepairPreview();
            _nodes.Clear(); _nodes.AddRange(nodes);
            _beams.Clear(); _beams.AddRange(beams);
            Fidelity01 = fidelity01;
            SpacingM = spacing;
            Stale = false;
            _hotNode = _hotBeam = -1;
            _dirty = true;
            return true;
        }

        public void Clear()
        {
            RepairPreview();
            _nodes.Clear();
            _beams.Clear();
            SpacingM = 0f;
            Stale = false;
            _hotNode = _hotBeam = -1;
            _dirty = true;
        }

        /// <summary>Copies OUT, not references — the saved document must not be
        /// a live view of the editor's lists.</summary>
        public void CaptureInto(VehicleLayoutData d)
        {
            if (d == null) return;
            if (_nodes.Count == 0)
            {
                d.latticeNodes = null;
                d.latticeBeams = null;
                d.latticeSpacing = 0f;
                d.latticeFidelity01 = Fidelity01;
                d.latticeDamage01 = Damage01;
                return;
            }
            d.latticeNodes = new LatticeNode[_nodes.Count];
            for (int i = 0; i < _nodes.Count; i++)
                d.latticeNodes[i] = new LatticeNode
                { localPos = _nodes[i].localPos, mass = _nodes[i].mass };
            d.latticeBeams = new LatticeBeam[_beams.Count];
            for (int i = 0; i < _beams.Count; i++)
                d.latticeBeams[i] = new LatticeBeam
                {
                    a = _beams[i].a, b = _beams[i].b, spring = _beams[i].spring,
                    dampingRatio = _beams[i].dampingRatio, breakStrain = _beams[i].breakStrain,
                };
            d.latticeSpacing = SpacingM;
            d.latticeFidelity01 = Fidelity01;
            d.latticeDamage01 = Damage01;
        }

        /// <summary>Rebuild from a layout, sanitised. The load's own deferred
        /// body commit is consumed so it does not read as sculpting.</summary>
        public void ApplyFrom(VehicleLayoutData d)
        {
            RepairPreview();
            _nodes.Clear();
            _beams.Clear();
            _hotNode = _hotBeam = -1;

            if (d?.latticeNodes != null)
            {
                foreach (LatticeNode n in d.latticeNodes)
                    if (n != null)
                        _nodes.Add(new LatticeNode { localPos = n.localPos, mass = n.mass });

                LatticeBeam[] beams = d.latticeBeams ?? System.Array.Empty<LatticeBeam>();
                var copy = new LatticeBeam[beams.Length];
                for (int i = 0; i < beams.Length; i++)
                    copy[i] = beams[i] == null ? null : new LatticeBeam
                    {
                        a = beams[i].a, b = beams[i].b, spring = beams[i].spring,
                        dampingRatio = beams[i].dampingRatio, breakStrain = beams[i].breakStrain,
                    };
                int dropped = LatticeBuilder.Sanitize(_nodes.Count, ref copy);
                if (dropped > 0)
                    Debug.LogWarning($"[StudioLattice] Dropped {dropped} beams the file " +
                                     "could not mean (bad indices or duplicates).");
                _beams.AddRange(copy);
            }

            Fidelity01 = d != null ? Mathf.Clamp01(d.latticeFidelity01) : 0.5f;
            Damage01 = d != null ? Mathf.Clamp01(d.latticeDamage01) : 0.5f;
            SpacingM = d?.latticeSpacing ?? 0f;
            Stale = false;
            _consumeCommits = 1;
            _dirty = true;
        }

        // ==================== edits ====================

        public int AddNode(Vector3 localPos)
        {
            RepairPreview();
            _nodes.Add(new LatticeNode { localPos = localPos });
            _dirty = true;
            return _nodes.Count - 1;
        }

        /// <summary>Remove a node, its beams, and remap every index above it —
        /// the document stays dense so the file stays legible.</summary>
        public bool RemoveNode(int index)
        {
            if (index < 0 || index >= _nodes.Count) return false;
            RepairPreview();
            _nodes.RemoveAt(index);
            for (int i = _beams.Count - 1; i >= 0; i--)
            {
                LatticeBeam b = _beams[i];
                if (b.a == index || b.b == index) { _beams.RemoveAt(i); continue; }
                if (b.a > index) b.a--;
                if (b.b > index) b.b--;
            }
            _hotNode = _hotBeam = -1;
            _dirty = true;
            return true;
        }

        /// <summary>Link two nodes. False for a self-link, an existing link, or
        /// an index that is not a node.</summary>
        public bool AddBeam(int a, int b, out int index)
        {
            index = -1;
            if (a == b || a < 0 || b < 0 || a >= _nodes.Count || b >= _nodes.Count) return false;
            foreach (LatticeBeam beam in _beams)
                if ((beam.a == a && beam.b == b) || (beam.a == b && beam.b == a)) return false;
            RepairPreview();
            _beams.Add(new LatticeBeam { a = Mathf.Min(a, b), b = Mathf.Max(a, b) });
            index = _beams.Count - 1;
            _dirty = true;
            return true;
        }

        public bool RemoveBeam(int index)
        {
            if (index < 0 || index >= _beams.Count) return false;
            RepairPreview();
            _beams.RemoveAt(index);
            _hotBeam = -1;
            _dirty = true;
            return true;
        }

        public void SetNodePos(int index, Vector3 localPos)
        {
            if (index < 0 || index >= _nodes.Count) return;
            RepairPreview();
            _nodes[index].localPos = localPos;
            _dirty = true;
        }

        private PropPlacement _dragSpec;
        private int _dragIndex = -1;

        /// <summary>
        /// A node's position wrapped as a <see cref="PropPlacement"/> so the
        /// existing gizmo can drag it with zero changes — <c>Attach</c> takes a
        /// spec, the spec has a <c>localPos</c>, and Move mode never touches the
        /// rest. The <c>source</c> string never enters a layout; it exists so a
        /// debugger can tell what the gizmo is holding.
        /// </summary>
        public PropPlacement NodeDragSpec(int index)
        {
            if (index < 0 || index >= _nodes.Count) return null;
            // Fresh every selection — a cached spec would hand the gizmo a
            // position from before the last inspector edit or regenerate.
            _dragSpec = new PropPlacement
            {
                source = "node:" + index,
                localPos = _nodes[index].localPos,
            };
            _dragIndex = index;
            return _dragSpec;
        }

        /// <summary>Pull the drag wrapper's position back into the node — the
        /// gizmo's Changed handler calls this.</summary>
        public void CommitDragSpec()
        {
            if (_dragSpec != null && _dragIndex >= 0) SetNodePos(_dragIndex, _dragSpec.localPos);
        }

        // ==================== crash test preview ====================

        /// <summary>One editor whack, N·s — roughly the car arriving at a wall
        /// at 4 m/s, so the dent previewed is the dent a real crash makes.</summary>
        public const float WhackImpulseNs = 6f;

        private Vehicles.LatticeSolver _preview;
        private LatticeBuilder.VertexBinding[] _previewBinds;
        private Mesh _previewMesh;
        private Vector3[] _previewBase, _previewWork;
        private Vector3 _previewScale;
        private float _previewClock;

        public bool PreviewActive => _preview != null;

        /// <summary>
        /// Punch the frame at a body-surface point (vehicle-local metres) and
        /// let the dent play out on the ACTUAL body mesh — the live "how much
        /// damage" feel the slider is tuned against. The solver is a fresh
        /// <see cref="Vehicles.LatticeSolver"/> over the current document at
        /// the current damage scale: exactly the object the driving path
        /// builds, so the editor cannot preview a physics the track does not
        /// have. The mesh writes are UNDONE by <see cref="RepairPreview"/> and
        /// by any document edit — the dents live in the mesh instance, never
        /// in the layout.
        /// </summary>
        public bool PreviewHit(Vector3 pointLocal, DeformableBody body)
        {
            if (_nodes.Count == 0 || body == null) return false;
            if (_preview == null && !StartPreview(body)) return false;

            _preview.SetDamageScale(LatticeBuilder.DamageScale(Damage01));
            Vector3 centre = body.Collision != null && body.Collision.BakedMesh != null
                ? body.Collision.BakedMesh.bounds.center
                : Vector3.zero;
            Vector3 dir = centre - pointLocal;
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.down;
            _preview.ApplyHit(pointLocal, dir.normalized, WhackImpulseNs);
            return true;
        }

        private bool StartPreview(DeformableBody body)
        {
            Mesh mesh = body.WorkMesh;
            if (mesh == null) return false;

            _preview = new Vehicles.LatticeSolver(_nodes.ToArray(), _beams.ToArray());
            _previewMesh = mesh;
            _previewBase = mesh.vertices;              // author units, pristine
            _previewWork = (Vector3[])_previewBase.Clone();
            _previewScale = body.RenderScale;

            var carLocal = new Vector3[_previewBase.Length];
            for (int i = 0; i < carLocal.Length; i++)
                carLocal[i] = Vector3.Scale(_previewBase[i], _previewScale);
            float spacing = SpacingM > 1e-4f ? SpacingM : 0.05f;
            _previewBinds = LatticeBuilder.BindVertices(_nodes.ToArray(), carLocal, spacing);
            _previewClock = 0f;
            return true;
        }

        /// <summary>The Repair button: pristine mesh back, preview solver gone.
        /// Also the invalidation path for every document edit — a stale solver
        /// previewing a lattice that no longer exists would be a lie.</summary>
        public void RepairPreview()
        {
            if (_preview == null) return;
            _preview.Dispose();          // persistent native memory, editor-side
            _preview = null;
            _previewBinds = null;
            if (_previewMesh != null && _previewBase != null)
            {
                _previewMesh.vertices = _previewBase;
                _previewMesh.RecalculateNormals();
            }
            _previewMesh = null;
            _previewBase = _previewWork = null;
        }

        private void Update()
        {
            if (_preview == null || _preview.Asleep) return;
            if (_previewMesh == null) { RepairPreview(); return; }   // mesh died under us

            // Fixed-dt catch-up loop — the editor's frame rate must not change
            // what a whack looks like. Capped so a hitched frame does not spin.
            const float dt = 1f / 240f;
            _previewClock = Mathf.Min(_previewClock + Time.deltaTime, 12f * dt);
            bool moved = false;
            while (_previewClock >= dt)
            {
                _previewClock -= dt;
                moved |= _preview.Step(dt);
            }
            if (!moved) return;
            Vehicles.CarSoftLattice.WriteVertices(_previewBinds, _preview, _previewBase,
                                                  _previewScale, _previewWork);
            _previewMesh.SetVertices(_previewWork);
            _previewMesh.RecalculateNormals();
        }

        // ==================== picking and highlight ====================

        public bool PickNode(Ray ray, out int index) =>
            LatticeBuilder.PickNode(ray, _nodes, transform.localToWorldMatrix,
                                    NodeRadiusM * 1.5f, out index);

        public bool PickBeam(Ray ray, out int index) =>
            LatticeBuilder.PickBeam(ray, _nodes, _beams, transform.localToWorldMatrix,
                                    BeamRadiusM * 2f, out index);

        /// <summary>−1 clears. Node and beam highlights are exclusive because
        /// the selection is.</summary>
        public void SetHighlight(int node, int beam)
        {
            _hotNode = node;
            _hotBeam = beam;
            _dirty = true;
        }

        public void SetVisible(bool on)
        {
            if (!on) RepairPreview();   // leaving the tab heals the crash test
            _visible = on;
            _beamGo.gameObject.SetActive(on);
            _nodeGo.gameObject.SetActive(on);
            _hotBeamGo.gameObject.SetActive(on);
            _hotNodeGo.gameObject.SetActive(false);   // re-shown by the rebuild
            if (on) _dirty = true;
        }

        // ==================== overlay ====================

        private void LateUpdate()
        {
            if (!_dirty || !_visible) return;
            _dirty = false;
            RebuildBeams();
            RebuildNodes();
            RebuildHot();
        }

        private void RebuildBeams()
        {
            _beamMesh.Clear();
            if (_beams.Count == 0) return;
            var verts = new Vector3[_beams.Count * 2];
            var idx = new int[_beams.Count * 2];
            for (int i = 0; i < _beams.Count; i++)
            {
                verts[i * 2] = _nodes[_beams[i].a].localPos;
                verts[i * 2 + 1] = _nodes[_beams[i].b].localPos;
                idx[i * 2] = i * 2;
                idx[i * 2 + 1] = i * 2 + 1;
            }
            _beamMesh.vertices = verts;
            _beamMesh.SetIndices(idx, MeshTopology.Lines, 0);
            _beamMesh.RecalculateBounds();
        }

        /// <summary>One octahedron per node, all in one mesh — a fine frame is
        /// one draw call, not six hundred spheres.</summary>
        private void RebuildNodes()
        {
            _nodeMesh.Clear();
            if (_nodes.Count == 0) return;

            float r = NodeRadiusM;
            var axes = new[]
            {
                new Vector3(r, 0f, 0f), new Vector3(-r, 0f, 0f),
                new Vector3(0f, r, 0f), new Vector3(0f, -r, 0f),
                new Vector3(0f, 0f, r), new Vector3(0f, 0f, -r),
            };
            // +x/-x, +y/-y, +z/-z corner indices per octahedron face.
            var faces = new[]
            {
                0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4,
                2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5,
            };

            var verts = new Vector3[_nodes.Count * 6];
            var tris = new int[_nodes.Count * faces.Length];
            for (int n = 0; n < _nodes.Count; n++)
            {
                for (int v = 0; v < 6; v++) verts[n * 6 + v] = _nodes[n].localPos + axes[v];
                for (int f = 0; f < faces.Length; f++) tris[n * faces.Length + f] = n * 6 + faces[f];
            }
            _nodeMesh.vertices = verts;
            _nodeMesh.SetTriangles(tris, 0);
            _nodeMesh.RecalculateNormals();
            _nodeMesh.RecalculateBounds();
        }

        private void RebuildHot()
        {
            _hotBeamMesh.Clear();
            if (_hotBeam >= 0 && _hotBeam < _beams.Count)
            {
                LatticeBeam b = _beams[_hotBeam];
                _hotBeamMesh.vertices = new[]
                { _nodes[b.a].localPos, _nodes[b.b].localPos };
                _hotBeamMesh.SetIndices(new[] { 0, 1 }, MeshTopology.Lines, 0);
                _hotBeamMesh.RecalculateBounds();
            }

            bool hotNodeOn = _visible && _hotNode >= 0 && _hotNode < _nodes.Count;
            _hotNodeGo.gameObject.SetActive(hotNodeOn);
            if (hotNodeOn)
            {
                _hotNodeGo.localPosition = _nodes[_hotNode].localPos;
                _hotNodeGo.localScale = Vector3.one * (NodeRadiusM * 2.6f);
            }
        }

        private static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
