using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AIHWSim.Pack.Circuits
{
    /// <summary>
    /// Builds one circuit scene from its manifest.
    ///
    /// <b>Nothing here is placed by hand and nothing here is placed by guess.</b>
    /// Every position in these circuits is derived — the centreline is a
    /// surveyed OpenStreetMap trace, the elevation is a DEM sampled along it,
    /// and the furniture is laid out by a deterministic, hash-seeded pass in the
    /// Blender project. This walks the manifest that pass wrote and reproduces
    /// it. Re-running clears the generated root and rebuilds; it never merges,
    /// because a merge would quietly keep whatever the last run got wrong.
    ///
    /// Geometry arrives in three roles and each gets a different treatment:
    ///
    /// <list type="bullet">
    /// <item><b>world</b> — the road, kerbs, run-off, terrain, barriers, sweeps
    /// and the batched buildings. Vertices are already in world space, so these
    /// are placed at the identity. Their pivot is the world origin, which is
    /// correct for a road and would be absurd for a prop.</item>
    /// <item><b>split</b> — one body per grandstand, so the landmarks can be
    /// grabbed individually. Interlagos' 3 061 building footprints stay batched:
    /// 3 000 GameObjects cost more in transform updates than the geometry costs
    /// to draw, which is the same reasoning that produced the batches upstream.</item>
    /// <item><b>instances</b> — a rigid prototype plus a transform. Trees are why
    /// this mode exists (16 626 at Spa) and are combined into spatial chunks
    /// rather than instantiated one GameObject each; see <see cref="BuildTrees"/>.</item>
    /// </list>
    /// </summary>
    public static class CircuitSceneBuilder
    {
        /// <summary>Tree chunk size in metres. Small enough that a chunk is a
        /// sensible thing to hide or delete while working on one corner, large
        /// enough that a circuit is ~100 objects and not ~16 000.</summary>
        private const float TreeCell = 250f;

        /// <summary>Vertex cap per chunk, chosen to stay under the 16-bit index
        /// ceiling (65 536).
        ///
        /// These meshes are committed, and this project serialises assets as
        /// text for readable diffs, so an index is eight hex characters at
        /// 32 bits and four at 16. Spa's forest is about half a million
        /// triangles; that one choice is tens of megabytes of repository.</summary>
        private const int TreeChunkVerts = 60000;

        /// <summary>Vertex compression for the committed tree chunks.
        ///
        /// Unity quantises positions and normals relative to the mesh's own
        /// bounding box, so the error scales with the chunk, not the circuit: a
        /// 250 m cell at Medium is centimetres, on a tree whose canopy is a
        /// seven-sided blob and whose radius already carries a stated 138–221 mm
        /// quantisation from the prototype buckets. Nothing here is measured
        /// against these vertices — the road is, and the road is not compressed.</summary>
        private const ModelImporterMeshCompression TreeCompression =
            ModelImporterMeshCompression.Medium;

        private const string ExplodePref = "TinyTorque.Circuits.ExplodeTrees";
        private const string StripedPref = "TinyTorque.Circuits.StripedKerbs";

        /// <summary>Kerb name prefixes. The striped band is a denser rebuild of
        /// the same cross-section by the same builder, so the two are the same
        /// surface; only one is ever drawn.</summary>
        private const string KerbPlain = "Trk_Kerb_";
        private const string KerbStripe = "Trk_KerbStripe_";

        /// <summary>
        /// Draw the red/white striped kerb band instead of the flat one. On by
        /// default — a striped kerb is the most recognisable thing about a
        /// circuit, and the flat one is the honest average of red and white,
        /// which is a dull red.
        ///
        /// Both bands are always in the scene and the toggle only switches
        /// <see cref="MeshRenderer.enabled"/>, so it is instant and needs no
        /// rebuild. The <b>collider stays on the plain band either way</b>, so
        /// this can never change what a car hits — the striped mesh is drawn
        /// geometry and nothing else.
        /// </summary>
        public static bool StripedKerbs
        {
            get => EditorPrefs.GetBool(StripedPref, true);
            set => EditorPrefs.SetBool(StripedPref, value);
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/Striped kerbs", priority = 222)]
        private static void ToggleStriped()
        {
            StripedKerbs = !StripedKerbs;
            int n = ApplyKerbVariantToOpenScene();
            CircuitPaths.Log("striped kerbs " + (StripedKerbs ? "on" : "off")
                             + (n > 0 ? " (" + n + " band(s) switched in the open scene)"
                                      : " — open a circuit scene to see it"));
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/Striped kerbs", true)]
        private static bool ToggleStripedCheck()
        {
            UnityEditor.Menu.SetChecked("Tools/TinyTorque Assets/Circuits/Striped kerbs",
                                        StripedKerbs);
            return true;
        }

        /// <summary>Flip the kerb variant in whatever circuit scene is open, and
        /// mark it dirty. Returns how many renderers it touched.</summary>
        public static int ApplyKerbVariantToOpenScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!scene.IsValid()) return 0;
            int n = 0;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r.name.StartsWith(KerbStripe)) { r.enabled = StripedKerbs; n++; }
                    else if (r.name.StartsWith(KerbPlain)) { r.enabled = !StripedKerbs; n++; }
                }
            if (n > 0) EditorSceneManager.MarkSceneDirty(scene);
            return n;
        }

        /// <summary>Place every tree as its own GameObject instead of combining
        /// them into chunks. Off by default: Spa's 16 626 trees are 16 626 scene
        /// entries and a scene file to match. On when you actually want to move
        /// individual trees.</summary>
        public static bool ExplodeTrees
        {
            get => EditorPrefs.GetBool(ExplodePref, false);
            set => EditorPrefs.SetBool(ExplodePref, value);
        }

        [MenuItem("Tools/TinyTorque Assets/Circuits/Explode trees to GameObjects", priority = 221)]
        private static void ToggleExplode() => ExplodeTrees = !ExplodeTrees;

        [MenuItem("Tools/TinyTorque Assets/Circuits/Explode trees to GameObjects", true)]
        private static bool ToggleExplodeCheck()
        {
            // Fully qualified: this assembly is inside `AIHWSim`, which has its
            // own `Menu` namespace, so a bare `Menu` binds to that and not to
            // UnityEditor's class.
            UnityEditor.Menu.SetChecked(
                "Tools/TinyTorque Assets/Circuits/Explode trees to GameObjects",
                ExplodeTrees);
            return true;
        }

        /// <summary>The last build's one-line summary, for the headless report.</summary>
        public static string LastReport = "";

        // ---------------------------------------------------------------------

        public static bool Build(CircuitManifest man)
        {
            string key = man.circuit;
            string disp = CircuitPaths.Display(key);
            CircuitPaths.EnsureLayout(key);
            string genDir = CircuitPaths.DirFor(key) + "/generated";
            PackPaths.EnsureFolder(genDir);
            // Clear first, for the same reason the FBX copy does: a rebuild that
            // produces fewer chunks than last time would otherwise leave orphans
            // that no scene references and every clone carries.
            foreach (string f in System.IO.Directory.GetFiles(
                         PackPaths.ToAbsolute(genDir), "Trees_*.asset"))
                AssetDatabase.DeleteAsset(PackPaths.ToProjectRelative(f));

            var mats = CircuitMaterials.Generate(man);
            var protoMesh = new Dictionary<string, Mesh>();
            var protoPrefab = new Dictionary<string, GameObject>();
            int missingMat = 0, missingMesh = 0;

            // -- prototypes become prefabs, once, before the scene exists ------
            foreach (var pe in man.prototypes)
            {
                string fbx = CircuitPaths.DirFor(key) + "/" + pe.fbx;
                var mesh = CircuitImport.LoadMesh(fbx);
                if (mesh == null) { missingMesh++; continue; }
                protoMesh[pe.key] = mesh;
                if (pe.key.StartsWith("tree_")) continue;   // chunked, no prefab
                protoPrefab[pe.key] =
                    MakePropPrefab(key, pe, mesh, mats, ref missingMat);
            }

            // -- the scene ------------------------------------------------------
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                                                    NewSceneMode.Single);
            var root = new GameObject("Circuit_" + disp);
            var groups = new Dictionary<string, Transform>();

            Transform Group(string name)
            {
                if (groups.TryGetValue(name, out var t)) return t;
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                groups[name] = go.transform;
                return go.transform;
            }

            // -- world meshes ---------------------------------------------------
            int placedWorld = 0;
            foreach (var e in man.world)
            {
                var mesh = CircuitImport.LoadMesh(CircuitPaths.DirFor(key) + "/" + e.fbx);
                if (mesh == null) { missingMesh++; continue; }
                var go = MakeMeshObject(e.name, mesh, e.materials, mats, ref missingMat,
                                        e.collider == "mesh");
                go.transform.SetParent(Group(GroupName(e.group)), false);
                // Both kerb variants go in; only one is drawn. See StripedKerbs.
                if (e.name.StartsWith(KerbStripe))
                    go.GetComponent<MeshRenderer>().enabled = StripedKerbs;
                else if (e.name.StartsWith(KerbPlain))
                    go.GetComponent<MeshRenderer>().enabled = !StripedKerbs;
                placedWorld++;
            }

            // -- split bodies ---------------------------------------------------
            int placedSplit = 0;
            var stands = Group("Grandstands");
            foreach (var e in man.split)
            {
                var mesh = CircuitImport.LoadMesh(CircuitPaths.DirFor(key) + "/" + e.fbx);
                if (mesh == null) { missingMesh++; continue; }
                string nm = string.IsNullOrEmpty(e.label) ? e.name
                                                          : e.name + " (" + e.label + ")";
                var go = MakeMeshObject(nm, mesh, e.materials, mats, ref missingMat,
                                        e.collider == "mesh");
                go.transform.SetParent(stands, false);
                placedSplit++;
            }

            // -- instances ------------------------------------------------------
            var trees = new List<CircuitManifest.Instance>();
            var byProto = new Dictionary<string, Transform>();
            int placedProps = 0, noProto = 0;
            var props = Group("Props");

            foreach (var inst in man.instances)
            {
                if (inst.proto != null && inst.proto.StartsWith("tree_"))
                {
                    trees.Add(inst);
                    continue;
                }
                if (!protoPrefab.TryGetValue(inst.proto ?? "", out var prefab)
                    || prefab == null)
                {
                    noProto++;
                    continue;
                }
                if (!byProto.TryGetValue(inst.proto, out var parent))
                {
                    var go = new GameObject(inst.proto);
                    go.transform.SetParent(props, false);
                    parent = go.transform;
                    byProto[inst.proto] = parent;
                }
                var o = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
                o.transform.SetPositionAndRotation(CircuitAxis.Position(inst.p),
                                                   CircuitAxis.Rotation(inst.q));
                o.transform.localScale = Vector3.one * (inst.s <= 0f ? 1f : inst.s);
                MarkStatic(o);
                placedProps++;
            }

            int placedTrees = BuildTrees(man, trees, Group("Trees"), protoMesh, mats,
                                         genDir, ref missingMat);

            // -- the start grid, and a camera looking down it -------------------
            int placedSpawns = MakeDrivable(disp, man);
            AimCamera(man);

            MarkStatic(root);
            EditorSceneManager.MarkSceneDirty(scene);
            string path = CircuitPaths.ScenePath(key);
            EditorSceneManager.SaveScene(scene, path);
            AssetDatabase.SaveAssets();

            // -- report ---------------------------------------------------------
            int wantSpawns = man.grid != null ? man.grid.Length : 0;
            int wantProps = 0, wantTrees = 0;
            foreach (var inst in man.instances)
            {
                if (inst.proto != null && inst.proto.StartsWith("tree_")) wantTrees++;
                else wantProps++;
            }
            bool ok = placedWorld == man.world.Length
                      && placedSplit == man.split.Length
                      && placedProps == wantProps
                      && placedTrees == wantTrees
                      && placedSpawns == wantSpawns
                      && missingMesh == 0;

            var sb = new StringBuilder();
            sb.AppendFormat("{0}: {1}/{2} world, {3}/{4} stands, {5}/{6} props, "
                            + "{7}/{8} trees, {11}/{12} grid slots, {9} tris, hash {10}",
                            disp, placedWorld, man.world.Length,
                            placedSplit, man.split.Length,
                            placedProps, wantProps, placedTrees, wantTrees,
                            man.counts != null
                                ? man.counts.world_tris + man.counts.split_tris : 0,
                            man.manifest_hash, placedSpawns, wantSpawns);
            // Count every drop by reason. "No grandstands appeared" and "every
            // grandstand was culled" look identical in a viewport and cost an
            // hour apiece to tell apart — UNITY_EXPORT.md §7 rule 3, and the
            // Blender side reports its own culls the same way.
            if (missingMesh > 0) sb.AppendFormat("; DROPPED {0} missing mesh", missingMesh);
            if (noProto > 0) sb.AppendFormat("; DROPPED {0} no prototype", noProto);
            if (missingMat > 0)
                sb.AppendFormat("; {0} material slot(s) fell back to grey", missingMat);
            foreach (var d in man.drops)
                sb.AppendFormat("; exporter dropped {0} x {1}", d.count, d.reason);
            if (man.verify != null && man.verify.checked_count > 0)
                sb.AppendFormat("; tree prototype {0:F0} mm axial over {1}",
                                man.verify.worst_axial_mm, man.verify.checked_count);
            sb.Append(ok ? "  OK" : "  INCOMPLETE");
            LastReport = sb.ToString();
            if (ok) CircuitPaths.Log(LastReport); else CircuitPaths.Err(LastReport);
            CircuitPaths.Log(disp + " scene -> " + path);
            return ok;
        }

        // ---------------------------------------------------------------------

        /// <summary>Collection name to scene group. Kept as a switch rather than
        /// used raw so the hierarchy reads in English and a new collection
        /// upstream lands somewhere sensible instead of creating "GROUND".</summary>
        private static string GroupName(string group)
        {
            switch (group)
            {
                case "TRACK": return "Track";
                case "RUNOFF": return "Runoff";
                case "TERRAIN": return "Terrain";
                case "MARKS": return "Markings";
                case "PITS": return "Pits";
                case "GROUND": return "Ground";
                case "BARRIER": return "Barriers";
                case "DETAIL": return "Detail";
                case "BUILDINGS": return "Buildings";
                default: return string.IsNullOrEmpty(group) ? "Misc" : group;
            }
        }

        private static GameObject MakeMeshObject(string name, Mesh mesh,
                                                 string[] matNames,
                                                 Dictionary<string, Material> mats,
                                                 ref int missing, bool collide)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials =
                CircuitMaterials.Bind(matNames, mats, ref missing);
            if (collide)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = false;      // a road is not convex, and neither is a barrier
            }
            MarkStatic(go);
            return go;
        }

        private static GameObject MakePropPrefab(string key,
                                                 CircuitManifest.ProtoEntry pe,
                                                 Mesh mesh,
                                                 Dictionary<string, Material> mats,
                                                 ref int missing)
        {
            var go = new GameObject(pe.key);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials =
                CircuitMaterials.Bind(pe.materials, mats, ref missing);
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            string path = CircuitPaths.PrefabDirFor(key) + "/" + pe.key + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        private static void MarkStatic(GameObject go) =>
            GameObjectUtility.SetStaticEditorFlags(
                go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic
                    | StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ContributeGI);

        // ---------------------------------------------------------------------

        /// <summary>
        /// Trees, combined into spatial chunks.
        ///
        /// Sixteen thousand GameObjects is the thing the Blender side batched
        /// them to avoid, and Unity has the same problem for the same reason. So
        /// the manifest's per-tree transforms — which are the valuable part, and
        /// the reason the exporter records them at all — are baked into one mesh
        /// per 250 m cell here. The transforms stay in the manifest, so an LOD
        /// group, a GPU-instanced draw or one GameObject per tree
        /// (<see cref="ExplodeTrees"/>) are all still reachable from the same
        /// data; what is fixed is only what this scene happens to contain.
        ///
        /// Each chunk gets four submeshes: bark, then the three foliage tones.
        /// The prototype carries one foliage slot and the instance says which
        /// tone belongs in it — species from one hash draw, colour from another,
        /// so a stand of one species still carries a spread of colour through it.
        /// </summary>
        private static int BuildTrees(CircuitManifest man,
                                      List<CircuitManifest.Instance> trees,
                                      Transform parent,
                                      Dictionary<string, Mesh> protoMesh,
                                      Dictionary<string, Material> mats,
                                      string genDir, ref int missingMat)
        {
            if (trees.Count == 0) return 0;

            // Bark comes from a prototype's own slot 0; the three foliage tones
            // are named by the manifest because two of them are on no exported
            // mesh — the prototype only ever carries one.
            var slotMats = new Material[4];
            string bark = "M_TrkBark";
            foreach (var pe in man.prototypes)
                if (pe.key.StartsWith("tree_") && pe.materials.Length > 0)
                { bark = pe.materials[0]; break; }
            var names = new List<string> { bark };
            for (int i = 0; i < 3; i++)
                names.Add(i < man.tree_leaf_materials.Length
                          ? man.tree_leaf_materials[i] : "M_TrkLeaf");
            slotMats = CircuitMaterials.Bind(names.ToArray(), mats, ref missingMat);

            if (ExplodeTrees)
            {
                int n = 0;
                foreach (var t in trees)
                {
                    if (!protoMesh.TryGetValue(t.proto, out var m)) continue;
                    var go = new GameObject(t.proto);
                    go.transform.SetParent(parent, false);
                    go.transform.SetPositionAndRotation(CircuitAxis.Position(t.p),
                                                        CircuitAxis.Rotation(t.q));
                    go.transform.localScale = Vector3.one * t.s;
                    go.AddComponent<MeshFilter>().sharedMesh = m;
                    go.AddComponent<MeshRenderer>().sharedMaterials = new[]
                        { slotMats[0], slotMats[Mathf.Clamp(t.mat, 1, 3)] };
                    MarkStatic(go);
                    n++;
                }
                return n;
            }

            var cells = new Dictionary<(int, int), List<CircuitManifest.Instance>>();
            foreach (var t in trees)
            {
                Vector3 p = CircuitAxis.Position(t.p);
                var cell = (Mathf.FloorToInt(p.x / TreeCell), Mathf.FloorToInt(p.z / TreeCell));
                if (!cells.TryGetValue(cell, out var list))
                    cells[cell] = list = new List<CircuitManifest.Instance>();
                list.Add(t);
            }

            // Deterministic order, so a rebuild produces the same asset names and
            // the same scene diff rather than a reshuffled one.
            var keys = new List<(int, int)>(cells.Keys);
            keys.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1)
                                                   : a.Item2.CompareTo(b.Item2));

            int placed = 0, chunk = 0;
            foreach (var cell in keys)
            {
                var list = cells[cell];
                int i = 0;
                while (i < list.Count)
                {
                    var verts = new List<Vector3>();
                    var norms = new List<Vector3>();
                    var tris = new List<int>[4]
                        { new List<int>(), new List<int>(), new List<int>(), new List<int>() };

                    while (i < list.Count && verts.Count < TreeChunkVerts)
                    {
                        var t = list[i++];
                        if (!protoMesh.TryGetValue(t.proto ?? "", out var m)) continue;
                        var trs = Matrix4x4.TRS(CircuitAxis.Position(t.p),
                                                CircuitAxis.Rotation(t.q),
                                                Vector3.one * (t.s <= 0f ? 1f : t.s));
                        int baseIdx = verts.Count;
                        var mv = m.vertices;
                        var mn = m.normals;
                        for (int v = 0; v < mv.Length; v++)
                        {
                            verts.Add(trs.MultiplyPoint3x4(mv[v]));
                            // Uniform scale plus a rotation, so the plain vector
                            // transform is the correct normal transform — no
                            // inverse-transpose needed, and none is done.
                            norms.Add(v < mn.Length
                                ? trs.MultiplyVector(mn[v]).normalized : Vector3.up);
                        }
                        AppendTris(tris[0], m, 0, baseIdx);
                        AppendTris(tris[Mathf.Clamp(t.mat, 1, 3)], m, 1, baseIdx);
                        placed++;
                    }
                    if (verts.Count == 0) continue;

                    var mesh = new Mesh { name = string.Format("Trees_{0:000}", chunk) };
                    // 16-bit wherever it fits, which the vertex cap makes the
                    // normal case. The fallback exists because a single cell
                    // could in principle hold one huge tree run.
                    mesh.indexFormat = verts.Count <= 65534
                        ? IndexFormat.UInt16 : IndexFormat.UInt32;
                    mesh.SetVertices(verts);
                    mesh.SetNormals(norms);
                    mesh.subMeshCount = 4;
                    for (int s = 0; s < 4; s++) mesh.SetTriangles(tris[s], s);
                    mesh.RecalculateBounds();
                    MeshUtility.SetMeshCompression(mesh, TreeCompression);
                    AssetDatabase.CreateAsset(mesh, genDir + "/" + mesh.name + ".asset");

                    var go = new GameObject(mesh.name);
                    go.transform.SetParent(parent, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterials = slotMats;
                    MarkStatic(go);
                    chunk++;
                }
            }
            return placed;
        }

        private static void AppendTris(List<int> dst, Mesh m, int sub, int baseIdx)
        {
            if (sub >= m.subMeshCount) return;
            var t = m.GetTriangles(sub);
            for (int i = 0; i < t.Length; i++) dst.Add(t[i] + baseIdx);
        }

        // ---------------------------------------------------------------------

        /// <summary>
        /// Make the scene drivable on Play, the way TTA_Sandbox is.
        ///
        /// Two components do it, and they are the same two any scene track needs.
        /// <c>TrackBootstrap</c> is the game: press Play and it builds the car,
        /// the camera, the HUD and the physics loop. <c>SceneTrackDescriptor</c>
        /// is what tells it that the scene it woke up in <i>is</i> a track —
        /// <c>TrackBootstrap.Awake</c> looks for one whenever it was not launched
        /// from the menu, which is exactly the Play-button case.
        ///
        /// The grid slots become <c>TrackSpawnMarker</c>s rather than the inert
        /// empties that used to stand here: same positions, same headings, same
        /// manifest, but now the game reads them. One marker per slot means
        /// <c>gridOrder</c> N starts player N on the real grid box, so a field of
        /// cars sits on the painted grid and follows the road, instead of being
        /// projected backwards from pole in a straight line.
        ///
        /// <b>FreeRoam, not Circuit</b>, and deliberately: Circuit claims a finish
        /// line and a dense run of ordered checkpoints, and nothing here authors
        /// either. Declaring it would buy a lap counter that never counts and a
        /// validator failure for a claim the scene cannot back. Driving works
        /// either way; lapping is a separate pass over the same spine.
        ///
        /// The bot corridor IS baked, because the manifest already carries the
        /// centreline and the half-width at every station — the exact two arrays
        /// the descriptor wants. It costs nothing to fill and it is what
        /// <c>TrackRespawn</c> uses to put a car back on the road.
        /// </summary>
        /// <returns>Grid slots placed, for the build report.</returns>
        private static int MakeDrivable(string disp, CircuitManifest man)
        {
            // Its own root object, outside `root`, so MarkStatic does not batch
            // the spawn markers into the scene's static geometry.
            var boot = new GameObject("TrackBootstrap");
            boot.AddComponent<AIHWSim.Core.TrackBootstrap>();

            var descGo = new GameObject("TrackDescriptor");
            var desc = descGo.AddComponent<AIHWSim.Track.SceneTrackDescriptor>();
            desc.displayName = disp;
            desc.kind = AIHWSim.TrackEd.TrackPresets.TrackKind.FreeRoam;
            desc.sceneOwnsSky = true;
            // No mesh here carries a SurfaceTag, so every collider takes this.
            // Asphalt is the honest default for a circuit: the road is most of
            // what a car touches, and the alternative is the 1.0 baseline, which
            // is not a surface at all. Grass and gravel are a tagging pass.
            desc.sceneFallbackFloor = 1;

            // The corridor, straight off the manifest spine — the same array the
            // validator measures the road against, so the bots' idea of the track
            // and the check that the track is where it should be come from one
            // source.
            int n = man.SpineCount;
            var line = new Vector3[n];
            var half = new float[n];
            for (int i = 0; i < n; i++)
            {
                line[i] = CircuitAxis.Position(man.Spine(i, 1), man.Spine(i, 2),
                                               man.Spine(i, 3));
                half[i] = man.Spine(i, 7);          // spine_fields[7] = half_width
            }
            desc.centerline = line;
            desc.halfWidths = half;
            desc.corridorClosed = true;             // it is a lap

            int slots = 0;
            foreach (var g in man.grid)
            {
                // slot is 1-based in the manifest, gridOrder is 0-based: slot 1 is
                // pole is player 1 is gridOrder 0.
                int order = g.slot - 1;
                var go = new GameObject(AIHWSim.Track.TrackSpawnMarker.NameFor(order));
                go.transform.SetParent(descGo.transform, false);
                go.transform.SetPositionAndRotation(CircuitAxis.Position(g.p),
                                                    CircuitAxis.Heading(g.heading_deg));
                go.AddComponent<AIHWSim.Track.TrackSpawnMarker>().gridOrder = order;
                slots++;
            }
            return slots;
        }

        /// <summary>Put the scene camera on the grid looking up the road, so
        /// opening the scene shows the circuit rather than the inside of the
        /// terrain skirt.</summary>
        private static void AimCamera(CircuitManifest man)
        {
            var cam = Camera.main;
            if (cam == null || man.grid == null || man.grid.Length == 0) return;
            var g = man.grid[0];
            Quaternion rot = CircuitAxis.Heading(g.heading_deg);
            cam.transform.SetPositionAndRotation(
                CircuitAxis.Position(g.p) + Vector3.up * 6f + rot * Vector3.back * 14f,
                Quaternion.Euler(8f, rot.eulerAngles.y, 0f));
            cam.farClipPlane = 4000f;
        }
    }
}
