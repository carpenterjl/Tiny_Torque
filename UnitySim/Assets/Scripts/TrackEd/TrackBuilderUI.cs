using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// IMGUI editor for the track builder, styled like the garage (dark VAB skin):
    /// a left palette with FLOOR / WALLS / OBSTACLES / MISC tabs, click+drag floor
    /// painting, item placement ghosts, a top bar (New/Save/Load/Drive/undo), and
    /// a right panel (map size, checkpoints, selection). The bootstrap owns the
    /// design + preview; this class mutates the design and asks for
    /// rebuilds/repaints.
    /// </summary>
    public sealed class TrackBuilderUI : MonoBehaviour
    {
        public TrackBuilderBootstrap bootstrap;

        private enum EditState { Idle, Painting, PlacingNew, DraggingExisting, DrawingSpline, DraggingPoint }

        private TrackDesign D => bootstrap.Design;

        private enum TabKind { Floor, Items, Spline }

        /// <summary>
        /// One row per tab. This used to be a name array plus `(ItemCategory)(_tab - 1)`
        /// arithmetic scattered across a dozen comparisons, which meant every new
        /// category silently renumbered the ones after it. The table is the single
        /// place a tab is defined; nothing else knows an index.
        /// </summary>
        private readonly struct TabDef
        {
            public readonly string Name;
            public readonly TabKind Kind;
            public readonly ItemCategory Cat;
            public TabDef(string name, TabKind kind, ItemCategory cat = ItemCategory.Wall)
            { Name = name; Kind = kind; Cat = cat; }
        }

        private static readonly TabDef[] Tabs =
        {
            new TabDef("FLOOR",   TabKind.Floor),
            new TabDef("WALLS",   TabKind.Items, ItemCategory.Wall),
            new TabDef("OBST",    TabKind.Items, ItemCategory.Obstacle),
            new TabDef("MISC",    TabKind.Items, ItemCategory.Misc),
            new TabDef("ARCADE",  TabKind.Items, ItemCategory.Arcade),
            new TabDef("SCENERY", TabKind.Items, ItemCategory.Scenery),
            new TabDef("SPLINE",  TabKind.Spline),
        };

        private const int FloorTabIndex = 0;

        private EditState _state = EditState.Idle;
        private int _tab;

        private bool OnFloorTab => Tabs[_tab].Kind == TabKind.Floor;
        private bool OnSplineTab => Tabs[_tab].Kind == TabKind.Spline;
        private int _selFloor;          // selected floor type (paint brush)
        private Vector2Int _lastPaintTile = new Vector2Int(-1, -1);

        private bool _showLoad;
        private string _status = "";
        private Vector2 _paletteScroll, _loadScroll;

        // Panel rects cached during OnGUI for PointerOverUI.
        private Rect _topRect, _leftRect, _rightRect, _loadRect;

        // Item placement / selection (state machine shared with the drag steps).
        private TrackGhost _ghost;
        private ItemDef _placingDef;
        private int _selItem = -1;      // selected index into D.items
        private int _dragItem = -1;     // item being moved (DraggingExisting)
        private Vector2 _downPos;

        // Selection highlight bookkeeping (re-applied after rebuilds).
        private int _highlighted = -1;
        private int _seenRebuild = -1;
        private static MaterialPropertyBlock _mpb;

        // Spline editing state.
        private int _selSpline = -1;
        private int _selPoint = -1;
        private int _handleVersion = -1;
        private int _handleSpline = -1;
        private int _handlePoints = -1;
        private int _handleSelPoint = -1;
        private readonly List<GameObject> _handles = new List<GameObject>();
        private readonly List<GameObject> _previewPool = new List<GameObject>();
        private Material _handleMat, _handleSelMat, _previewMat;

        private void Update()
        {
            if (bootstrap == null || bootstrap.Built == null) return;

            bool overUI = PointerOverUI();
            bootstrap.Orbit.blockDrag = overUI || _state != EditState.Idle;

            // Keep the selection tint in sync across selection changes + rebuilds.
            if (_highlighted != _selItem || _seenRebuild != bootstrap.RebuildVersion)
            {
                ApplyHighlight(_highlighted, false);
                ApplyHighlight(_selItem, true);
                _highlighted = _selItem;
                _seenRebuild = bootstrap.RebuildVersion;
            }

            EnsureSplineHandles();

            switch (_state)
            {
                case EditState.Idle: UpdateIdle(overUI); break;
                case EditState.Painting: UpdatePainting(); break;
                case EditState.PlacingNew:
                case EditState.DraggingExisting: UpdatePlacing(overUI); break;
                case EditState.DrawingSpline: UpdateDrawingSpline(overUI); break;
                case EditState.DraggingPoint: UpdateDraggingPoint(); break;
            }
        }

        // ---- idle: hotkeys, paint start, selection ---------------------------

        private void UpdateIdle(bool overUI)
        {
            if (InputReader.UndoPressed()) { bootstrap.TryUndo(); _selItem = -1; return; }
            if (InputReader.RedoPressed()) { bootstrap.TryRedo(); _selItem = -1; return; }
            if (InputReader.FocusPressed())
            {
                if (OnSplineTab && ValidSpline(_selSpline))
                    FocusSpline(_selSpline);
                else if (_selItem >= 0 && _selItem < D.items.Count)
                    bootstrap.Orbit.FocusOn(new Vector3(D.items[_selItem].x, 0f, D.items[_selItem].z), 3.5f);
                else bootstrap.FrameMap();
            }

            if (OnSplineTab)
            {
                UpdateSplineIdle(overUI);
                return;
            }
            if (InputReader.DeletePressed() && _selItem >= 0) { DeleteSelected(); return; }

            if (overUI || !InputReader.LeftMousePressed()) return;

            // Click priority: an item under the pointer selects/moves it...
            int hitItem = RaycastItem();
            if (hitItem >= 0)
            {
                _selItem = hitItem;
                _dragItem = hitItem;
                _downPos = InputReader.PointerPosition();
                // Move begins if the mouse travels; handled as DraggingExisting.
                _state = EditState.DraggingExisting;
                StartDragExisting();
                return;
            }

            // ...otherwise the FLOOR tab paints (ribbon runs take precedence over
            // the slab tiles underneath them).
            if (OnFloorTab)
            {
                if (TryPaintRibbon(startStroke: true)) return;
                if (RayToTile(out var tile))
                {
                    bootstrap.BreakUndoCoalescing();
                    bootstrap.PushUndo("paint");
                    _state = EditState.Painting;
                    PaintTile(tile);
                    _lastPaintTile = tile;
                    _selItem = -1;
                }
            }
        }

        /// <summary>
        /// Paint the ribbon segment under the pointer with the floor brush when a
        /// ribbon run is the nearest surface. Returns true when a ribbon was hit.
        /// </summary>
        private bool TryPaintRibbon(bool startStroke)
        {
            var run = RaycastMarker<SplineRunMarker>(out var hit);
            if (run == null) return false;

            // Prefer the tile floor when it is actually in front of the ribbon
            // (e.g. pointer past the ribbon edge) — compare hit distances.
            var ray = PointerRay();
            if (bootstrap.Built.floorCollider != null &&
                bootstrap.Built.floorCollider.Raycast(ray, out var fhit, 3000f) &&
                fhit.distance < (hit - ray.origin).magnitude - 0.01f)
                return false;

            int nearest = 0;
            float best = float.MaxValue;
            for (int k = 0; k < run.samplePos.Length; k++)
            {
                float d2 = (run.samplePos[k] - hit).sqrMagnitude;
                if (d2 < best) { best = d2; nearest = k; }
            }

            if (run.splineIndex >= D.splines.Count) return true;
            var s = D.splines[run.splineIndex];
            int p = Mathf.Clamp(run.samplePointIndex[nearest], 0, s.surface.Count - 1);
            if (s.surface[p] != _selFloor)
            {
                if (startStroke)
                {
                    bootstrap.BreakUndoCoalescing();
                    bootstrap.PushUndo("paint");
                }
                s.surface[p] = _selFloor;
                bootstrap.RequestRebuild();
            }
            if (startStroke)
            {
                _state = EditState.Painting;
                _lastPaintTile = new Vector2Int(-1, -1);
                _selItem = -1;
            }
            return true;
        }

        // ---- floor painting --------------------------------------------------

        private void UpdatePainting()
        {
            if (!InputReader.LeftMouseHeld())
            {
                _state = EditState.Idle;
                bootstrap.BreakUndoCoalescing();
                return;
            }
            if (TryPaintRibbon(startStroke: false)) { _lastPaintTile = new Vector2Int(-1, -1); return; }
            if (!RayToTile(out var tile)) return;
            if (tile == _lastPaintTile) return;

            // Bresenham-walk from the previous tile so fast sweeps leave no gaps
            // (fresh stroke or a hop off a ribbon paints just the current tile).
            if (_lastPaintTile.x < 0) PaintTile(tile);
            else
                foreach (var t in GridLine(_lastPaintTile, tile))
                    PaintTile(t);
            _lastPaintTile = tile;
        }

        private void PaintTile(Vector2Int t)
        {
            if (D.FloorAt(t.x, t.y) == _selFloor) return;
            D.SetFloor(t.x, t.y, _selFloor);
            bootstrap.RepaintTile(t.x, t.y);
        }

        private static System.Collections.Generic.IEnumerable<Vector2Int> GridLine(Vector2Int a, Vector2Int b)
        {
            int dx = Mathf.Abs(b.x - a.x), dz = Mathf.Abs(b.y - a.y);
            int sx = a.x < b.x ? 1 : -1, sz = a.y < b.y ? 1 : -1;
            int err = dx - dz;
            var p = a;
            while (true)
            {
                yield return p;
                if (p == b) yield break;
                int e2 = 2 * err;
                if (e2 > -dz) { err -= dz; p.x += sx; }
                if (e2 < dx) { err += dz; p.y += sz; }
            }
        }

        // ---- spline editing -----------------------------------------------------

        private bool ValidSpline(int i) => i >= 0 && i < D.splines.Count;

        private void FocusSpline(int i)
        {
            var s = D.splines[i];
            if (s.Count == 0) return;
            var c = Vector3.zero;
            foreach (var p in s.points) c += p;
            c /= s.Count;
            bootstrap.Orbit.FocusOn(c, Mathf.Max(4.5f, s.Count * 1f));
        }

        /// <summary>Idle input on the SPLINE tab: pick handles, select splines, insert points.</summary>
        private void UpdateSplineIdle(bool overUI)
        {
            if (InputReader.DeletePressed())
            {
                if (ValidSpline(_selSpline) && _selPoint >= 0) { DeleteSelectedPoint(); return; }
                if (ValidSpline(_selSpline)) { DeleteSpline(_selSpline); return; }
            }

            if (overUI || !InputReader.LeftMousePressed()) return;

            // 1. Point handle under the pointer → select + start dragging.
            var handle = RaycastMarker<SplinePointMarker>(out _);
            if (handle != null)
            {
                _selSpline = handle.spline;
                _selPoint = handle.point;
                bootstrap.BreakUndoCoalescing();
                bootstrap.PushUndo("spline_move");
                _state = EditState.DraggingPoint;
                return;
            }

            // 2. Ribbon run: same spline → insert a point; other spline → select it.
            var run = RaycastMarker<SplineRunMarker>(out var runHit);
            if (run != null)
            {
                if (run.splineIndex == _selSpline && ValidSpline(_selSpline))
                {
                    InsertPointOnRun(run, runHit);
                }
                else
                {
                    _selSpline = run.splineIndex;
                    _selPoint = -1;
                }
            }
        }

        private void InsertPointOnRun(SplineRunMarker run, Vector3 hit)
        {
            int nearest = 0;
            float best = float.MaxValue;
            for (int k = 0; k < run.samplePos.Length; k++)
            {
                float d2 = (run.samplePos[k] - hit).sqrMagnitude;
                if (d2 < best) { best = d2; nearest = k; }
            }
            var s = D.splines[run.splineIndex];
            int after = Mathf.Clamp(run.samplePointIndex[nearest], 0, s.Count - 1);
            bootstrap.BreakUndoCoalescing();
            bootstrap.PushUndo("spline_insert");
            var p = hit - Vector3.up * RibbonMeshBuilder.TopOffset;
            s.InsertPoint(after + 1, p);
            _selPoint = after + 1;
            bootstrap.RebuildAll();
        }

        /// <summary>Click-through drawing: every map click appends a control point.</summary>
        private void UpdateDrawingSpline(bool overUI)
        {
            if (InputReader.CancelPressed()) { EndDrawing(); return; }
            if (overUI || !InputReader.LeftMousePressed()) return;
            if (!ValidSpline(_selSpline)) { _state = EditState.Idle; return; }
            if (!RayToFloorPoint(out var hit)) return;

            var s = D.splines[_selSpline];
            s.AddPoint(new Vector3(hit.x, 0f, hit.z));
            _selPoint = s.Count - 1;
            if (s.Count >= 2) bootstrap.RequestRebuild();
        }

        /// <summary>Finish (or discard a degenerate) spline drawing.</summary>
        private void EndDrawing()
        {
            if (ValidSpline(_selSpline) && D.splines[_selSpline].Count < 2)
            {
                D.splines.RemoveAt(_selSpline);
                _selSpline = -1;
                _selPoint = -1;
            }
            _state = EditState.Idle;
            bootstrap.RebuildAll();
        }

        /// <summary>Drag a control point in its own horizontal plane; re-mesh on release.</summary>
        private void UpdateDraggingPoint()
        {
            if (!ValidSpline(_selSpline) || _selPoint < 0 ||
                _selPoint >= D.splines[_selSpline].Count)
            {
                _state = EditState.Idle;
                return;
            }
            var s = D.splines[_selSpline];

            if (!InputReader.LeftMouseHeld())
            {
                ClearPreview();
                _state = EditState.Idle;
                bootstrap.RebuildAll();
                return;
            }

            var ray = PointerRay();
            float planeY = s.points[_selPoint].y;
            if (Mathf.Abs(ray.direction.y) > 0.0001f)
            {
                float t = (planeY - ray.origin.y) / ray.direction.y;
                if (t > 0f)
                {
                    var hit = ray.origin + ray.direction * t;
                    s.points[_selPoint] = new Vector3(hit.x, planeY, hit.z);
                    UpdatePreviewStrip(s);
                }
            }
        }

        private void DeleteSelectedPoint()
        {
            var s = D.splines[_selSpline];
            bootstrap.BreakUndoCoalescing();
            bootstrap.PushUndo("spline_delpoint");
            s.RemovePoint(_selPoint);
            _selPoint = -1;
            if (s.Count < 2) { D.splines.RemoveAt(_selSpline); _selSpline = -1; }
            bootstrap.RebuildAll();
        }

        private void DeleteSpline(int i)
        {
            if (!ValidSpline(i)) return;
            bootstrap.BreakUndoCoalescing();
            bootstrap.PushUndo("spline_delete");
            D.splines.RemoveAt(i);
            if (_selSpline == i) { _selSpline = -1; _selPoint = -1; }
            else if (_selSpline > i) _selSpline--;
            bootstrap.RebuildAll();
        }

        /// <summary>Nearest marker of type T under the pointer (markers may sit on parents).</summary>
        private T RaycastMarker<T>(out Vector3 hitPoint) where T : Component
        {
            hitPoint = default;
            var hits = Physics.RaycastAll(PointerRay(), 3000f);
            float best = float.MaxValue;
            T found = null;
            foreach (var h in hits)
            {
                var m = h.collider.GetComponentInParent<T>();
                if (m == null) continue;
                if (h.distance < best) { best = h.distance; found = m; hitPoint = h.point; }
            }
            return found;
        }

        // ---- spline handles + drag preview ---------------------------------------

        /// <summary>Sphere handles for the selected spline's control points.</summary>
        private void EnsureSplineHandles()
        {
            bool want = OnSplineTab && ValidSpline(_selSpline) && _state != EditState.DraggingPoint;
            int points = want ? D.splines[_selSpline].Count : 0;
            if (_handleVersion == bootstrap.RebuildVersion && _handleSpline == _selSpline &&
                _handlePoints == points && _handleSelPoint == _selPoint && (want || _handles.Count == 0))
                return;

            foreach (var h in _handles) if (h != null) Destroy(h);
            _handles.Clear();
            _handleVersion = bootstrap.RebuildVersion;
            _handleSpline = _selSpline;
            _handlePoints = points;
            _handleSelPoint = _selPoint;
            if (!want) return;

            _handleMat ??= TrackBuilder.StandardMat(GarageSkin.Accent);
            _handleSelMat ??= TrackBuilder.StandardMat(Color.white);

            var s = D.splines[_selSpline];
            for (int i = 0; i < s.Count; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"splinePoint_{i}";
                go.transform.position = s.points[i] + Vector3.up * (RibbonMeshBuilder.TopOffset + 0.12f);
                go.transform.localScale = Vector3.one * 0.2f;
                go.GetComponent<Renderer>().sharedMaterial = i == _selPoint ? _handleSelMat : _handleMat;
                var m = go.AddComponent<SplinePointMarker>();
                m.spline = _selSpline;
                m.point = i;
                _handles.Add(go);
            }
        }

        /// <summary>Cheap collider-less box strip previewing the curve during a drag.</summary>
        private void UpdatePreviewStrip(SplineSpec s)
        {
            _previewMat ??= PartVisualFactory.MakeGhostMat(new Color(0.4f, 1f, 0.55f, 0.5f));
            var samples = SplineMath.SampleAll(s, 0.8f);
            int needed = Mathf.Max(0, samples.Count - (s.closed ? 0 : 1));

            while (_previewPool.Count < needed)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "splinePreview";
                Destroy(box.GetComponent<Collider>());
                box.layer = PartVisualFactory.VizLayer;
                box.GetComponent<Renderer>().sharedMaterial = _previewMat;
                _previewPool.Add(box);
            }
            for (int i = 0; i < _previewPool.Count; i++)
            {
                bool on = i < needed;
                _previewPool[i].SetActive(on);
                if (!on) continue;
                Vector3 a = samples[i].pos;
                Vector3 b = samples[(i + 1) % samples.Count].pos;
                var box = _previewPool[i].transform;
                box.position = (a + b) * 0.5f + Vector3.up * 0.08f;
                box.rotation = (b - a).sqrMagnitude > 1e-6f
                    ? Quaternion.LookRotation(b - a) : Quaternion.identity;
                box.localScale = new Vector3(0.09f, 0.02f, Vector3.Distance(a, b));
            }
        }

        private void ClearPreview()
        {
            foreach (var b in _previewPool) if (b != null) b.SetActive(false);
        }

        // ---- item placement (ghost) ------------------------------------------

        /// <summary>Palette click: begin placing a new item with a ghost.</summary>
        private void StartPlacing(ItemDef def)
        {
            CancelPlacement();
            _placingDef = def;
            _ghost = TrackGhost.For(def);
            _state = EditState.PlacingNew;
            _selItem = -1;
        }

        private void StartDragExisting()
        {
            var it = D.items[_dragItem];
            var def = TrackCatalog.Item(it.itemId);
            if (def == null) { _state = EditState.Idle; return; }
            _placingDef = def;
            _ghost = TrackGhost.For(def);
            _ghost.Yaw = it.yawDeg;
            SetItemVisible(_dragItem, false);
        }

        private void UpdatePlacing(bool overUI)
        {
            if (InputReader.CancelPressed()) { CancelPlacement(); return; }

            // Scroll rotates the ghost in 15° steps (orbit zoom is blocked).
            float scroll = InputReader.ScrollDelta();
            if (Mathf.Abs(scroll) > 0.0001f)
                _ghost.Yaw = Mathf.Repeat(_ghost.Yaw + (scroll > 0 ? 15f : -15f), 360f);

            Vector3 pos = default;
            float yaw = _ghost.Yaw;
            var rot = Quaternion.Euler(0f, _ghost.Yaw, 0f);
            Vector3 hit = default, normal = Vector3.up;
            bool onRibbon = false;
            bool valid = !overUI && RayToSurfacePoint(out hit, out normal, out onRibbon);
            if (valid)
            {
                if (onRibbon)
                {
                    // Free placement on the ribbon, aligned to its surface.
                    pos = hit;
                    rot = Quaternion.FromToRotation(Vector3.up, normal) * rot;
                }
                else
                {
                    valid = SnapPose(hit, out pos, out yaw);
                    rot = Quaternion.Euler(0f, yaw, 0f);
                }
            }
            if (valid)
            {
                _ghost.SetPose(pos, rot);
                _ghost.SetValid(IsPlacementAllowed());
            }
            _ghost.SetVisible(valid);

            bool commit = _state == EditState.PlacingNew
                ? InputReader.LeftMousePressed() && !overUI
                : InputReader.LeftMouseReleased();

            if (!commit) return;
            if (!valid || !IsPlacementAllowed())
            {
                if (_state == EditState.DraggingExisting) CancelPlacement(); // put it back
                return;
            }
            CommitPlacement(_ghost.Root.transform.position, onRibbon ? _ghost.Yaw : yaw);
        }

        private bool SnapPose(Vector3 hit, out Vector3 pos, out float yaw)
        {
            pos = default; yaw = _ghost.Yaw;
            if (!D.WorldToTile(hit, out int tx, out int tz)) return false;
            Vector3 c = D.TileCenter(tx, tz);

            if (_placingDef.snap == SnapMode.TileEdge)
            {
                // Snap to the nearest of the tile's 4 edge midpoints; the edge
                // seeds the heading (scroll still rotates from there).
                float half = D.tileSize * 0.5f;
                Vector3[] edges =
                {
                    c + new Vector3(0, 0,  half), c + new Vector3(0, 0, -half),
                    c + new Vector3( half, 0, 0), c + new Vector3(-half, 0, 0),
                };
                float[] edgeYaw = { 0f, 0f, 90f, 90f };
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < edges.Length; i++)
                {
                    float d2 = (hit - edges[i]).sqrMagnitude;
                    if (d2 < bestD) { bestD = d2; best = i; }
                }
                pos = edges[best];
                yaw = Mathf.Repeat(edgeYaw[best] + Mathf.Round(_ghost.Yaw / 15f) * 15f, 360f);
                return true;
            }

            pos = c;
            return true;
        }

        /// <summary>Single-instance rules: one finish line, one spawn point.</summary>
        private bool IsPlacementAllowed()
        {
            if (_placingDef == null) return false;
            if (_placingDef.behavior != ItemBehavior.Finish && _placingDef.behavior != ItemBehavior.Spawn)
                return true;
            int existing = D.CountBehavior(_placingDef.behavior);
            // Moving the only instance of itself is fine.
            if (_state == EditState.DraggingExisting &&
                TrackCatalog.Item(D.items[_dragItem].itemId)?.behavior == _placingDef.behavior)
                existing--;
            return existing < 1;
        }

        private void CommitPlacement(Vector3 pos, float yaw)
        {
            if (_state == EditState.PlacingNew)
            {
                bootstrap.BreakUndoCoalescing();
                bootstrap.PushUndo("place");
                var it = new PlacedItem { itemId = _placingDef.id, x = pos.x, y = pos.y, z = pos.z, yawDeg = yaw };
                if (_placingDef.behavior == ItemBehavior.Checkpoint)
                {
                    int max = -1;
                    foreach (var cp in D.OrderedCheckpoints()) max = Mathf.Max(max, cp.order);
                    it.order = max + 1;
                }
                D.items.Add(it);
                _selItem = D.items.Count - 1;
            }
            else
            {
                bootstrap.BreakUndoCoalescing();
                bootstrap.PushUndo("move");
                var it = D.items[_dragItem];
                it.x = pos.x; it.y = pos.y; it.z = pos.z; it.yawDeg = yaw;
                _selItem = _dragItem;
            }
            ClearGhost();
            _state = EditState.Idle;
            bootstrap.RebuildAll();
        }

        private void CancelPlacement()
        {
            if (_state == EditState.DraggingExisting && _dragItem >= 0)
                SetItemVisible(_dragItem, true);
            ClearGhost();
            _state = EditState.Idle;
        }

        private void ClearGhost()
        {
            _ghost?.Destroy();
            _ghost = null;
            _placingDef = null;
            _dragItem = -1;
        }

        private void DeleteSelected()
        {
            if (_selItem < 0 || _selItem >= D.items.Count) return;
            bootstrap.BreakUndoCoalescing();
            bootstrap.PushUndo("delete");
            D.items.RemoveAt(_selItem);
            D.NormalizeCheckpointOrders();
            _selItem = -1;
            bootstrap.RebuildAll();
        }

        /// <summary>Tint an item's renderers toward the accent color (selection).</summary>
        private void ApplyHighlight(int index, bool on)
        {
            if (index < 0) return;
            var root = FindItemRoot(index);
            if (root == null) return;
            _mpb ??= new MaterialPropertyBlock();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!on) { r.SetPropertyBlock(null); continue; }
                var baseCol = r.sharedMaterial != null ? r.sharedMaterial.color : Color.white;
                _mpb.SetColor("_Color", Color.Lerp(baseCol, GarageSkin.Accent, 0.45f));
                r.SetPropertyBlock(_mpb);
            }
        }

        private void SetItemVisible(int index, bool visible)
        {
            var root = FindItemRoot(index);
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
        }

        private Transform FindItemRoot(int index)
        {
            if (bootstrap.Built?.root == null) return null;
            foreach (var m in bootstrap.Built.root.GetComponentsInChildren<PlacedItemMarker>(true))
                if (m.index == index) return m.transform;
            return null;
        }

        // ---- raycasts ----------------------------------------------------------

        private Ray PointerRay()
        {
            Vector2 p = InputReader.PointerPosition();
            return bootstrap.Cam.ScreenPointToRay(new Vector3(p.x, p.y, 0f));
        }

        private bool RayToFloorPoint(out Vector3 point)
        {
            point = default;
            var floor = bootstrap.Built?.floorCollider;
            if (floor == null) return false;
            if (!floor.Raycast(PointerRay(), out var hit, 3000f)) return false;
            point = hit.point;
            return true;
        }

        /// <summary>
        /// Nearest placement surface under the pointer: the floor slab or any
        /// spline ribbon run (whichever the ray reaches first). Raised ribbons
        /// therefore accept items just like the floor does.
        /// </summary>
        private bool RayToSurfacePoint(out Vector3 point, out Vector3 normal, out bool onRibbon)
        {
            point = default;
            normal = Vector3.up;
            onRibbon = false;
            var built = bootstrap.Built;
            if (built == null) return false;

            var ray = PointerRay();
            float best = float.MaxValue;
            bool found = false;

            if (built.floorCollider != null && built.floorCollider.Raycast(ray, out var fhit, 3000f))
            {
                best = fhit.distance; point = fhit.point; normal = fhit.normal; found = true;
            }
            foreach (var mc in built.ribbonColliders)
            {
                if (mc != null && mc.Raycast(ray, out var rhit, 3000f) && rhit.distance < best)
                {
                    best = rhit.distance; point = rhit.point; normal = rhit.normal;
                    onRibbon = true; found = true;
                }
            }
            return found;
        }

        private bool RayToTile(out Vector2Int tile)
        {
            tile = default;
            if (!RayToFloorPoint(out var p)) return false;
            if (!D.WorldToTile(p, out int tx, out int tz)) return false;
            tile = new Vector2Int(tx, tz);
            return true;
        }

        /// <summary>Item index under the pointer, or -1 (items keep real colliders).</summary>
        private int RaycastItem()
        {
            var hits = Physics.RaycastAll(PointerRay(), 3000f);
            float best = float.MaxValue;
            int bestIdx = -1;
            foreach (var h in hits)
            {
                var m = h.collider.GetComponentInParent<PlacedItemMarker>();
                if (m == null) continue;
                if (h.distance < best) { best = h.distance; bestIdx = m.index; }
            }
            return bestIdx;
        }

        // ---- GUI ----------------------------------------------------------------

        private void OnGUI()
        {
            if (bootstrap == null) return;
            GUI.skin = GarageSkin.Skin;

            DrawTopBar();
            DrawLeftPanel();
            DrawRightPanel();
            if (_showLoad) DrawLoadList();
            else _loadRect = default;
        }

        private void DrawTopBar()
        {
            float w = Mathf.Min(Screen.width - 20f, 900f);
            _topRect = new Rect((Screen.width - w) * 0.5f, 8f, w, 44f);
            GUILayout.BeginArea(_topRect, GUI.skin.box);
            GUILayout.BeginHorizontal();

            GUILayout.Label("Track", GUILayout.Width(40));
            D.name = GUILayout.TextField(D.name, GUILayout.Width(180));

            if (GUILayout.Button("New", GUILayout.Width(56)))
            {
                bootstrap.SetDesign(TrackDesign.Default());
                bootstrap.ClearHistory();
                _selItem = -1;
                _status = "New 20×20 map.";
            }
            if (GUILayout.Button("Save", GUILayout.Width(56)))
            {
                string path = TrackLibrary.Save(D);
                _status = $"Saved: {System.IO.Path.GetFileName(path)}";
            }
            if (GUILayout.Button(_showLoad ? "Load ▲" : "Load ▼", GUILayout.Width(70)))
                _showLoad = !_showLoad;

            GUI.enabled = bootstrap.CanUndo;
            if (GUILayout.Button("↶", GUILayout.Width(34))) { bootstrap.TryUndo(); _selItem = -1; }
            GUI.enabled = bootstrap.CanRedo;
            if (GUILayout.Button("↷", GUILayout.Width(34))) { bootstrap.TryRedo(); _selItem = -1; }
            GUI.enabled = true;

            GUILayout.FlexibleSpace();
            GUILayout.Label(_status, GarageSkin.StatLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Drive ▶", GUILayout.Width(80), GUILayout.Height(28)))
                DoDrive();

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void DoDrive()
        {
            TrackLibrary.Save(D);
            SessionConfig.SetSinglePlayer(); // builder Drive is always a solo session
            GameFlow.ActiveTrack = D.Clone();
            GameFlow.LoadTrack();
        }

        private void DrawLeftPanel()
        {
            float h = Mathf.Min(Screen.height - 120f, 560f);
            _leftRect = new Rect(10f, 60f, 190f, h);

            // Two-row tabs read better in 190 px.
            GUILayout.BeginArea(_leftRect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 4; i++) DrawTabButton(i);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 4; i < Tabs.Length; i++) DrawTabButton(i);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            _paletteScroll = GUILayout.BeginScrollView(_paletteScroll);
            if (OnFloorTab) DrawFloorPalette();
            else if (OnSplineTab) DrawSplineTab();
            else DrawItemPalette(Tabs[_tab].Cat);
            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            GUILayout.Label(
                OnFloorTab ? "Click/drag paints tiles"
                : OnSplineTab ? (_state == EditState.DrawingSpline
                    ? "Click map to add points\nEsc/Done finishes"
                    : "Drag spheres to reshape\nClick ribbon to insert point")
                : "Click icon, then click map\nScroll rotates · Esc cancels", GarageSkin.StatLabel);
            GUILayout.EndArea();
        }

        private void DrawTabButton(int i)
        {
            bool active = _tab == i;
            if (GUILayout.Toggle(active, Tabs[i].Name, active ? GarageSkin.TabActive : GUI.skin.button,
                GUILayout.Height(24)) && !active)
            {
                _tab = i;
                CancelPlacement();
                if (_state == EditState.DrawingSpline) EndDrawing();
            }
        }

        private void DrawSplineTab()
        {
            if (_state == EditState.DrawingSpline)
            {
                GUILayout.Label($"Drawing — {(_selSpline >= 0 && ValidSpline(_selSpline) ? D.splines[_selSpline].Count : 0)} points",
                    GarageSkin.Header);
                if (GUILayout.Button("Done", GUILayout.Height(28))) EndDrawing();
                return;
            }

            if (GUILayout.Button("＋ New Spline", GUILayout.Height(30)))
            {
                bootstrap.BreakUndoCoalescing();
                bootstrap.PushUndo("spline_new");
                D.splines.Add(new SplineSpec());
                _selSpline = D.splines.Count - 1;
                _selPoint = -1;
                _state = EditState.DrawingSpline;
            }
            GUILayout.Space(6);

            for (int i = 0; i < D.splines.Count; i++)
            {
                GUILayout.BeginHorizontal();
                bool sel = _selSpline == i;
                if (GUILayout.Toggle(sel, $"Spline {i + 1} · {D.splines[i].Count} pts",
                    sel ? GarageSkin.TabActive : GUI.skin.button) && !sel)
                {
                    _selSpline = i;
                    _selPoint = -1;
                    FocusSpline(i);
                }
                if (GUILayout.Button("✕", GUILayout.Width(26)))
                {
                    DeleteSpline(i);
                    GUILayout.EndHorizontal();
                    break; // list changed mid-iteration
                }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawFloorPalette()
        {
            var floors = TrackCatalog.Floors;
            for (int i = 0; i < floors.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = i; k < Mathf.Min(i + 2, floors.Length); k++)
                    DrawFloorSwatch(k);
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(4);
            GUILayout.Label($"Brush: {floors[_selFloor].label}", GarageSkin.Header);
        }

        private void DrawFloorSwatch(int type)
        {
            var f = TrackCatalog.Floors[type];
            var content = new GUIContent(f.Tex, f.label);
            bool sel = _selFloor == type;
            GUILayout.BeginVertical(GUILayout.Width(80));
            if (GUILayout.Button(content, sel ? GarageSkin.TabActive : GUI.skin.button,
                GUILayout.Width(76), GUILayout.Height(56)))
            {
                _selFloor = type;
                _tab = FloorTabIndex;
            }
            GUILayout.Label(f.label, GarageSkin.StatLabel);
            GUILayout.EndVertical();
        }

        private void DrawItemPalette(ItemCategory cat)
        {
            var defs = new System.Collections.Generic.List<ItemDef>();
            foreach (var it in TrackCatalog.Items)
                if (it.category == cat && it.theme.Length == 0) defs.Add(it);

            DrawIconGrid(defs);

            // Themed props get a header per family rather than a tab each — four
            // near-empty tabs would be worse than one scrolling list.
            foreach (var theme in TrackCatalog.Themes)
            {
                defs.Clear();
                foreach (var it in TrackCatalog.Items)
                    if (it.category == cat && it.theme == theme) defs.Add(it);
                if (defs.Count == 0) continue;

                GUILayout.Space(4);
                GUILayout.Label(theme, GarageSkin.Header);
                DrawIconGrid(defs);
            }
        }

        private void DrawIconGrid(System.Collections.Generic.List<ItemDef> defs)
        {
            for (int i = 0; i < defs.Count; i += 2)
            {
                GUILayout.BeginHorizontal();
                for (int k = i; k < Mathf.Min(i + 2, defs.Count); k++)
                {
                    var def = defs[k];
                    GUILayout.BeginVertical(GUILayout.Width(80));
                    if (GUILayout.Button(new GUIContent(TrackIconFactory.ItemIcon(def.id), def.label),
                        GUILayout.Width(76), GUILayout.Height(56)))
                        StartPlacing(def);
                    GUILayout.Label(def.label, GarageSkin.StatLabel);
                    GUILayout.EndVertical();
                }
                GUILayout.EndHorizontal();
            }
        }

        private Vector2 _rightScroll;

        private void DrawRightPanel()
        {
            float h = Mathf.Min(Screen.height - 120f, 480f);
            _rightRect = new Rect(Screen.width - 230f, 60f, 220f, h);
            GUILayout.BeginArea(_rightRect, GUI.skin.box);

            // Scroll so small game views (editor) never clip the panel.
            _rightScroll = GUILayout.BeginScrollView(_rightScroll);
            GUILayout.Label("MAP", GarageSkin.Header);
            GUILayout.Label($"{D.width} × {D.length} tiles ({D.WorldWidth:0}×{D.WorldLength:0} m)");
            DrawResizeRow("West");
            DrawResizeRow("East");
            DrawResizeRow("South");
            DrawResizeRow("North");
            GUILayout.Space(8);

            DrawCheckpointPanel();
            GUILayout.Space(8);
            if (OnSplineTab) DrawSplinePanel();
            else DrawSelectionPanel();
            GUILayout.EndScrollView();

            GUILayout.Label("T top-down · F frame · MMB pan\nRMB orbit · Del delete", GarageSkin.StatLabel);
            GUILayout.EndArea();
        }

        private void DrawResizeRow(string label)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(50));
            if (GUILayout.Button("−", GUILayout.Width(34))) ApplyResize(label, -1);
            if (GUILayout.Button("+", GUILayout.Width(34))) ApplyResize(label, +1);
            GUILayout.EndHorizontal();
        }

        private void ApplyResize(string edge, int delta)
        {
            bootstrap.BreakUndoCoalescing();
            bootstrap.PushUndo("resize");
            int w = 0, e = 0, s = 0, n = 0;
            switch (edge)
            {
                case "West": w = delta; break;
                case "East": e = delta; break;
                case "South": s = delta; break;
                case "North": n = delta; break;
            }
            D.Resize(w, e, s, n);
            _selItem = -1;
            bootstrap.RebuildAll();
        }

        private void DrawCheckpointPanel()
        {
            var cps = D.OrderedCheckpoints();
            GUILayout.Label($"CHECKPOINTS ({cps.Count})", GarageSkin.Header);
            for (int i = 0; i < cps.Count; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"CP {i + 1}", GUILayout.Width(50));
                GUI.enabled = i > 0;
                if (GUILayout.Button("▲", GUILayout.Width(30))) SwapCheckpointOrder(cps, i, i - 1);
                GUI.enabled = i < cps.Count - 1;
                if (GUILayout.Button("▼", GUILayout.Width(30))) SwapCheckpointOrder(cps, i, i + 1);
                GUI.enabled = true;
                if (GUILayout.Button("Go", GUILayout.Width(34)))
                    bootstrap.Orbit.FocusOn(new Vector3(cps[i].x, 0f, cps[i].z), 3.5f);
                GUILayout.EndHorizontal();
            }
        }

        private void SwapCheckpointOrder(System.Collections.Generic.List<PlacedItem> cps, int a, int b)
        {
            bootstrap.BreakUndoCoalescing();
            bootstrap.PushUndo("cp_order");
            (cps[a].order, cps[b].order) = (cps[b].order, cps[a].order);
            D.NormalizeCheckpointOrders();
        }

        private void DrawSplinePanel()
        {
            if (!ValidSpline(_selSpline))
            {
                GUILayout.Label("SPLINE", GarageSkin.Header);
                GUILayout.Label("Select or draw a spline.", GarageSkin.StatLabel);
                return;
            }
            var s = D.splines[_selSpline];
            GUILayout.Label($"SPLINE {_selSpline + 1}", GarageSkin.Header);

            bool closed = GUILayout.Toggle(s.closed, " Closed loop");
            bool walls = GUILayout.Toggle(s.edgeWalls, " Edge walls");
            bool stripes = GUILayout.Toggle(s.edgeStripes, " Kerb stripes");
            if (closed != s.closed || walls != s.edgeWalls || stripes != s.edgeStripes)
            {
                bootstrap.PushUndo("spline_opt");
                s.closed = closed; s.edgeWalls = walls; s.edgeStripes = stripes;
                bootstrap.RequestRebuild();
            }

            if (_selPoint >= 0 && _selPoint < s.Count)
            {
                GUILayout.Space(4);
                GUILayout.Label($"Point {_selPoint + 1}/{s.Count}", GarageSkin.Header);

                float w = SliderRow("Width", s.widths[_selPoint], 0.5f, 3f);
                if (!Mathf.Approximately(w, s.widths[_selPoint]))
                {
                    bootstrap.PushUndo("spline_w");
                    s.widths[_selPoint] = w;
                    bootstrap.RequestRebuild();
                }
                float h = SliderRow("Height", s.points[_selPoint].y, 0f, 2f);
                if (!Mathf.Approximately(h, s.points[_selPoint].y))
                {
                    bootstrap.PushUndo("spline_h");
                    var p = s.points[_selPoint]; p.y = h; s.points[_selPoint] = p;
                    bootstrap.RequestRebuild();
                }
                float bank = SliderRow("Bank", s.rollDeg[_selPoint], -45f, 45f);
                if (!Mathf.Approximately(bank, s.rollDeg[_selPoint]))
                {
                    bootstrap.PushUndo("spline_bank");
                    s.rollDeg[_selPoint] = bank;
                    bootstrap.RequestRebuild();
                }

                // Segment surface (from this point to the next).
                int surf = Mathf.Clamp(s.surface[_selPoint], 0, TrackCatalog.Floors.Length - 1);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Surface", GUILayout.Width(52));
                if (GUILayout.Button("◀", GUILayout.Width(24)))
                    ChangePointSurface(s, (surf - 1 + TrackCatalog.Floors.Length) % TrackCatalog.Floors.Length, false);
                GUILayout.Label(TrackCatalog.Floors[surf].label, GarageSkin.Header);
                if (GUILayout.Button("▶", GUILayout.Width(24)))
                    ChangePointSurface(s, (surf + 1) % TrackCatalog.Floors.Length, false);
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Apply surface to whole spline"))
                    ChangePointSurface(s, surf, true);

                if (GUILayout.Button("Delete point")) DeleteSelectedPoint();
            }
            else
            {
                GUILayout.Label("Click a sphere handle to edit a point.", GarageSkin.StatLabel);
            }

            if (GUILayout.Button("Delete spline")) DeleteSpline(_selSpline);
        }

        // GUILayout slider row helper (label + slider + value).
        private float SliderRow(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label} {value:0.0}", GUILayout.Width(80));
            float nv = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            return nv;
        }

        private void ChangePointSurface(SplineSpec s, int surf, bool whole)
        {
            bootstrap.PushUndo("spline_surf");
            if (whole)
                for (int i = 0; i < s.surface.Count; i++) s.surface[i] = surf;
            else
                s.surface[_selPoint] = surf;
            bootstrap.RequestRebuild();
        }

        private void DrawSelectionPanel()
        {
            GUILayout.Label("SELECTION", GarageSkin.Header);
            if (_selItem < 0 || _selItem >= D.items.Count)
            {
                GUILayout.Label("Click an item to select it.", GarageSkin.StatLabel);
                return;
            }
            var it = D.items[_selItem];
            var def = TrackCatalog.Item(it.itemId);
            GUILayout.Label($"{(def != null ? def.label : it.itemId)}  ·  {it.yawDeg:0}°");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Rotate 15°"))
            {
                bootstrap.PushUndo("rotate");
                it.yawDeg = Mathf.Repeat(it.yawDeg + 15f, 360f);
                bootstrap.RequestRebuild();
            }
            if (GUILayout.Button("Delete")) DeleteSelected();
            GUILayout.EndHorizontal();
        }

        private void DrawLoadList()
        {
            float h = Mathf.Min(Screen.height - 140f, 320f);
            _loadRect = new Rect(_topRect.x + 240f, 56f, 260f, h);
            GUILayout.BeginArea(_loadRect, GUI.skin.box);
            GUILayout.Label("SAVED TRACKS", GarageSkin.Header);
            _loadScroll = GUILayout.BeginScrollView(_loadScroll);
            // Built-in presets (read-only): clicking clones one to edit.
            GUILayout.Label("Presets:", GarageSkin.StatLabel);
            foreach (var pname in TrackPresets.DisplayNames())
                if (GUILayout.Button(pname))
                {
                    var d = TrackPresets.Resolve(pname);
                    if (d != null)
                    {
                        bootstrap.SetDesign(d);
                        bootstrap.ClearHistory();
                        _selItem = -1;
                        _showLoad = false;
                        _status = $"Loaded: {pname}";
                    }
                }
            GUILayout.Space(4);
            GUILayout.Label("Saved:", GarageSkin.StatLabel);
            var names = TrackLibrary.List();
            if (names.Count == 0) GUILayout.Label("(none saved yet)", GarageSkin.StatLabel);
            foreach (var name in names)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(name))
                {
                    var d = TrackLibrary.Load(name);
                    if (d != null)
                    {
                        bootstrap.SetDesign(d);
                        bootstrap.ClearHistory();
                        _selItem = -1;
                        _showLoad = false;
                        _status = $"Loaded: {name}";
                    }
                }
                if (GUILayout.Button("✕", GUILayout.Width(28)))
                {
                    TrackLibrary.Delete(name);
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ---- pointer/UI hit test ------------------------------------------------

        private bool PointerOverUI()
        {
            Vector2 p = InputReader.PointerPosition();
            var gui = new Vector2(p.x, Screen.height - p.y);
            return _topRect.Contains(gui) || _leftRect.Contains(gui) ||
                   _rightRect.Contains(gui) || (_showLoad && _loadRect.Contains(gui));
        }
    }
}
