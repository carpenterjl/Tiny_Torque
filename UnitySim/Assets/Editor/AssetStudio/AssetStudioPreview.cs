using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.AssetTools
{
    /// <summary>Which of the game's token tables a preview binds through.</summary>
    public enum TokenTable
    {
        /// <summary>Pick from the asset's kind and key.</summary>
        Auto,
        /// <summary>The shell table — <c>PartVisualFactory.AccentTokens</c>.</summary>
        BodyAccents,
        /// <summary>The wheel table — <c>PartVisualFactory.WheelTokens</c>.</summary>
        Wheel,
        /// <summary>The Tiguan's manifest-built table, body and wheels alike.</summary>
        Tiguan,
        /// <summary><c>CosmeticCatalog.Tokens</c>.</summary>
        Cosmetic,
        /// <summary>Bind nothing — see the mesh as the FBX carries it.</summary>
        None,
    }

    /// <summary>
    /// Everything the author can turn in the preview. A plain serializable class
    /// so the window can hold it in a <c>[SerializeField]</c> and keep the camera
    /// where you left it across a script compile.
    /// </summary>
    [System.Serializable]
    public sealed class PreviewOptions
    {
        public float yaw = 135f;
        public float pitch = 16f;
        public float zoom = 1f;

        /// <summary>The design's body size, the numerator of the game's own
        /// <c>bodySize / BodyMeshAuthorSize</c> render scale. Editable because
        /// stretching a shell by changing bodySize is what that divide is FOR,
        /// and seeing the result is the only way to judge it.</summary>
        public Vector3 bodySize = CarVehicle.BodyMeshAuthorSize;

        /// <summary>Target radius for a wheel — the numerator of
        /// <c>radius / AuthorRadiusFor(style)</c>.</summary>
        public float wheelRadius = PartVisualFactory.WheelAuthorRadius;

        /// <summary>Right-hand corner: the game spins the mesh half a turn about
        /// the vertical when +X points inboard, rather than mirroring it.</summary>
        public bool rightSide;

        /// <summary>Pre-apply the uniform scale and quarter turn the export needs.
        /// Off by default, on purpose — an uncorrected export rendering twelve
        /// times too big and lying on its side is the thing worth seeing first.</summary>
        public bool applyCorrection;

        public bool showRuler = true;
        public bool isolate;

        /// <summary>Bind from the draft's per-slot mapping instead of the legacy
        /// token tables. Only meaningful when a draft exists; turning it off is
        /// how you compare "what I am authoring" against "what the game does with
        /// this mesh today".</summary>
        public bool useDraft = true;

        public TokenTable table = TokenTable.Auto;
        public Color bodyColor = new Color(0.80f, 0.20f, 0.22f);

        /// <summary>Index into the preview's renderer list, or -1. Set by
        /// clicking; survives the mouse leaving.</summary>
        public int selectedRenderer = -1;

        /// <summary>Submesh slot within that renderer, or -1 for the whole
        /// renderer.</summary>
        public int selectedSlot = -1;

        /// <summary>What the mouse is over right now. Not serialized in spirit —
        /// it is rewritten every repaint — but it lives here so the overlay has
        /// one place to read a selection from.</summary>
        [System.NonSerialized] public int hoverRenderer = -1;
        [System.NonSerialized] public int hoverSlot = -1;

        /// <summary>Click wins over hover; hover shows through when nothing is
        /// pinned, so running the mouse down the object list walks the highlight
        /// over the car without 41 clicks.</summary>
        public int EffectiveRenderer =>
            selectedRenderer >= 0 ? selectedRenderer : hoverRenderer;

        public int EffectiveSlot => selectedRenderer >= 0 ? selectedSlot : hoverSlot;
    }

    /// <summary>
    /// The Asset Studio 3D preview: a <see cref="PreviewRenderUtility"/> scene
    /// holding one asset, transformed by the game's own rules and bound through
    /// the game's own binder.
    ///
    /// <b>Not <c>PartIconFactory</c> and not <c>PartMeshLibrary.TryInstantiate</c>.</b>
    /// Both are runtime code and both call <c>Object.Destroy</c>, which in edit
    /// mode does not destroy anything — it logs and returns — so a preview built
    /// on them would slowly fill the OPEN scene with stripped car parts. Every
    /// teardown here is <c>DestroyImmediate</c>, and the geometry lives in the
    /// preview's private scene where the open scene cannot see it. What the
    /// preview does reuse is the part that matters: the transform rules from
    /// <c>CarVehicle.BuildBodyVisual</c> / <c>PartVisualFactory.BuildWheelViz</c>,
    /// the framing helper <c>LocalRendererBounds</c>, and
    /// <c>PartVisualFactory.BindByToken</c> — the literal loop the car runs.
    ///
    /// <b>What it deliberately does not reproduce.</b> Wheel finishes (the chrome
    /// / gold / neon overlay on styles 6-8) are applied by a private step keyed on
    /// the style INT, and an FBX key does not carry one; the battery, antennas,
    /// light bars and every track prop bind from small tables written inline at
    /// their own call sites. Those cases are labelled approximate in the window
    /// rather than guessed at here.
    /// </summary>
    public sealed class AssetStudioPreview : System.IDisposable
    {
        private PreviewRenderUtility _prev;

        // Rig:  Root -> Design (render scale) -> Correction (proposed fix) -> FBX
        // Correction sits INSIDE Design because it is a property of the mesh, not
        // of the design: it turns the raw export into something the design's
        // divide can then scale, which is exactly the order a committed asset
        // will apply them in.
        private GameObject _root, _design, _correction, _inst, _ruler;
        private Material _bodyMat, _highlightMat, _rulerMat, _tickMat, _unboundMat;
        private AssetStudioDraftMaterials _baker;

        private readonly List<Renderer> _rends = new List<Renderer>();
        private readonly List<Material[]> _bound = new List<Material[]>();

        private string _builtPath = "";
        private string _bindKey = "";
        private string _overlayKey = "";

        /// <summary>The renderers of the loaded asset, in traversal order — the
        /// same order and the same objects the window lists.</summary>
        public IReadOnlyList<Renderer> Renderers => _rends;

        /// <summary>Renderers left holding a null material in at least one slot
        /// after binding. Non-zero means multi-submesh: the token binder assigns
        /// <c>sharedMaterial</c>, which is slot 0 and slot 0 only.</summary>
        public int RenderersWithEmptySlots { get; private set; }

        /// <summary>Total submesh slots across the asset.</summary>
        public int SlotCount { get; private set; }

        /// <summary>What binding produced for renderer <paramref name="i"/>,
        /// before highlight and isolate. The window reads this rather than the
        /// live <c>sharedMaterials</c>, which the overlay is allowed to
        /// scribble on.</summary>
        public Material[] BoundMaterials(int i) =>
            i >= 0 && i < _bound.Count ? _bound[i] : System.Array.Empty<Material>();

        /// <summary>Renderer bounds of the transformed asset, in metres.</summary>
        public Bounds WorldBounds { get; private set; }

        /// <summary>Empty when the preview is showing something; a sentence when
        /// it is not.</summary>
        public string Problem { get; private set; } = "";

        // ==================== build ====================

        private void EnsureUtility()
        {
            if (_prev != null) return;
            _prev = new PreviewRenderUtility();
            _prev.camera.fieldOfView = 30f;
            _prev.camera.clearFlags = CameraClearFlags.SolidColor;
            _prev.camera.backgroundColor = new Color(0.16f, 0.17f, 0.19f, 1f);
            _prev.ambientColor = new Color(0.28f, 0.29f, 0.32f, 1f);

            _prev.lights[0].intensity = 1.15f;
            _prev.lights[0].transform.rotation = Quaternion.Euler(38f, 132f, 0f);
            _prev.lights[0].shadows = LightShadows.Soft;
            _prev.lights[1].intensity = 0.55f;
            _prev.lights[1].transform.rotation = Quaternion.Euler(-14f, -70f, 0f);
        }

        /// <summary>Drop the loaded asset. Cheap to call — the next Draw rebuilds.</summary>
        public void Invalidate() => _builtPath = "";

        /// <summary>Force the next Draw to re-bind without reloading the mesh. What
        /// an undo needs: <c>Undo</c> rolls the draft back without going through
        /// SetDirty in an order this class can observe, so the revision token it
        /// watches is not enough on its own.</summary>
        public void InvalidateBinding() => _bindKey = "";

        private void Load(string prefabPath)
        {
            DestroyRig();
            _builtPath = prefabPath;
            Problem = "";

            var src = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (src == null)
            {
                Problem = "Unity has no imported model at " + prefabPath;
                return;
            }

            _root = new GameObject("AssetStudioPreviewRig") { hideFlags = HideAndDont };
            _design = Child("Design", _root.transform);
            _correction = Child("Correction", _design.transform);

            _inst = Object.Instantiate(src);
            Hide(_inst);
            _inst.transform.SetParent(_correction.transform, false);
            _inst.transform.localPosition = Vector3.zero;
            _inst.transform.localRotation = Quaternion.identity;

            _prev.AddSingleGO(_root);

            _rends.Clear();
            _rends.AddRange(_inst.GetComponentsInChildren<Renderer>(true));
            if (_rends.Count == 0) Problem = "the imported model has no renderers";

            _bindKey = "";
            _overlayKey = "";
        }

        private const HideFlags HideAndDont = HideFlags.HideAndDontSave;

        private static GameObject Child(string name, Transform parent)
        {
            var go = new GameObject(name) { hideFlags = HideAndDont };
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void Hide(GameObject go)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                t.gameObject.hideFlags = HideAndDont;
        }

        // ==================== binding ====================

        /// <summary>
        /// Resolve <see cref="TokenTable.Auto"/> the way the game's call sites do:
        /// by what the asset IS. Anything carrying "tiguan" in its key goes to the
        /// manifest table, because that car's pieces are named "tig*" and match
        /// nothing in the shared tables — on the wrong table it arrives as one
        /// flat colour, which is precisely the failure being previewed.
        /// </summary>
        public static TokenTable Resolve(TokenTable chosen, AssetRow row)
        {
            if (chosen != TokenTable.Auto) return chosen;
            string k = (row?.key ?? row?.label ?? "").ToLowerInvariant();
            if (k.Contains("tiguan")) return TokenTable.Tiguan;
            return row?.kind switch
            {
                AssetKind.Wheel => TokenTable.Wheel,
                AssetKind.Cosmetic => TokenTable.Cosmetic,
                _ => TokenTable.BodyAccents,
            };
        }

        /// <summary>True when the resolved table is the exact one the game uses
        /// for that asset, rather than the nearest available approximation.</summary>
        public static bool TableIsExact(AssetKind kind) =>
            kind == AssetKind.CarBody || kind == AssetKind.Wheel
            || kind == AssetKind.Cosmetic || kind == AssetKind.Unassigned;

        private void Bind(AssetRow row, PreviewOptions o, AssetStudioDraft draft)
        {
            bool byDraft = o.useDraft && draft != null;
            TokenTable table = Resolve(o.table, row);

            // GetDirtyCount ticks on every SetDirty, so it is exactly "the draft
            // changed" without this class knowing which field. Editing a
            // smoothness therefore rebuilds the materials and nothing else.
            int rev = draft != null ? EditorUtility.GetDirtyCount(draft) : 0;
            string key = (byDraft ? "draft" + rev : table.ToString())
                         + "|" + ColorUtility.ToHtmlStringRGB(o.bodyColor);
            if (key == _bindKey) return;
            _bindKey = key;

            // Lift the highlight before rebinding. The binder writes
            // sharedMaterial — slot 0 — so on a multi-submesh renderer a
            // highlight parked in slot 1 would survive the rebind and then be
            // snapshotted as if binding had produced it.
            ClearOverlay();

            if (_bodyMat == null)
                _bodyMat = new Material(Shader.Find("Standard")) { hideFlags = HideAndDont };
            _bodyMat.color = o.bodyColor;

            // A COMMITTED asset binds the way the game binds it, not the way the
            // token tables would. The game routes it through AssetManifestBinder
            // (PartMeshLibrary.TryInstantiate stamps a PartManifestBinding and
            // both token binders hand off to it), so a preview still reading the
            // table would disagree with Play about which panel is chrome — the
            // one failure this window exists to catch. Owed since R3 and payable
            // now that the commit pipeline can produce such an asset.
            //
            // The DRAFT path still wins over it, and deliberately: a draft is the
            // edit in progress, and the whole point of previewing one is to see a
            // change before it is committed.
            if (byDraft) BindFromDraft(draft, o.bodyColor);
            else if (BindThroughManifest(row, o.bodyColor)) { }
            else switch (table)
            {
                // The shell path, verbatim: "paint*" takes the tintable material,
                // everything else takes the first token its name contains.
                case TokenTable.BodyAccents:
                    PartVisualFactory.BindByToken(_inst, PartVisualFactory.AccentTokens, _bodyMat);
                    break;
                case TokenTable.Tiguan when row?.kind != AssetKind.Wheel:
                    PartVisualFactory.BindByToken(_inst, PartVisualFactory.TiguanTokens, _bodyMat);
                    break;
                // The wheel path, verbatim: no paint channel, and the tyre
                // material is the fallback — a piece that matches no token coming
                // out tyre-black is a fact about the table, so the preview passes
                // the game's own material rather than a look-alike grey.
                case TokenTable.Tiguan:
                    PartMeshLibrary.AssignByName(_inst, null, PartVisualFactory.TiguanTokens);
                    break;
                case TokenTable.Wheel:
                    PartMeshLibrary.AssignByName(_inst, PartVisualFactory.TyreMaterial,
                                                 PartVisualFactory.WheelTokens);
                    break;
                case TokenTable.Cosmetic:
                    PartMeshLibrary.AssignByName(_inst, _bodyMat, Garage.CosmeticCatalog.Tokens);
                    break;
            }

            // Snapshot what binding produced, so highlight and isolate are
            // overlays that can be lifted rather than edits that cannot.
            _bound.Clear();
            SlotCount = 0;
            RenderersWithEmptySlots = 0;
            foreach (Renderer r in _rends)
            {
                Material[] mats = r.sharedMaterials;
                _bound.Add(mats);
                SlotCount += mats.Length;
                foreach (Material m in mats)
                    if (m == null || m == _unboundMat) { RenderersWithEmptySlots++; break; }
            }
            _overlayKey = "";
        }

        /// <summary>
        /// Bind a COMMITTED asset the way the game binds it, or return false and
        /// leave the token switch to it.
        ///
        /// The stamp is the seam at runtime too — <c>TryInstantiate</c> writes a
        /// <c>PartManifestBinding</c> when and only when the asset ships a
        /// manifest — so the preview stamps the same component on its own
        /// instance and calls the same binder. It does not reimplement the
        /// decision; asking whether the stamp took IS the decision.
        ///
        /// The paint material is the rig's <c>_bodyMat</c> for a body and null for
        /// everything else, which is the split the game makes: only the body path
        /// has a design colour to hand a paint-channel material.
        /// </summary>
        private bool BindThroughManifest(AssetRow row, Color tint)
        {
            if (_inst == null || row == null || !row.HasProject
                || string.IsNullOrEmpty(row.key)) return false;

            string root = row.rootName == "Cosmetics" ? Garage.CosmeticCatalog.MeshRoot
                        : row.rootName == "TrackProps" ? PartMeshLibrary.PropRoot
                        : PartMeshLibrary.PartRoot;

            PartManifestBinding stamp = _inst.GetComponent<PartManifestBinding>()
                                        ?? AssetManifestBinder.TryStamp(_inst, row.key, root);
            if (stamp == null) return false;

            _bodyMat.color = tint;
            AssetManifestBinder.Bind(stamp, row.kind == AssetKind.CarBody ? _bodyMat : null, null);
            return true;
        }

        /// <summary>
        /// Bind every renderer per SLOT from the draft — the thing the token
        /// tables structurally cannot do, since <c>AssignByName</c> writes
        /// <c>sharedMaterial</c> and that is slot 0.
        ///
        /// A slot the draft has not mapped gets a loud unbound material rather
        /// than null. Null renders as Unity's pink error shader in some paths and
        /// as nothing in others; neither says "you have not assigned this yet",
        /// and this is the one screen where that sentence is the whole message.
        /// </summary>
        private void BindFromDraft(AssetStudioDraft draft, Color tint)
        {
            _baker ??= new AssetStudioDraftMaterials();
            _baker.ClearMaterials();

            if (_unboundMat == null)
            {
                _unboundMat = new Material(Shader.Find("Standard"))
                {
                    name = "(unbound slot)",
                    color = new Color(0.85f, 0.10f, 0.75f),
                    hideFlags = HideAndDont,
                };
                _unboundMat.SetFloat("_Glossiness", 0.1f);
            }

            foreach (Renderer r in _rends)
            {
                if (r == null) continue;
                DraftObject o = draft.Object(r.gameObject.name);
                int slots = Mathf.Max(1, r.sharedMaterials?.Length ?? 1);
                var mats = new Material[slots];
                for (int s = 0; s < slots; s++)
                {
                    string name = o != null && s < o.slots.Count ? o.slots[s] : "";
                    DraftMaterial dm = string.IsNullOrEmpty(name) ? null : draft.Material(name);
                    mats[s] = dm != null
                        ? _baker.Get(dm, draft.sourceDir, tint)
                        : _unboundMat;
                }
                r.sharedMaterials = mats;
            }
        }

        /// <summary>Textures the draft names that its export folder does not
        /// have. Empty until a draft bind has run.</summary>
        public IReadOnlyList<string> MissingMaps =>
            _baker != null ? _baker.MissingMaps : System.Array.Empty<string>();

        /// <summary>Put every renderer back the way binding left it.</summary>
        private void ClearOverlay()
        {
            for (int i = 0; i < _rends.Count && i < _bound.Count; i++)
            {
                if (_rends[i] == null) continue;
                _rends[i].sharedMaterials = _bound[i];
                _rends[i].enabled = true;
            }
            _overlayKey = "";
        }

        private void Overlay(PreviewOptions o)
        {
            int sel = o.EffectiveRenderer, slot = o.EffectiveSlot;
            string key = sel + "/" + slot + "/" + o.isolate;
            if (key == _overlayKey) return;
            _overlayKey = key;

            if (_highlightMat == null)
            {
                _highlightMat = new Material(Shader.Find("Standard"))
                {
                    color = new Color(1f, 0.55f, 0.05f),
                    hideFlags = HideAndDont,
                };
                _highlightMat.EnableKeyword("_EMISSION");
                _highlightMat.SetColor("_EmissionColor", new Color(0.7f, 0.3f, 0.02f));
            }

            for (int i = 0; i < _rends.Count; i++)
            {
                Renderer r = _rends[i];
                if (r == null) continue;
                bool picked = i == sel;
                r.enabled = !o.isolate || picked || sel < 0;

                Material[] mats = _bound[i];
                if (picked)
                {
                    mats = (Material[])mats.Clone();
                    if (slot >= 0 && slot < mats.Length)
                        mats[slot] = _highlightMat;
                    else
                        for (int s = 0; s < mats.Length; s++) mats[s] = _highlightMat;
                }
                r.sharedMaterials = mats;
            }
        }

        // ==================== transform ====================

        /// <summary>
        /// Apply the game's own render transform, plus the export's proposed
        /// correction as a separate inner node.
        ///
        /// The two are kept apart on purpose. The design divide is what the game
        /// does today and must be shown exactly; the correction is a proposal
        /// about the MESH that the commit pipeline will record and that R2 will
        /// settle the manifest wording for. Composing them into one number here
        /// would quietly pick that answer.
        /// </summary>
        private void Transform(AssetRow row, PreviewOptions o, TtExport x, float target)
        {
            float scale = 1f;
            float yawFix = 0f;
            if (o.applyCorrection && x != null && x.UnityDims != Vector3.zero)
            {
                scale = x.UniformScaleTo(target);
                yawFix = x.LengthRunsAlongX ? -90f : 0f;
            }
            _correction.transform.localScale = Vector3.one * scale;
            _correction.transform.localRotation = Quaternion.Euler(0f, yawFix, 0f);

            if (row != null && row.kind == AssetKind.Wheel)
            {
                // BuildWheelViz: one factor, radius over the radius the mesh
                // renders unscaled at — and a half turn about the vertical on the
                // corner where +X points inboard, never a negative scale.
                float authored = AuthorRadiusFor(row.key);
                _design.transform.localScale =
                    Vector3.one * (Mathf.Max(0.001f, o.wheelRadius) / authored);
                _design.transform.localRotation = o.rightSide
                    ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            }
            else
            {
                // BuildBodyVisual: per-axis bodySize / BodyMeshAuthorSize, except
                // the Tiguan, which takes no scale at all because its renderer
                // bounds carry mirrors and roof rails that its collision box
                // deliberately does not.
                bool tiguan = (row?.key ?? "").Contains("tiguan");
                _design.transform.localScale = tiguan
                    ? Vector3.one
                    : new Vector3(o.bodySize.x / CarVehicle.BodyMeshAuthorSize.x,
                                  o.bodySize.y / CarVehicle.BodyMeshAuthorSize.y,
                                  o.bodySize.z / CarVehicle.BodyMeshAuthorSize.z);
                _design.transform.localRotation = Quaternion.identity;
            }

            WorldBounds = PartVisualFactory.LocalRendererBounds(_root.transform,
                                                               _design.transform);
        }

        /// <summary>The radius at which a wheel mesh renders unscaled, keyed off
        /// the FBX name rather than the style int — <c>AuthorRadiusFor</c> takes
        /// the int, and a browsed asset has a key instead. Same two answers.</summary>
        public static float AuthorRadiusFor(string key) =>
            (key ?? "").Contains("tiguan")
                ? PartVisualFactory.TiguanWheelAuthorRadius
                : PartVisualFactory.WheelAuthorRadius;

        /// <summary>The length the ruler measures: the contract the asset is
        /// supposed to meet, which is what makes it worth drawing.</summary>
        public static float RulerLength(AssetRow row) =>
            row != null && row.kind == AssetKind.Wheel
                ? AuthorRadiusFor(row.key) * 2f
                : CarVehicle.BodyMeshAuthorSize.z;

        // ==================== ruler ====================

        private void Ruler(float length, bool show)
        {
            if (!show)
            {
                if (_ruler != null) { Object.DestroyImmediate(_ruler); _ruler = null; }
                return;
            }

            if (_rulerMat == null)
            {
                _rulerMat = Unlit(new Color(0.20f, 0.85f, 0.95f));
                _tickMat = Unlit(new Color(0.95f, 0.85f, 0.20f));
            }
            if (_ruler == null)
            {
                _ruler = Child("Ruler", _root.transform);
                float t = length * 0.012f;
                Bar(_ruler.transform, _rulerMat, Vector3.zero, new Vector3(t, t, length));
                for (int s = -1; s <= 1; s += 2)
                    Bar(_ruler.transform, _tickMat,
                        new Vector3(0f, 0f, s * length * 0.5f),
                        new Vector3(t * 5f, t * 5f, t));
            }

            // Under the asset and clear of it, so it reads as a scale reference
            // rather than as part of the model.
            Bounds b = WorldBounds;
            _ruler.transform.localPosition =
                new Vector3(b.center.x, b.min.y - Mathf.Max(length, b.size.y) * 0.10f, b.center.z);
        }

        private static void Bar(Transform parent, Material mat, Vector3 pos, Vector3 scale)
        {
            var go = new GameObject("bar") { hideFlags = HideAndDont };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh =
                Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static Material Unlit(Color c)
        {
            var m = new Material(Shader.Find("Standard")) { color = c, hideFlags = HideAndDont };
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * 0.9f);
            return m;
        }

        // ==================== draw ====================

        /// <summary>
        /// Bring the rig up to date with <paramref name="o"/>, and — on a Repaint
        /// only — render it into <paramref name="rect"/>.
        ///
        /// <b>The Repaint gate is not tidiness.</b> IMGUI runs this method on
        /// every event, and on the Layout pass GUILayout has not resolved widths
        /// yet, so the rect it hands back still carries the layout request rather
        /// than a measurement. Feeding that to <c>BeginPreview</c> asks for a
        /// render texture of whatever the request said, which is how a 100000-wide
        /// preview and a console full of "requested size is too large for the
        /// current maxRenderTextureSize" happen. Repaint is the one pass where the
        /// rect is a fact.
        ///
        /// The state above the gate still runs every pass, because picking and
        /// the ruler read <see cref="WorldBounds"/> and a MouseUp must not be
        /// answered from bounds computed for a different zoom.
        /// </summary>
        public void Draw(Rect rect, AssetRow row, PreviewOptions o, string prefabPath,
                         TtExport export, AssetStudioDraft draft)
        {
            EnsureUtility();
            if (prefabPath != _builtPath) Load(prefabPath);
            if (_root == null)
            {
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.HelpBox(rect, Problem, MessageType.Warning);
                return;
            }

            Bind(row, o, draft);
            Overlay(o);
            float target = RulerLength(row);
            Transform(row, o, export, target);
            Ruler(target, o.showRuler);

            Bounds b = WorldBounds;
            float radius = Mathf.Max(b.extents.magnitude, 1e-4f);
            float dist = radius * 3.4f / Mathf.Max(0.05f, o.zoom);
            Quaternion look = Quaternion.Euler(o.pitch, o.yaw, 0f);
            Vector3 eye = b.center - look * Vector3.forward * dist;

            Camera cam = _prev.camera;
            cam.transform.SetPositionAndRotation(eye, look);
            cam.nearClipPlane = Mathf.Max(1e-4f, dist * 0.01f);
            cam.farClipPlane = dist * 12f;

            if (Event.current.type != EventType.Repaint) return;

            // A second belt on top of the Repaint gate. A docked window mid-drag,
            // a collapsed pane or a zero-height layout can all hand over a rect
            // that is real and still not renderable, and the failure mode is a
            // driver-level allocation error rather than a blank box.
            if (rect.width < 4f || rect.height < 4f) return;
            if (rect.width > 8192f || rect.height > 8192f)
            {
                EditorGUI.HelpBox(rect, "Preview area is " + (int)rect.width + " x "
                    + (int)rect.height + " px, which is past what a render texture "
                    + "can be. Resize the window.", MessageType.Warning);
                return;
            }

            _prev.BeginPreview(rect, GUIStyle.none);
            _prev.Render(true);
            Texture tex = _prev.EndPreview();
            GUI.DrawTexture(rect, tex, ScaleMode.StretchToFill, false);
        }

        /// <summary>
        /// A camera ray for a point inside the last drawn rect, built from the
        /// rect and the field of view rather than from
        /// <c>Camera.ScreenPointToRay</c> — the preview camera's pixel rect is
        /// only meaningful between Begin and End, and picking happens outside it.
        /// </summary>
        public Ray RayThrough(Rect rect, Vector2 point)
        {
            Camera cam = _prev?.camera;
            if (cam == null || rect.width < 1f || rect.height < 1f)
                return new Ray(Vector3.zero, Vector3.forward);

            float u = (point.x - rect.x) / rect.width;
            float v = (point.y - rect.y) / rect.height;   // GUI y grows downward
            float t = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var dir = new Vector3((u * 2f - 1f) * t * (rect.width / rect.height),
                                  (1f - v * 2f) * t, 1f);
            return new Ray(cam.transform.position,
                           cam.transform.TransformDirection(dir).normalized);
        }

        /// <summary>
        /// Nearest renderer whose world bounds the ray enters, or -1.
        ///
        /// <b>Bounds level, and it cannot be otherwise here.</b> Triangle picking
        /// needs the CPU copy of the mesh, and <c>PartModelPostprocessor</c>
        /// leaves <c>isReadable</c> off for everything except body shells, track
        /// props and the Tiguan's wheels. So a click lands on the nearest BOX, and
        /// on a car with 41 overlapping parts that is sometimes the wrong one —
        /// the object list beside the viewport is the exact way to select.
        /// </summary>
        public int Pick(Ray ray)
        {
            int best = -1;
            float bestT = float.MaxValue;
            for (int i = 0; i < _rends.Count; i++)
            {
                Renderer r = _rends[i];
                if (r == null || !r.enabled) continue;
                if (r.bounds.IntersectRay(ray, out float t) && t < bestT) { bestT = t; best = i; }
            }
            return best;
        }

        // ==================== teardown ====================

        private void DestroyRig()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = _design = _correction = _inst = _ruler = null;
            Kill(ref _bodyMat);
            Kill(ref _highlightMat);
            Kill(ref _rulerMat);
            Kill(ref _tickMat);
            Kill(ref _unboundMat);
            _baker?.Dispose();
            _baker = null;
            _rends.Clear();
            _bound.Clear();
            SlotCount = 0;
            RenderersWithEmptySlots = 0;
        }

        private static void Kill(ref Material m)
        {
            if (m != null) Object.DestroyImmediate(m);
            m = null;
        }

        /// <summary>
        /// Idempotent, and it has to be: the window calls this from OnDisable
        /// (which runs before every domain reload) and again from OnDestroy. A
        /// PreviewRenderUtility that is garbage collected without Cleanup leaves
        /// its scene and render texture behind and says so in the console.
        /// </summary>
        public void Dispose()
        {
            DestroyRig();
            if (_prev != null) { _prev.Cleanup(); _prev = null; }
            _builtPath = "";
        }
    }
}
