using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.UI;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// The body editor's panel and its pointer handling.
    ///
    /// IMGUI, gamepad-navigable through <see cref="MenuNav"/>, and following that
    /// class's ground rule to the letter: <b>every value that decides WHICH
    /// controls exist is snapshotted on the Layout pass</b> and drawn from the
    /// snapshot, so a mode switch or a body swap made from a handler cannot change
    /// the control count between a Layout and its paired Repaint. Actions that
    /// rebuild the world — opening another body, loading a layout — are recorded
    /// as an intent here and carried out from <see cref="Update"/>, which is
    /// always pass-safe.
    ///
    /// The pointer work is deliberately thin: it turns screen coordinates into a
    /// ray and hands the ray to <see cref="DeformableBody"/>. Nothing about
    /// falloff, welding or commit timing lives here.
    /// </summary>
    public sealed class BodyEditorUI : MonoBehaviour
    {
        public BodyEditorBootstrap bootstrap;

        private enum Mode { Morph, Sculpt }

        // ---- live state -------------------------------------------------------------
        private Mode _mode = Mode.Morph;
        private int _bodyIdx;
        private int _fileIdx;
        private bool _showFiles;
        private string _saveName = "layout";
        private float _radius01 = 0.35f;
        private float _strength01 = 0.5f;
        private SculptDir _dir = SculptDir.SurfaceNormal;

        // ---- Layout-pass snapshots (see the class note) -----------------------------
        private Mode _modeDraw;
        private bool _showFilesDraw;
        private int _morphCountDraw;
        private int _bodyCountDraw;
        private int _fileCountDraw;

        // ---- deferred intents, executed from Update ---------------------------------
        private int _pendingBody = -1;
        private string _pendingLoad;

        // ---- pointer ----------------------------------------------------------------
        private bool _sculpting;
        private bool _hovering;
        private Vector3 _hoverPoint;
        private Rect _panelRect;
        private List<string> _files = new List<string>();
        private bool _filesStale = true;

        private static readonly string[] DirLabels = { "Normal", "Vertical", "Lateral" };

        private IReadOnlyList<BodyDef> Bodies => BodyMeshSource.Eligible();

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
                float len = bootstrap != null && bootstrap.Body != null
                    ? bootstrap.Body.BodyLengthM : CarVehicle.BodyMeshAuthorSize.z;
                return Mathf.Lerp(RadiusMinPerLength, RadiusMaxPerLength, _radius01) * len;
            }
        }

        /// <summary>Pointer travel is multiplied by this before it becomes surface
        /// travel. Below 1 the brush drags more slowly than the mouse, which is
        /// what makes a small correction possible at all.</summary>
        private float Strength => Mathf.Lerp(0.15f, 2f, _strength01);

        // ---- input --------------------------------------------------------------------

        private void Update()
        {
            if (bootstrap == null) return;

            // Deferred intents first: doing these here rather than from OnGUI is
            // what keeps a rebuilt body from changing the control count halfway
            // through an IMGUI pass.
            if (_pendingBody >= 0)
            {
                var list = Bodies;
                if (_pendingBody < list.Count) bootstrap.SetBody(list[_pendingBody]);
                _pendingBody = -1;
                _sculpting = false;
            }
            if (_pendingLoad != null)
            {
                bootstrap.LoadLayout(_pendingLoad);
                _pendingLoad = null;
                _sculpting = false;
            }

            DeformableBody body = bootstrap.Body;
            if (body == null) return;

            bool over = PointerOverUI();

            if (_mode == Mode.Sculpt)
            {
                Ray ray = bootstrap.Cam.ScreenPointToRay(InputReader.PointerPosition());

                if (!_sculpting && !over && InputReader.LeftMousePressed())
                    _sculpting = body.TryBeginSculpt(ray, RadiusM);

                if (_sculpting)
                {
                    if (InputReader.LeftMouseHeld()) body.SculptTo(ray, _dir, Strength);
                    if (InputReader.LeftMouseReleased()) { body.EndSculpt(); _sculpting = false; }
                }
                else
                {
                    // One raycast a frame against one collider, purely so the
                    // brush ring can be drawn where it will actually bite. It
                    // cooks nothing and reads the same stale-between-edits
                    // collider the stroke would have used.
                    _hovering = false;
                    MeshCollider col = body.Collision != null ? body.Collision.Collider : null;
                    if (!over && col != null && col.Raycast(ray, out RaycastHit hit, 1000f))
                    {
                        _hoverPoint = hit.point;
                        _hovering = true;
                    }
                }
            }
            else
            {
                if (_sculpting) { body.EndSculpt(); _sculpting = false; }
                _hovering = false;
            }

            if (bootstrap.Orbit != null)
            {
                bootstrap.Orbit.blockDrag = over || _sculpting;
                bootstrap.Orbit.blockZoom = over;
            }
        }

        /// <summary>Panel rects are cached in UI units because they were drawn
        /// under the <see cref="UIScale"/> matrix; the raw screen pointer has to be
        /// converted the same way.</summary>
        private bool PointerOverUI() => _panelRect.Contains(UIScale.GuiPointer());

        // ---- panel ----------------------------------------------------------------

        private void OnGUI()
        {
            if (bootstrap == null) return;
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();
            MenuNav.BeginFrame("bodyed");

            if (Event.current.type == EventType.Layout)
            {
                _modeDraw = _mode;
                _showFilesDraw = _showFiles;
                _bodyCountDraw = Bodies.Count;
                _morphCountDraw = bootstrap.Body != null && bootstrap.Body.MorphNames != null
                    ? bootstrap.Body.MorphNames.Length : 0;
                if (_filesStale) { _files = BodyLayoutLibrary.List(); _filesStale = false; }
                _fileCountDraw = _files.Count;
            }

            DrawPanel();
            if (_modeDraw == Mode.Sculpt && _hovering) DrawBrushRing();

            MenuNav.EndFrame();
            UIScale.End();
        }

        private void DrawPanel()
        {
            _panelRect = PanelLayout.LeftRect(280f);
            GUILayout.BeginArea(_panelRect, GUI.skin.box);
            GUILayout.Label("Body editor", GarageSkin.Title);

            DeformableBody body = bootstrap.Body;

            // --- which body ---
            int bi = MenuNav.Cycle("Body", Mathf.Clamp(_bodyIdx, 0, Mathf.Max(0, _bodyCountDraw - 1)),
                                   _bodyCountDraw,
                                   i => i < Bodies.Count ? Bodies[i].label : "?", 60f);
            if (bi != _bodyIdx) { _bodyIdx = bi; _pendingBody = bi; }

            GUILayout.Space(4f);
            int mi = MenuNav.Cycle("Mode", (int)_modeDraw, 2,
                                   i => i == 0 ? "Morph" : "Sculpt", 60f);
            if (mi != (int)_modeDraw) _mode = (Mode)mi;

            GUILayout.Space(6f);

            if (_modeDraw == Mode.Morph) DrawMorphPage(body);
            else DrawSculptPage();

            GUILayout.Space(6f);
            DrawWheelbase(body);

            GUILayout.Space(6f);
            DrawFilePage();

            GUILayout.FlexibleSpace();
            DrawReadout();
            GUILayout.EndArea();
        }

        private void DrawMorphPage(DeformableBody body)
        {
            GUILayout.Label("Morphs", GarageSkin.Header);
            for (int i = 0; i < _morphCountDraw; i++)
            {
                string label = i < BodyMorphs.All.Length
                    ? BodyMorphs.Label(BodyMorphs.All[i]) : "Morph " + i;
                float w01 = body != null ? body.MorphWeight(i) * 0.01f : 0f;
                // GarageSkin.Slider01 prints the value as a percentage itself, so
                // the label carries only the name.
                if (MenuNav.Slider01(label, ref w01) && body != null)
                    body.UpdateVehicleMorph(i, w01 * 100f);
            }
            if (MenuNav.Button("Reset morphs") && body != null) body.ResetMorphs();
        }

        private void DrawSculptPage()
        {
            GUILayout.Label("Brush", GarageSkin.Header);
            MenuNav.Slider01($"Radius {RadiusM * 1000f:0} mm", ref _radius01);
            MenuNav.Slider01($"Strength {Strength:0.00}×", ref _strength01);
            int d = MenuNav.Cycle("Push", (int)_dir, DirLabels.Length, i => DirLabels[i], 60f);
            if (d != (int)_dir) _dir = (SculptDir)d;
            if (MenuNav.Button("Reset sculpt") && bootstrap.Body != null)
                bootstrap.Body.ResetOffsets();
        }

        private void DrawWheelbase(DeformableBody body)
        {
            if (body == null) return;
            GUILayout.Label("Layout", GarageSkin.Header);
            float lo = body.WheelbaseMin, hi = body.WheelbaseMax;
            float t = Mathf.InverseLerp(lo, hi, body.WheelbaseM);
            if (MenuNav.Slider01($"Wheelbase {body.WheelbaseM * 1000f:0} mm", ref t))
                body.SetWheelbase(Mathf.Lerp(lo, hi, t));
        }

        private void DrawFilePage()
        {
            GUILayout.Label("Layout file", GarageSkin.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(44f));
            _saveName = GUILayout.TextField(_saveName ?? "", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            if (MenuNav.Button("Save") && bootstrap.Body != null)
            {
                bootstrap.Body.SaveVehicleToFile(_saveName);
                _filesStale = true;
            }

            bool show = MenuNav.Toggle(_showFilesDraw, "Load…");
            if (show != _showFilesDraw)
            {
                _showFiles = show;
                if (show) _filesStale = true;
            }

            if (!_showFilesDraw) return;

            if (_fileCountDraw == 0)
            {
                GUILayout.Label("no saved layouts", GarageSkin.StatLabel);
                return;
            }
            _fileIdx = MenuNav.Cycle("", Mathf.Clamp(_fileIdx, 0, _fileCountDraw - 1),
                                     _fileCountDraw, i => _files[i], 0f);
            if (MenuNav.Button("Load selected"))
                _pendingLoad = _files[Mathf.Clamp(_fileIdx, 0, _fileCountDraw - 1)];
        }

        /// <summary>
        /// The measured drag of the shape currently on the stand, beside what the
        /// catalogue says the undeformed body is. Plain labels, no nav — there is
        /// nothing here to activate.
        /// </summary>
        private void DrawReadout()
        {
            BodyDragReadout r = bootstrap.Readout;
            DeformableBody body = bootstrap.Body;
            GUILayout.Label("Measured", GarageSkin.Header);

            if (r == null || !r.HasMeasured)
            {
                GUILayout.Label("not measured yet", GarageSkin.StatLabel);
                return;
            }

            string cdBase = r.HasBaseline ? $"  (catalogue {r.Baseline.cd:0.000})" : "";
            string aBase = r.HasBaseline ? $"  ({r.Baseline.frontalArea:0.00000})" : "";
            GUILayout.Label($"Cd  {r.Latest.cd:0.000}{cdBase}", GarageSkin.StatLabel);
            GUILayout.Label($"Frontal area  {r.Latest.frontalArea:0.00000} m²{aBase}",
                            GarageSkin.StatLabel);

            float cda = r.Latest.cd * r.Latest.frontalArea;
            string delta = r.HasBaseline ? $"   {r.CdaChangePercent:+0.0;-0.0;0.0} %" : "";
            GUILayout.Label($"Cd·A  {cda:0.00000} m²{delta}", GarageSkin.StatLabel);

            if (body != null)
                GUILayout.Label($"{body.VertexCount} verts · {r.Triangles} tris · " +
                                $"{body.OffsetCount} pulled", GarageSkin.StatLabel);

            GUILayout.Label(_modeDraw == Mode.Sculpt
                ? "LMB drag on the body to sculpt · RMB orbit · wheel zoom"
                : "RMB orbit · MMB pan · wheel zoom", GarageSkin.StatLabel);
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
