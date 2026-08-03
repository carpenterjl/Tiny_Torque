using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>
    /// The draft editing panel: identity, geometry, materials and per-slot mesh
    /// binding with damage authoring.
    ///
    /// <b>Every edit goes through <c>Undo.RecordObject</c> before the assignment.</b>
    /// That ordering is the whole trick — the object still holds the OLD value at
    /// the moment it is recorded, so the undo stack gets the state to return to.
    /// Recording after assigning would push the new value twice and Ctrl+Z would
    /// do nothing, which is worse than no undo at all because it looks like it
    /// worked.
    ///
    /// A serializable class rather than a static helper so its scroll positions,
    /// search text and open foldouts live in the window's <c>[SerializeField]</c>
    /// state and survive a domain reload with everything else.
    /// </summary>
    [System.Serializable]
    public sealed class AssetStudioDraftEditor
    {
        [SerializeField] private Vector2 _matScroll, _objScroll;
        [SerializeField] private string _objSearch = "";
        [SerializeField] private bool _groupByMaterial;
        [SerializeField] private List<string> _openMaterials = new List<string>();
        [SerializeField] private List<string> _openObjects = new List<string>();
        [SerializeField] private bool _showHeader = true, _showMaterials = true,
                                      _showObjects = true, _showCommit = true;

        private const float Row = 18f;

        /// <summary>What one laid-out line actually consumes: the row plus
        /// <c>EditorGUIUtility.standardVerticalSpacing</c>. The virtual list's
        /// spacers are computed from this, and getting it wrong shows up as the
        /// content sliding under the scrollbar.</summary>
        private const float Pitch = Row + 2f;

        private const float MatViewHeight = 190f;
        private const float ObjViewHeight = 260f;

        private static readonly Color HeaderTint = new Color(1f, 1f, 1f, 0.06f);
        private static readonly Color WarnTint = new Color(0.95f, 0.55f, 0.10f, 0.20f);
        private static readonly Color HoverTint = new Color(0.24f, 0.38f, 0.58f, 0.22f);
        private static readonly Color SelectTint = new Color(0.24f, 0.38f, 0.58f, 0.55f);

        private static string[] _kindNames;
        private static string[] KindNames =>
            _kindNames ??= System.Enum.GetNames(typeof(AssetKind));

        /// <summary>Renderer index the mouse is over this repaint, or -1. Read by
        /// the window and handed to the preview.</summary>
        public int HoverRenderer { get; private set; } = -1;

        /// <summary>Submesh slot the mouse is over, or -1 for the whole mesh.</summary>
        public int HoverSlot { get; private set; } = -1;

        // Foldout changes are DEFERRED to the end of the pass rather than applied
        // where they are clicked. Opening a row changes how many controls the rest
        // of this list emits, and IMGUI allocates that count during the Layout
        // event — so mutating it mid-pass is how "Getting control 7's position in
        // a group with only 6 controls" happens. Nothing here changes shape until
        // the pass is over and the next Layout can measure it.
        private string _pendingObject, _pendingMaterial;
        private bool _pendingObjectOn, _pendingMaterialOn;
        private bool _pendingExpandAll, _pendingCollapseAll;

        public void Draw(AssetStudioDraft draft, AssetRow row, PreviewOptions preview,
                         IReadOnlyList<Renderer> renderers)
        {
            HoverRenderer = -1;
            HoverSlot = -1;
            if (draft == null) return;

            DrawHeader(draft, row);
            DrawMaterials(draft, preview);
            DrawObjects(draft, preview, renderers);
            DrawCommit(draft, row);

            if (_pendingObject != null)
            {
                Toggle(_openObjects, _pendingObject, _pendingObjectOn);
                _pendingObject = null;
            }
            if (_pendingMaterial != null)
            {
                Toggle(_openMaterials, _pendingMaterial, _pendingMaterialOn);
                _pendingMaterial = null;
            }
            if (_pendingExpandAll)
            {
                foreach (DraftObject o in draft.objects) Toggle(_openObjects, o.name, true);
                _pendingExpandAll = false;
            }
            if (_pendingCollapseAll)
            {
                _openObjects.Clear();
                _pendingCollapseAll = false;
            }
        }

        // ==================== identity and geometry ====================

        private void DrawHeader(AssetStudioDraft draft, AssetRow row)
        {
            _showHeader = Foldout(_showHeader, "Draft");
            if (!_showHeader) return;

            using (new EditorGUI.IndentLevelScope())
            {
                StringRow(draft, "Key", draft.key, v => draft.key = v);
                EditorGUILayout.HelpBox(
                    "The key IS the file name the asset is committed under, and the "
                    + "game asks Resources for it by that stem alone. CarVehicle only "
                    + "ever builds \"body_*\" keys and BuildWheelViz only ever builds "
                    + "\"wheel_*\" — the prefix is a requirement, not a habit.",
                    MessageType.None);

                int kindIdx = Mathf.Max(0, System.Array.IndexOf(KindNames, draft.kind));
                EditorGUI.BeginChangeCheck();
                int newKind = EditorGUILayout.Popup("Kind", kindIdx, KindNames);
                if (EditorGUI.EndChangeCheck() && newKind != kindIdx)
                {
                    Rec(draft, "kind");
                    draft.kind = KindNames[newKind];
                    Dirty(draft);
                }

                int modeIdx = Mathf.Max(0, System.Array.IndexOf(
                    DraftMaterialModes.All, draft.materialMode));
                EditorGUI.BeginChangeCheck();
                int newMode = EditorGUILayout.Popup("Materials", modeIdx,
                                                    DraftMaterialModes.All);
                if (EditorGUI.EndChangeCheck() && newMode != modeIdx)
                {
                    Rec(draft, "material mode");
                    draft.materialMode = DraftMaterialModes.All[newMode];
                    Dirty(draft);
                }

                if (draft.materialMode == DraftMaterialModes.Verbatim)
                    EditorGUILayout.HelpBox(
                        "Verbatim keeps the FBX's own .mat assets, exactly as Blender "
                        + "wrote them. The costs are real and are not hidden: the game "
                        + "cannot tint this body, so garage paint mode stands down for "
                        + "it, and the material list below becomes a record rather than "
                        + "a control.", MessageType.Info);

                EditorGUILayout.LabelField("From", string.IsNullOrEmpty(draft.sourceAssetName)
                    ? "(not synced)" : draft.sourceAssetName + "   " + draft.sourceExportedAtUtc);

                EditorGUILayout.Space(2f);
                DrawRegistry(draft);
                EditorGUILayout.Space(2f);
                DrawGeometry(draft, row);
                EditorGUILayout.Space(2f);
                DrawActions(draft, row);
            }
            EditorGUILayout.Space(4f);
        }

        /// <summary>
        /// The handful of facts a catalogue row has that an FBX cannot carry.
        ///
        /// Short on purpose. Everything else about a committed row is derived —
        /// the mesh key IS the key, the paintable flag is whether any material
        /// claims the paint channel, the label falls back to the key — so what is
        /// asked for here is only what genuinely cannot be measured or inferred.
        /// A drag coefficient is the whole of that list for a body, which is why
        /// the commit refuses without one rather than reaching for a default.
        /// </summary>
        private void DrawRegistry(AssetStudioDraft draft)
        {
            StringRow(draft, "Label", draft.label, v => draft.label = v);

            AssetKind kind = draft.Kind;
            if (kind == AssetKind.CarBody)
            {
                EditorGUI.BeginChangeCheck();
                float cd = EditorGUILayout.FloatField("Drag cd", draft.cd);
                if (EditorGUI.EndChangeCheck()) { Rec(draft, "cd"); draft.cd = cd; Dirty(draft); }

                EditorGUI.BeginChangeCheck();
                float clA = EditorGUILayout.FloatField("Downforce clA (m2)", draft.clA);
                if (EditorGUI.EndChangeCheck()) { Rec(draft, "clA"); draft.clA = clA; Dirty(draft); }

                if (draft.cd < 0f)
                    EditorGUILayout.HelpBox(
                        "No drag coefficient yet, and the commit will refuse without one. "
                        + "It cannot be measured off geometry and the game will not run "
                        + "without it: 0.15 is a teardrop, 0.45-0.6 a saloon, 0.8-0.95 a "
                        + "bluff off-roader or a slab-sided wrecker, 1.2 a flat plate "
                        + "broadside. clA is what the SHELL does with no parts on it, so "
                        + "zero is the honest answer unless a wing is modelled in.",
                        MessageType.Warning);
            }
            else if (kind == AssetKind.Wheel)
            {
                EditorGUI.BeginChangeCheck();
                bool offered = EditorGUILayout.Toggle("Offered in garage", draft.garageOffered);
                if (EditorGUI.EndChangeCheck())
                { Rec(draft, "garageOffered"); draft.garageOffered = offered; Dirty(draft); }
            }
            else if (kind == AssetKind.Cosmetic)
            {
                EnumRow(draft, "Slot", draft.cosmeticSlot,
                        typeof(Garage.CosmeticSlot), v => draft.cosmeticSlot = v);
                EnumRow(draft, "Rarity", draft.cosmeticRarity,
                        typeof(Garage.Rarity), v => draft.cosmeticRarity = v);
                EnumRow(draft, "Theme", draft.cosmeticTheme,
                        typeof(Garage.CosmeticTheme), v => draft.cosmeticTheme = v);
                StringRow(draft, "Blurb", draft.description, v => draft.description = v);
                EditorGUILayout.HelpBox(
                    "Scrap value and shop price are NOT authored here — they come from "
                    + "the rarity, through the same table the 47 shipped cosmetics use. "
                    + "A new hat must not be able to reprice the economy. There is no "
                    + "cheat code either, matching every cosmetic already in the pack.",
                    MessageType.None);
            }
        }

        /// <summary>An enum-valued string field: a popup over the names, storing
        /// the NAME. Strings all the way down, for the reason the manifest gives
        /// about ordinals reordering silently.</summary>
        private static void EnumRow(AssetStudioDraft d, string label, string value,
                                    System.Type type, System.Action<string> set)
        {
            string[] names = System.Enum.GetNames(type);
            int idx = Mathf.Max(0, System.Array.IndexOf(names, value));
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUILayout.Popup(label, idx, names);
            if (!EditorGUI.EndChangeCheck() || picked == idx) return;
            Rec(d, label);
            set(names[picked]);
            Dirty(d);
        }

        private void DrawGeometry(AssetStudioDraft draft, AssetRow row)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 size = EditorGUILayout.Vector3Field("authorSize", draft.authorSize);
            if (EditorGUI.EndChangeCheck())
            {
                Rec(draft, "authorSize");
                draft.authorSize = size;
                Dirty(draft);
            }

            EditorGUI.BeginChangeCheck();
            float yaw = EditorGUILayout.FloatField("authorYawDeg", draft.authorYawDeg);
            if (EditorGUI.EndChangeCheck())
            {
                Rec(draft, "authorYawDeg");
                draft.authorYawDeg = yaw;
                Dirty(draft);
            }

            if (Mathf.Abs(Mathf.Repeat(draft.authorYawDeg, 90f)) > 0.01f)
                EditorGUILayout.HelpBox(
                    "authorYawDeg must be a multiple of 90. A bounding box does not "
                    + "survive an arbitrary rotation, so authorSize would stop meaning "
                    + "anything the moment this is 37 degrees.", MessageType.Error);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(row?.Export == null))
                    if (GUILayout.Button("Propose from export.json", GUILayout.Height(20f)))
                        AssetStudioDrafts.Propose(draft, row);
                EditorGUILayout.LabelField(
                    draft.authorScale > 0f && Mathf.Abs(draft.authorScale - 1f) > 1e-4f
                        ? $"uniform x{draft.authorScale:0.#####}  (1/{1f / draft.authorScale:0.###})"
                        : "uniform x1", EditorStyles.miniLabel, GUILayout.Width(190f));
            }

            EditorGUILayout.HelpBox(
                "One uniform factor, never a per-axis fit. The old exporter scaled "
                + "every shell to length 0.420 with a single number, which is why "
                + "PartModelValidator pins those bodies' length and leaves their width "
                + "free — CarVehicle.BodyMeshAuthorSize is a nominal divisor, not a "
                + "measurement of any shell, and fitting a mesh to it axis by axis "
                + "would change proportions nothing ever changed.", MessageType.None);
        }

        private void DrawActions(AssetStudioDraft draft, AssetRow row)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(row?.Export == null))
                    if (GUILayout.Button("Sync from export", GUILayout.Height(22f)))
                        AssetStudioDrafts.Sync(draft, row);
                if (GUILayout.Button("Save draft", GUILayout.Height(22f)))
                    AssetStudioDrafts.Save(draft);
                if (GUILayout.Button("Select draft asset", GUILayout.Height(22f)))
                    Selection.activeObject = draft;
            }

            int unverified = draft.UnverifiedSlots();
            int dangling = draft.DanglingSlots();
            if (unverified > 0)
                EditorGUILayout.HelpBox(
                    $"{unverified} multi-slot object{(unverified == 1 ? "" : "s")} still "
                    + "carry a PROPOSED slot order. The import gives the slot count and "
                    + "nothing else — materialImportMode is None, so every slot arrives "
                    + "null — and export.json lists an object's materials in file order, "
                    + "not slot order. Check each one in the preview and tick it.",
                    MessageType.Warning);
            if (dangling > 0)
                EditorGUILayout.HelpBox(
                    $"{dangling} slot{(dangling == 1 ? " points" : "s point")} at a "
                    + "material this draft does not have — usually a rename in Blender "
                    + "since the last sync.", MessageType.Error);
        }

        // ==================== commit ====================

        /// <summary>
        /// The one panel in this window that writes to <c>Resources/</c>.
        ///
        /// It leads with the refusals rather than with the button, because the
        /// interesting state of a draft that is not ready is WHY, and a greyed-out
        /// button that will not say is the version of this screen nobody can use.
        /// </summary>
        private void DrawCommit(AssetStudioDraft draft, AssetRow row)
        {
            _showCommit = Foldout(_showCommit, "Commit");
            if (!_showCommit) return;

            using (new EditorGUI.IndentLevelScope())
            {
                TtExport x = row?.Export;
                CommitState state = AssetStudioCommit.StateOf(draft.Kind, draft.key, x);
                EditorGUILayout.LabelField("State", AssetStudioCommit.Describe(state));
                EditorGUILayout.LabelField("Destination",
                    AssetStudioCommit.FbxPathFor(draft.Kind, draft.key));

                if (state == CommitState.SourceDrifted)
                    EditorGUILayout.HelpBox(
                        "The export has changed since this was committed. Sync the draft "
                        + "first, then commit: sync keeps every damage tag and slot mapping "
                        + "by OBJECT NAME and reports anything the export no longer has, "
                        + "rather than dropping forty hand-set tags in silence.",
                        MessageType.Warning);
                if (state == CommitState.ProjectEdited)
                    EditorGUILayout.HelpBox(
                        "The FBX under Resources/ no longer matches what was committed — "
                        + "somebody edited the copy. Committing again will overwrite it "
                        + "with the export's version.", MessageType.Warning);

                // The override, and its reason, sit together because they are one
                // decision. The reason is mandatory, is written into the manifest,
                // and is printed by every [AST] run — an override that goes quiet
                // is just a gate somebody switched off.
                bool failed = x?.verification != null && !x.verification.passed;
                if (failed || draft.verificationOverridden)
                {
                    EditorGUI.BeginChangeCheck();
                    bool over = EditorGUILayout.Toggle("Override verification",
                                                       draft.verificationOverridden);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Rec(draft, "verification override");
                        draft.verificationOverridden = over;
                        Dirty(draft);
                    }
                    if (draft.verificationOverridden)
                    {
                        StringRow(draft, "Reason", draft.overrideReason,
                                  v => draft.overrideReason = v);
                        EditorGUILayout.HelpBox(
                            "This reason is written into the manifest and printed on every "
                            + "[AST] run for as long as the override lasts.",
                            MessageType.Warning);
                    }
                }

                string refusal = AssetStudioCommit.Refusal(draft, row);
                if (!string.IsNullOrEmpty(refusal))
                    EditorGUILayout.HelpBox("Not ready: " + refusal, MessageType.Error);

                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(refusal)))
                    if (GUILayout.Button("Commit to " + AssetStudio.RootName(
                            AssetStudioCommit.RootFor(draft.Kind)), GUILayout.Height(24f)))
                    {
                        AssetStudioDrafts.Save(draft);
                        AssetStudioCommit.Result res = AssetStudioCommit.Commit(draft, row);
                        if (res.ok) AssetStudio.Log(AssetStudioCommit.Line(res));
                        else AssetStudio.Error(draft.key + ": " + res.problem);
                        AssetStudioCatalog.Refresh();
                    }
            }
            EditorGUILayout.Space(4f);
        }

        // ==================== materials ====================

        private void DrawMaterials(AssetStudioDraft draft, PreviewOptions preview)
        {
            _showMaterials = Foldout(_showMaterials,
                $"Materials  ({draft.materials.Count})");
            if (!_showMaterials) return;

            _matScroll = EditorGUILayout.BeginScrollView(
                _matScroll, GUILayout.Height(MatViewHeight));

            foreach (DraftMaterial m in draft.materials)
            {
                if (m == null) continue;
                bool open = _openMaterials.Contains(m.name);

                Rect r = EditorGUILayout.GetControlRect(false, Row);
                bool now = EditorGUI.Foldout(new Rect(r.x, r.y, 14f, r.height),
                                             open, GUIContent.none, true);
                if (now != open) { _pendingMaterial = m.name; _pendingMaterialOn = now; }

                EditorGUI.DrawRect(new Rect(r.x + 16f, r.y + 2f, 14f, 14f), Swatch(m));

                var nameRect = new Rect(r.x + 34f, r.y, Mathf.Max(120f, r.width - 200f), r.height);
                EditorGUI.LabelField(nameRect, m.name + (m.baked ? "   baked" : ""));

                var paintRect = new Rect(r.xMax - 160f, r.y, 160f, r.height);
                EditorGUI.BeginChangeCheck();
                bool paint = EditorGUI.ToggleLeft(paintRect, "paint channel (tintable)",
                                                  m.paintChannel);
                if (EditorGUI.EndChangeCheck())
                {
                    Rec(draft, "paint channel");
                    m.paintChannel = paint;
                    Dirty(draft);
                }

                if (!now) continue;
                using (new EditorGUI.IndentLevelScope(2))
                {
                    if (m.baked)
                        EditorGUILayout.HelpBox(
                            "Baked: the textures carry the look and the export gave no "
                            + "flat values. Marked as the paint channel by default so "
                            + "bodyColor MULTIPLIES the livery — the artwork survives "
                            + "and the car can still be themed.", MessageType.None);

                    ColorRow(draft, "Albedo", m.rgb, v => m.rgb = v);
                    SliderRow(draft, "Metallic", m.metallic, v => m.metallic = v);
                    SliderRow(draft, "Smoothness", m.smoothness, v => m.smoothness = v);
                    SliderRow(draft, "Alpha", m.alpha, v => m.alpha = v);
                    ColorRow(draft, "Emission", m.emission, v => m.emission = v);
                    FloatRow(draft, "Emission strength", m.emissionStrength,
                             v => m.emissionStrength = Mathf.Max(0f, v));

                    if (m.HasMaps)
                    {
                        EditorGUILayout.LabelField("Maps", EditorStyles.miniBoldLabel);
                        Map("Albedo", m.mapAlbedo);
                        Map("Metallic/smoothness", m.mapMetallicSmoothness);
                        Map("Emission", m.mapEmission);
                        Map("Normal", m.mapNormal);
                    }

                    EditorGUILayout.LabelField("Used by",
                        UsageCount(draft, m.name) + " slots", EditorStyles.miniLabel);
                }
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(4f);

            void Map(string label, string file)
            {
                if (!string.IsNullOrEmpty(file))
                    EditorGUILayout.LabelField(label, file, EditorStyles.miniLabel);
            }
        }

        private static int UsageCount(AssetStudioDraft draft, string materialName)
        {
            int n = 0;
            foreach (DraftObject o in draft.objects)
            {
                if (o?.slots == null) continue;
                foreach (string s in o.slots) if (s == materialName) n++;
            }
            return n;
        }

        private static Color Swatch(DraftMaterial m) =>
            m.baked ? new Color(0.5f, 0.5f, 0.5f) : m.rgb.gamma;

        // ==================== objects, per slot ====================

        /// <summary>One line in the object list: either a group header or an
        /// object, with the height it will take so the list can be culled without
        /// laying every row out.</summary>
        private struct Entry
        {
            public string header;      // null for an object row
            public DraftObject obj;
            public int rendererIndex;
            public float height;
        }

        private void DrawObjects(AssetStudioDraft draft, PreviewOptions preview,
                                 IReadOnlyList<Renderer> renderers)
        {
            _showObjects = Foldout(_showObjects, $"Child meshes  ({draft.objects.Count})");
            if (!_showObjects) return;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _objSearch = GUILayout.TextField(_objSearch, EditorStyles.toolbarSearchField,
                                                 GUILayout.Width(180f));
                _groupByMaterial = GUILayout.Toggle(_groupByMaterial, "Group by material",
                    EditorStyles.toolbarButton, GUILayout.Width(120f));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Expand all", EditorStyles.toolbarButton, GUILayout.Width(72f)))
                    _pendingExpandAll = true;
                if (GUILayout.Button("Collapse all", EditorStyles.toolbarButton, GUILayout.Width(80f)))
                    _pendingCollapseAll = true;
            }

            List<Entry> entries = BuildEntries(draft, renderers);

            _objScroll = EditorGUILayout.BeginScrollView(
                _objScroll, GUILayout.Height(ObjViewHeight));

            // Virtualised: only the rows the scroll window actually shows are laid
            // out, with one spacer standing in for everything above and one for
            // everything below. With 41 objects this is a nicety; with a destroyed
            // city block of props it is the difference between a list and a stall.
            float top = 0f, y = 0f;
            int first = 0, last = entries.Count - 1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (y + entries[i].height < _objScroll.y) { first = i + 1; top = y + entries[i].height; }
                if (y > _objScroll.y + ObjViewHeight) { last = i - 1; break; }
                y += entries[i].height;
            }
            float bottom = 0f;
            for (int i = last + 1; i < entries.Count; i++) bottom += entries[i].height;

            if (top > 0f) GUILayout.Space(top);
            for (int i = first; i <= last && i < entries.Count; i++)
            {
                if (entries[i].header != null) DrawGroupHeader(entries[i].header);
                else DrawObjectRow(draft, entries[i], preview);
            }
            if (bottom > 0f) GUILayout.Space(bottom);

            EditorGUILayout.EndScrollView();
        }

        private List<Entry> BuildEntries(AssetStudioDraft draft,
                                         IReadOnlyList<Renderer> renderers)
        {
            var entries = new List<Entry>();
            if (_groupByMaterial)
            {
                foreach (DraftMaterial m in draft.materials)
                {
                    var members = new List<DraftObject>();
                    foreach (DraftObject o in draft.objects)
                        if (Passes(o) && o.slots.Contains(m.name)) members.Add(o);
                    if (members.Count == 0) continue;
                    entries.Add(new Entry { header = m.name + $"   ({members.Count})", height = Pitch });
                    foreach (DraftObject o in members) entries.Add(ObjectEntry(o, renderers));
                }
                var loose = new List<DraftObject>();
                foreach (DraftObject o in draft.objects)
                    if (Passes(o) && !AnyMapped(o)) loose.Add(o);
                if (loose.Count > 0)
                {
                    entries.Add(new Entry { header = $"unassigned   ({loose.Count})", height = Pitch });
                    foreach (DraftObject o in loose) entries.Add(ObjectEntry(o, renderers));
                }
            }
            else
            {
                foreach (DraftObject o in draft.objects)
                    if (Passes(o)) entries.Add(ObjectEntry(o, renderers));
            }
            return entries;

            bool Passes(DraftObject o) =>
                o != null && (string.IsNullOrEmpty(_objSearch)
                    || o.name.IndexOf(_objSearch, System.StringComparison.OrdinalIgnoreCase) >= 0);

            bool AnyMapped(DraftObject o)
            {
                foreach (string s in o.slots) if (!string.IsNullOrEmpty(s)) return true;
                return false;
            }
        }

        private Entry ObjectEntry(DraftObject o, IReadOnlyList<Renderer> renderers)
        {
            bool open = _openObjects.Contains(o.name);
            // header row + (one per slot, verified toggle, role, health, group)
            float h = Pitch + (open ? Pitch * (o.slots.Count + 4) : 0f);
            return new Entry { obj = o, height = h, rendererIndex = IndexOf(renderers, o.name) };
        }

        private static int IndexOf(IReadOnlyList<Renderer> renderers, string name)
        {
            if (renderers == null) return -1;
            for (int i = 0; i < renderers.Count; i++)
                if (renderers[i] != null && renderers[i].gameObject.name == name) return i;
            return -1;
        }

        private static void DrawGroupHeader(string text)
        {
            Rect r = EditorGUILayout.GetControlRect(false, Row);
            EditorGUI.DrawRect(r, HeaderTint);
            EditorGUI.LabelField(r, text, EditorStyles.miniBoldLabel);
        }

        private void DrawObjectRow(AssetStudioDraft draft, Entry e, PreviewOptions preview)
        {
            DraftObject o = e.obj;
            bool open = _openObjects.Contains(o.name);
            bool pinned = preview.selectedRenderer >= 0
                          && preview.selectedRenderer == e.rendererIndex;

            Rect r = EditorGUILayout.GetControlRect(false, Row);
            if (Event.current.type == EventType.Repaint)
            {
                if (pinned) EditorGUI.DrawRect(r, SelectTint);
                else if (!o.slotsVerified && o.slots.Count > 1) EditorGUI.DrawRect(r, WarnTint);
                if (r.Contains(Event.current.mousePosition))
                {
                    if (!pinned) EditorGUI.DrawRect(r, HoverTint);
                    HoverRenderer = e.rendererIndex;
                }
            }

            bool now = EditorGUI.Foldout(new Rect(r.x, r.y, 14f, r.height), open,
                                         GUIContent.none, true);
            if (now != open) { _pendingObject = o.name; _pendingObjectOn = now; }

            var nameRect = new Rect(r.x + 16f, r.y, Mathf.Max(120f, r.width * 0.45f), r.height);
            EditorGUI.LabelField(nameRect, o.name);

            var infoRect = new Rect(nameRect.xMax, r.y, r.width - nameRect.width - 16f, r.height);
            string slotText = o.slots.Count == 1
                ? Or(o.slots[0], "(no material)")
                : o.slots.Count + " slots" + (o.slotsVerified ? "" : "  unverified");
            EditorGUI.LabelField(infoRect, slotText + "      " + o.role,
                                 EditorStyles.miniLabel);

            if (Event.current.type == EventType.MouseDown
                && r.Contains(Event.current.mousePosition) && Event.current.button == 0
                && Event.current.mousePosition.x > r.x + 14f)
            {
                preview.selectedRenderer = pinned ? -1 : e.rendererIndex;
                preview.selectedSlot = -1;
                Event.current.Use();
            }

            if (!now) return;
            using (new EditorGUI.IndentLevelScope(2))
            {
                string[] names = draft.MaterialNames();
                for (int s = 0; s < o.slots.Count; s++)
                {
                    int idx = Mathf.Max(0, System.Array.IndexOf(names, o.slots[s]));
                    Rect sr = EditorGUILayout.GetControlRect(false, Row);
                    if (Event.current.type == EventType.Repaint
                        && sr.Contains(Event.current.mousePosition))
                    {
                        EditorGUI.DrawRect(sr, HoverTint);
                        HoverRenderer = e.rendererIndex;
                        HoverSlot = s;
                    }

                    EditorGUI.BeginChangeCheck();
                    int pick = EditorGUI.Popup(sr, "slot " + s, idx, names);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Rec(draft, "slot binding");
                        o.slots[s] = pick == 0 ? "" : names[pick];
                        Dirty(draft);
                    }
                }

                EditorGUI.BeginChangeCheck();
                bool verified = EditorGUILayout.ToggleLeft(
                    o.slots.Count > 1
                        ? "Slot order checked against the preview"
                        : "Binding checked against the preview",
                    o.slotsVerified);
                if (EditorGUI.EndChangeCheck())
                {
                    Rec(draft, "slots verified");
                    o.slotsVerified = verified;
                    Dirty(draft);
                }

                int roleIdx = DraftRoles.IndexOf(o.role);
                EditorGUI.BeginChangeCheck();
                int newRole = EditorGUILayout.Popup("Damage role", roleIdx, DraftRoles.All);
                if (EditorGUI.EndChangeCheck() && newRole != roleIdx)
                {
                    Rec(draft, "damage role");
                    o.role = DraftRoles.All[newRole];
                    Dirty(draft);
                }

                FloatRow(draft, "Health (hp)", o.healthHp, v => o.healthHp = Mathf.Max(0f, v));
                StringRow(draft, "Damage group", o.group, v => o.group = v);
            }
        }

        // ==================== small helpers ====================

        private static string Or(string a, string fallback) =>
            string.IsNullOrEmpty(a) ? fallback : a;

        private static bool Foldout(bool state, string title) =>
            EditorGUILayout.Foldout(state, title, true, EditorStyles.foldoutHeader);

        private static void Toggle(List<string> set, string key, bool on)
        {
            if (on) { if (!set.Contains(key)) set.Add(key); }
            else set.Remove(key);
        }

        private static void Rec(AssetStudioDraft d, string what) =>
            Undo.RecordObject(d, "Asset Studio: " + what);

        private static void Dirty(AssetStudioDraft d) => EditorUtility.SetDirty(d);

        private static void SliderRow(AssetStudioDraft d, string label, float value,
                                      System.Action<float> set)
        {
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.Slider(label, value, 0f, 1f);
            if (!EditorGUI.EndChangeCheck()) return;
            Rec(d, label);
            set(v);
            Dirty(d);
        }

        private static void FloatRow(AssetStudioDraft d, string label, float value,
                                     System.Action<float> set)
        {
            EditorGUI.BeginChangeCheck();
            float v = EditorGUILayout.FloatField(label, value);
            if (!EditorGUI.EndChangeCheck()) return;
            Rec(d, label);
            set(v);
            Dirty(d);
        }

        private static void StringRow(AssetStudioDraft d, string label, string value,
                                      System.Action<string> set)
        {
            EditorGUI.BeginChangeCheck();
            string v = EditorGUILayout.TextField(label, value);
            if (!EditorGUI.EndChangeCheck()) return;
            Rec(d, label);
            set(v);
            Dirty(d);
        }

        private static void ColorRow(AssetStudioDraft d, string label, Color value,
                                     System.Action<Color> set)
        {
            EditorGUI.BeginChangeCheck();
            Color v = EditorGUILayout.ColorField(label, value);
            if (!EditorGUI.EndChangeCheck()) return;
            Rec(d, label);
            set(v);
            Dirty(d);
        }
    }
}
