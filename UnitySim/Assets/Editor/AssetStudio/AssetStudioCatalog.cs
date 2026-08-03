using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>Where a row came from and how far along it is.</summary>
    public enum RowStatus
    {
        /// <summary>In Resources, no manifest — one of the assets the project has
        /// always shipped, bound by the legacy substring token tables.</summary>
        Imported,
        /// <summary>In Resources with a manifest beside it: Asset Studio owns it.</summary>
        Managed,
        /// <summary>An external export with nothing in the project yet.</summary>
        New,
        /// <summary>An export folder that exists but could not be read.</summary>
        Broken,
    }

    /// <summary>
    /// One browsable asset — an FBX already in <c>Resources/</c>, an export folder
    /// that is not in the project yet, or (later) both at once.
    ///
    /// Cheap to build: discovery reads file names and nothing else. The expensive
    /// parts — parsing an export, loading a prefab to count its child meshes — are
    /// deferred to selection, because a project with 200 assets would otherwise pay
    /// for all of them on every refresh to show a list.
    /// </summary>
    public sealed class AssetRow
    {
        /// <summary>The game key: the FBX stem, which is what
        /// <c>Resources.Load</c> resolves. Empty for an external export that has
        /// not been given one yet.</summary>
        public string key = "";

        /// <summary>What the row is called in the list — the key once imported, the
        /// export folder name before that.</summary>
        public string label = "";

        public AssetKind kind = AssetKind.Unassigned;
        public RowStatus status = RowStatus.New;

        /// <summary>"PartModels" / "TrackProps" / "Cosmetics", or "" when the row is
        /// external and no destination has been chosen.</summary>
        public string rootName = "";

        /// <summary>Project-relative FBX path, or "" for a row that is external
        /// only.</summary>
        public string fbxAssetPath = "";

        /// <summary>Absolute path of the external export folder, or "".</summary>
        public string exportDir = "";

        /// <summary>Why the export could not be read, when
        /// <see cref="status"/> is <see cref="RowStatus.Broken"/>.</summary>
        public string problem = "";

        public bool HasProject => !string.IsNullOrEmpty(fbxAssetPath);
        public bool HasExport => !string.IsNullOrEmpty(exportDir);

        // ---- lazily loaded detail -------------------------------------------

        private TtExport _export;
        private bool _exportTried;

        /// <summary>The parsed export, or null. Parsed once, on first ask.</summary>
        public TtExport Export
        {
            get
            {
                if (!_exportTried && HasExport)
                {
                    _exportTried = true;
                    _export = TtExport.Load(exportDir, out problem);
                    if (_export == null) status = RowStatus.Broken;
                }
                return _export;
            }
        }

        private GameObject _prefab;
        private bool _prefabTried;

        /// <summary>The imported FBX as Unity sees it, or null. This is the only
        /// honest source of submesh slot ORDER — the export names an object's
        /// materials but not which slot each one occupies.</summary>
        public GameObject Prefab
        {
            get
            {
                if (!_prefabTried && HasProject)
                {
                    _prefabTried = true;
                    _prefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxAssetPath);
                }
                return _prefab;
            }
        }

        /// <summary>Every renderer in the imported FBX, in traversal order — the
        /// child-mesh list the game will actually bind. Empty for a row that is
        /// external only.</summary>
        public List<Renderer> Renderers()
        {
            var list = new List<Renderer>();
            if (Prefab != null) list.AddRange(Prefab.GetComponentsInChildren<Renderer>(true));
            return list;
        }

        private CommitState _commit;
        private bool _commitTried;

        /// <summary>
        /// How this row's committed copy stands against its export.
        ///
        /// <b>Lazy and cached, and only for a Managed row.</b> Answering it means
        /// hashing two FBX files, which is exactly the kind of work
        /// <see cref="AssetStudioCatalog.Refresh"/> promises not to do — discovery
        /// reads file names. Computing it here means only the rows the list
        /// actually draws pay, once per refresh, and only the ones carrying a
        /// manifest pay at all.
        /// </summary>
        public CommitState Commit
        {
            get
            {
                if (_commitTried) return _commit;
                _commitTried = true;
                _commit = status == RowStatus.Managed
                    ? AssetStudioCommit.StateOf(kind, key, Export)
                    : CommitState.NotCommitted;
                return _commit;
            }
        }

        public void Invalidate()
        {
            _export = null; _exportTried = false;
            _prefab = null; _prefabTried = false;
            _commit = CommitState.NotCommitted; _commitTried = false;
        }
    }

    /// <summary>
    /// Discovery: everything already in the three <c>Resources/</c> roots, plus
    /// every per-asset folder under the configured Blender export root, merged into
    /// one list.
    ///
    /// The merge is by KEY, and until an asset carries a manifest naming its source
    /// there is nothing to merge on — the export calls itself <c>POLICE</c> and the
    /// shipped car is <c>body_patrol</c>, and no amount of string manipulation
    /// should be allowed to guess that those are the same thing. So an external
    /// export lists as its own row until it is committed, at which point the
    /// manifest records the link.
    ///
    /// <b>Stale and Drifted are deliberately absent here.</b> They are differences
    /// between a manifest and its source export, and manifests do not exist yet;
    /// they arrive with the commit pipeline that writes them.
    /// </summary>
    public static class AssetStudioCatalog
    {
        private static readonly List<AssetRow> _rows = new List<AssetRow>();
        private static bool _built;

        public static IReadOnlyList<AssetRow> Rows
        {
            get { if (!_built) Refresh(); return _rows; }
        }

        public static void Refresh()
        {
            _rows.Clear();
            _built = true;

            foreach (string root in AssetStudio.Roots)
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                string rootName = AssetStudio.RootName(root);

                foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { root }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    // FindAssets recurses; a model in a subfolder is not a key the
                    // game can ask for, because Resources.Load is given the stem
                    // alone. Keep the list honest about what is reachable.
                    if (Path.GetDirectoryName(path)?.Replace('\\', '/') != root) continue;

                    string key = Path.GetFileNameWithoutExtension(path);
                    var row = new AssetRow
                    {
                        key = key,
                        label = key,
                        rootName = rootName,
                        fbxAssetPath = path,
                        kind = KindFor(rootName, key),
                        status = File.Exists(AssetStudio.ManifestPathFor(path))
                            ? RowStatus.Managed : RowStatus.Imported,
                    };
                    _rows.Add(row);
                }
            }

            foreach (string dir in ExportFolders())
            {
                _rows.Add(new AssetRow
                {
                    label = Path.GetFileName(dir),
                    exportDir = dir,
                    status = RowStatus.New,
                    kind = AssetKind.Unassigned,
                });
            }

            _rows.Sort((a, b) =>
            {
                int s = a.status.CompareTo(b.status);
                return s != 0 ? s : string.CompareOrdinal(a.label, b.label);
            });
        }

        /// <summary>Every folder under the export root that holds an
        /// <c>export.json</c>, plus the root itself when it IS one — so a picker
        /// aimed one level too deep still finds its asset.</summary>
        public static List<string> ExportFolders()
        {
            var found = new List<string>();
            string src = AssetStudio.SourceDir;
            if (string.IsNullOrEmpty(src)) return found;

            if (File.Exists(Path.Combine(src, TtExport.FileName))) { found.Add(src); return found; }
            foreach (string sub in Directory.GetDirectories(src))
                if (File.Exists(Path.Combine(sub, TtExport.FileName))) found.Add(sub);
            found.Sort(string.CompareOrdinal);
            return found;
        }

        /// <summary>
        /// Inferred from the Resources root and the key prefix, because that is
        /// exactly how the game decides: <c>CarVehicle.BodyMeshKey</c> only ever
        /// builds "body_*" and <c>PartVisualFactory.BuildWheelViz</c> only ever
        /// builds "wheel_*". Everything else in PartModels — the battery, the
        /// antennas, the light bars — renders at authored size with no scale
        /// contract at all, which is a different set of checks and so a different
        /// kind.
        /// </summary>
        public static AssetKind KindFor(string rootName, string key)
        {
            if (rootName == "Cosmetics") return AssetKind.Cosmetic;
            if (rootName == "TrackProps") return AssetKind.Prop;
            if (key.StartsWith("body_")) return AssetKind.CarBody;
            if (key.StartsWith("wheel_")) return AssetKind.Wheel;
            return AssetKind.Fitting;
        }

        public static AssetRow Find(string key, string exportDir)
        {
            foreach (AssetRow r in Rows)
            {
                if (!string.IsNullOrEmpty(key) && r.key == key) return r;
                if (!string.IsNullOrEmpty(exportDir) && r.exportDir == exportDir) return r;
            }
            return null;
        }

        public static int CountOf(RowStatus s)
        {
            int n = 0;
            foreach (AssetRow r in Rows) if (r.status == s) n++;
            return n;
        }
    }
}
