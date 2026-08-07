using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>The studio's five jobs, one per tab.</summary>
    public enum StudioTab { Body, Parts, Frame, Paint, Drive }

    /// <summary>
    /// Vehicle Studio's panel and its pointer handling.
    ///
    /// IMGUI, gamepad-navigable through <see cref="MenuNav"/>, and following that
    /// class's ground rule to the letter: <b>every value that decides WHICH
    /// controls exist is snapshotted on the Layout pass</b> and drawn from the
    /// snapshot, so a tab change, a body swap or a part being deleted from a
    /// handler cannot change the control count between a Layout and its paired
    /// Repaint. With four tabs, a palette whose length depends on the category, and
    /// an inspector that only exists when something is selected, there are a lot of
    /// those — they are all in one block below, and every one of them is read from
    /// the copy.
    ///
    /// <b>Anything that changes the world is an INTENT.</b> Opening a body, loading
    /// a layout, adding or removing a part, leaving for the track: all recorded
    /// here and carried out from <see cref="Update"/>, which is always pass-safe.
    ///
    /// The pointer work stays thin: it turns screen coordinates into a ray and
    /// hands the ray to the body, the gizmo or the vehicle. Nothing about falloff,
    /// welding, drag maths or commit timing lives here.
    /// </summary>
    public sealed class BodyEditorUI : MonoBehaviour
    {
        public BodyEditorBootstrap bootstrap;

        private enum BodyTool { Morph, Sculpt }

        // ---- live state -------------------------------------------------------------
        private StudioTab _tab = StudioTab.Body;
        private BodyTool _bodyTool = BodyTool.Morph;
        private int _bodyIdx;
        private int _catIdx;
        private int _fileIdx;
        private bool _showFiles;
        private string _saveName = "layout";
        private string _vehicleName = "Studio Vehicle";
        private float _radius01 = 0.35f;
        private float _strength01 = 0.5f;
        private SculptDir _dir = SculptDir.SurfaceNormal;

        private PlacedProp _selected;
        private int _channelIdx;
        private bool _paintBody = true;
        private FeatureTint _edit = new FeatureTint();

        // ---- crash frame ------------------------------------------------------------
        private enum LatticeSel { None, Node, Beam }
        private LatticeSel _latSel = LatticeSel.None;
        private int _latIndex = -1;
        private float _fidelity01 = 0.5f;
        private bool _linkArmed;          // L pressed: the next node click links
        private bool _generateArmed;      // Generate pressed once: the next press does it
        private bool _whackArmed;         // Crash test armed: body clicks are hits
        private StudioTab _lastTab = StudioTab.Body;

        // ---- Layout-pass snapshots (see the class note) -----------------------------
        private StudioTab _tabDraw;
        private BodyTool _bodyToolDraw;
        private bool _showFilesDraw;
        private int _morphCountDraw;
        private int _bodyCountDraw;
        private int _fileCountDraw;
        private int _catCountDraw;
        private int _itemCountDraw;
        private int _channelCountDraw;
        private bool _hasSelectionDraw;
        private bool _paintBodyDraw = true;
        private bool _hasLatticeDraw;
        private int _latNodeCountDraw, _latBeamCountDraw;
        private LatticeSel _latSelDraw = LatticeSel.None;
        private int _latIndexDraw = -1;
        private bool _latticeStaleDraw, _generateArmedDraw, _linkArmedDraw;
        private bool _whackArmedDraw, _previewActiveDraw;

        // ---- deferred intents, executed from Update ---------------------------------
        private int _pendingBody = -1;
        private string _pendingLoad;
        private string _pendingAdd;
        private bool _pendingDelete, _pendingDuplicate, _pendingMirror, _pendingDrive;
        private bool _pendingClearParts;
        private bool _pendingGenerate, _pendingClearLattice, _pendingAddNode, _pendingLatDelete;
        private bool _pendingRepair;
        private int _pendingLink = -1;

        // ---- pointer ----------------------------------------------------------------
        private bool _sculpting;
        private bool _hovering;
        private Vector3 _hoverPoint;
        private Rect _panelRect, _hintRect;
        private List<string> _files = new List<string>();
        private bool _filesStale = true;
        private string _status = "";

        private static readonly string[] DirLabels = { "Normal", "Vertical", "Lateral" };

        private IReadOnlyList<BodyDef> Bodies => BodyMeshSource.Eligible();
        private IReadOnlyList<StudioPartLibrary.Category> Cats => StudioPartLibrary.Categories();

        private StudioVehicle Vehicle => bootstrap != null ? bootstrap.Vehicle : null;
        private DeformableBody Body => bootstrap != null ? bootstrap.Body : null;
        private TransformGizmo Gizmo => bootstrap != null ? bootstrap.Gizmo : null;
        private StudioLattice Lattice => Vehicle != null ? Vehicle.Lattice : null;

        // ---- brush size ---------------------------------------------------------------
        //
        // As a fraction of the body's length, so the same slider position means
        // the same visual bite on a 0.42 m shell and on anything else.
        private const float RadiusMinPerLength = 0.02f;
        private const float RadiusMaxPerLength = 0.35f;

        private float RadiusM
        {
            get
            {
                float len = Body != null ? Body.BodyLengthM : CarVehicle.BodyMeshAuthorSize.z;
                return Mathf.Lerp(RadiusMinPerLength, RadiusMaxPerLength, _radius01) * len;
            }
        }

        /// <summary>Pointer travel is multiplied by this before it becomes surface
        /// travel. Below 1 the brush drags more slowly than the mouse, which is
        /// what makes a small correction possible at all.</summary>
        private float Strength => Mathf.Lerp(0.15f, 2f, _strength01);

        /// <summary>True while the body tab's sculpt tool owns the pointer.</summary>
        private bool SculptActive => _tab == StudioTab.Body && _bodyTool == BodyTool.Sculpt;

        // ==================== input ====================

        private void Start()
        {
            // A layout consumed by the bootstrap in Awake may carry a fidelity;
            // the slider should show it rather than the default.
            if (Lattice != null) _fidelity01 = Lattice.Fidelity01;
        }

        private void Update()
        {
            if (bootstrap == null) return;

            StudioIcons.Pump();
            RunIntents();
            HandleTabChange();

            bool over = PointerOverUI();
            Camera cam = bootstrap.Cam;
            Ray ray = cam != null
                ? cam.ScreenPointToRay(InputReader.PointerPosition())
                : new Ray(Vector3.zero, Vector3.forward);

            if (SculptActive) UpdateSculpt(ray, over);
            else
            {
                if (_sculpting && Body != null) { Body.EndSculpt(); _sculpting = false; }
                _hovering = false;
            }

            UpdateSelectionKeys();
            UpdateLatticeKeys();
            UpdateGizmo(ray, over);

            if (bootstrap.Orbit != null)
            {
                bool busy = _sculpting || (Gizmo != null && Gizmo.Dragging);
                bootstrap.Orbit.blockDrag = over || busy;
                bootstrap.Orbit.blockZoom = over;
            }
        }

        /// <summary>
        /// Carry out everything the panel asked for. Here rather than in the
        /// handler that asked, because all of it rebuilds objects the panel is in
        /// the middle of drawing.
        /// </summary>
        private void RunIntents()
        {
            if (_pendingBody >= 0)
            {
                var list = Bodies;
                if (_pendingBody < list.Count) bootstrap.SetBody(list[_pendingBody]);
                _pendingBody = -1;
                Deselect();
                _sculpting = false;
            }

            if (_pendingLoad != null)
            {
                Deselect();
                DeselectLattice();
                bootstrap.LoadLayout(_pendingLoad);
                _status = $"Loaded '{_pendingLoad}'.";
                _pendingLoad = null;
                _sculpting = false;
                if (Lattice != null) _fidelity01 = Lattice.Fidelity01;
            }

            if (_pendingAdd != null && Vehicle != null)
            {
                Vector3 at = DropPoint();
                PlacedProp p = Vehicle.AddProp(_pendingAdd, at);
                _pendingAdd = null;
                if (p != null) Select(p);
            }

            if (_pendingDuplicate && Vehicle != null && _selected != null)
            {
                PlacedProp copy = Vehicle.Duplicate(_selected);
                if (copy != null) Select(copy);
            }
            _pendingDuplicate = false;

            if (_pendingMirror && Vehicle != null && _selected != null)
            {
                PlacedProp twin = Vehicle.MirrorProp(_selected);
                if (twin != null) Select(twin);
            }
            _pendingMirror = false;

            if (_pendingDelete && Vehicle != null && _selected != null)
            {
                Vehicle.RemoveProp(_selected);
                Deselect();
            }
            _pendingDelete = false;

            if (_pendingClearParts && Vehicle != null)
            {
                Vehicle.ClearProps();
                Deselect();
            }
            _pendingClearParts = false;

            RunLatticeIntents();

            if (_pendingDrive)
            {
                _pendingDrive = false;
                if (Vehicle != null) StudioSession.TestDrive(Vehicle.Capture());
            }
        }

        private void RunLatticeIntents()
        {
            StudioLattice lat = Lattice;
            if (lat == null)
            {
                _pendingGenerate = _pendingClearLattice = _pendingAddNode = _pendingLatDelete = false;
                _pendingLink = -1;
                return;
            }

            if (_pendingGenerate)
            {
                _pendingGenerate = false;
                DeselectLattice();   // every index is about to die
                if (lat.Generate(Body, _fidelity01))
                    _status = $"Frame: {lat.Nodes.Count} nodes, {lat.Beams.Count} beams.";
                else
                    _status = "Too fine for this body — that frame would blow the beam " +
                              "budget the driving step can carry.";
            }

            if (_pendingClearLattice)
            {
                _pendingClearLattice = false;
                DeselectLattice();
                lat.Clear();
                _status = "Frame removed.";
            }

            if (_pendingAddNode)
            {
                _pendingAddNode = false;
                int idx = lat.AddNode(DropPoint());
                SelectLatticeNode(idx);
                _status = "Node added — link it or it carries nothing.";
            }

            if (_pendingLink >= 0)
            {
                int target = _pendingLink;
                _pendingLink = -1;
                _linkArmed = false;
                if (_latSel == LatticeSel.Node && lat.AddBeam(_latIndex, target, out int bi))
                {
                    SelectLatticeBeam(bi);
                    _status = "Linked.";
                }
                else _status = "Those two are already linked.";
            }

            if (_pendingLatDelete)
            {
                _pendingLatDelete = false;
                if (_latSel == LatticeSel.Node) lat.RemoveNode(_latIndex);
                else if (_latSel == LatticeSel.Beam) lat.RemoveBeam(_latIndex);
                DeselectLattice();
            }

            if (_pendingRepair)
            {
                _pendingRepair = false;
                lat.RepairPreview();
                _status = "Good as new.";
            }
        }

        /// <summary>
        /// Two tabs attach the gizmo to two different documents, so a tab change
        /// has to re-point it — a node spec left attached on the PARTS tab would
        /// let a part-click drag a node. Link/generate arming dies too: an armed
        /// destructive press must not survive a context change.
        /// </summary>
        private void HandleTabChange()
        {
            if (_tab == _lastTab) return;
            _lastTab = _tab;
            _linkArmed = false;
            _generateArmed = false;
            _whackArmed = false;
            Lattice?.SetVisible(_tab == StudioTab.Frame);

            TransformGizmo giz = Gizmo;
            if (giz == null || Vehicle == null) return;
            if (_tab == StudioTab.Parts && _selected != null)
                giz.Attach(_selected.Spec, Vehicle.transform);
            else if (_tab == StudioTab.Frame && _latSel == LatticeSel.Node
                     && Lattice != null && _latIndex >= 0 && _latIndex < Lattice.Nodes.Count)
                AttachNodeGizmo(_latIndex);
            else
                giz.Detach();
        }

        /// <summary>
        /// Where a newly chosen part lands: on the body under the cursor if the
        /// cursor is on the body, otherwise just above the car's middle. Never off
        /// in space — a part nobody can see is a part nobody can select.
        /// </summary>
        private Vector3 DropPoint()
        {
            Camera cam = bootstrap.Cam;
            if (cam != null && !PointerOverUI() && Vehicle != null)
            {
                Ray ray = cam.ScreenPointToRay(InputReader.PointerPosition());
                if (Vehicle.TrySurfacePoint(ray, out Vector3 local)) return local;
            }
            float len = Body != null ? Body.BodyLengthM : 0.42f;
            return new Vector3(0f, 0.18f * len, 0f);
        }

        private void UpdateSculpt(Ray ray, bool over)
        {
            DeformableBody body = Body;
            if (body == null) return;

            if (!_sculpting && !over && InputReader.LeftMousePressed())
                _sculpting = body.TryBeginSculpt(ray, RadiusM);

            if (_sculpting)
            {
                if (InputReader.LeftMouseHeld()) body.SculptTo(ray, _dir, Strength);
                if (InputReader.LeftMouseReleased()) { body.EndSculpt(); _sculpting = false; }
                return;
            }

            // One raycast a frame against one collider, purely so the brush ring
            // can be drawn where it will actually bite. It cooks nothing and reads
            // the same stale-between-edits collider the stroke would have used.
            _hovering = false;
            MeshCollider col = body.Collision != null ? body.Collision.Collider : null;
            if (!over && col != null && col.Raycast(ray, out RaycastHit hit, 1000f))
            {
                _hoverPoint = hit.point;
                _hovering = true;
            }
        }

        /// <summary>
        /// The gizmo's whole pointer story: handles win the click, then parts, then
        /// bare bodywork deselects.
        /// </summary>
        private void UpdateGizmo(Ray ray, bool over)
        {
            TransformGizmo giz = Gizmo;
            if (giz == null || Vehicle == null) return;

            giz.Refresh(bootstrap.Cam);

            if (giz.Dragging)
            {
                if (KeyTable.Pressed(StudioHints.Cancel)) { giz.CancelDrag(); return; }
                if (InputReader.LeftMouseHeld()) giz.DragTo(ray);
                if (InputReader.LeftMouseReleased()) giz.EndDrag();
                return;
            }

            if (over || SculptActive) return;
            if (_tab != StudioTab.Parts && _tab != StudioTab.Frame) return;
            if (!InputReader.LeftMousePressed()) return;

            if (giz.TryBeginDrag(ray)) return;

            if (_tab == StudioTab.Frame) { PickLattice(ray); return; }

            PlacedProp hit = Vehicle.Pick(ray);
            if (hit != null) Select(hit);
            else Deselect();
        }

        /// <summary>The FRAME tab's click: nodes beat beams (a beam's endpoints
        /// are always under a node), and an armed link consumes the node click
        /// instead of selecting it.</summary>
        private void PickLattice(Ray ray)
        {
            StudioLattice lat = Lattice;
            if (lat == null) return;

            // An armed crash test consumes every body click as a hit — whack
            // around the car, watch it dent, then Escape out.
            if (_whackArmed)
            {
                if (Vehicle.TrySurfacePoint(ray, out Vector3 local)
                    && lat.PreviewHit(local, Body))
                    _status = "Whack. Escape stops the crash test.";
                return;
            }

            if (lat.PickNode(ray, out int node))
            {
                if (_linkArmed && _latSel == LatticeSel.Node && node != _latIndex)
                    _pendingLink = node;
                else
                    SelectLatticeNode(node);
                return;
            }
            if (lat.PickBeam(ray, out int beam)) { SelectLatticeBeam(beam); return; }
            DeselectLattice();
        }

        private void SelectLatticeNode(int index)
        {
            _latSel = LatticeSel.Node;
            _latIndex = index;
            _linkArmed = false;
            Lattice?.SetHighlight(index, -1);
            AttachNodeGizmo(index);
        }

        private void SelectLatticeBeam(int index)
        {
            _latSel = LatticeSel.Beam;
            _latIndex = index;
            _linkArmed = false;
            Lattice?.SetHighlight(-1, index);
            Gizmo?.Detach();
        }

        private void DeselectLattice()
        {
            _latSel = LatticeSel.None;
            _latIndex = -1;
            _linkArmed = false;
            Lattice?.SetHighlight(-1, -1);
            if (_tab == StudioTab.Frame) Gizmo?.Detach();
        }

        private void AttachNodeGizmo(int index)
        {
            TransformGizmo giz = Gizmo;
            StudioLattice lat = Lattice;
            if (giz == null || lat == null || Vehicle == null) return;
            PropPlacement spec = lat.NodeDragSpec(index);
            if (spec == null) return;
            giz.Mode = GizmoMode.Move;   // a point has nothing to turn or stretch
            // Same unsubscribe-first rule Select() documents.
            giz.Changed -= OnNodeGizmoChanged;
            giz.Changed += OnNodeGizmoChanged;
            giz.Changed -= OnGizmoChanged;
            giz.Attach(spec, Vehicle.transform);
        }

        private void OnNodeGizmoChanged() => Lattice?.CommitDragSpec();

        private void UpdateSelectionKeys()
        {
            if (_tab != StudioTab.Parts) return;
            TransformGizmo giz = Gizmo;
            if (giz == null) return;

            // KeyTable rather than UnityEngine.Input: this project builds with both
            // input backends live, and KeyTable is the one place that knows how to
            // ask either of them.
            if (KeyTable.Pressed(StudioHints.Move)) giz.Mode = GizmoMode.Move;
            if (KeyTable.Pressed(StudioHints.Rotate)) giz.Mode = GizmoMode.Rotate;
            if (KeyTable.Pressed(StudioHints.Scale)) giz.Mode = GizmoMode.Scale;
            if (KeyTable.Pressed(StudioHints.ToggleSnap)) giz.Snapping = !giz.Snapping;
            if (KeyTable.Pressed(StudioHints.ToggleSpace))
                giz.Space = giz.Space == GizmoSpace.Body ? GizmoSpace.Part : GizmoSpace.Body;

            if (_selected == null) return;
            if (KeyTable.Pressed(StudioHints.Duplicate)) _pendingDuplicate = true;
            if (KeyTable.Pressed(StudioHints.Mirror)) _pendingMirror = true;
            if (KeyTable.Pressed(StudioHints.Delete)) _pendingDelete = true;
        }

        private void UpdateLatticeKeys()
        {
            if (_tab != StudioTab.Frame) return;
            TransformGizmo giz = Gizmo;
            if (giz != null)
            {
                giz.Mode = GizmoMode.Move;   // held, not just set on attach
                if (KeyTable.Pressed(StudioHints.ToggleSnap)) giz.Snapping = !giz.Snapping;
            }

            if (KeyTable.Pressed(StudioHints.AddNode)) _pendingAddNode = true;
            if (KeyTable.Pressed(StudioHints.Link) && _latSel == LatticeSel.Node)
            {
                _linkArmed = !_linkArmed;
                _status = _linkArmed ? "Link armed — click the node to connect to." : "";
            }
            if (KeyTable.Pressed(StudioHints.Delete) && _latSel != LatticeSel.None)
                _pendingLatDelete = true;
            if (KeyTable.Pressed(StudioHints.Cancel) && !(giz != null && giz.Dragging))
            {
                if (_whackArmed) { _whackArmed = false; _status = ""; }
                else if (_generateArmed) { _generateArmed = false; _status = ""; }
                else if (_linkArmed) { _linkArmed = false; _status = ""; }
                else DeselectLattice();
            }
        }

        private void Select(PlacedProp prop)
        {
            if (_selected == prop) return;
            if (_selected != null) _selected.SetSelected(false);
            _selected = prop;
            _channelIdx = 0;

            TransformGizmo giz = Gizmo;
            if (_selected == null)
            {
                giz?.Detach();
                return;
            }

            _selected.SetSelected(true);
            _paintBody = false;
            if (giz == null || Vehicle == null) return;
            // Unsubscribe first: Select runs on every click, and += without −=
            // would stack one callback per selection until a single drag re-posed
            // the part a hundred times a frame. The NODE handler comes off too —
            // left subscribed, a part drag would write its motion into whatever
            // node the frame tab last held.
            giz.Changed -= OnGizmoChanged;
            giz.Changed -= OnNodeGizmoChanged;
            giz.Changed += OnGizmoChanged;
            giz.Attach(_selected.Spec, Vehicle.transform);
        }

        private void Deselect() => Select(null);

        private void OnGizmoChanged() => _selected?.ApplyPose();

        /// <summary>Panel rects are cached in UI units because they were drawn
        /// under the <see cref="UIScale"/> matrix; the raw screen pointer has to be
        /// converted the same way.</summary>
        private bool PointerOverUI()
        {
            Vector2 p = UIScale.GuiPointer();
            return _panelRect.Contains(p) || _hintRect.Contains(p);
        }

        // ==================== panel ====================

        private void OnGUI()
        {
            if (bootstrap == null) return;
            GUI.skin = StudioSkin.Skin;
            UIScale.Begin();
            MenuNav.BeginFrame("studio");

            if (Event.current.type == EventType.Layout) Snapshot();

            DrawPanel();
            DrawHintBar();
            if (SculptActive && _hovering) DrawBrushRing();

            MenuNav.EndFrame();
            UIScale.End();
        }

        /// <summary>
        /// Freeze everything the draw path branches on. One method, so a new
        /// control-count driver has one obvious place to be added — and so the
        /// reason they exist is stated once.
        /// </summary>
        private void Snapshot()
        {
            _tabDraw = _tab;
            _bodyToolDraw = _bodyTool;
            _showFilesDraw = _showFiles;
            _bodyCountDraw = Bodies.Count;
            _morphCountDraw = Body != null && Body.MorphNames != null ? Body.MorphNames.Length : 0;
            _catCountDraw = Cats.Count;
            _catIdx = _catCountDraw > 0 ? Mathf.Clamp(_catIdx, 0, _catCountDraw - 1) : 0;
            _itemCountDraw = _catCountDraw > 0 ? Cats[_catIdx].items.Length : 0;

            _hasSelectionDraw = _selected != null;
            _paintBodyDraw = _paintBody || _selected == null;
            FeatureChannels.Binding b = PaintBinding();
            _channelCountDraw = b != null ? b.Names.Count : 0;
            _channelIdx = _channelCountDraw > 0
                ? Mathf.Clamp(_channelIdx, 0, _channelCountDraw - 1) : 0;

            StudioLattice lat = Lattice;
            _hasLatticeDraw = lat != null && lat.HasLattice;
            _latNodeCountDraw = lat != null ? lat.Nodes.Count : 0;
            _latBeamCountDraw = lat != null ? lat.Beams.Count : 0;
            _latticeStaleDraw = lat != null && lat.Stale;
            _generateArmedDraw = _generateArmed;
            _linkArmedDraw = _linkArmed;
            _whackArmedDraw = _whackArmed;
            _previewActiveDraw = lat != null && lat.PreviewActive;
            // A selection whose index died (delete, regenerate) is dropped HERE,
            // so the draw pass never sees an inspector for a thing that is gone.
            int latCount = _latSel == LatticeSel.Node ? _latNodeCountDraw : _latBeamCountDraw;
            if (_latSel != LatticeSel.None && (_latIndex < 0 || _latIndex >= latCount))
                DeselectLattice();
            _latSelDraw = _latSel;
            _latIndexDraw = _latIndex;

            if (_filesStale) { _files = BodyLayoutLibrary.List(); _filesStale = false; }
            _fileCountDraw = _files.Count;
        }

        private FeatureChannels.Binding PaintBinding() =>
            _paintBodyDraw ? Body?.Channels : _selected?.Channels;

        private void DrawPanel()
        {
            _panelRect = PanelLayout.LeftRect(330f);
            GUILayout.BeginArea(_panelRect, GUI.skin.box);
            GUILayout.Label("Vehicle Studio", GarageSkin.Title);

            DrawTabs();
            GUILayout.Space(6f);

            switch (_tabDraw)
            {
                case StudioTab.Body: DrawBodyTab(); break;
                case StudioTab.Parts: DrawPartsTab(); break;
                case StudioTab.Frame: DrawFrameTab(); break;
                case StudioTab.Paint: DrawPaintTab(); break;
                default: DrawDriveTab(); break;
            }

            GUILayout.FlexibleSpace();
            DrawReadout();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, StudioSkin.Sub);
            GUILayout.EndArea();
        }

        private static readonly string[] TabNames = { "BODY", "PARTS", "FRAME", "PAINT", "DRIVE" };

        private void DrawTabs()
        {
            GUILayout.BeginHorizontal();
            for (int i = 0; i < TabNames.Length; i++)
            {
                // Drawn through MenuNav so the pad reaches them, and marked rather
                // than restyled: swapping GUI.skin.button mid-panel mutates a skin
                // every other control on screen is drawing through, and a marker in
                // the label costs nothing and cannot leak.
                string label = (int)_tabDraw == i ? "▸" + TabNames[i] : TabNames[i];
                if (MenuNav.Button(label, GUILayout.Height(StudioSkin.RowHeight)))
                    _tab = (StudioTab)i;
            }
            GUILayout.EndHorizontal();
        }

        // ---- BODY ---------------------------------------------------------------------

        private void DrawBodyTab()
        {
            DeformableBody body = Body;

            int bi = MenuNav.Cycle("Shell", Mathf.Clamp(_bodyIdx, 0, Mathf.Max(0, _bodyCountDraw - 1)),
                                   _bodyCountDraw,
                                   i => i < Bodies.Count ? Bodies[i].label : "?", 70f);
            if (bi != _bodyIdx) { _bodyIdx = bi; _pendingBody = bi; }

            GUILayout.Space(4f);
            int ti = MenuNav.Cycle("Tool", (int)_bodyToolDraw, 2,
                                   i => i == 0 ? "Morph" : "Sculpt", 70f);
            if (ti != (int)_bodyToolDraw) _bodyTool = (BodyTool)ti;

            GUILayout.Space(6f);
            if (_bodyToolDraw == BodyTool.Morph)
            {
                GUILayout.Label("Shape", StudioSkin.Header);
                for (int i = 0; i < _morphCountDraw; i++)
                {
                    string label = i < BodyMorphs.All.Length
                        ? BodyMorphs.Label(BodyMorphs.All[i]) : "Morph " + i;
                    float w01 = body != null ? body.MorphWeight(i) * 0.01f : 0f;
                    if (MenuNav.Slider01(label, ref w01) && body != null)
                        body.UpdateVehicleMorph(i, w01 * 100f);
                }
                if (MenuNav.Button("Reset shape") && body != null) body.ResetMorphs();
            }
            else
            {
                GUILayout.Label("Brush", StudioSkin.Header);
                MenuNav.Slider01($"Radius {RadiusM * 1000f:0} mm", ref _radius01);
                MenuNav.Slider01($"Strength {Strength:0.00}×", ref _strength01);
                int d = MenuNav.Cycle("Push", (int)_dir, DirLabels.Length, i => DirLabels[i], 70f);
                if (d != (int)_dir) _dir = (SculptDir)d;
                if (MenuNav.Button("Reset sculpt") && body != null) body.ResetOffsets();
            }

            GUILayout.Space(6f);
            DrawWheelbase(body);
        }

        private void DrawWheelbase(DeformableBody body)
        {
            if (body == null) return;
            GUILayout.Label("Stance", StudioSkin.Header);
            float lo = body.WheelbaseMin, hi = body.WheelbaseMax;
            float t = Mathf.InverseLerp(lo, hi, body.WheelbaseM);
            if (MenuNav.Slider01($"Wheelbase {body.WheelbaseM * 1000f:0} mm", ref t))
                body.SetWheelbase(Mathf.Lerp(lo, hi, t));
        }

        // ---- PARTS --------------------------------------------------------------------

        private Vector2 _paletteScroll;

        private void DrawPartsTab()
        {
            int ci = MenuNav.Cycle("Group", _catIdx, _catCountDraw,
                                   i => i < Cats.Count ? Cats[i].title : "?", 70f);
            if (ci != _catIdx) _catIdx = ci;

            GUILayout.Label($"{_itemCountDraw} parts — click one, then click the car",
                            StudioSkin.Sub);

            // A fixed height so the palette cannot push the inspector off the
            // bottom of the panel on a body with eighty harvestable features.
            _paletteScroll = GUILayout.BeginScrollView(_paletteScroll, GUILayout.Height(210f));
            if (_catCountDraw > 0)
            {
                StudioPartLibrary.Category cat = Cats[_catIdx];
                for (int i = 0; i < _itemCountDraw && i < cat.items.Length; i++)
                {
                    StudioPartLibrary.Entry e = cat.items[i];
                    Texture2D icon = StudioIcons.Get(e.source);
                    if (MenuNav.Button("      " + e.label, GUILayout.Height(34f)))
                        _pendingAdd = e.source;
                    if (icon != null && Event.current.type == EventType.Repaint)
                    {
                        // Drawn over the button's left edge rather than inside it:
                        // an ImageAbove button would make every row 60 px tall and
                        // the list unusable at eighty entries.
                        Rect r = GUILayoutUtility.GetLastRect();
                        GUI.DrawTexture(new Rect(r.x + 3f, r.y + 2f, 30f, 30f), icon);
                    }
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            DrawToolRow();
            GUILayout.Space(4f);
            DrawSelectionInspector();
        }

        private void DrawToolRow()
        {
            TransformGizmo giz = Gizmo;
            if (giz == null) return;

            int m = MenuNav.Cycle("Tool", (int)giz.Mode, 3,
                                  i => i == 0 ? "Move" : i == 1 ? "Turn" : "Size", 70f);
            if (m != (int)giz.Mode) giz.Mode = (GizmoMode)m;

            GUILayout.BeginHorizontal();
            bool snap = MenuNav.Toggle(giz.Snapping, giz.Snapping ? " Snap ON" : " Snap off");
            giz.Snapping = snap;
            int sp = MenuNav.Cycle("", (int)giz.Space, 2, i => i == 0 ? "Part axes" : "Car axes", 0f);
            giz.Space = (GizmoSpace)sp;
            GUILayout.EndHorizontal();

            if (giz.Snapping)
                GUILayout.Label($"grid {giz.SnapPos * 1000f:0} mm · {giz.SnapDeg:0}°",
                                StudioSkin.Sub);
            GUILayout.Label(StudioHints.ToolTip(giz.Mode), StudioSkin.Sub);
        }

        private void DrawSelectionInspector()
        {
            if (!_hasSelectionDraw)
            {
                GUILayout.Label("Nothing selected.", StudioSkin.Sub);
                if (MenuNav.Button("Remove every part")) _pendingClearParts = true;
                return;
            }

            PropPlacement spec = _selected.Spec;
            GUILayout.Label(spec.label, StudioSkin.Header);
            GUILayout.Label(spec.source, StudioSkin.Sub);

            Vector3 p = spec.localPos;
            GUILayout.Label($"x {p.x * 1000f:0} · y {p.y * 1000f:0} · z {p.z * 1000f:0} mm",
                            StudioSkin.Sub);
            Vector3 sc = spec.Scale;
            string size = spec.IsUniformScale
                ? $"{sc.x:0.00}×"
                : $"{sc.x:0.00} · {sc.y:0.00} · {sc.z:0.00}×";
            GUILayout.Label($"turn {spec.euler.y:0}°   size {size}", StudioSkin.Sub);

            GUILayout.BeginHorizontal();
            if (MenuNav.Button("Copy")) _pendingDuplicate = true;
            if (MenuNav.Button("Mirror")) _pendingMirror = true;
            if (MenuNav.Button("Remove")) _pendingDelete = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (MenuNav.Button("Centre"))
            {
                spec.localPos = new Vector3(0f, spec.localPos.y, spec.localPos.z);
                _selected.ApplyPose();
            }
            if (MenuNav.Button("Upright"))
            {
                spec.euler = Vector3.zero;
                _selected.ApplyPose();
            }
            if (MenuNav.Button("Size 1×"))
            {
                spec.ResetScale();
                _selected.ApplyPose();
            }
            if (MenuNav.Button("Home")) SendHome(spec);
            GUILayout.EndHorizontal();
        }

        // ---- FRAME --------------------------------------------------------------------

        /// <summary>Log slider position → multiplier 0.1×..10× of a default.
        /// Log rather than linear because "half as stiff" and "twice as stiff"
        /// should be the same distance from the middle.</summary>
        private static float LogFactor(float t01) => Mathf.Pow(10f, 2f * t01 - 1f);

        private static float LogT(float value, float reference) =>
            Mathf.Clamp01((Mathf.Log10(Mathf.Max(value / Mathf.Max(reference, 1e-9f), 1e-4f)) + 1f) * 0.5f);

        private void DrawFrameTab()
        {
            GUILayout.Label("Crash frame", StudioSkin.Header);
            GUILayout.Label(_hasLatticeDraw
                                ? $"{_latNodeCountDraw} nodes · {_latBeamCountDraw} beams"
                                : "No frame yet — Generate wraps the body in one.",
                            StudioSkin.Sub);
            if (_latticeStaleDraw)
                GUILayout.Label("Body changed since this frame was made — Generate " +
                                "refits it (manual edits are lost).", StudioSkin.Danger);

            float len = Body != null ? Body.BodyLengthM : 0.42f;
            float spacingMm = LatticeBuilder.SpacingFor(_fidelity01, len) * 1000f;
            MenuNav.Slider01($"Detail {spacingMm:0} mm grid", ref _fidelity01);

            GUILayout.BeginHorizontal();
            string genLabel = _generateArmedDraw ? "Really generate?"
                            : _hasLatticeDraw ? "Regenerate" : "Generate";
            if (MenuNav.Button(genLabel, GUILayout.Height(34f)))
            {
                // Two presses when a frame (and possibly hand edits) would be
                // destroyed; one when there is nothing to lose. Armed state dies
                // on any other action — see HandleTabChange.
                if (_generateArmed || !_hasLatticeDraw)
                {
                    _pendingGenerate = true;
                    _generateArmed = false;
                }
                else
                {
                    _generateArmed = true;
                    _status = "This rebuilds the frame and discards manual edits — press again.";
                }
            }
            if (_hasLatticeDraw && MenuNav.Button("Remove", GUILayout.Width(80f)))
                _pendingClearLattice = true;
            GUILayout.EndHorizontal();

            TransformGizmo giz = Gizmo;
            if (giz != null)
            {
                bool snap = MenuNav.Toggle(giz.Snapping, giz.Snapping ? " Snap ON" : " Snap off");
                giz.Snapping = snap;
            }

            // Damage feel, tuned live: the slider is the SAME number the
            // driving path scales hits by, and the crash test whacks the body
            // through the same solver — what dents here dents on track.
            if (_hasLatticeDraw)
            {
                StudioLattice lat = Lattice;
                float dmg = lat != null ? lat.Damage01 : 0.5f;
                if (MenuNav.Slider01(
                        $"Damage {LatticeBuilder.DamageScale(dmg):0.0}×", ref dmg)
                    && lat != null)
                    lat.Damage01 = dmg;

                GUILayout.BeginHorizontal();
                if (MenuNav.Button(_whackArmedDraw ? "Whacking — click the body"
                                                   : "Crash test", GUILayout.Height(30f)))
                {
                    _whackArmed = !_whackArmed;
                    _linkArmed = false;
                    _status = _whackArmed ? "Click the body to whack it." : "";
                }
                if (_previewActiveDraw && MenuNav.Button("Repair", GUILayout.Width(80f)))
                    _pendingRepair = true;
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6f);
            if (_linkArmedDraw)
                GUILayout.Label("Link armed — click the node to connect to.", StudioSkin.Danger);
            DrawLatticeInspector();
        }

        private void DrawLatticeInspector()
        {
            StudioLattice lat = Lattice;
            if (lat == null) return;

            switch (_latSelDraw)
            {
                case LatticeSel.Node when _latIndexDraw >= 0 && _latIndexDraw < lat.Nodes.Count:
                    DrawNodeInspector(lat, _latIndexDraw);
                    break;
                case LatticeSel.Beam when _latIndexDraw >= 0 && _latIndexDraw < lat.Beams.Count:
                    DrawBeamInspector(lat, _latIndexDraw);
                    break;
                default:
                    GUILayout.Label("Click a node or beam to edit it.", StudioSkin.Sub);
                    break;
            }
        }

        private void DrawNodeInspector(StudioLattice lat, int index)
        {
            LatticeNode node = lat.Nodes[index];
            GUILayout.Label($"Node {index}", StudioSkin.Header);
            Vector3 p = node.localPos;
            GUILayout.Label($"x {p.x * 1000f:0} · y {p.y * 1000f:0} · z {p.z * 1000f:0} mm",
                            StudioSkin.Sub);

            // The slider spans 0.1×..10× of the derived default, and writes the
            // RESOLVED value — the saved file is self-contained either way.
            float def = LatticeBuilder.ResolveMass(0f, lat.Nodes.Count);
            float mass = LatticeBuilder.ResolveMass(node.mass, lat.Nodes.Count);
            float t = LogT(mass, def);
            if (MenuNav.Slider01($"Mass {mass * 1000f:0.0} g", ref t))
                node.mass = def * LogFactor(t);

            GUILayout.BeginHorizontal();
            if (MenuNav.Button(_linkArmedDraw ? "Pick target…" : "Link"))
            {
                _linkArmed = !_linkArmed;
                _status = _linkArmed ? "Link armed — click the node to connect to." : "";
            }
            if (MenuNav.Button("Remove")) _pendingLatDelete = true;
            GUILayout.EndHorizontal();
        }

        private void DrawBeamInspector(StudioLattice lat, int index)
        {
            LatticeBeam beam = lat.Beams[index];
            GUILayout.Label($"Beam {beam.a} — {beam.b}", StudioSkin.Header);
            float rest = LatticeBuilder.RestLength(lat.Nodes, beam);
            GUILayout.Label($"rest {rest * 1000f:0.0} mm", StudioSkin.Sub);

            float defMass = LatticeBuilder.ResolveMass(0f, lat.Nodes.Count);
            float nAvg = lat.Nodes.Count > 0 ? 2f * lat.Beams.Count / lat.Nodes.Count : 1f;
            float defK = LatticeBuilder.DefaultSpring(defMass, nAvg);

            float k = LatticeBuilder.ResolveSpring(beam.spring, defMass, nAvg);
            float tk = LogT(k, defK);
            if (MenuNav.Slider01($"Spring {k:0.0} N/m", ref tk))
                beam.spring = defK * LogFactor(tk);

            float z = LatticeBuilder.ResolveDampingRatio(beam.dampingRatio);
            if (MenuNav.Slider01($"Damping ζ {z:0.00}", ref z))
                beam.dampingRatio = z;

            float bs = LatticeBuilder.ResolveBreakStrain(beam.breakStrain);
            float tb = Mathf.InverseLerp(0.05f, 1f, bs);
            if (MenuNav.Slider01($"Breaks at {bs * 100f:0} % stretch", ref tb))
                beam.breakStrain = Mathf.Lerp(0.05f, 1f, tb);

            if (MenuNav.Button("Remove")) _pendingLatDelete = true;
        }

        /// <summary>Put a harvested shell feature back where it sat on its own
        /// body — the one placement that has an exactly right answer.</summary>
        private void SendHome(PropPlacement spec)
        {
            PartSource ps = PartSource.Parse(spec.source);
            if (ps.kind != PartKind.ShellFeature)
            {
                _status = "Only a part taken off a shell has a home to go back to.";
                return;
            }
            if (!ShellFeatureSource.TryHome(ps.id, ps.channel, out Vector3 home))
            {
                _status = "That shell is not in this build.";
                return;
            }
            spec.localPos = home;
            spec.euler = Vector3.zero;
            spec.ResetScale();
            _selected.ApplyPose();
            _status = "Put back where it came from.";
        }

        // ---- PAINT --------------------------------------------------------------------

        private void DrawPaintTab()
        {
            int t = MenuNav.Cycle("Target", _paintBodyDraw ? 0 : 1, 2,
                                  i => i == 0 ? "Body" : "Selected part", 70f);
            if ((t == 0) != _paintBodyDraw)
            {
                if (t == 1 && _selected == null) _status = "Select a part first.";
                else _paintBody = t == 0;
            }

            FeatureChannels.Binding binding = PaintBinding();
            if (binding == null || _channelCountDraw == 0)
            {
                GUILayout.Label("Nothing paintable here.", StudioSkin.Sub);
                return;
            }

            int ch = MenuNav.Cycle("Feature", _channelIdx, _channelCountDraw,
                                   i => i < binding.Names.Count
                                        ? FeatureChannels.Label(binding.Names[i]) : "?", 70f);
            if (ch != _channelIdx)
            {
                _channelIdx = ch;
                _edit = StudioPaint.Sample(binding, _channelIdx);
                LoadExistingTint(binding);
            }

            string channel = binding.Names[Mathf.Clamp(_channelIdx, 0, binding.Names.Count - 1)];
            GUILayout.Label($"{binding.Triangles[_channelIdx]} triangles", StudioSkin.Sub);

            // Colour, as three sliders. A wheel or a hex field would be prettier
            // and would need a pad-navigable control this project does not have;
            // three sliders are reachable with a stick on the first try.
            float r = _edit.color.r, g = _edit.color.g, b = _edit.color.b;
            bool moved = MenuNav.Slider01("Red", ref r);
            moved |= MenuNav.Slider01("Green", ref g);
            moved |= MenuNav.Slider01("Blue", ref b);
            if (moved) _edit.color = new Color(r, g, b, _edit.color.a);

            float metal = Mathf.Max(0f, _edit.metallic);
            if (MenuNav.Slider01("Metal", ref metal)) _edit.metallic = metal;
            float gloss = _edit.smoothness < 0f ? 0.5f : _edit.smoothness;
            if (MenuNav.Slider01("Gloss", ref gloss)) _edit.smoothness = gloss;
            float glow = Mathf.Max(0f, _edit.emission) / MaxEmission;
            if (MenuNav.Slider01("Glow", ref glow)) _edit.emission = glow * MaxEmission;

            int fi = MenuNav.Cycle("Finish", StudioTextures.IndexOf(_edit.texture),
                                   StudioTextures.All.Length,
                                   i => StudioTextures.All[i].label, 70f);
            if (fi != StudioTextures.IndexOf(_edit.texture))
            {
                _edit.texture = StudioTextures.All[fi].key;
                _edit.tiling = 0f;   // 0 = the finish's own default
            }

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (MenuNav.Button("Apply")) ApplyTint(channel);
            if (MenuNav.Button("Clear")) ClearTint(channel);
            GUILayout.EndHorizontal();

            if (_paintBodyDraw && Body != null)
            {
                bool hidden = Body.IsHidden(channel);
                bool want = MenuNav.Toggle(hidden, hidden ? " Hidden" : " Hide this feature");
                if (want != hidden)
                {
                    Body.SetChannelHidden(channel, want);
                    _status = want
                        ? "Removed — it is out of the collider and the drag figure too."
                        : "Back on the car.";
                }
            }
        }

        /// <summary>Emission the "Glow" slider reaches at 1. Beyond about this the
        /// Standard shader's bloom-free look stops changing.</summary>
        private const float MaxEmission = 4f;

        /// <summary>Pull an existing tint for this channel into the editing copy,
        /// so opening the panel on a painted feature shows what it is wearing
        /// rather than resetting it.</summary>
        private void LoadExistingTint(FeatureChannels.Binding binding)
        {
            FeatureTint[] tints = CurrentTints();
            if (tints == null) return;
            string channel = binding.Names[Mathf.Clamp(_channelIdx, 0, binding.Names.Count - 1)];
            foreach (FeatureTint t in tints)
                if (t != null && t.channel == channel) { _edit = t.Clone(); return; }
        }

        private FeatureTint[] CurrentTints() =>
            _paintBodyDraw ? Body?.Tints : _selected?.Spec?.tints;

        private void ApplyTint(string channel)
        {
            _edit.channel = channel;
            FeatureTint[] next = Upsert(CurrentTints(), _edit.Clone());
            StoreTints(next);
            _status = $"Painted {FeatureChannels.Label(channel)}.";
        }

        private void ClearTint(string channel)
        {
            FeatureTint[] next = Remove(CurrentTints(), channel);
            StoreTints(next);
            _edit = StudioPaint.Sample(PaintBinding(), _channelIdx);
            _status = $"{FeatureChannels.Label(channel)} back to how it shipped.";
        }

        private void StoreTints(FeatureTint[] tints)
        {
            if (_paintBodyDraw) Body?.SetTints(tints);
            else if (_selected?.Spec != null)
            {
                _selected.Spec.tints = tints;
                _selected.ApplyPaint();
            }
        }

        private static FeatureTint[] Upsert(FeatureTint[] list, FeatureTint tint)
        {
            var next = new List<FeatureTint>();
            if (list != null)
                foreach (FeatureTint t in list)
                    if (t != null && t.channel != tint.channel) next.Add(t);
            next.Add(tint);
            return next.ToArray();
        }

        private static FeatureTint[] Remove(FeatureTint[] list, string channel)
        {
            if (list == null) return null;
            var next = new List<FeatureTint>();
            foreach (FeatureTint t in list)
                if (t != null && t.channel != channel) next.Add(t);
            return next.Count > 0 ? next.ToArray() : null;
        }

        // ---- DRIVE --------------------------------------------------------------------

        private void DrawDriveTab()
        {
            GUILayout.Label("Take it out", StudioSkin.Header);
            if (MenuNav.Button("Test drive", GUILayout.Height(38f))) _pendingDrive = true;
            GUILayout.Label("Pause ▸ Garage on the track brings you back here with " +
                            "every edit intact.", StudioSkin.Sub);

            GUILayout.Space(8f);
            GUILayout.Label("Save as a vehicle", StudioSkin.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50f));
            _vehicleName = GUILayout.TextField(_vehicleName ?? "", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (MenuNav.Button("Save vehicle") && Vehicle != null)
            {
                string path = StudioSession.SaveAsVehicle(_vehicleName, Vehicle.Capture());
                _status = path != null
                    ? $"Saved to {path} — it is in the garage's load list now."
                    : "Could not save that vehicle; see the console.";
            }

            GUILayout.Space(8f);
            GUILayout.Label("Layout file", StudioSkin.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(50f));
            _saveName = GUILayout.TextField(_saveName ?? "", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (MenuNav.Button("Save layout") && Vehicle != null)
            {
                string path = BodyLayoutLibrary.SaveVehicleToFile(_saveName, Vehicle.Capture());
                _status = path != null ? "Layout saved." : "Could not save that layout.";
                _filesStale = true;
            }

            bool show = MenuNav.Toggle(_showFilesDraw, " Load…");
            if (show != _showFilesDraw)
            {
                _showFiles = show;
                if (show) _filesStale = true;
            }
            if (!_showFilesDraw) return;

            if (_fileCountDraw == 0)
            {
                GUILayout.Label("no saved layouts", StudioSkin.Sub);
                return;
            }
            _fileIdx = MenuNav.Cycle("", Mathf.Clamp(_fileIdx, 0, _fileCountDraw - 1),
                                     _fileCountDraw, i => _files[i], 0f);
            if (MenuNav.Button("Load selected"))
                _pendingLoad = _files[Mathf.Clamp(_fileIdx, 0, _fileCountDraw - 1)];
        }

        // ---- readout + hints -----------------------------------------------------------

        /// <summary>
        /// The measured drag of the shape currently on the stand, beside what the
        /// catalogue says the undeformed body is. Plain labels, no nav — there is
        /// nothing here to activate.
        /// </summary>
        private void DrawReadout()
        {
            BodyDragReadout r = bootstrap.Readout;
            DeformableBody body = Body;
            GUILayout.Label("Measured", StudioSkin.Header);

            if (r == null || !r.HasMeasured)
            {
                GUILayout.Label("not measured yet", StudioSkin.Sub);
                return;
            }

            string cdBase = r.HasBaseline ? $"  (catalogue {r.Baseline.cd:0.000})" : "";
            GUILayout.Label($"Cd  {r.Latest.cd:0.000}{cdBase}", StudioSkin.Sub);
            GUILayout.Label($"Frontal area  {r.Latest.frontalArea:0.00000} m²", StudioSkin.Sub);

            float cda = r.Latest.cd * r.Latest.frontalArea;
            string delta = r.HasBaseline ? $"   {r.CdaChangePercent:+0.0;-0.0;0.0} %" : "";
            GUILayout.Label($"Cd·A  {cda:0.00000} m²{delta}", StudioSkin.Sub);

            if (body != null)
                GUILayout.Label($"{body.VertexCount} verts · {r.Triangles} tris · " +
                                $"{body.OffsetCount} pulled · " +
                                $"{(Vehicle != null ? Vehicle.Props.Count : 0)} parts",
                                StudioSkin.Sub);
        }

        /// <summary>The prompt strip under the viewport — which buttons do what,
        /// named for the device in the player's hands.</summary>
        private void DrawHintBar()
        {
            _hintRect = PanelLayout.HintRect(720f, 30f);
            GUI.Box(_hintRect, GUIContent.none);
            bool hasSel = _tabDraw == StudioTab.Frame
                ? _latSelDraw != LatticeSel.None
                : _hasSelectionDraw;
            GUI.Label(_hintRect, StudioHints.For(_tabDraw, hasSel), StudioSkin.Hint);
        }

        /// <summary>The brush footprint, projected to screen so its size is the
        /// size it will actually bite — a fixed-pixel cursor would lie about the
        /// radius the moment anybody zoomed.</summary>
        private void DrawBrushRing()
        {
            if (Event.current.type != EventType.Repaint || bootstrap.Cam == null) return;

            Camera cam = bootstrap.Cam;
            Vector3 c = cam.WorldToScreenPoint(_hoverPoint);
            if (c.z <= 0f) return;
            Vector3 e = cam.WorldToScreenPoint(_hoverPoint + cam.transform.right * RadiusM);
            float px = Mathf.Abs(e.x - c.x);
            if (px < 2f || px > 4000f) return;

            // Screen pixels → UI units, because everything here is drawn under the
            // UIScale matrix.
            float s = UIScale.S;
            var r = new Rect((c.x - px) / s, (Screen.height - c.y - px) / s,
                             2f * px / s, 2f * px / s);
            Color prev = GUI.color;
            GUI.color = new Color(GarageSkin.Accent.r, GarageSkin.Accent.g,
                                  GarageSkin.Accent.b, 0.85f);
            GUI.DrawTexture(r, BodyEdMaterials.BrushRing());
            GUI.color = prev;
        }
    }
}
