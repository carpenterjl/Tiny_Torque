using System.Collections.Generic;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Garage paint mode: freehand pixel painting onto the body shell's UV-mapped
    /// texture. While active it cooks temporary MeshColliders from the shell's
    /// (CPU-readable) meshes so a pointer ray yields a hit UV
    /// (<see cref="RaycastHit.textureCoord"/>), stamps a round brush into a
    /// working 256×256 texture shown live on the car, and syncs the result into
    /// <see cref="VehicleDesign.liveryPng"/> at each stroke end — so undo, save,
    /// snapshots and LAN transfer all ride the ordinary design JSON. Only the
    /// authored shells (Shell / LowRacer / Buggy) are paintable; the primitive
    /// Box/Wedge compounds have no usable UVs. Owned + driven by GarageUI.
    /// </summary>
    public sealed class BodyPainter
    {
        public const int Res = 256;

        public Color BrushColor = new Color(0.95f, 0.25f, 0.2f);
        public float BrushPx = 8f;
        public bool MirrorBrush;

        public bool Active { get; private set; }
        public bool Stroking { get; private set; }

        private GarageBootstrap _b;
        private CarVehicle _car;                 // the preview car we're attached to
        private Texture2D _tex;
        private Color32[] _px;
        private readonly List<MeshCollider> _cols = new List<MeshCollider>();
        private Vector2 _lastUV;
        private bool _hasLastUV;
        private bool _dirty;                     // unsynced pixels this stroke
        private string _syncedPng;               // design.liveryPng our texture mirrors

        public static bool CanPaint(VehicleDesign d) =>
            d != null && CarVehicle.HasPaintableBody(d.bodyShape);

        // ---- lifecycle ----

        public void Enter(GarageBootstrap bootstrap)
        {
            _b = bootstrap;
            Active = true;
            Attach();
        }

        /// <summary>Leave paint mode: sync, tear down colliders, rebuild so the
        /// preview owns a factory-decoded copy of the livery.</summary>
        public void Exit()
        {
            if (!Active) return;
            EndStroke();
            Detach();
            Active = false;
            _b.RequestRebuild();
            if (_tex != null) { Object.Destroy(_tex); _tex = null; _px = null; }
        }

        /// <summary>
        /// Call every frame while active: the preview rebuilds under us on undo /
        /// slider edits, so re-attach to the current car when it changes.
        /// </summary>
        public void Sync()
        {
            if (!Active) return;
            if (_car != _b.PreviewCar) Attach();
        }

        private void Attach()
        {
            Detach();
            _car = _b.PreviewCar;
            if (_car == null) return;

            // Working texture: current livery, else a bodyColor fill. Reload
            // whenever the design's livery diverges from what we mirror — undo /
            // redo / load restore liveryPng behind our back.
            if (_tex == null)
            {
                _tex = new Texture2D(Res, Res, TextureFormat.RGBA32, false)
                {
                    name = "livery_paint",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                LoadFromDesign();
            }
            else if (_b.Design.liveryPng != _syncedPng)
            {
                LoadFromDesign();
            }

            // Temporary MeshColliders on the shell's mesh pieces (body meshes are
            // imported CPU-readable for exactly this; they sit on the car's own
            // layer so the pointer ray reaches them).
            var bodyHolder = _car.transform.Find("BodyMesh");
            if (bodyHolder != null)
                foreach (var mf in bodyHolder.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    _cols.Add(mc);
                }

            _car.SetBodyTexture(_tex);
        }

        private void Detach()
        {
            foreach (var mc in _cols)
                if (mc != null) Object.Destroy(mc);
            _cols.Clear();
            _car = null;
        }

        // ---- painting ----

        /// <summary>Would this pointer ray land on the shell? (pre-stroke check)</summary>
        public bool Hits(Ray ray) => Active && TryHitUV(ray, out _, out _);

        /// <summary>
        /// Paint (or eyedrop) at the pointer ray. Returns true when the ray hit
        /// the body shell. <paramref name="eyedrop"/> picks the colour instead.
        /// </summary>
        public bool PaintAt(Ray ray, bool eyedrop = false)
        {
            if (!Active || _tex == null || !TryHitUV(ray, out Vector2 uv, out RaycastHit hit))
            {
                _hasLastUV = false;
                return false;
            }

            if (eyedrop)
            {
                var c = _px[UvIndex(uv)];
                BrushColor = new Color(c.r / 255f, c.g / 255f, c.b / 255f);
                _hasLastUV = false;
                return true;
            }

            Stroking = true;
            if (_hasLastUV) StampLine(_lastUV, uv);
            else Stamp(uv);
            _lastUV = uv; _hasLastUV = true;

            // Mirror brush: reflect the ray across the car's centre plane and
            // stamp the opposite side too (world-space, so asymmetric UV islands
            // don't matter).
            if (MirrorBrush && TryHitUV(MirrorRay(ray), out Vector2 muv, out _))
                Stamp(muv);

            _tex.SetPixels32(_px);
            _tex.Apply(false);
            return true;
        }

        /// <summary>Stroke finished (mouse up / mode exit): encode into the design.</summary>
        public void EndStroke()
        {
            _hasLastUV = false;
            Stroking = false;
            if (!_dirty || _tex == null) return;
            _dirty = false;
            _syncedPng = System.Convert.ToBase64String(ImageConversion.EncodeToPNG(_tex));
            _b.Design.liveryPng = _syncedPng;
        }

        /// <summary>Refill with the body colour (Clear button; caller pushes undo).</summary>
        public void Clear()
        {
            if (_tex == null) return;
            Fill(_b.Design.bodyColor);
            _px = _tex.GetPixels32();
            _tex.Apply(false);
            _dirty = false;
            _syncedPng = "";
            _b.Design.liveryPng = "";
        }

        // (Re)load the working texture from the design's livery / body colour.
        private void LoadFromDesign()
        {
            bool loaded = false;
            string png = _b.Design.liveryPng;
            if (!string.IsNullOrEmpty(png))
            {
                try { loaded = ImageConversion.LoadImage(_tex, System.Convert.FromBase64String(png)); }
                catch (System.FormatException) { }
            }
            if (!loaded) Fill(_b.Design.bodyColor);
            _px = _tex.GetPixels32();
            _tex.Apply(false);
            _syncedPng = png;
            _dirty = false;
        }

        // ---- internals ----

        private bool TryHitUV(Ray ray, out Vector2 uv, out RaycastHit bodyHit)
        {
            uv = default; bodyHit = default;
            var hits = Physics.RaycastAll(ray, 200f);
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in hits)
            {
                if (!(h.collider is MeshCollider mc) || !_cols.Contains(mc)) continue;
                if (h.distance < best) { best = h.distance; bodyHit = h; found = true; }
            }
            if (!found) return false;
            uv = bodyHit.textureCoord;
            return true;
        }

        private Ray MirrorRay(Ray ray)
        {
            Transform root = _car.transform;
            Vector3 lo = root.InverseTransformPoint(ray.origin);
            Vector3 ld = root.InverseTransformDirection(ray.direction);
            lo.x = -lo.x; ld.x = -ld.x;
            return new Ray(root.TransformPoint(lo), root.TransformDirection(ld));
        }

        private int UvIndex(Vector2 uv)
        {
            int x = Mathf.Clamp((int)(uv.x * Res), 0, Res - 1);
            int y = Mathf.Clamp((int)(uv.y * Res), 0, Res - 1);
            return y * Res + x;
        }

        // Filled circle at a UV point.
        private void Stamp(Vector2 uv)
        {
            int cx = (int)(uv.x * Res), cy = (int)(uv.y * Res);
            int r = Mathf.Max(1, Mathf.RoundToInt(BrushPx * 0.5f));
            int r2 = r * r;
            var c = (Color32)BrushColor;
            for (int y = cy - r; y <= cy + r; y++)
            {
                if (y < 0 || y >= Res) continue;
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (x < 0 || x >= Res) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    _px[y * Res + x] = c;
                }
            }
            _dirty = true;
        }

        // Interpolated stamps so fast drags leave a continuous stroke — but only
        // within the same UV island (a big jump means the cursor crossed onto a
        // different island; bridging would smear across unrelated faces).
        private void StampLine(Vector2 a, Vector2 b)
        {
            float distPx = (b - a).magnitude * Res;
            if (distPx > 40f) { Stamp(b); return; }   // island jump — don't bridge
            int steps = Mathf.Max(1, Mathf.CeilToInt(distPx / Mathf.Max(1f, BrushPx * 0.4f)));
            for (int i = 1; i <= steps; i++)
                Stamp(Vector2.Lerp(a, b, i / (float)steps));
        }

        private void Fill(Color color)
        {
            var c = (Color32)color;
            var px = new Color32[Res * Res];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            _tex.SetPixels32(px);
        }
    }
}
