using System.Collections.Generic;
using System.IO;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// The Asset Studio front end: browse everything the game can load beside
    /// everything Blender has exported, and see what stands between the two.
    ///
    /// Read-only in this milestone, deliberately. The window's first job is to make
    /// the invisible failure visible — an export whose object names carry no token
    /// binds nothing and arrives as a correctly shaped car in one flat colour, with
    /// no error anywhere — and being able to SEE that is worth shipping before
    /// anything can be written.
    ///
    /// Every action the window grows will also be a menu item, because the pipeline
    /// has to be runnable headlessly; the window exists to put the state of an asset
    /// in front of the author, which a menu cannot do. Authoring itself stays
    /// interactive: "assign M_Police_Chrome to Police_Handles slot 0" is not a menu
    /// item in any useful sense, and the draft asset is what carries an authored
    /// decision across to the headless half.
    /// </summary>
    public sealed class AssetStudioWindow : EditorWindow
    {
        // Window state. Every field here is [SerializeField] so it survives a
        // domain reload — EditorWindow serializes those across a script compile,
        // and a browser that forgets what you had selected every time you touch a
        // file is a browser you stop using.
        [SerializeField] private string _selectedKey = "";
        [SerializeField] private string _selectedExportDir = "";
        [SerializeField] private string _search = "";
        [SerializeField] private int _kindFilter;
        [SerializeField] private int _statusFilter;
        [SerializeField] private Vector2 _listScroll, _detailScroll;
        [SerializeField] private List<string> _expanded = new List<string>();
        [SerializeField] private bool _showFixes, _showObjects = true;

        private const float ListWidth = 280f;
        private const float RowHeight = 18f;

        private static readonly string[] KindNames =
            { "All kinds", "Car body", "Wheel", "Cosmetic", "Prop", "Fitting", "Unassigned" };
        private static readonly string[] StatusNames =
            { "All", "Imported", "Managed", "New", "Broken" };

        [MenuItem(AssetStudio.Menu + "Asset Studio", priority = AssetStudio.PrioWindow)]
        public static void Open()
        {
            var w = GetWindow<AssetStudioWindow>(false, "Asset Studio", true);
            w.minSize = new Vector2(880f, 520f);
            w.Show();
        }

        [MenuItem(AssetStudio.Menu + "Set export folder...", priority = AssetStudio.PrioSetSource)]
        public static void SetSourceFolder()
        {
            string start = AssetStudio.SourceDir;
            if (string.IsNullOrEmpty(start)) start = Directory.GetCurrentDirectory();
            string picked = EditorUtility.OpenFolderPanel(
                "Blender export root (the folder CONTAINING the per-asset folders)",
                start, "");
            if (string.IsNullOrEmpty(picked)) return;

            EditorPrefs.SetString(AssetStudio.SourcePrefKey, picked);
            AssetStudioCatalog.Refresh();
            if (AssetStudio.IsExportRoot(picked))
                AssetStudio.Log("export folder set to " + picked + " ("
                                + AssetStudioCatalog.ExportFolders().Count + " assets)");
            else
                AssetStudio.Warn("no export.json under " + picked
                                 + " — nothing will list as External.");
        }

        private void OnFocus() => AssetStudioCatalog.Refresh();

        private void OnGUI()
        {
            DrawToolbar();
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawList();
                DrawDetail();
            }
        }

        // ==================== toolbar ====================

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _search = GUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                                              GUILayout.Width(200f));
                _kindFilter = EditorGUILayout.Popup(_kindFilter, KindNames,
                                                    EditorStyles.toolbarPopup,
                                                    GUILayout.Width(110f));
                _statusFilter = EditorGUILayout.Popup(_statusFilter, StatusNames,
                                                      EditorStyles.toolbarPopup,
                                                      GUILayout.Width(90f));
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60f)))
                    AssetStudioCatalog.Refresh();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Set export folder...", EditorStyles.toolbarButton))
                    SetSourceFolder();
            }
        }

        // ==================== left: the list ====================

        private void DrawList()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(ListWidth)))
            {
                string problem = AssetStudio.SourceProblem;
                if (!string.IsNullOrEmpty(problem))
                    EditorGUILayout.HelpBox(problem, MessageType.Info);

                _listScroll = EditorGUILayout.BeginScrollView(_listScroll);

                RowStatus[] order =
                    { RowStatus.New, RowStatus.Broken, RowStatus.Managed, RowStatus.Imported };
                foreach (RowStatus s in order)
                {
                    var group = new List<AssetRow>();
                    foreach (AssetRow r in AssetStudioCatalog.Rows)
                        if (r.status == s && Passes(r)) group.Add(r);
                    if (group.Count == 0) continue;

                    EditorGUILayout.LabelField($"{Title(s)}  ({group.Count})",
                                               EditorStyles.miniBoldLabel);
                    foreach (AssetRow r in group) DrawRow(r);
                    EditorGUILayout.Space(4f);
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private static string Title(RowStatus s) => s switch
        {
            RowStatus.New => "External, not imported",
            RowStatus.Broken => "Unreadable",
            RowStatus.Managed => "Managed by Asset Studio",
            _ => "In the project",
        };

        private bool Passes(AssetRow r)
        {
            if (_statusFilter > 0 && (int)r.status != _statusFilter - 1) return false;
            if (_kindFilter > 0 && (int)r.kind != _kindFilter - 1) return false;
            if (string.IsNullOrEmpty(_search)) return true;
            return r.label.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0
                || r.key.IndexOf(_search, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawRow(AssetRow r)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, RowHeight);
            bool selected = IsSelected(r);

            if (Event.current.type == EventType.Repaint && selected)
                EditorGUI.DrawRect(rect, new Color(0.24f, 0.38f, 0.58f, 0.55f));

            var label = new Rect(rect.x, rect.y, rect.width - 58f, rect.height);
            var kind = new Rect(rect.xMax - 58f, rect.y, 58f, rect.height);
            EditorGUI.LabelField(label, r.label);
            EditorGUI.LabelField(kind, KindShort(r.kind), EditorStyles.miniLabel);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                _selectedKey = r.key;
                _selectedExportDir = r.exportDir;
                GUI.FocusControl(null);
                Event.current.Use();
                Repaint();
            }
        }

        private bool IsSelected(AssetRow r) =>
            (!string.IsNullOrEmpty(r.key) && r.key == _selectedKey)
            || (!string.IsNullOrEmpty(r.exportDir) && r.exportDir == _selectedExportDir);

        private static string KindShort(AssetKind k) => k switch
        {
            AssetKind.CarBody => "body",
            AssetKind.Wheel => "wheel",
            AssetKind.Cosmetic => "cosm",
            AssetKind.Prop => "prop",
            AssetKind.Fitting => "fit",
            _ => "?",
        };

        // ==================== right: the detail ====================

        private void DrawDetail()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                AssetRow r = AssetStudioCatalog.Find(_selectedKey, _selectedExportDir);
                if (r == null)
                {
                    EditorGUILayout.HelpBox(
                        "Select an asset on the left.\n\n"
                        + "\"In the project\" is what the game can already load. "
                        + "\"External, not imported\" is what Blender has exported and "
                        + "the game has never seen.", MessageType.None);
                    return;
                }

                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

                DrawIdentity(r);
                TtExport x = r.Export;
                if (r.status == RowStatus.Broken)
                    EditorGUILayout.HelpBox("This export could not be read: " + r.problem,
                                            MessageType.Error);
                if (x != null)
                {
                    DrawSource(x);
                    DrawVerification(x);
                    DrawContract(r, x);
                    DrawNotes(x);
                    DrawMaterials(x);
                }
                DrawObjects(r, x);

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawIdentity(AssetRow r)
        {
            EditorGUILayout.LabelField(r.label, EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Key", string.IsNullOrEmpty(r.key)
                    ? "(none yet — assigned when it is committed)" : r.key);
                EditorGUILayout.LabelField("Kind", r.kind.ToString());
                EditorGUILayout.LabelField("Status", Title(r.status));
                if (r.HasProject)
                    EditorGUILayout.LabelField("In project", r.fbxAssetPath);
                if (r.HasExport)
                    EditorGUILayout.LabelField("Export folder", r.exportDir);
            }

            if (r.status == RowStatus.Imported)
                EditorGUILayout.HelpBox(
                    "Bound by the legacy substring token tables — its object names are "
                    + "matched case-insensitively, first match wins, against "
                    + "PartVisualFactory's hand-ordered tables. Asset Studio does not "
                    + "own this asset and will not change it.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!r.HasProject))
                    if (GUILayout.Button("Select FBX", GUILayout.Height(22f)))
                        Selection.activeObject =
                            AssetDatabase.LoadAssetAtPath<Object>(r.fbxAssetPath);
                using (new EditorGUI.DisabledScope(!r.HasExport))
                    if (GUILayout.Button("Show export folder", GUILayout.Height(22f)))
                        EditorUtility.RevealInFinder(r.exportDir);
            }
            EditorGUILayout.Space();
        }

        private static void DrawSource(TtExport x)
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Asset name", x.assetName ?? "");
                EditorGUILayout.LabelField("Blend", Path.GetFileName(x.sourceBlend ?? ""));
                EditorGUILayout.LabelField("Exported", x.exportedAtUtc ?? "");
                EditorGUILayout.LabelField("Blender", x.blenderVersion ?? "");
                EditorGUILayout.LabelField("Export schema", x.exportSettingsVersion.ToString());
                EditorGUILayout.LabelField("FBX", x.fbxFile ?? "");
                EditorGUILayout.LabelField("Textures", (x.textures?.Length ?? 0).ToString());
            }
            EditorGUILayout.Space();
        }

        private static void DrawVerification(TtExport x)
        {
            if (x.verification == null) return;
            EditorGUILayout.LabelField("Exporter verification", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Passed", x.verification.passed ? "yes" : "NO");
                EditorGUILayout.LabelField("Size in Unity axes", Fmt(x.UnityDims) + "  m");
                foreach (string issue in x.verification.issues ?? System.Array.Empty<string>())
                    EditorGUILayout.HelpBox(issue, MessageType.Error);
            }
            EditorGUILayout.Space();
        }

        /// <summary>
        /// The geometry contract, and the two corrections the old pipeline applied
        /// that this exporter does not.
        ///
        /// Every arcade shell was built by scaling to length 0.420 m with ONE
        /// factor — which is why PartModelValidator pins those bodies' length and
        /// leaves their width free ("real widths 0.17-0.20 after uniform scale").
        /// Proportions were never altered, and this panel must not imply otherwise:
        /// CarVehicle.BodyMeshAuthorSize is a nominal DIVISOR, not a measurement of
        /// any shell, so comparing an export to it axis by axis reads as distortion
        /// where there is none.
        /// </summary>
        private static void DrawContract(AssetRow r, TtExport x)
        {
            if (r.kind != AssetKind.CarBody && r.kind != AssetKind.Wheel
                && r.kind != AssetKind.Unassigned) return;
            if (x.UnityDims == Vector3.zero) return;

            EditorGUILayout.LabelField("Geometry contract", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                bool wheel = r.kind == AssetKind.Wheel;
                float target = wheel ? PartVisualFactory.WheelAuthorRadius * 2f
                                     : CarVehicle.BodyMeshAuthorSize.z;
                EditorGUILayout.LabelField(wheel ? "Wanted diameter" : "Wanted length",
                                           target.ToString("0.###") + " m");
                EditorGUILayout.LabelField("Measured", Fmt(x.UnityDims) + "  m");

                float scale = x.UniformScaleTo(target);
                float yaw = x.LengthRunsAlongX ? -90f : 0f;
                Vector3 after = x.AuthorSizeAfter(scale, yaw);

                if (Mathf.Abs(scale - 1f) > 0.001f || Mathf.Abs(yaw) > 0.5f)
                {
                    var msg = new System.Text.StringBuilder();
                    msg.Append("This export needs the corrections the old pipeline "
                               + "applied and this one does not.\n");
                    if (Mathf.Abs(scale - 1f) > 0.001f)
                        msg.Append($"\n  Uniform scale  x{scale:0.#####}   "
                                   + $"(1/{1f / scale:0.###})");
                    if (Mathf.Abs(yaw) > 0.5f)
                        msg.Append("\n  Yaw  -90 deg   the long axis is X; the game "
                                   + "builds every car facing +Z");
                    msg.Append("\n\nRecorded as authorSize " + Fmt(after)
                               + " and applied at load, so the FBX keeps agreeing "
                               + "with the .blend.");
                    EditorGUILayout.HelpBox(msg.ToString(), MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox("Already on the contract — no scale or yaw "
                                            + "correction needed.", MessageType.Info);
                }
            }
            EditorGUILayout.Space();
        }

        private void DrawNotes(TtExport x)
        {
            int fixes = x.geometryFixes.Length;
            int warns = x.textureWarnings.Length;
            if (fixes == 0 && warns == 0) return;

            EditorGUILayout.LabelField("Exporter notes", EditorStyles.boldLabel);
            foreach (string w in x.textureWarnings)
                EditorGUILayout.HelpBox(w, MessageType.Warning);

            if (fixes > 0)
            {
                _showFixes = EditorGUILayout.Foldout(_showFixes,
                    $"The exporter changed your geometry — {fixes} "
                    + (fixes == 1 ? "object" : "objects") + " repaired", true,
                    EditorStyles.foldoutHeader);
                if (_showFixes)
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.HelpBox(
                            "These repairs are ALREADY APPLIED in the FBX — nothing is "
                            + "broken. They are worth fixing at the source, because the "
                            + "next export of the same blend will repair them again.",
                            MessageType.Info);
                        foreach (string f in x.geometryFixes)
                            EditorGUILayout.LabelField("- " + f, EditorStyles.wordWrappedMiniLabel);
                    }
            }
            EditorGUILayout.Space();
        }

        private void DrawMaterials(TtExport x)
        {
            EditorGUILayout.LabelField($"Materials  ({x.materials.Length})",
                                       EditorStyles.boldLabel);
            foreach (TtMaterial m in x.materials)
            {
                if (m == null) continue;
                Rect row = EditorGUILayout.GetControlRect(false, RowHeight);
                var swatch = new Rect(row.x + 14f, row.y + 2f, 14f, 14f);
                var head = new Rect(row.x + 32f, row.y, row.width - 32f, row.height);

                bool open = _expanded.Contains(m.name);
                bool now = EditorGUI.Foldout(new Rect(row.x, row.y, 14f, row.height),
                                             open, GUIContent.none, true);
                if (now != open)
                {
                    if (now) _expanded.Add(m.name); else _expanded.Remove(m.name);
                }
                EditorGUI.DrawRect(swatch, m.Swatch);
                EditorGUI.LabelField(head, $"{m.name}    {m.ObjectCount} "
                                           + (m.ObjectCount == 1 ? "object" : "objects")
                                           + (m.baked ? "    baked" : ""));

                if (!now) continue;
                using (new EditorGUI.IndentLevelScope(2))
                {
                    if (m.baked)
                        EditorGUILayout.LabelField("Look",
                            "baked — the textures carry it; the flat values are absent");
                    else if (m.values != null)
                    {
                        EditorGUILayout.LabelField("Metallic", m.values.metallic.ToString("0.###"));
                        EditorGUILayout.LabelField("Smoothness",
                            m.values.Smoothness.ToString("0.###")
                            + $"   (roughness {m.values.roughness:0.###})");
                        if (m.values.emissionStrength > 0f)
                            EditorGUILayout.LabelField("Emission",
                                m.values.emissionStrength.ToString("0.###"));
                        if (m.values.alpha < 1f)
                            EditorGUILayout.LabelField("Alpha", m.values.alpha.ToString("0.###"));
                    }

                    if (m.maps != null && m.maps.Any)
                    {
                        Map("Albedo", m.maps.baseColor);
                        Map("Metallic/smoothness", m.maps.metallicSmoothness);
                        Map("Emission", m.maps.emission);
                        Map("Normal", m.maps.normal);
                    }

                    foreach (string o in m.objects ?? System.Array.Empty<string>())
                        EditorGUILayout.LabelField(" ", o, EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.Space();

            void Map(string label, string file)
            {
                if (!string.IsNullOrEmpty(file)) EditorGUILayout.LabelField(label, file);
            }
        }

        /// <summary>
        /// The child meshes. Two lists that should agree and do not have to: what
        /// the export SAYS the asset is made of, and what Unity actually imported.
        /// Showing both is how a renamed or dropped object becomes visible before
        /// it becomes a hole in a car.
        /// </summary>
        private void DrawObjects(AssetRow r, TtExport x)
        {
            List<Renderer> rends = r.Renderers();
            List<string> declared = x?.AllObjects() ?? new List<string>();
            if (rends.Count == 0 && declared.Count == 0) return;

            int n = Mathf.Max(rends.Count, declared.Count);
            _showObjects = EditorGUILayout.Foldout(_showObjects,
                $"Child meshes  ({n})", true, EditorStyles.foldoutHeader);
            if (!_showObjects) return;

            using (new EditorGUI.IndentLevelScope())
            {
                if (rends.Count > 0)
                {
                    EditorGUILayout.LabelField("Imported", EditorStyles.miniBoldLabel);
                    foreach (Renderer rd in rends)
                    {
                        int slots = rd.sharedMaterials?.Length ?? 0;
                        string mats = slots == 0 ? "no material slots"
                            : slots == 1 ? Name(rd.sharedMaterials[0])
                            : slots + " slots: " + string.Join(", ", Names(rd.sharedMaterials));
                        EditorGUILayout.LabelField(rd.gameObject.name, mats);
                    }
                    EditorGUILayout.HelpBox(
                        "Slot ORDER can only be read here, off the imported FBX — "
                        + "export.json names an object's materials but not which "
                        + "submesh each one occupies.", MessageType.None);
                }

                if (declared.Count > 0)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.LabelField("Declared by export.json",
                                               EditorStyles.miniBoldLabel);
                    foreach (string o in declared)
                    {
                        List<TtMaterial> on = x.MaterialsOn(o);
                        var names = new List<string>();
                        foreach (TtMaterial m in on) names.Add(m.name);
                        EditorGUILayout.LabelField(o, string.Join(", ", names));
                    }
                }
            }
        }

        private static string Name(Material m) => m == null ? "(none)" : m.name;

        private static List<string> Names(Material[] ms)
        {
            var list = new List<string>();
            foreach (Material m in ms) list.Add(Name(m));
            return list;
        }

        private static string Fmt(Vector3 v) =>
            $"{v.x:0.####} x {v.y:0.####} x {v.z:0.####}";
    }
}
