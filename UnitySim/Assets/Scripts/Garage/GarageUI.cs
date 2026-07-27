using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// KSP-VAB-style garage UI (IMGUI). Left: category tabs — BODY (shape/size/
    /// colour/mass/steering) and PARTS (an icon palette that starts a drag-place).
    /// Right: the parts list + a per-part inspector. Parts are placed and moved by
    /// grab-and-drag with a translucent ghost; a mirror mode makes symmetric twins.
    /// Bottom-left: a live stats readout. Undo/redo (Ctrl+Z / Ctrl+Y), focus (F),
    /// pan (MMB), and an aim-vector toggle round out the editor feel.
    /// </summary>
    public sealed class GarageUI : MonoBehaviour
    {
        public GarageBootstrap bootstrap;

        private enum DragState { Idle, MouseDownOnMarker, PlacingNew, DraggingExisting }

        private PartType _selType = PartType.Wheel;
        private int _sel = -1;
        private string _nameField = "";
        private bool _showLoad;
        private string _status = "";
        private Vector2 _loadScroll, _partScroll, _leftScroll, _inspectorScroll;

        private int _leftTab;                 // 0 = BODY, 1 = PARTS, 2 = PAINT
        private bool _mirrorMode;
        private bool _snapEnabled;            // grid-snap placement (N)
        private const float SnapPos = 0.005f; // 5 mm position grid

        // Body paint mode (PAINT tab).
        private readonly BodyPainter _painter = new BodyPainter();
        private int _paintStroke;             // unique undo key per stroke

        // Drag machine
        private DragState _drag = DragState.Idle;
        private Vector2 _downPos;
        private PartType _downType;
        private int _downIndex;
        private PartGhost _ghost, _ghostTwin;
        private PartType _placingKind;        // which spec family the drag edits
        private WheelSpec _pendingWheel;
        private SensorSpec _pendingSensor;
        private AeroSpec _pendingAero;
        private BatterySpec _pendingBattery;
        private AntennaSpec _pendingAntenna;
        private LightSpec _pendingLight;
        private int _dragTwinIndex = -1;

        private Rect _leftRect, _rightRect, _topRect, _loadRect, _statsRect;
        private VehicleDesign D => bootstrap.Design;

        private bool WheelSelected => _selType == PartType.Wheel && _sel >= 0 && _sel < D.wheels.Count;
        private bool SensorSelected => _selType == PartType.Sensor && _sel >= 0 && _sel < D.sensors.Count;
        private bool AeroSelected => _selType == PartType.Aero && _sel >= 0 && _sel < D.aero.Count;
        private bool BatterySelected => _selType == PartType.Battery && _sel >= 0 && _sel < D.batteries.Count;
        private bool AntennaSelected => _selType == PartType.Antenna && _sel >= 0 && _sel < D.antennas.Count;
        private bool LightSelected => _selType == PartType.Light && _sel >= 0 && _sel < D.lights.Count;

        // Palette grouped into sub-categories; each entry carries a one-line
        // description surfaced by the hover tooltip.
        private static readonly (string title, (string key, string label, string desc)[] items)[]
            PaletteCategories =
        {
            ("WHEELS", new[]
            {
                ("wheel", "Wheel", "Free-rolling wheel — steerable or fixed; tyre style per wheel."),
                ("wheel_powered", "Powered wheel", "Driven wheel — brushed DC motor + gearbox on the axle."),
            }),
            ("SENSORS", new[]
            {
                ("camera", "Camera", "Streams grayscale frames to firmware; aimable, FOV/rate tunable."),
                ("tof", "ToF", "Time-of-flight ranger — distance along its aim, up to 4 m."),
                ("encoder", "Encoder", "Wheel encoder — tick count + angular velocity, CPR tunable."),
                ("suspension", "Susp sensor", "Reads one wheel's spring force, compression and strut angle."),
            }),
            ("AERO", new[]
            {
                ("wing", "Wing", "Downforce at its mount point — rear placement plants the rear. Angle tunable."),
                ("splitter", "Splitter", "Front lip — nose downforce with little drag."),
                ("sidedam", "Side dam", "Sill skirt — steady downforce along the sides."),
                ("canard", "Canard", "Small nose winglet — trims front balance. Angle tunable."),
            }),
            ("POWER", new[]
            {
                ("battery", "Battery", "Powers the motor bus — voltage sags under load. Mass shifts the CoM."),
            }),
            ("MISC", new[]
            {
                ("antenna", "Antenna", "WiFi whip — cosmetic only. Mirrors like any body part."),
                ("light", "Lights", "Roof light bar / pod cluster — cosmetic, emissive. The bar strobes."),
            }),
        };

        // Hover tooltip state (palette icons + placed parts in the scene).
        private const float HoverDelay = 0.35f;
        private string _hoverKey, _hoverLabel, _hoverDesc; // palette hover (this repaint)
        private string _hoverSceneText;                    // scene-marker hover
        private string _lastHoverId;                       // for the delay timer
        private float _hoverSince;
        private PartPreviewRig _previewRig;
        private bool _previewShown;

        private void Start()
        {
            _nameField = D != null ? D.name : "New Vehicle";
            // Render the palette thumbnails once now, not during OnGUI.
            foreach (var cat in PaletteCategories)
                foreach (var e in cat.items) PartIconFactory.Icon(e.key);
        }

        private void OnDisable()
        {
            ClearGhosts();
            _painter.Exit();
            _previewRig?.Destroy();
            _previewRig = null;
        }

        // ==================== Scene interaction ====================

        private void Update()
        {
            if (bootstrap == null) return;
            bool overUI = PointerOverUI();
            if (bootstrap.Orbit != null)
            {
                // Orbit (RMB) and pan (MMB) stay live while holding a part — only
                // the UI panels block them. The scroll wheel belongs to ghost-yaw
                // during a drag, so zoom then needs Ctrl held.
                bootstrap.Orbit.blockDrag = overUI;
                bootstrap.Orbit.blockZoom =
                    overUI || (_drag != DragState.Idle && !InputReader.CtrlHeld());
            }

            if (_drag == DragState.Idle)
            {
                if (InputReader.UndoPressed()) { if (bootstrap.TryUndo()) AfterHistory(); return; }
                if (InputReader.RedoPressed()) { if (bootstrap.TryRedo()) AfterHistory(); return; }
                if (InputReader.MirrorTogglePressed()) _mirrorMode = !_mirrorMode;
                if (InputReader.FocusPressed()) FocusSelection();
            }
            if (InputReader.SnapTogglePressed()) _snapEnabled = !_snapEnabled;

            if (_leftTab == 2)
            {
                // Paint mode replaces the drag machine entirely.
                _painter.Sync();
                UpdatePaintInput(overUI);
                _hoverSceneText = null;
            }
            else
            {
                switch (_drag)
                {
                    case DragState.Idle:            UpdateIdle(overUI); break;
                    case DragState.MouseDownOnMarker: UpdateMouseDown(); break;
                    default:                        UpdateDragging(overUI); break;
                }
                UpdateSceneHover(overUI);
            }
            if (_previewShown) _previewRig?.Tick(Time.unscaledDeltaTime);
        }

        // Paint-mode pointer handling: LMB strokes (Alt = eyedropper); camera
        // orbit/pan/zoom stay fully live (no drag state in paint mode).
        private void UpdatePaintInput(bool overUI)
        {
            if (!_painter.Active || bootstrap.Cam == null) return;

            if (InputReader.LeftMouseHeld() && !overUI)
            {
                Ray ray = bootstrap.Cam.ScreenPointToRay(InputReader.PointerPosition());
                bool eyedrop = InputReader.AltHeld();
                // Pre-stroke design snapshot (unique key per stroke so two quick
                // strokes stay two undo steps).
                if (InputReader.LeftMousePressed() && !eyedrop && _painter.Hits(ray))
                    bootstrap.PushUndo("paint" + _paintStroke++);
                _painter.PaintAt(ray, eyedrop);
            }
            else if (_painter.Stroking)
            {
                _painter.EndStroke();
            }
        }

        // Hovering a placed part's marker in the showroom → name/description text.
        private void UpdateSceneHover(bool overUI)
        {
            _hoverSceneText = null;
            if (overUI || _drag != DragState.Idle || bootstrap.Cam == null) return;
            Ray ray = bootstrap.Cam.ScreenPointToRay(InputReader.PointerPosition());
            if (!Physics.Raycast(ray, out RaycastHit hit, 200f)) return;
            var pm = hit.collider.GetComponentInParent<PartMarker>();
            if (pm != null) _hoverSceneText = SceneHoverText(pm.type, pm.index);
        }

        private string SceneHoverText(PartType type, int i)
        {
            switch (type)
            {
                case PartType.Wheel when i >= 0 && i < D.wheels.Count:
                {
                    var w = D.wheels[i];
                    string drive = w.powered ? "powered" : "free";
                    string steer = w.allowsSteering ? ", steered" : "";
                    return $"{w.name} — wheel ({drive}{steer}, r {w.radius * 1000f:0} mm)";
                }
                case PartType.Sensor when i >= 0 && i < D.sensors.Count:
                {
                    var s = D.sensors[i];
                    return $"{s.name} — {s.kind} sensor";
                }
                case PartType.Aero when i >= 0 && i < D.aero.Count:
                {
                    var a = D.aero[i];
                    string ang = a.kind == AeroKind.Wing || a.kind == AeroKind.Canard
                        ? $", {a.angleDeg:0}°" : "";
                    return $"{a.name} — {a.kind}{ang}";
                }
                case PartType.Battery when i >= 0 && i < D.batteries.Count:
                {
                    var b = D.batteries[i];
                    return $"{b.name} — battery ({b.nominalV:0.0} V, {b.massKg * 1000f:0} g)";
                }
                case PartType.Antenna when i >= 0 && i < D.antennas.Count:
                    return $"{D.antennas[i].name} — antenna (cosmetic)";
                case PartType.Light when i >= 0 && i < D.lights.Count:
                    return $"{D.lights[i].name} — lights (cosmetic)";
                default:
                    return null;
            }
        }

        private void UpdateIdle(bool overUI)
        {
            if (overUI || bootstrap.Cam == null || !InputReader.LeftMousePressed()) return;

            Ray ray = bootstrap.Cam.ScreenPointToRay(InputReader.PointerPosition());
            if (!Physics.Raycast(ray, out RaycastHit hit, 200f)) return;

            var pm = hit.collider.GetComponentInParent<PartMarker>();
            if (pm != null)
            {
                _drag = DragState.MouseDownOnMarker;
                _downType = pm.type; _downIndex = pm.index;
                _downPos = InputReader.PointerPosition();
            }
            else if ((WheelSelected || SensorSelected) &&
                     bootstrap.PreviewRoot != null && hit.collider.gameObject == bootstrap.PreviewRoot)
            {
                // Quick click-to-move an already-selected part.
                bootstrap.PushUndo("move");
                MoveSelectedToBody(hit);
            }
        }

        private void UpdateMouseDown()
        {
            if (InputReader.LeftMouseReleased())
            {
                Select(_downType, _downIndex);
                _drag = DragState.Idle;
                return;
            }
            if ((InputReader.PointerPosition() - _downPos).magnitude > 6f)
            {
                Select(_downType, _downIndex);
                StartDragExisting(_downType, _downIndex);
            }
        }

        private void UpdateDragging(bool overUI)
        {
            if (InputReader.CancelPressed()) { CancelDrag(); return; }

            // Plain scroll rotates the held part; Ctrl+scroll is camera zoom
            // (handled by OrbitCamera via the blockZoom gate in Update).
            float scroll = InputReader.ScrollDelta();
            if (Mathf.Abs(scroll) > 0.0001f && _ghost != null && !InputReader.CtrlHeld())
                _ghost.Yaw += Mathf.Sign(scroll) * 15f;

            bool valid = false;
            RaycastHit bodyHit = default;
            if (!overUI && bootstrap.Cam != null && bootstrap.PreviewRoot != null)
            {
                Ray ray = bootstrap.Cam.ScreenPointToRay(InputReader.PointerPosition());
                var hits = Physics.RaycastAll(ray, 200f);
                foreach (var h in hits)
                    if (h.collider.gameObject == bootstrap.PreviewRoot) { bodyHit = h; valid = true; break; }
            }

            if (valid) PoseGhosts(bodyHit);
            _ghost?.SetValid(valid);
            _ghostTwin?.SetValid(valid);

            bool commit = _drag == DragState.PlacingNew
                ? InputReader.LeftMousePressed()
                : InputReader.LeftMouseReleased();
            if (commit && valid && !overUI) CommitDrag(bodyHit);
        }

        // ---- drag lifecycle ----

        private void StartDragExisting(PartType type, int index)
        {
            _drag = DragState.DraggingExisting;
            _placingKind = type;
            _dragTwinIndex = -1;
            bootstrap.SetPartVisible(type, index, false);

            if (type == PartType.Wheel)
            {
                var w = D.wheels[index];
                _ghost = PartGhost.ForWheel(w.radius, w.powered, w.yaw);
                var twin = SymmetryUtil.FindTwin(D, w);
                if (twin != null)
                {
                    _dragTwinIndex = D.wheels.IndexOf(twin);
                    bootstrap.SetPartVisible(PartType.Wheel, _dragTwinIndex, false);
                    _ghostTwin = PartGhost.ForWheel(twin.radius, twin.powered, twin.yaw);
                }
            }
            else if (type == PartType.Aero)
            {
                var a = D.aero[index];
                _ghost = PartGhost.ForAero(a.kind, a.angleDeg, a.sizeScale, a.yawDeg);
                var twin = SymmetryUtil.FindTwin(D, a);
                if (twin != null)
                {
                    _dragTwinIndex = D.aero.IndexOf(twin);
                    bootstrap.SetPartVisible(PartType.Aero, _dragTwinIndex, false);
                    _ghostTwin = PartGhost.ForAero(twin.kind, twin.angleDeg, twin.sizeScale, twin.yawDeg);
                }
            }
            else if (type == PartType.Battery)
            {
                _ghost = PartGhost.ForBattery();   // centerline part, no twin
            }
            else if (type == PartType.Antenna)
            {
                var a = D.antennas[index];
                _ghost = PartGhost.ForAntenna(a.tiltDeg, a.sizeScale, a.yawDeg, a.antennaStyle);
                var twin = SymmetryUtil.FindTwin(D, a);
                if (twin != null)
                {
                    _dragTwinIndex = D.antennas.IndexOf(twin);
                    bootstrap.SetPartVisible(PartType.Antenna, _dragTwinIndex, false);
                    _ghostTwin = PartGhost.ForAntenna(twin.tiltDeg, twin.sizeScale, twin.yawDeg, twin.antennaStyle);
                }
            }
            else if (type == PartType.Light)
            {
                var l = D.lights[index];
                _ghost = PartGhost.ForLight(l.style, l.sizeScale, l.yawDeg);
                var twin = SymmetryUtil.FindTwin(D, l);
                if (twin != null)
                {
                    _dragTwinIndex = D.lights.IndexOf(twin);
                    bootstrap.SetPartVisible(PartType.Light, _dragTwinIndex, false);
                    _ghostTwin = PartGhost.ForLight(twin.style, twin.sizeScale, twin.yawDeg);
                }
            }
            else
            {
                var s = D.sensors[index];
                _ghost = PartGhost.ForSensor(s.kind, s.aimEuler.y);
                var twin = SymmetryUtil.FindTwin(D, s);
                if (twin != null)
                {
                    _dragTwinIndex = D.sensors.IndexOf(twin);
                    bootstrap.SetPartVisible(PartType.Sensor, _dragTwinIndex, false);
                    _ghostTwin = PartGhost.ForSensor(twin.kind, twin.aimEuler.y);
                }
            }
        }

        private void StartPlacing(string key)
        {
            ClearGhosts();
            _drag = DragState.PlacingNew;
            _dragTwinIndex = -1;
            _status = "Move onto the body and click to place. Scroll = rotate, Esc = cancel.";

            if (key == "wheel" || key == "wheel_powered")
            {
                _placingKind = PartType.Wheel;
                bool powered = key == "wheel_powered";
                _pendingWheel = new WheelSpec { name = UniqueName(powered ? "motor" : "wheel"), powered = powered };
                _ghost = PartGhost.ForWheel(_pendingWheel.radius, powered, 0f);
                if (_mirrorMode) _ghostTwin = PartGhost.ForWheel(_pendingWheel.radius, powered, 0f);
            }
            else if (key == "battery")
            {
                // Centerline part: no mirror twin, no yaw editing.
                _placingKind = PartType.Battery;
                _pendingBattery = new BatterySpec { name = UniqueName("battery") };
                _ghost = PartGhost.ForBattery();
            }
            else if (key == "wing" || key == "splitter" || key == "sidedam" || key == "canard")
            {
                _placingKind = PartType.Aero;
                AeroKind kind = key == "splitter" ? AeroKind.Splitter
                              : key == "sidedam" ? AeroKind.SideDam
                              : key == "canard" ? AeroKind.Canard : AeroKind.Wing;
                _pendingAero = new AeroSpec { kind = kind, name = UniqueName(key) };
                _ghost = PartGhost.ForAero(kind, _pendingAero.angleDeg, 1f, 0f);
                if (_mirrorMode) _ghostTwin = PartGhost.ForAero(kind, _pendingAero.angleDeg, 1f, 0f);
            }
            else if (key == "antenna")
            {
                _placingKind = PartType.Antenna;
                _pendingAntenna = new AntennaSpec { name = UniqueName("antenna") };
                _ghost = PartGhost.ForAntenna(_pendingAntenna.tiltDeg, 1f, 0f);
                if (_mirrorMode) _ghostTwin = PartGhost.ForAntenna(_pendingAntenna.tiltDeg, 1f, 0f);
            }
            else if (key == "light")
            {
                _placingKind = PartType.Light;
                _pendingLight = new LightSpec { name = UniqueName("light") };
                _ghost = PartGhost.ForLight(_pendingLight.style, 1f, 0f);
                if (_mirrorMode) _ghostTwin = PartGhost.ForLight(_pendingLight.style, 1f, 0f);
            }
            else
            {
                _placingKind = PartType.Sensor;
                SensorType kind = key == "camera" ? SensorType.Camera
                                : key == "encoder" ? SensorType.Encoder
                                : key == "suspension" ? SensorType.Suspension : SensorType.Tof;
                _pendingSensor = new SensorSpec { kind = kind, name = UniqueName(kind.ToString().ToLower()) };
                if (kind == SensorType.Encoder || kind == SensorType.Suspension) _pendingSensor.wheelIndex = 0;
                _ghost = PartGhost.ForSensor(kind, 0f);
                if (_mirrorMode) _ghostTwin = PartGhost.ForSensor(kind, 0f);
            }
        }

        private void PoseGhosts(RaycastHit hit)
        {
            Transform root = bootstrap.PreviewRoot.transform;
            if (_placingKind == PartType.Wheel)
            {
                ComputeWheelPlace(hit, out Vector3 lp, out float yaw);
                _ghost.SetPose(root.TransformPoint(lp), root.rotation * Quaternion.Euler(0f, yaw, 0f));
                if (_ghostTwin != null)
                {
                    bool show = Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone;
                    _ghostTwin.Root.SetActive(show);
                    if (show)
                        _ghostTwin.SetPose(root.TransformPoint(new Vector3(-lp.x, lp.y, lp.z)),
                                           root.rotation * Quaternion.Euler(0f, -yaw, 0f));
                }
            }
            else if (_placingKind == PartType.Aero)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out float yaw);
                _ghost.SetPose(root.TransformPoint(lp), root.rotation * Quaternion.Euler(0f, yaw, 0f));
                if (_ghostTwin != null)
                {
                    bool show = Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone;
                    _ghostTwin.Root.SetActive(show);
                    if (show)
                        _ghostTwin.SetPose(root.TransformPoint(new Vector3(-lp.x, lp.y, lp.z)),
                                           root.rotation * Quaternion.Euler(0f, -yaw, 0f));
                }
            }
            else if (_placingKind == PartType.Battery)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out _);
                _ghost.SetPose(root.TransformPoint(lp), root.rotation);
            }
            else if (_placingKind == PartType.Antenna || _placingKind == PartType.Light)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out float yaw);
                _ghost.SetPose(root.TransformPoint(lp), root.rotation * Quaternion.Euler(0f, yaw, 0f));
                if (_ghostTwin != null)
                {
                    bool show = Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone;
                    _ghostTwin.Root.SetActive(show);
                    if (show)
                        _ghostTwin.SetPose(root.TransformPoint(new Vector3(-lp.x, lp.y, lp.z)),
                                           root.rotation * Quaternion.Euler(0f, -yaw, 0f));
                }
            }
            else
            {
                ComputeSensorPlace(hit, out Vector3 lp, out Vector3 aim);
                _ghost.SetPose(root.TransformPoint(lp), root.rotation * Quaternion.Euler(aim));
                if (_ghostTwin != null)
                {
                    bool show = Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone;
                    _ghostTwin.Root.SetActive(show);
                    if (show)
                        _ghostTwin.SetPose(root.TransformPoint(new Vector3(-lp.x, lp.y, lp.z)),
                                           root.rotation * Quaternion.Euler(aim.x, -aim.y, -aim.z));
                }
            }
        }

        private void CommitDrag(RaycastHit hit)
        {
            bool mirror = _drag == DragState.PlacingNew ? (_ghostTwin != null) : (_dragTwinIndex >= 0);

            if (_placingKind == PartType.Wheel)
            {
                ComputeWheelPlace(hit, out Vector3 lp, out float yaw);
                if (_drag == DragState.PlacingNew)
                {
                    bootstrap.PushUndo("add");
                    _pendingWheel.localPos = lp; _pendingWheel.yaw = yaw;
                    D.wheels.Add(_pendingWheel);
                    int idx = D.wheels.Count - 1;
                    if (mirror && Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone)
                        MakeWheelTwin(_pendingWheel);
                    FinishDrag(PartType.Wheel, idx);
                }
                else
                {
                    bootstrap.PushUndo("move");
                    var w = D.wheels[_sel];
                    w.localPos = lp; w.yaw = yaw;
                    SymmetryUtil.SyncTwin(D, w);
                    FinishDrag(PartType.Wheel, _sel);
                }
            }
            else if (_placingKind == PartType.Aero)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out float yaw);
                if (_drag == DragState.PlacingNew)
                {
                    bootstrap.PushUndo("add");
                    _pendingAero.localPos = lp; _pendingAero.yawDeg = yaw;
                    D.aero.Add(_pendingAero);
                    int idx = D.aero.Count - 1;
                    if (mirror && Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone)
                        MakeAeroTwin(_pendingAero);
                    FinishDrag(PartType.Aero, idx);
                }
                else
                {
                    bootstrap.PushUndo("move");
                    var a = D.aero[_sel];
                    a.localPos = lp; a.yawDeg = yaw;
                    SymmetryUtil.SyncTwin(D, a);
                    FinishDrag(PartType.Aero, _sel);
                }
            }
            else if (_placingKind == PartType.Battery)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out _);
                if (_drag == DragState.PlacingNew)
                {
                    bootstrap.PushUndo("add");
                    _pendingBattery.localPos = lp;
                    D.batteries.Add(_pendingBattery);
                    FinishDrag(PartType.Battery, D.batteries.Count - 1);
                }
                else
                {
                    bootstrap.PushUndo("move");
                    D.batteries[_sel].localPos = lp;
                    FinishDrag(PartType.Battery, _sel);
                }
            }
            else if (_placingKind == PartType.Antenna)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out float yaw);
                if (_drag == DragState.PlacingNew)
                {
                    bootstrap.PushUndo("add");
                    _pendingAntenna.localPos = lp; _pendingAntenna.yawDeg = yaw;
                    D.antennas.Add(_pendingAntenna);
                    int idx = D.antennas.Count - 1;
                    if (mirror && Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone)
                        MakeAntennaTwin(_pendingAntenna);
                    FinishDrag(PartType.Antenna, idx);
                }
                else
                {
                    bootstrap.PushUndo("move");
                    var a = D.antennas[_sel];
                    a.localPos = lp; a.yawDeg = yaw;
                    SymmetryUtil.SyncTwin(D, a);
                    FinishDrag(PartType.Antenna, _sel);
                }
            }
            else if (_placingKind == PartType.Light)
            {
                ComputeAeroPlace(hit, out Vector3 lp, out float yaw);
                if (_drag == DragState.PlacingNew)
                {
                    bootstrap.PushUndo("add");
                    _pendingLight.localPos = lp; _pendingLight.yawDeg = yaw;
                    D.lights.Add(_pendingLight);
                    int idx = D.lights.Count - 1;
                    if (mirror && Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone)
                        MakeLightTwin(_pendingLight);
                    FinishDrag(PartType.Light, idx);
                }
                else
                {
                    bootstrap.PushUndo("move");
                    var l = D.lights[_sel];
                    l.localPos = lp; l.yawDeg = yaw;
                    SymmetryUtil.SyncTwin(D, l);
                    FinishDrag(PartType.Light, _sel);
                }
            }
            else
            {
                ComputeSensorPlace(hit, out Vector3 lp, out Vector3 aim);
                if (_drag == DragState.PlacingNew)
                {
                    bootstrap.PushUndo("add");
                    _pendingSensor.localPos = lp; _pendingSensor.aimEuler = aim;
                    D.sensors.Add(_pendingSensor);
                    int idx = D.sensors.Count - 1;
                    if (mirror && Mathf.Abs(lp.x) > SymmetryUtil.CenterDeadzone)
                        MakeSensorTwin(_pendingSensor);
                    FinishDrag(PartType.Sensor, idx);
                }
                else
                {
                    bootstrap.PushUndo("move");
                    var s = D.sensors[_sel];
                    s.localPos = lp; s.aimEuler = aim;
                    SymmetryUtil.SyncTwin(D, s);
                    FinishDrag(PartType.Sensor, _sel);
                }
            }
        }

        private void MakeWheelTwin(WheelSpec src)
        {
            src.mirrorGroup = SymmetryUtil.NextGroupId(D);
            var tw = src.Clone();
            tw.name = src.name + "_m";
            SymmetryUtil.MirrorInto(src, tw);   // keeps tw.mirrorGroup (copied) + name
            D.wheels.Add(tw);
        }

        private void MakeSensorTwin(SensorSpec src)
        {
            src.mirrorGroup = SymmetryUtil.NextGroupId(D);
            var tw = src.Clone();
            tw.name = src.name + "_m";
            SymmetryUtil.MirrorInto(src, tw);
            D.sensors.Add(tw);
        }

        private void MakeAeroTwin(AeroSpec src)
        {
            src.mirrorGroup = SymmetryUtil.NextGroupId(D);
            var tw = src.Clone();
            tw.name = src.name + "_m";
            SymmetryUtil.MirrorInto(src, tw);
            D.aero.Add(tw);
        }

        private void MakeAntennaTwin(AntennaSpec src)
        {
            src.mirrorGroup = SymmetryUtil.NextGroupId(D);
            var tw = src.Clone();
            tw.name = src.name + "_m";
            SymmetryUtil.MirrorInto(src, tw);
            D.antennas.Add(tw);
        }

        private void MakeLightTwin(LightSpec src)
        {
            src.mirrorGroup = SymmetryUtil.NextGroupId(D);
            var tw = src.Clone();
            tw.name = src.name + "_m";
            SymmetryUtil.MirrorInto(src, tw);
            D.lights.Add(tw);
        }

        private void FinishDrag(PartType type, int index)
        {
            ClearGhosts();
            _drag = DragState.Idle;
            _pendingWheel = null; _pendingSensor = null; _pendingAero = null; _pendingBattery = null; _pendingAntenna = null; _pendingLight = null;
            bootstrap.RebuildPreview();
            Select(type, index);
        }

        private void CancelDrag()
        {
            if (_drag == DragState.DraggingExisting)
            {
                bootstrap.SetPartVisible(_selType, _sel, true);
                if (_dragTwinIndex >= 0) bootstrap.SetPartVisible(_selType, _dragTwinIndex, true);
            }
            ClearGhosts();
            _drag = DragState.Idle;
            _pendingWheel = null; _pendingSensor = null; _pendingAero = null; _pendingBattery = null; _pendingAntenna = null; _pendingLight = null;
            _status = "Cancelled.";
        }

        private void ClearGhosts()
        {
            _ghost?.Destroy(); _ghost = null;
            _ghostTwin?.Destroy(); _ghostTwin = null;
        }

        /// <summary>Round a body-local position to the 5 mm grid when snap is on.</summary>
        private Vector3 SnapLocal(Vector3 lp)
        {
            if (!_snapEnabled) return lp;
            return new Vector3(
                Mathf.Round(lp.x / SnapPos) * SnapPos,
                Mathf.Round(lp.y / SnapPos) * SnapPos,
                Mathf.Round(lp.z / SnapPos) * SnapPos);
        }

        private void ComputeWheelPlace(RaycastHit hit, out Vector3 localPos, out float yaw)
        {
            Transform root = bootstrap.PreviewRoot.transform;
            Vector3 local = root.InverseTransformPoint(hit.point);
            Vector3 localN = root.InverseTransformDirection(hit.normal);
            localPos = SnapLocal(local + localN * 0.012f);
            yaw = _ghost != null ? _ghost.Yaw : 0f;
        }

        private void ComputeAeroPlace(RaycastHit hit, out Vector3 localPos, out float yaw)
        {
            Transform root = bootstrap.PreviewRoot.transform;
            Vector3 local = root.InverseTransformPoint(hit.point);
            Vector3 localN = root.InverseTransformDirection(hit.normal);
            localPos = SnapLocal(local + localN * 0.008f);
            yaw = _ghost != null ? _ghost.Yaw : 0f;
        }

        private void ComputeSensorPlace(RaycastHit hit, out Vector3 localPos, out Vector3 aimEuler)
        {
            Transform root = bootstrap.PreviewRoot.transform;
            Vector3 local = root.InverseTransformPoint(hit.point);
            Vector3 localN = root.InverseTransformDirection(hit.normal);
            localPos = SnapLocal(local + localN * 0.008f);
            float y = _ghost != null ? _ghost.Yaw : 0f;
            Quaternion world = Quaternion.AngleAxis(y, root.up) * Quaternion.LookRotation(hit.normal, Vector3.up);
            aimEuler = (Quaternion.Inverse(root.rotation) * world).eulerAngles;
        }

        private void MoveSelectedToBody(RaycastHit hit)
        {
            Transform root = bootstrap.PreviewRoot.transform;
            Vector3 local = root.InverseTransformPoint(hit.point);
            Vector3 localN = root.InverseTransformDirection(hit.normal);
            if (WheelSelected)
            {
                var w = D.wheels[_sel];
                w.localPos = SnapLocal(local + localN * 0.012f);
                SymmetryUtil.SyncTwin(D, w);
            }
            else if (SensorSelected)
            {
                var spec = D.sensors[_sel];
                spec.localPos = SnapLocal(local + localN * 0.008f);
                Quaternion world = Quaternion.LookRotation(hit.normal, Vector3.up);
                spec.aimEuler = (Quaternion.Inverse(root.rotation) * world).eulerAngles;
                SymmetryUtil.SyncTwin(D, spec);
            }
            bootstrap.RequestRebuild();
        }

        private void FocusSelection()
        {
            Vector3? p = null;
            if (WheelSelected && bootstrap.PreviewCar != null)
                p = bootstrap.PreviewCar.GetWheelTransform(_sel)?.position;
            else if (SensorSelected && bootstrap.PreviewSensors != null && _sel < bootstrap.PreviewSensors.Length)
                p = bootstrap.PreviewSensors[_sel].transform.position;
            else if (AeroSelected && bootstrap.PreviewAero != null &&
                     _sel < bootstrap.PreviewAero.Length && bootstrap.PreviewAero[_sel] != null)
                p = bootstrap.PreviewAero[_sel].transform.position;
            else if (BatterySelected && bootstrap.PreviewBatteries != null &&
                     _sel < bootstrap.PreviewBatteries.Length && bootstrap.PreviewBatteries[_sel] != null)
                p = bootstrap.PreviewBatteries[_sel].transform.position;
            else if (AntennaSelected && bootstrap.PreviewAntennas != null &&
                     _sel < bootstrap.PreviewAntennas.Length && bootstrap.PreviewAntennas[_sel] != null)
                p = bootstrap.PreviewAntennas[_sel].transform.position;
            else if (LightSelected && bootstrap.PreviewLights != null &&
                     _sel < bootstrap.PreviewLights.Length && bootstrap.PreviewLights[_sel] != null)
                p = bootstrap.PreviewLights[_sel].transform.position;
            if (p.HasValue && bootstrap.Orbit != null) bootstrap.Orbit.FocusOn(p.Value, 1.0f);
        }

        private void AfterHistory()
        {
            _sel = -1;
            _nameField = D.name;
            _status = "";
        }

        private void Select(PartType type, int index)
        {
            _selType = type;
            _sel = index;
            bootstrap.SetHighlight(type, index);
        }

        // ==================== IMGUI ====================

        private void OnGUI()
        {
            if (bootstrap == null) return;
            GUI.skin = GarageSkin.Skin;
            if (Event.current.type == EventType.Repaint) _hoverKey = null;
            DrawTopBar();
            DrawLeftPanel();
            DrawRightPanel();
            DrawStatsPanel();
            if (_showLoad) DrawLoadList();
            if (_drag != DragState.Idle) DrawDragHint();
            DrawHoverTooltip();
        }

        // Floating tooltip (drawn topmost): palette icons get name + description +
        // a live rotating 3D preview of the real part; scene markers get an info
        // line. Appears after a short hover dwell.
        private void DrawHoverTooltip()
        {
            if (Event.current.type != EventType.Repaint) return;

            bool palette = _hoverKey != null;
            string id = palette ? "pal:" + _hoverKey : (_hoverSceneText != null ? "scene:" + _hoverSceneText : null);
            if (id == null)
            {
                _lastHoverId = null;
                if (_previewShown) { _previewRig?.Hide(); _previewShown = false; }
                return;
            }
            if (id != _lastHoverId)
            {
                _lastHoverId = id;
                _hoverSince = Time.unscaledTime;
            }
            bool due = Time.unscaledTime - _hoverSince >= HoverDelay;
            if (!due || (!palette && _hoverSceneText == null))
            {
                if (_previewShown) { _previewRig?.Hide(); _previewShown = false; }
                return;
            }

            // Size the box: palette tooltips embed the 3D preview.
            const float pad = 8f;
            float w, h;
            string title, desc;
            if (palette)
            {
                title = _hoverLabel; desc = _hoverDesc;
                w = PartPreviewRig.Size + pad * 2f;
                float descH = GUI.skin.label.CalcHeight(new GUIContent(desc), w - pad * 2f);
                h = pad + 20f + descH + 4f + PartPreviewRig.Size + pad;
                _previewRig ??= new PartPreviewRig();
                _previewRig.Show(_hoverKey);
                _previewShown = true;
            }
            else
            {
                title = null; desc = _hoverSceneText;
                w = Mathf.Min(340f, GUI.skin.label.CalcSize(new GUIContent(desc)).x + pad * 2f + 6f);
                h = pad * 2f + GUI.skin.label.CalcHeight(new GUIContent(desc), w - pad * 2f);
                if (_previewShown) { _previewRig?.Hide(); _previewShown = false; }
            }

            // Anchor near the pointer, offset so the box never sits under it,
            // flipped/clamped at the screen edges.
            Vector2 mp = Event.current.mousePosition;
            float x = mp.x + 18f, y = mp.y + 18f;
            if (x + w > Screen.width - 4f) x = mp.x - w - 18f;
            if (y + h > Screen.height - 4f) y = mp.y - h - 18f;
            x = Mathf.Clamp(x, 4f, Mathf.Max(4f, Screen.width - w - 4f));
            y = Mathf.Clamp(y, 4f, Mathf.Max(4f, Screen.height - h - 4f));
            var box = new Rect(x, y, w, h);

            GUI.Box(box, GUIContent.none);
            float cy = box.y + pad;
            if (title != null)
            {
                GUI.Label(new Rect(box.x + pad, cy, w - pad * 2f, 20f), title, GarageSkin.Header);
                cy += 20f;
            }
            float dh = GUI.skin.label.CalcHeight(new GUIContent(desc), w - pad * 2f);
            GUI.Label(new Rect(box.x + pad, cy, w - pad * 2f, dh), desc);
            cy += dh + 4f;
            if (palette && _previewRig?.Texture != null)
                GUI.DrawTexture(
                    new Rect(box.x + pad, cy, PartPreviewRig.Size, PartPreviewRig.Size),
                    _previewRig.Texture, ScaleMode.ScaleToFit, true);
        }

        private void DrawDragHint()
        {
            var r = new Rect(Screen.width * 0.5f - 200f, Screen.height - 40f, 400f, 26f);
            var st = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter };
            GUI.Box(r, "Placing… click body to drop • scroll rotate • Esc cancel", st);
        }

        private void DrawTopBar()
        {
            float w = 600f, h = 34f;
            _topRect = new Rect((Screen.width - w) * 0.5f, 6f, w, h);
            GUILayout.BeginArea(_topRect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name:", GUILayout.Width(42));
            _nameField = GUILayout.TextField(_nameField, GUILayout.Width(170));
            if (GUILayout.Button("Save", GUILayout.Width(56))) DoSave();
            if (GUILayout.Button(_showLoad ? "Load ▲" : "Load ▼", GUILayout.Width(64))) _showLoad = !_showLoad;
            if (GUILayout.Button("New", GUILayout.Width(48))) DoNew();
            GUI.enabled = bootstrap.CanUndo;
            if (GUILayout.Button("↶", GUILayout.Width(28)) && bootstrap.TryUndo()) AfterHistory();
            GUI.enabled = bootstrap.CanRedo;
            if (GUILayout.Button("↷", GUILayout.Width(28)) && bootstrap.TryRedo()) AfterHistory();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Drive ▶", GUILayout.Width(80))) DoDrive();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DrawLeftPanel()
        {
            float w = 250f, h = Screen.height - 100f - 130f;
            _leftRect = new Rect(8f, 50f, w, h);
            GUILayout.BeginArea(_leftRect, GUI.skin.box);

            int newTab = GUILayout.Toolbar(_leftTab, new[] { "BODY", "PARTS", "PAINT" });
            if (newTab != _leftTab)
            {
                if (newTab == 2)
                {
                    if (_drag != DragState.Idle) CancelDrag();
                    if (BodyPainter.CanPaint(D)) _painter.Enter(bootstrap);
                }
                else if (_leftTab == 2)
                {
                    _painter.Exit();
                }
                _leftTab = newTab;
            }
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            bool aim = GUILayout.Toggle(bootstrap.ShowAimVectors, " Aim");
            if (aim != bootstrap.ShowAimVectors) bootstrap.SetAimVectorsVisible(aim);
            _mirrorMode = GUILayout.Toggle(_mirrorMode, " Mirror ✕2 (X)");
            _snapEnabled = GUILayout.Toggle(_snapEnabled, " Snap 5mm (N)");
            GUILayout.EndHorizontal();

            // Scroll so small game views (editor) never clip the tab content.
            _leftScroll = GUILayout.BeginScrollView(_leftScroll);
            switch (_leftTab)
            {
                case 0: DrawBodyTab(); break;
                case 1: DrawPartsTab(); break;
                default: DrawPaintTab(); break;
            }
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status))
            {
                GUILayout.Space(6);
                GUILayout.Label(_status);
            }
            GUILayout.EndArea();
        }

        private void DrawBodyTab()
        {
            Header("BODY");
            // Rows of four — eight shapes no longer fit one line.
            var shapes = (BodyShape[])System.Enum.GetValues(typeof(BodyShape));
            for (int row = 0; row < shapes.Length; row += 4)
            {
                GUILayout.BeginHorizontal();
                for (int i = row; i < Mathf.Min(row + 4, shapes.Length); i++)
                {
                    BodyShape shape = shapes[i];
                    bool on = D.bodyShape == shape;
                    if (GUILayout.Toggle(on, shape.ToString(), GUI.skin.button) && !on)
                    {
                        bootstrap.PushUndo("shape");
                        D.bodyShape = shape;
                        bootstrap.RebuildPreview();
                    }
                }
                GUILayout.EndHorizontal();
            }

            D.bodySize.x = Slider("Width", D.bodySize.x, 0.12f, 0.35f);
            D.bodySize.y = Slider("Height", D.bodySize.y, 0.04f, 0.18f);
            D.bodySize.z = Slider("Length", D.bodySize.z, 0.25f, 0.60f);
            bool comp = GUILayout.Toggle(D.useCompositeMass, " Composite mass & CoM");
            if (comp != D.useCompositeMass)
            {
                bootstrap.PushUndo("compmass");
                D.useCompositeMass = comp;
                bootstrap.RequestRebuild();
            }
            D.mass = Slider(comp ? "Chassis kg" : "Mass (kg)", D.mass, comp ? 0.3f : 0.8f, 5f);

            GUILayout.Space(4);
            GUILayout.Label("Colour");
            D.bodyColor.r = Slider("R", D.bodyColor.r, 0f, 1f);
            D.bodyColor.g = Slider("G", D.bodyColor.g, 0f, 1f);
            D.bodyColor.b = Slider("B", D.bodyColor.b, 0f, 1f);

            Header("STEERING");
            D.steerRate = Slider("Servo °/s", D.steerRate, 60f, 1200f);
            D.servoStallNm = Slider("Stall N·m (0=ideal)", D.servoStallNm, 0f, 2f);
            D.ackermannPct = Slider("Ackermann %", D.ackermannPct, 0f, 100f);

            Header("REALISM");
            D.imuVibration = Slider("IMU vibration", D.imuVibration, 0f, 0.5f);
            D.wheelVelNoiseStd = Slider("wheel_vel σ", D.wheelVelNoiseStd, 0f, 2f);
            D.wheelVelQuantCpr = IntSlider("wheel_vel CPR", D.wheelVelQuantCpr, 0, 2048);
        }

        private void DrawPartsTab()
        {
            Header("ADD PART");
            GUILayout.Label("Click an icon, then click the body.", GarageSkin.StatLabel);
            foreach (var cat in PaletteCategories)
            {
                GUILayout.Space(2);
                GUILayout.Label(cat.title, GarageSkin.Header);
                for (int i = 0; i < cat.items.Length; i += 2)
                {
                    GUILayout.BeginHorizontal();
                    DrawPaletteIcon(cat.items[i]);
                    if (i + 1 < cat.items.Length) DrawPaletteIcon(cat.items[i + 1]);
                    GUILayout.EndHorizontal();
                }
            }
        }

        private void DrawPaletteIcon((string key, string label, string desc) e)
        {
            var tex = PartIconFactory.Icon(e.key);
            if (GUILayout.Button(new GUIContent(tex, e.label), GUILayout.Width(112), GUILayout.Height(72)))
                StartPlacing(e.key);
            // Hover detection for the floating tooltip + 3D preview (repaint pass
            // only — that's when layout rects are valid).
            if (Event.current.type == EventType.Repaint &&
                GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                _hoverKey = e.key;
                _hoverLabel = e.label;
                _hoverDesc = e.desc;
            }
        }

        private static readonly Color[] PaintSwatches =
        {
            new Color(0.95f, 0.25f, 0.20f), new Color(1.00f, 0.62f, 0.20f),
            new Color(1.00f, 0.90f, 0.25f), new Color(0.35f, 0.85f, 0.35f),
            new Color(0.20f, 0.55f, 0.95f), new Color(0.55f, 0.35f, 0.95f),
            Color.white,                    new Color(0.75f, 0.75f, 0.78f),
            new Color(0.45f, 0.45f, 0.48f), new Color(0.12f, 0.12f, 0.14f),
            new Color(0.55f, 0.35f, 0.20f), new Color(1.00f, 0.55f, 0.75f),
        };

        private void DrawPaintTab()
        {
            Header("PAINT BODY");
            if (!BodyPainter.CanPaint(D))
            {
                GUILayout.Label("Painting needs a moulded shell body —\nswitch to Shell, LowRacer or Buggy\non the BODY tab.", GarageSkin.StatLabel);
                return;
            }
            if (!_painter.Active) _painter.Enter(bootstrap);

            GUILayout.Label("Click the car to paint. Alt+click picks a colour.", GarageSkin.StatLabel);

            // Swatches, 6 per row.
            Color prevBg = GUI.backgroundColor;
            for (int i = 0; i < PaintSwatches.Length; i += 6)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < i + 6 && j < PaintSwatches.Length; j++)
                {
                    GUI.backgroundColor = PaintSwatches[j];
                    if (GUILayout.Button(" ", GUILayout.Width(30), GUILayout.Height(22)))
                        _painter.BrushColor = PaintSwatches[j];
                }
                GUILayout.EndHorizontal();
            }
            GUI.backgroundColor = prevBg;

            // Brush colour fine-tune + preview chip (view state — no undo).
            var bc = _painter.BrushColor;
            bc.r = RawSlider("R", bc.r, 0f, 1f);
            bc.g = RawSlider("G", bc.g, 0f, 1f);
            bc.b = RawSlider("B", bc.b, 0f, 1f);
            _painter.BrushColor = bc;
            GUI.backgroundColor = bc;
            GUILayout.Box(" ", GUILayout.Height(14));
            GUI.backgroundColor = prevBg;

            _painter.BrushPx = RawSlider("Brush px", _painter.BrushPx, 2f, 24f);
            _painter.MirrorBrush = GUILayout.Toggle(_painter.MirrorBrush, " Mirror brush");

            GUILayout.Space(6);
            if (GUILayout.Button("Clear paint"))
            {
                bootstrap.PushUndo("clearpaint");
                _painter.Clear();
            }
        }

        private void DrawRightPanel()
        {
            float w = 270f, h = Screen.height - 100f;
            _rightRect = new Rect(Screen.width - w - 8f, 50f, w, h);
            GUILayout.BeginArea(_rightRect, GUI.skin.box);
            Header("PARTS");

            _partScroll = GUILayout.BeginScrollView(_partScroll, GUILayout.Height(150));
            for (int i = 0; i < D.wheels.Count; i++)
            {
                var wsp = D.wheels[i];
                bool on = _selType == PartType.Wheel && _sel == i;
                string tag = wsp.powered ? "motor" : "wheel";
                string link = wsp.mirrorGroup >= 0 ? " ⇋" : "";
                if (GUILayout.Toggle(on, $"⊙ {wsp.name}  ({tag}){link}", GUI.skin.button) && !on)
                    Select(PartType.Wheel, i);
            }
            for (int i = 0; i < D.sensors.Count; i++)
            {
                var s = D.sensors[i];
                bool on = _selType == PartType.Sensor && _sel == i;
                string link = s.mirrorGroup >= 0 ? " ⇋" : "";
                if (GUILayout.Toggle(on, $"• {s.name}  ({s.kind}){link}", GUI.skin.button) && !on)
                    Select(PartType.Sensor, i);
            }
            for (int i = 0; i < D.aero.Count; i++)
            {
                var a = D.aero[i];
                bool on = _selType == PartType.Aero && _sel == i;
                string link = a.mirrorGroup >= 0 ? " ⇋" : "";
                if (GUILayout.Toggle(on, $"▲ {a.name}  ({a.kind}){link}", GUI.skin.button) && !on)
                    Select(PartType.Aero, i);
            }
            for (int i = 0; i < D.batteries.Count; i++)
            {
                var b = D.batteries[i];
                bool on = _selType == PartType.Battery && _sel == i;
                string bus = i == 0 ? " · bus" : "";
                if (GUILayout.Toggle(on, $"▮ {b.name}  ({b.nominalV:0.#} V{bus})", GUI.skin.button) && !on)
                    Select(PartType.Battery, i);
            }
            for (int i = 0; i < D.antennas.Count; i++)
            {
                var a = D.antennas[i];
                bool on = _selType == PartType.Antenna && _sel == i;
                string link = a.mirrorGroup >= 0 ? " ⇋" : "";
                if (GUILayout.Toggle(on, $"┃ {a.name}  (antenna){link}", GUI.skin.button) && !on)
                    Select(PartType.Antenna, i);
            }
            for (int i = 0; i < D.lights.Count; i++)
            {
                var l = D.lights[i];
                bool on = _selType == PartType.Light && _sel == i;
                string link = l.mirrorGroup >= 0 ? " ⇋" : "";
                string kind = l.style == 0 ? "light bar" : "light pods";
                if (GUILayout.Toggle(on, $"▣ {l.name}  ({kind}){link}", GUI.skin.button) && !on)
                    Select(PartType.Light, i);
            }
            GUILayout.EndScrollView();

            // The inspector scrolls too — motor/aero inspectors outgrow small views.
            _inspectorScroll = GUILayout.BeginScrollView(_inspectorScroll);
            if (WheelSelected) DrawWheelInspector(D.wheels[_sel]);
            else if (SensorSelected) DrawSensorInspector(D.sensors[_sel]);
            else if (AeroSelected) DrawAeroInspector(D.aero[_sel]);
            else if (BatterySelected) DrawBatteryInspector(D.batteries[_sel]);
            else if (AntennaSelected) DrawAntennaInspector(D.antennas[_sel]);
            else if (LightSelected) DrawLightInspector(D.lights[_sel]);
            else GUILayout.Label("Select a part (list or click its marker).\nDrag a marker to move it; grab a palette\nicon to add one.");
            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private void DrawWheelInspector(WheelSpec w)
        {
            Header("EDIT WHEEL");
            w.name = NameField(w.name);
            DrawMirrorRow(w.mirrorGroup, () => BreakLink());

            GUILayout.Label("Position");
            w.localPos.x = Slider("X", w.localPos.x, -0.20f, 0.20f);
            w.localPos.y = Slider("Y", w.localPos.y, -0.09f, 0.03f);
            w.localPos.z = Slider("Z", w.localPos.z, -0.32f, 0.32f);
            w.yaw = Slider("Heading°", w.yaw, -180f, 180f);
            w.radius = Slider("Radius", w.radius, 0.02f, 0.07f);

            string[] styleNames = { "Slick", "Knobby", "Rally", "Coupe", "Baja", "Steelie" };
            int st = Mathf.Clamp(w.wheelStyle, 0, styleNames.Length - 1);
            if (GUILayout.Button("Tyre style: " + styleNames[st]))
            {
                bootstrap.PushUndo("tyre");
                w.wheelStyle = (st + 1) % styleNames.Length;
                bootstrap.RequestRebuild();
            }

            GUILayout.Label("Suspension");
            w.suspStiffness = Slider("Stiffness", w.suspStiffness, 50f, 2000f);
            // Legacy JSON stores ratio 0 (= raw damper 15). Show its equivalent so
            // the slider isn't pinned left; touching it makes the value explicit.
            float zeta = w.suspDampingRatio > 0f ? w.suspDampingRatio : 0.65f;
            w.suspDampingRatio = Slider("Damping ζ", zeta, 0.1f, 2f);
            w.suspTravel = Slider("Travel m", w.suspTravel, 0.01f, 0.08f);
            w.suspAngleDeg = Slider("Strut angle°", w.suspAngleDeg, -30f, 30f);
            // Strut length: 0 = rigid mount / no visible strut. A longer arm drops the
            // wheel and (via the motion ratio) softens the effective rate + adds travel.
            w.suspLength = Slider("Strut len mm", w.suspLength * 1000f, 0f, 60f) / 1000f;
            if (w.suspLength > 0f)
            {
                float effRate = SuspensionGeometry.EffectiveRate(w.suspStiffness, w.suspLength);
                float effTravel = SuspensionGeometry.EffectiveTravel(w.suspTravel, w.suspLength);
                GUILayout.Label($"  → rate {effRate:0} N/m · travel {effTravel * 1000f:0} mm");
            }
            float grip = w.gripMult > 0f ? w.gripMult : 1f;
            w.gripMult = Slider("Grip ×", grip, 0.3f, 2f);

            GUILayout.Label("Tire realism");
            w.loadSensitivity = Slider("Load sens", w.loadSensitivity, 0f, 0.4f);
            w.balloonPct = Slider("Balloon %", w.balloonPct, 0f, 12f);
            if (D.useCompositeMass)
                w.massKg = Slider("Mass g (0=auto)", w.massKg * 1000f, 0f, 400f) / 1000f;

            bool steer = GUILayout.Toggle(w.allowsSteering, " Allows steering");
            if (steer != w.allowsSteering) { bootstrap.PushUndo("steer"); w.allowsSteering = steer; bootstrap.RequestRebuild(); }
            if (w.allowsSteering)
            {
                bool rev = GUILayout.Toggle(w.reverseSteering, " Reverse steering input");
                if (rev != w.reverseSteering) { bootstrap.PushUndo("rev"); w.reverseSteering = rev; bootstrap.RequestRebuild(); }
                w.steerAngle = Slider("Steer°", w.steerAngle, 5f, 45f);
            }

            bool powered = GUILayout.Toggle(w.powered, " Powered (motor)");
            if (powered != w.powered) { bootstrap.PushUndo("powered"); w.powered = powered; bootstrap.RequestRebuild(); }
            if (w.powered) DrawMotorInspector(w);

            SymmetryUtil.SyncTwin(D, w);

            GUILayout.Space(6);
            if (GUILayout.Button("Delete wheel")) DeleteSelected();
        }

        private void DrawSensorInspector(SensorSpec spec)
        {
            Header("EDIT: " + spec.kind);
            spec.name = NameField(spec.name);
            DrawMirrorRow(spec.mirrorGroup, () => BreakLink());

            GUILayout.Label("Position");
            spec.localPos.x = Slider("X", spec.localPos.x, -0.18f, 0.18f);
            spec.localPos.y = Slider("Y", spec.localPos.y, -0.05f, 0.25f);
            spec.localPos.z = Slider("Z", spec.localPos.z, -0.30f, 0.30f);
            GUILayout.Label("Aim");
            spec.aimEuler.y = Slider("Yaw", spec.aimEuler.y, -180f, 180f);
            spec.aimEuler.x = Slider("Pitch", spec.aimEuler.x, -90f, 90f);

            switch (spec.kind)
            {
                case SensorType.Tof:
                    spec.range = Slider("Range m", spec.range, 0.2f, 8f);
                    spec.coneRays = IntSlider("Rays", spec.coneRays, 1, 7);
                    spec.coneAngle = Slider("Cone°", spec.coneAngle, 0f, 30f);
                    break;
                case SensorType.Encoder:
                    spec.wheelIndex = IntSlider("Wheel", spec.wheelIndex, 0, Mathf.Max(0, D.wheels.Count - 1));
                    spec.cprTicks = IntSlider("CPR", spec.cprTicks, 16, 2048);
                    spec.encoderGearRatio = Slider("Gear", spec.encoderGearRatio, 1f, 50f);
                    break;
                case SensorType.Camera:
                    spec.camWidth = IntSlider("Width", spec.camWidth, 16, 128);
                    spec.camHeight = IntSlider("Height", spec.camHeight, 16, 96);
                    spec.camFov = Slider("FOV", spec.camFov, 20f, 110f);
                    spec.camRateHz = Slider("Rate Hz", spec.camRateHz, 1f, 30f);
                    break;
                case SensorType.Suspension:
                    spec.wheelIndex = IntSlider("Wheel", spec.wheelIndex, 0, Mathf.Max(0, D.wheels.Count - 1));
                    break;
            }

            if (D.useCompositeMass)
                spec.massKg = Slider("Mass g (0=auto)", spec.massKg * 1000f, 0f, 100f) / 1000f;

            if (spec.kind != SensorType.Camera)
            {
                GUILayout.Label("Realism");
                spec.noiseStd = Slider("Noise σ", spec.noiseStd, 0f, 0.5f);
                spec.noiseQuant = Slider("Quant step", spec.noiseQuant, 0f, 0.1f);
                spec.driftRate = Slider("Drift /√s", spec.driftRate, 0f, 0.05f);
                spec.updateRateHz = Slider("Rate Hz (0=tick)", spec.updateRateHz, 0f, 100f);
                spec.latencyMs = Slider("Latency ms", spec.latencyMs, 0f, 100f);
            }

            SymmetryUtil.SyncTwin(D, spec);

            GUILayout.Space(6);
            if (GUILayout.Button("Delete sensor")) DeleteSelected();
        }

        private void DrawAeroInspector(AeroSpec a)
        {
            Header("EDIT: " + a.kind);
            a.name = NameField(a.name);
            DrawMirrorRow(a.mirrorGroup, () => BreakLink());

            GUILayout.Label("Position");
            a.localPos.x = Slider("X", a.localPos.x, -0.18f, 0.18f);
            a.localPos.y = Slider("Y", a.localPos.y, -0.05f, 0.25f);
            a.localPos.z = Slider("Z", a.localPos.z, -0.30f, 0.30f);
            a.yawDeg = Slider("Heading°", a.yawDeg, -180f, 180f);

            if (a.kind == AeroKind.Wing || a.kind == AeroKind.Canard)
                a.angleDeg = Slider("Angle°", a.angleDeg, 0f, 20f);
            a.sizeScale = Slider("Size ×", a.sizeScale, 0.6f, 1.6f);

            // Live effect readout at 10 m/s (straight-line, head-on flow).
            AeroDynamics.PartCoefficients(a.kind, a.angleDeg, out float clA, out float cdA);
            float q10 = 0.5f * AeroDynamics.AirDensity * 100f * a.sizeScale * a.sizeScale;
            GUILayout.Label($"@10 m/s: ↓{q10 * clA:0.00} N  drag {q10 * cdA:0.000} N",
                GarageSkin.StatLabel);
            var st = VehicleStats.Compute(D);
            float comZ = st.composite ? st.com.z : 0f;
            string lever = a.localPos.z > comZ
                ? $"{(a.localPos.z - comZ) * 1000f:0} mm ahead of CoM → loads the front"
                : $"{(comZ - a.localPos.z) * 1000f:0} mm behind CoM → loads the rear";
            GUILayout.Label(lever, GarageSkin.StatLabel);
            if (st.hasAeroParts)
                GUILayout.Label($"Aero balance: {st.aeroFrontPct:0} % front", GarageSkin.StatLabel);
            if (Mathf.Abs(Mathf.DeltaAngle(a.yawDeg, 0f)) > 90f &&
                (a.kind == AeroKind.Wing || a.kind == AeroKind.Canard))
                GUILayout.Label("⚠ mounted backwards — makes LIFT at speed", GarageSkin.StatLabel);

            if (D.useCompositeMass)
                a.massKg = Slider("Mass g (0=auto)", a.massKg * 1000f, 0f, 100f) / 1000f;

            SymmetryUtil.SyncTwin(D, a);

            GUILayout.Space(6);
            if (GUILayout.Button("Delete part")) DeleteSelected();
        }

        private void DrawAntennaInspector(AntennaSpec a)
        {
            Header("EDIT: ANTENNA");
            a.name = NameField(a.name);
            DrawMirrorRow(a.mirrorGroup, () => BreakLink());

            GUILayout.Label("Position");
            a.localPos.x = Slider("X", a.localPos.x, -0.18f, 0.18f);
            a.localPos.y = Slider("Y", a.localPos.y, -0.05f, 0.25f);
            a.localPos.z = Slider("Z", a.localPos.z, -0.30f, 0.30f);
            a.yawDeg = Slider("Heading°", a.yawDeg, -180f, 180f);
            a.tiltDeg = Slider("Tilt°", a.tiltDeg, 0f, 45f);
            a.sizeScale = Slider("Size ×", a.sizeScale, 0.6f, 1.6f);

            string[] antStyles = { "Stub", "Whip", "Flag", "Twin" };
            int ast = Mathf.Clamp(a.antennaStyle, 0, antStyles.Length - 1);
            if (GUILayout.Button("Style: " + antStyles[ast]))
            {
                bootstrap.PushUndo("antstyle");
                a.antennaStyle = (ast + 1) % antStyles.Length;
                bootstrap.RebuildPreview();
            }

            if (D.useCompositeMass)
                a.massKg = Slider("Mass g (0=auto)", a.massKg * 1000f, 0f, 60f) / 1000f;

            SymmetryUtil.SyncTwin(D, a);

            GUILayout.Space(6);
            if (GUILayout.Button("Delete part")) DeleteSelected();
        }

        private void DrawLightInspector(LightSpec l)
        {
            Header("EDIT: LIGHTS");
            l.name = NameField(l.name);
            DrawMirrorRow(l.mirrorGroup, () => BreakLink());

            GUILayout.Label("Position");
            l.localPos.x = Slider("X", l.localPos.x, -0.18f, 0.18f);
            l.localPos.y = Slider("Y", l.localPos.y, -0.05f, 0.25f);
            l.localPos.z = Slider("Z", l.localPos.z, -0.30f, 0.30f);
            l.yawDeg = Slider("Heading°", l.yawDeg, -180f, 180f);
            l.sizeScale = Slider("Size ×", l.sizeScale, 0.6f, 1.6f);

            string[] lightStyles = { "Bar", "Pods" };
            int lst = Mathf.Clamp(l.style, 0, lightStyles.Length - 1);
            if (GUILayout.Button("Style: " + lightStyles[lst]))
            {
                bootstrap.PushUndo("lightstyle");
                l.style = (lst + 1) % lightStyles.Length;
                bootstrap.RebuildPreview();
            }

            if (D.useCompositeMass)
                l.massKg = Slider("Mass g (0=auto)", l.massKg * 1000f, 0f, 60f) / 1000f;

            SymmetryUtil.SyncTwin(D, l);

            GUILayout.Space(6);
            if (GUILayout.Button("Delete part")) DeleteSelected();
        }

        private void DrawBatteryInspector(BatterySpec b)
        {
            Header("EDIT: BATTERY");
            b.name = NameField(b.name);
            if (_sel == 0)
                GUILayout.Label("Powers the motor bus (first battery).", GarageSkin.StatLabel);
            else
                GUILayout.Label("Extra pack: mass only.", GarageSkin.StatLabel);

            GUILayout.Label("Position");
            b.localPos.x = Slider("X", b.localPos.x, -0.15f, 0.15f);
            b.localPos.y = Slider("Y", b.localPos.y, -0.06f, 0.15f);
            b.localPos.z = Slider("Z", b.localPos.z, -0.30f, 0.30f);

            b.massKg = Slider("Mass g", b.massKg * 1000f, 80f, 350f) / 1000f;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Cells", GUILayout.Width(50f));
            float[] volts = { 3.7f, 7.4f, 11.1f };
            string[] tags = { "1S", "2S", "3S" };
            for (int i = 0; i < volts.Length; i++)
            {
                bool on = Mathf.Approximately(b.nominalV, volts[i]);
                if (GUILayout.Toggle(on, tags[i], GUI.skin.button) && !on)
                {
                    bootstrap.PushUndo("cells");
                    b.nominalV = volts[i];
                    bootstrap.RequestRebuild();
                }
            }
            GUILayout.EndHorizontal();
            b.internalR = Slider("Int. R Ω", b.internalR, 0.005f, 0.1f);
            b.capacitymAh = Slider("mAh (0=∞)", b.capacitymAh, 0f, 8000f);

            GUILayout.Space(6);
            if (GUILayout.Button("Delete battery")) DeleteSelected();
        }

        private void DrawMirrorRow(int group, System.Action onBreak)
        {
            if (group < 0) return;
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Mirrored (group {group})");
            if (GUILayout.Button("Break link", GUILayout.Width(90))) onBreak();
            GUILayout.EndHorizontal();
        }

        private void BreakLink()
        {
            bootstrap.PushUndo("breaklink");
            if (WheelSelected)
            {
                var w = D.wheels[_sel];
                var twin = SymmetryUtil.FindTwin(D, w);
                w.mirrorGroup = -1;
                if (twin != null) twin.mirrorGroup = -1;
            }
            else if (SensorSelected)
            {
                var s = D.sensors[_sel];
                var twin = SymmetryUtil.FindTwin(D, s);
                s.mirrorGroup = -1;
                if (twin != null) twin.mirrorGroup = -1;
            }
            else if (AeroSelected)
            {
                var a = D.aero[_sel];
                var twin = SymmetryUtil.FindTwin(D, a);
                a.mirrorGroup = -1;
                if (twin != null) twin.mirrorGroup = -1;
            }
            else if (AntennaSelected)
            {
                var a = D.antennas[_sel];
                var twin = SymmetryUtil.FindTwin(D, a);
                a.mirrorGroup = -1;
                if (twin != null) twin.mirrorGroup = -1;
            }
            else if (LightSelected)
            {
                var l = D.lights[_sel];
                var twin = SymmetryUtil.FindTwin(D, l);
                l.mirrorGroup = -1;
                if (twin != null) twin.mirrorGroup = -1;
            }
            _status = "Mirror link broken.";
        }

        private void DrawMotorInspector(WheelSpec w)
        {
            GUILayout.BeginHorizontal();
            bool constMode = w.motorEntryMode == 0;
            if (GUILayout.Toggle(constMode, "Constants", GUI.skin.button) && !constMode)
            {
                bootstrap.PushUndo("motormode");
                w.motorEntryMode = 0;
            }
            if (GUILayout.Toggle(!constMode, "Datasheet", GUI.skin.button) && constMode)
            {
                bootstrap.PushUndo("motormode");
                w.motorDatasheet = MotorModel.ToDatasheet(w.motor);
                w.motorEntryMode = 1;
            }
            GUILayout.EndHorizontal();

            if (w.motorEntryMode == 0)
            {
                w.motor.maxVoltage = Slider("Vmax", w.motor.maxVoltage, 3.7f, 12f);
                w.motor.kt = Slider("Kt", w.motor.kt, 0.001f, 0.02f);
                w.motor.resistance = Slider("R Ω", w.motor.resistance, 0.02f, 1f);
                w.motor.gearRatio = Slider("Gear", w.motor.gearRatio, 1f, 30f);
                w.motor.efficiency = Slider("Eff", w.motor.efficiency, 0.3f, 1f);
                w.motor.noLoadCurrent = Slider("I0 A", w.motor.noLoadCurrent, 0f, 5f);
                w.motor.viscousDamping = Slider("Visc", w.motor.viscousDamping, 0f, 0.0005f);
                w.motor.maxCurrent = Slider("Imax A (0=∞)", w.motor.maxCurrent, 0f, 100f);
            }
            else
            {
                var ds = w.motorDatasheet;
                ds.nominalVoltage = Slider("Vnom", ds.nominalVoltage, 3.7f, 12f);
                ds.stallTorque = Slider("Stall τ", ds.stallTorque, 0.02f, 1.5f);
                ds.noLoadRpm = Slider("NoLoad rpm", ds.noLoadRpm, 5000f, 40000f);
                ds.noLoadCurrent = Slider("I0 A", ds.noLoadCurrent, 0f, 5f);
                w.motorDatasheet = ds;
                MotorModel.ApplyDatasheet(ref w.motor, in ds);
                w.motor.maxCurrent = Slider("Imax A (0=∞)", w.motor.maxCurrent, 0f, 100f);
                GUILayout.Label($"→ Kt {w.motor.kt:0.####}  R {w.motor.resistance:0.###}Ω  gear {w.motor.gearRatio:0.#}");
            }

            DrawEscRealism(w);
        }

        /// <summary>Drivetrain/ESC realism rows (shared by both motor entry modes).</summary>
        private void DrawEscRealism(WheelSpec w)
        {
            GUILayout.Label("DRIVETRAIN / ESC", GarageSkin.Header);
            w.motor.coulombScale = Slider("Coulomb ×", w.motor.coulombScale, 0f, 2f);
            w.motor.rotorInertia = Slider("Rotor J µ", w.motor.rotorInertia * 1e6f, 0f, 20f) * 1e-6f;
            w.motor.escTimeConstMs = Slider("ESC lag ms", w.motor.escTimeConstMs, 0f, 20f);
            w.motor.escDeadbandV = Slider("Deadband V", w.motor.escDeadbandV, 0f, 0.5f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("PWM", GUILayout.Width(50f));
            int[] steps = { 0, 256, 512, 1024, 2048 };
            foreach (int s in steps)
            {
                bool on = w.motor.escPwmSteps == s;
                if (GUILayout.Toggle(on, s == 0 ? "off" : s.ToString(), GUI.skin.button) && !on)
                {
                    bootstrap.PushUndo("pwm");
                    w.motor.escPwmSteps = s;
                    bootstrap.RequestRebuild();
                }
            }
            GUILayout.EndHorizontal();
            // Drive/brake/reverse behaviour (0-on-old-JSON sentinels resolve to
            // drag 0 / strength 100 / lock 150 ms at runtime).
            w.motor.escDragBrakePct = Slider("Drag brake %", w.motor.escDragBrakePct, 0f, 30f);
            w.motor.escBrakeStrengthPct = Slider("Brake str %", w.motor.escBrakeStrengthPct, 0f, 100f);
            w.motor.escReverseLockMs = Slider("Rev lock ms", w.motor.escReverseLockMs, 0f, 500f);
        }

        private void DrawStatsPanel()
        {
            var s = VehicleStats.Compute(D);
            float w = 250f, h = (D.useCompositeMass ? 230f : 190f) + (s.hasAeroParts ? 18f : 0f);
            _statsRect = new Rect(8f, Screen.height - h - 8f, w, h);
            GUILayout.BeginArea(_statsRect, GUI.skin.box);
            GUILayout.Label("STATS", GarageSkin.Header);
            GUILayout.Label($"Mass: {s.totalMass:0.00} kg", GarageSkin.StatLabel);
            GUILayout.Label($"Wheels: {s.wheels}   powered {s.powered} · steered {s.steered}", GarageSkin.StatLabel);
            GUILayout.Label($"Stall torque: {s.totalStallTorqueNm:0.00} N·m", GarageSkin.StatLabel);
            GUILayout.Label($"Top speed: {s.estTopSpeedMs:0.0} m/s (drag-limited)", GarageSkin.StatLabel);
            GUILayout.Label($"@ top: drag {s.dragAtTopN:0.00} N · downforce {s.downforceAtTopN:0.00} N", GarageSkin.StatLabel);
            if (s.hasAeroParts)
                GUILayout.Label($"Aero balance: {s.aeroFrontPct:0} % front / {100f - s.aeroFrontPct:0} % rear", GarageSkin.StatLabel);
            string sag = s.sagPct > 80f ? $"{s.sagPct:0} % (bottoms out!)" : $"{s.sagPct:0} %";
            GUILayout.Label($"Ride freq: {s.rideFreqHz:0.0} Hz · sag {sag}", GarageSkin.StatLabel);
            if (s.composite)
            {
                GUILayout.Label($"CoM: z {s.com.z * 1000f:+0;-0} mm · y {s.com.y * 1000f:+0;-0} mm", GarageSkin.StatLabel);
                GUILayout.Label($"F/R {s.frontWeightPct:0}/{100f - s.frontWeightPct:0} % · yaw I {s.yawInertia * 1000f:0.0} g·m²", GarageSkin.StatLabel);
            }
            GUILayout.EndArea();
        }

        private void DrawLoadList()
        {
            float w = 220f, h = 260f;
            _loadRect = new Rect(_topRect.x, _topRect.yMax + 4f, w, h);
            GUILayout.BeginArea(_loadRect, GUI.skin.box);
            _loadScroll = GUILayout.BeginScrollView(_loadScroll);
            // Built-in presets (read-only): clicking clones one to edit.
            GUILayout.Label("Presets:");
            foreach (var n in VehiclePresets.DisplayNames())
                if (GUILayout.Button(n)) DoLoad(n);
            GUILayout.Space(4);
            GUILayout.Label("Saved vehicles:");
            var names = VehicleLibrary.List();
            if (names.Count == 0) GUILayout.Label("(none saved yet)");
            foreach (var n in names)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(n)) DoLoad(n);
                if (GUILayout.Button("✕", GUILayout.Width(24))) { VehicleLibrary.Delete(n); }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ==================== Actions ====================

        private void DeleteSelected()
        {
            bootstrap.PushUndo("del");
            if (WheelSelected)
            {
                var w = D.wheels[_sel];
                var twin = SymmetryUtil.FindTwin(D, w);
                if (twin != null) D.wheels.Remove(twin);
                D.wheels.Remove(w);
            }
            else if (SensorSelected)
            {
                var s = D.sensors[_sel];
                var twin = SymmetryUtil.FindTwin(D, s);
                if (twin != null) D.sensors.Remove(twin);
                D.sensors.Remove(s);
            }
            else if (AeroSelected)
            {
                var a = D.aero[_sel];
                var twin = SymmetryUtil.FindTwin(D, a);
                if (twin != null) D.aero.Remove(twin);
                D.aero.Remove(a);
            }
            else if (BatterySelected)
            {
                D.batteries.RemoveAt(_sel);
            }
            else if (AntennaSelected)
            {
                var a = D.antennas[_sel];
                var twin = SymmetryUtil.FindTwin(D, a);
                if (twin != null) D.antennas.Remove(twin);
                D.antennas.Remove(a);
            }
            else if (LightSelected)
            {
                var l = D.lights[_sel];
                var twin = SymmetryUtil.FindTwin(D, l);
                if (twin != null) D.lights.Remove(twin);
                D.lights.Remove(l);
            }
            else return;
            _sel = -1;
            bootstrap.RebuildPreview();
        }

        private string UniqueName(string bas)
        {
            var used = new HashSet<string>();
            foreach (var s in D.sensors) used.Add(s.name);
            foreach (var w in D.wheels) used.Add(w.name);
            foreach (var a in D.aero) used.Add(a.name);
            foreach (var b in D.batteries) used.Add(b.name);
            foreach (var a in D.antennas) used.Add(a.name);
            foreach (var l in D.lights) used.Add(l.name);
            int i = 1;
            string n = bas + i;
            while (used.Contains(n)) { i++; n = bas + i; }
            return n;
        }

        private void DoSave()
        {
            D.name = string.IsNullOrWhiteSpace(_nameField) ? "vehicle" : _nameField.Trim();
            _nameField = D.name;
            string path = VehicleLibrary.Save(D);
            _status = "Saved: " + System.IO.Path.GetFileName(path);
        }

        private void DoLoad(string name)
        {
            // Presets clone into an editable design; Save then writes a user copy.
            var d = VehiclePresets.Resolve(name) ?? VehicleLibrary.Load(name);
            if (d == null) { _status = "Load failed: " + name; return; }
            _sel = -1;
            _showLoad = false;
            _nameField = d.name;
            bootstrap.SetDesign(d);
            bootstrap.ClearHistory();
            _status = "Loaded: " + name;
        }

        private void DoNew()
        {
            _sel = -1;
            _showLoad = false;
            _nameField = "New Vehicle";
            bootstrap.SetDesign(new VehicleDesign { name = "New Vehicle" });
            bootstrap.ClearHistory();
            _status = "New blank design.";
        }

        private void DoDrive()
        {
            DoSave();
            SessionConfig.SetSinglePlayer(); // garage Drive is always a solo session
            GameFlow.ActiveDesign = D.Clone();
            GameFlow.LoadTrack();
        }

        // ==================== Helpers ====================

        private static void Header(string text)
        {
            GUILayout.Space(6);
            GUILayout.Label(text, GarageSkin.Header);
        }

        private string NameField(string current)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(44));
            string nn = GUILayout.TextField(current);
            GUILayout.EndHorizontal();
            if (nn != current) { bootstrap.PushUndo("name"); bootstrap.RequestRebuild(); }
            return nn;
        }

        private float Slider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            GUILayout.Label(value.ToString("0.###"), GUILayout.Width(48));
            float nv = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            if (!Mathf.Approximately(nv, value))
            {
                bootstrap.PushUndo("slider:" + label);
                bootstrap.RequestRebuild();
            }
            return nv;
        }

        private int IntSlider(string label, int value, int min, int max)
        {
            return Mathf.RoundToInt(Slider(label, value, min, max));
        }

        // Slider for view-state values (brush colour/size): no undo, no rebuild.
        private static float RawSlider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            GUILayout.Label(value.ToString("0.##"), GUILayout.Width(48));
            float nv = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            return nv;
        }

        private bool PointerOverUI()
        {
            Vector2 p = InputReader.PointerPosition();
            Vector2 g = new Vector2(p.x, Screen.height - p.y);
            if (_leftRect.Contains(g) || _rightRect.Contains(g) || _topRect.Contains(g) || _statsRect.Contains(g)) return true;
            if (_showLoad && _loadRect.Contains(g)) return true;
            return false;
        }
    }
}
