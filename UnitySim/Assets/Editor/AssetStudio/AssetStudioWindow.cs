using System.Collections.Generic;
using System.IO;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// The Asset Studio front end: browse everything the game can load beside
    /// everything Blender has exported, see it rendered the way the game will
    /// build it, and see what stands between the two.
    ///
    /// Still read-only about the ASSETS. The one thing it writes is a preview
    /// staging copy, and only when asked (see <see cref="AssetStudioStaging"/>):
    /// Unity cannot render a mesh it has not imported, so a picture of an
    /// un-imported export is the single part of this window that a copy is the
    /// price of. Nothing under <c>Resources/</c> is touched.
    ///
    /// The window's first job is to make the invisible failure visible — an
    /// export whose object names carry no token binds nothing and arrives as a
    /// correctly shaped car in one flat colour, with no error anywhere. The
    /// preview is where that stops being a paragraph and becomes a picture.
    ///
    /// Every action the window grows will also be a menu item, because the
    /// pipeline has to be runnable headlessly; the window exists to put the state
    /// of an asset in front of the author, which a menu cannot do. Authoring
    /// itself stays interactive: "assign M_Police_Chrome to Police_Handles slot 0"
    /// is not a menu item in any useful sense, and the draft asset is what carries
    /// an authored decision across to the headless half.
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
        [SerializeField] private bool _showPreview = true;
        [SerializeField] private float _previewHeight = 300f;
        [SerializeField] private PreviewOptions _preview = new PreviewOptions();

        // The rig itself cannot be serialized — a PreviewRenderUtility owns a
        // scene and a render texture, neither of which survives a domain reload.
        // It is rebuilt on demand and disposed in OnDisable, which is the call
        // that runs BEFORE a reload.
        private AssetStudioPreview _rig;
        private bool _orbiting, _dragged, _resizing;

        /// <summary>True only while the rig is showing the SELECTED asset. The rig
        /// keeps the last thing it loaded when the preview is collapsed or an
        /// export has nothing staged, and listing that asset's renderers under a
        /// different row would be a quietly wrong list.</summary>
        private bool _previewLive;

        private int _hoverRenderer = -1, _hoverSlot = -1;

        private const float ListWidth = 280f;
        private const float RowHeight = 18f;

        private static readonly Color SelectionTint = new Color(0.24f, 0.38f, 0.58f, 0.55f);
        private static readonly Color HoverTint = new Color(0.24f, 0.38f, 0.58f, 0.22f);

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

        private void OnEnable() => wantsMouseMove = true;
        private void OnFocus() => AssetStudioCatalog.Refresh();

        // Both, and both idempotent. OnDisable runs before a domain reload and
        // when the tab is merely hidden; OnDestroy runs when it is closed. Miss
        // either and Unity reports a leaked PreviewRenderUtility.
        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void Release()
        {
            _rig?.Dispose();
            _rig = null;
        }

        private void OnGUI()
        {
            // Hover is discovered by the rows themselves during Repaint, so it
            // has to start each repaint empty — otherwise the last row the mouse
            // crossed stays lit after the pointer has left the list entirely.
            if (Event.current.type == EventType.Repaint) _hoverRenderer = _hoverSlot = -1;

            DrawToolbar();
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawList();
                DrawDetail();
            }
            if (Event.current.type == EventType.MouseMove) Repaint();
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
                _showPreview = GUILayout.Toggle(_showPreview, "Preview",
                                                EditorStyles.toolbarButton, GUILayout.Width(60f));
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
                EditorGUI.DrawRect(rect, SelectionTint);

            var label = new Rect(rect.x, rect.y, rect.width - 58f, rect.height);
            var kind = new Rect(rect.xMax - 58f, rect.y, 58f, rect.height);
            EditorGUI.LabelField(label, r.label);
            EditorGUI.LabelField(kind, KindShort(r.kind), EditorStyles.miniLabel);

            if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
            {
                Select(r);
                GUI.FocusControl(null);
                Event.current.Use();
                Repaint();
            }
        }

        /// <summary>Selecting a different asset drops the mesh selection with it —
        /// "renderer 17" means nothing once the car underneath it changed.</summary>
        private void Select(AssetRow r)
        {
            _selectedKey = r.key;
            _selectedExportDir = r.exportDir;
            _preview.selectedRenderer = -1;
            _preview.selectedSlot = -1;
            _hoverRenderer = _hoverSlot = -1;
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

        // ==================== right: preview + detail ====================

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

                TtExport x = r.Export;
                _previewLive = false;
                if (_showPreview) DrawPreview(r, x);

                _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

                DrawIdentity(r);
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

                // Hover is computed against Repaint-time rects, so a change is
                // known one repaint after the mouse moved. Asking for another
                // repaint is what makes the highlight land where the pointer
                // stopped rather than one row behind it.
                if (_hoverRenderer != _preview.hoverRenderer || _hoverSlot != _preview.hoverSlot)
                {
                    _preview.hoverRenderer = _hoverRenderer;
                    _preview.hoverSlot = _hoverSlot;
                    Repaint();
                }
            }
        }

        // ---- the viewport ----

        private void DrawPreview(AssetRow r, TtExport x)
        {
            string path = PreviewPath(r, x, out string why);

            if (string.IsNullOrEmpty(path))
            {
                EditorGUILayout.HelpBox(why, MessageType.Info);
                using (new EditorGUI.DisabledScope(x == null))
                    if (GUILayout.Button("Stage for preview", GUILayout.Height(22f)))
                    {
                        string staged = AssetStudioStaging.Stage(x, out string p);
                        if (string.IsNullOrEmpty(staged)) AssetStudio.Error(p);
                        _rig?.Invalidate();
                    }
                EditorGUILayout.Space(4f);
                return;
            }

            _rig ??= new AssetStudioPreview();

            Rect view = GUILayoutUtility.GetRect(
                10f, 100000f, _previewHeight, _previewHeight, GUILayout.ExpandWidth(true));
            HandleViewport(view);
            _rig.Draw(view, r, _preview, path, x);
            _previewLive = _rig.Renderers.Count > 0;
            DrawViewportLabels(view, r);
            DrawSplitter();
            DrawPreviewControls(r, x);
        }

        /// <summary>
        /// Where the mesh Unity can actually render lives. Three answers, and the
        /// third is the interesting one: an export that is not in the project has
        /// no mesh object at all, and no amount of preview code changes that —
        /// the AssetDatabase does not reach outside <c>Assets/</c>.
        /// </summary>
        private static string PreviewPath(AssetRow r, TtExport x, out string why)
        {
            why = "";
            if (r.HasProject) return r.fbxAssetPath;
            if (x == null)
            {
                why = "Nothing to preview: this row has no imported model and no "
                    + "readable export.";
                return "";
            }
            if (AssetStudioStaging.IsStaged(x)) return AssetStudioStaging.PathFor(x);

            why = "This export is not in the project, and Unity can only render a mesh "
                + "it has imported.\n\n\"Stage for preview\" copies just the FBX into "
                + AssetStudio.StagingDir + " — outside Resources/, so it cannot become "
                + "a key the game finds, and Tools > Asset Studio > Clear preview "
                + "staging removes it again. Committing the asset later copies from "
                + "the export, never from staging.";
            return "";
        }

        private void HandleViewport(Rect view)
        {
            Event e = Event.current;
            bool inside = view.Contains(e.mousePosition);

            switch (e.type)
            {
                case EventType.MouseDown when inside && e.button == 0:
                    _orbiting = true;
                    _dragged = false;
                    GUI.FocusControl(null);
                    e.Use();
                    break;

                case EventType.MouseDrag when _orbiting:
                    _preview.yaw -= e.delta.x * 0.6f;
                    _preview.pitch = Mathf.Clamp(_preview.pitch + e.delta.y * 0.6f, -89f, 89f);
                    if (e.delta.sqrMagnitude > 1f) _dragged = true;
                    Repaint();
                    e.Use();
                    break;

                case EventType.MouseUp when _orbiting:
                    // A click that did not turn the camera is a pick. Bounds
                    // level: PartModelPostprocessor leaves isReadable off for
                    // everything but shells, props and the Tiguan's wheels, so
                    // there is no CPU mesh to test triangles against.
                    if (!_dragged && inside && _rig != null)
                    {
                        int hit = _rig.Pick(_rig.RayThrough(view, e.mousePosition));
                        _preview.selectedRenderer =
                            hit == _preview.selectedRenderer ? -1 : hit;
                        _preview.selectedSlot = -1;
                    }
                    _orbiting = false;
                    Repaint();
                    e.Use();
                    break;

                case EventType.ScrollWheel when inside:
                    _preview.zoom = Mathf.Clamp(_preview.zoom * (1f - e.delta.y * 0.05f),
                                                0.15f, 14f);
                    Repaint();
                    e.Use();
                    break;
            }
        }

        private void DrawViewportLabels(Rect view, AssetRow r)
        {
            if (Event.current.type != EventType.Repaint || !_previewLive) return;

            Vector3 size = _rig.WorldBounds.size;
            float target = AssetStudioPreview.RulerLength(r);
            string kindLine = r.kind == AssetKind.Wheel
                ? $"ruler {target * 1000f:0} mm diameter"
                : $"ruler {target:0.###} m";

            var box = new Rect(view.x + 6f, view.yMax - 38f, view.width - 12f, 32f);
            EditorGUI.DropShadowLabel(box,
                $"{Fmt(size)} m rendered      {kindLine}      "
                + $"{_rig.Renderers.Count} renderers / {_rig.SlotCount} slots");
        }

        private void DrawSplitter()
        {
            Rect sp = GUILayoutUtility.GetRect(10f, 100000f, 5f, 5f);
            EditorGUI.DrawRect(sp, new Color(0f, 0f, 0f, 0.20f));
            EditorGUIUtility.AddCursorRect(sp, MouseCursor.ResizeVertical);

            Event e = Event.current;
            if (e.type == EventType.MouseDown && sp.Contains(e.mousePosition))
            {
                _resizing = true;
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseDrag)
            {
                _previewHeight = Mathf.Clamp(_previewHeight + e.delta.y, 140f, 1200f);
                Repaint();
                e.Use();
            }
            else if (_resizing && e.type == EventType.MouseUp)
            {
                _resizing = false;
                e.Use();
            }
        }

        // ---- the controls under it ----

        private void DrawPreviewControls(AssetRow r, TtExport x)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _preview.table = (TokenTable)EditorGUILayout.EnumPopup(
                    _preview.table, EditorStyles.toolbarPopup, GUILayout.Width(110f));

                bool canCorrect = x != null && x.UnityDims != Vector3.zero;
                using (new EditorGUI.DisabledScope(!canCorrect))
                    _preview.applyCorrection = GUILayout.Toggle(
                        canCorrect && _preview.applyCorrection, "Apply correction",
                        EditorStyles.toolbarButton, GUILayout.Width(110f));

                _preview.showRuler = GUILayout.Toggle(_preview.showRuler, "Ruler",
                    EditorStyles.toolbarButton, GUILayout.Width(48f));
                _preview.isolate = GUILayout.Toggle(_preview.isolate, "Isolate",
                    EditorStyles.toolbarButton, GUILayout.Width(54f));

                if (GUILayout.Button("Reset view", EditorStyles.toolbarButton,
                                     GUILayout.Width(76f)))
                {
                    _preview.yaw = 135f;
                    _preview.pitch = 16f;
                    _preview.zoom = 1f;
                }

                GUILayout.FlexibleSpace();

                if (x != null && !r.HasProject
                    && GUILayout.Button("Re-stage", EditorStyles.toolbarButton,
                                        GUILayout.Width(70f)))
                {
                    AssetStudioStaging.Stage(x, out string p);
                    if (!string.IsNullOrEmpty(p)) AssetStudio.Error(p);
                    _rig?.Invalidate();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (r.kind == AssetKind.Wheel)
                {
                    EditorGUILayout.LabelField("Radius", GUILayout.Width(48f));
                    _preview.wheelRadius = EditorGUILayout.Slider(
                        _preview.wheelRadius, 0.01f, 0.5f);
                    _preview.rightSide = GUILayout.Toggle(_preview.rightSide,
                        _preview.rightSide ? "R" : "L", EditorStyles.miniButton,
                        GUILayout.Width(26f));
                    if (GUILayout.Button("Author", EditorStyles.miniButton, GUILayout.Width(56f)))
                        _preview.wheelRadius = AssetStudioPreview.AuthorRadiusFor(r.key);
                }
                else
                {
                    EditorGUILayout.LabelField("bodySize", GUILayout.Width(58f));
                    _preview.bodySize = EditorGUILayout.Vector3Field(GUIContent.none,
                                                                    _preview.bodySize);
                    if (GUILayout.Button("Nominal", EditorStyles.miniButton, GUILayout.Width(60f)))
                        _preview.bodySize = CarVehicle.BodyMeshAuthorSize;
                    _preview.bodyColor = EditorGUILayout.ColorField(GUIContent.none,
                        _preview.bodyColor, false, false, false, GUILayout.Width(44f));
                }
            }

            DrawPreviewNotes(r, x);
            EditorGUILayout.Space(2f);
        }

        /// <summary>
        /// The two things the preview cannot promise, said where they matter
        /// rather than buried in a doc comment: which assets bind through a table
        /// the preview actually has, and what a null material slot means.
        /// </summary>
        private void DrawPreviewNotes(AssetRow r, TtExport x)
        {
            if (!AssetStudioPreview.TableIsExact(r.kind))
                EditorGUILayout.HelpBox(
                    "Approximate binding. Bodies, wheels and cosmetics bind through the "
                    + "tables the preview is showing; the battery, antennas, light bars "
                    + "and track props bind from small tables written inline at their own "
                    + "call sites, which are not exposed. Shape and scale are exact "
                    + "either way.", MessageType.None);

            if (_rig != null && _rig.RenderersWithEmptySlots > 0)
                EditorGUILayout.HelpBox(
                    $"{_rig.RenderersWithEmptySlots} of {_rig.Renderers.Count} renderers "
                    + "still have an empty material slot. The token binder assigns "
                    + "sharedMaterial — slot 0 and slot 0 only — so a multi-submesh "
                    + "object gets its first material and nothing else. Binding per slot "
                    + "is what the manifest path is for.", MessageType.Warning);

            if (_preview.applyCorrection && x != null)
                EditorGUILayout.HelpBox(
                    "Showing the export WITH the proposed uniform scale and quarter turn "
                    + "pre-applied. That correction is a proposal about the mesh — nothing "
                    + "on disk has changed, and the game would still render this asset the "
                    + "way it looks with the toggle off.", MessageType.None);
        }

        // ==================== detail sections ====================

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
                               + "with the .blend. \"Apply correction\" above the "
                               + "viewport shows the result.");
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
        ///
        /// The imported list drives the preview. Hovering a row lights that mesh
        /// up in the viewport, clicking pins it, and a multi-submesh object opens
        /// into one row per SLOT — which is the granularity a car binds at and the
        /// granularity the token binder cannot reach.
        /// </summary>
        private void DrawObjects(AssetRow r, TtExport x)
        {
            IReadOnlyList<Renderer> rends = LiveRenderers(r);
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
                    for (int i = 0; i < rends.Count; i++) DrawMeshRow(i, rends[i]);
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

        /// <summary>
        /// The renderers to list: the preview's instance when one is up, because
        /// those carry the materials BINDING produced, and the imported prefab's
        /// otherwise. Both walk the same hierarchy with the same call, so index i
        /// means the same mesh either way — which is what lets a row select into
        /// the viewport.
        /// </summary>
        private IReadOnlyList<Renderer> LiveRenderers(AssetRow r) =>
            _previewLive ? _rig.Renderers : r.Renderers();

        private void DrawMeshRow(int i, Renderer rd)
        {
            if (rd == null) return;
            Material[] mats = _previewLive ? _rig.BoundMaterials(i) : rd.sharedMaterials;
            if (mats == null || mats.Length == 0) mats = new Material[] { null };

            bool multi = mats.Length > 1;
            string summary = multi ? mats.Length + " slots" : Name(mats[0]);
            Row(i, -1, rd.gameObject.name, summary, EditorStyles.label);

            if (!multi) return;
            for (int s = 0; s < mats.Length; s++)
                Row(i, s, "    slot " + s, Name(mats[s]), EditorStyles.miniLabel);
        }

        private void Row(int rendererIndex, int slot, string label, string value, GUIStyle style)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, RowHeight);
            bool pinned = _preview.selectedRenderer == rendererIndex
                          && _preview.selectedSlot == slot;

            if (Event.current.type == EventType.Repaint)
            {
                if (pinned) EditorGUI.DrawRect(rect, SelectionTint);
                if (rect.Contains(Event.current.mousePosition))
                {
                    if (!pinned) EditorGUI.DrawRect(rect, HoverTint);
                    _hoverRenderer = rendererIndex;
                    _hoverSlot = slot;
                }
            }

            float half = Mathf.Max(120f, rect.width * 0.45f);
            EditorGUI.LabelField(new Rect(rect.x, rect.y, half, rect.height), label, style);
            EditorGUI.LabelField(new Rect(rect.x + half, rect.y, rect.width - half, rect.height),
                                 value, EditorStyles.miniLabel);

            if (Event.current.type == EventType.MouseDown
                && rect.Contains(Event.current.mousePosition))
            {
                if (pinned) { _preview.selectedRenderer = -1; _preview.selectedSlot = -1; }
                else { _preview.selectedRenderer = rendererIndex; _preview.selectedSlot = slot; }
                Event.current.Use();
                Repaint();
            }
        }

        private static string Name(Material m) => m == null ? "(no material)" : m.name;

        private static string Fmt(Vector3 v) =>
            $"{v.x:0.####} x {v.y:0.####} x {v.z:0.####}";
    }
}
