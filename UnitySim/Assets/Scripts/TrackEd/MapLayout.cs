using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Authoring helpers for the four maps ported from the TinyTorque Blender
    /// preview maps (<c>tt_11_map.py</c>, <c>tt_16_toy_map.py</c>,
    /// <c>tt_17_ench_map.py</c>, <c>tt_18_haunt_map.py</c>).
    ///
    /// Everything here works in the SOURCE'S OWN COORDINATES so a ported line
    /// can be read straight across from the Python. Blender
    /// <c>spawn(src["volcano"], c, (100, -305, 0), rot_z=24)</c> becomes
    /// <c>L.Prop("dt_volcano", 100, -305, 24)</c> — same numbers, same order,
    /// same meaning. Three conversions happen inside, once, instead of at
    /// several hundred call sites:
    ///
    ///   * <b>Scale.</b> 1 authored metre is 0.1 game metres — the exact 1/10
    ///     fiction the props were exported at, so a layout divided by anything
    ///     else would put the buildings at the wrong spacing for their size.
    ///   * <b>Axes.</b> Blender X/Y is game X/Z, and the two systems have
    ///     opposite handedness about the up axis, so a Blender <c>rot_z</c> of
    ///     +θ is a game yaw of −θ. Getting this backwards mirrors the whole map
    ///     and is close to invisible in a top-down screenshot.
    ///   * <b>Centring.</b> The source maps are not centred on their origin
    ///     (the enchanted vale runs from −46 to +76 in Y) but a TrackDesign
    ///     always is, so each port carries a shift.
    ///
    /// The scatter uses <see cref="System.Random"/> rather than Python's
    /// Mersenne Twister: same regions, same densities, same rules, different
    /// individual positions. That is the one place the port is not literal.
    /// </summary>
    public sealed class MapLayout
    {
        /// <summary>Authored metres to game metres.</summary>
        public const float S = 0.1f;

        private readonly TrackDesign _d;
        private readonly System.Random _rng;
        private readonly float _shiftX, _shiftZ;   // authored units, pre-scale

        public MapLayout(TrackDesign design, int seed, float shiftX = 0f, float shiftZ = 0f)
        {
            _d = design;
            _rng = new System.Random(seed);
            _shiftX = shiftX;
            _shiftZ = shiftZ;
        }

        // ---- coordinates ----------------------------------------------------

        /// <summary>Authored X to world X.</summary>
        public float X(float bx) => (bx + _shiftX) * S;

        /// <summary>Authored Y to world Z.</summary>
        public float Z(float by) => (by + _shiftZ) * S;

        /// <summary>Authored Z (height) to world Y.</summary>
        public static float Up(float bz) => bz * S;

        /// <summary>Authored <c>rot_z</c> to game yaw. See the class remarks:
        /// the handedness flip is the whole reason this is a method.</summary>
        public static float Yaw(float rotZ) => -rotZ;

        // ---- rng (mirrors the Python names the source modules use) ----------

        public double Random01() => _rng.NextDouble();

        public float Uniform(float lo, float hi) => lo + (float)_rng.NextDouble() * (hi - lo);

        /// <summary>Half-open, like Python's <c>randrange</c>.</summary>
        public int RandRange(int loInclusive, int hiExclusive) =>
            _rng.Next(loInclusive, hiExclusive);

        public T Choice<T>(params T[] options) => options[_rng.Next(options.Length)];

        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // ---- placement ------------------------------------------------------

        /// <summary>
        /// Place a scenery prop using the source map's own coordinates and
        /// rotation convention. <paramref name="pinned"/> is for decorative
        /// fills of otherwise-dynamic props (the domino kerb, the floor
        /// bricks): hundreds of live Rigidbodies buy nothing.
        /// </summary>
        public PlacedItem Prop(string id, float bx, float by, float rotZ = 0f,
            float scale = 1f, float bz = 0f, bool pinned = false)
        {
            var it = new PlacedItem
            {
                itemId = id,
                x = X(bx), z = Z(by), y = Up(bz),
                yawDeg = Mathf.Repeat(Yaw(rotZ), 360f),
                scale = scale,
                pinned = pinned,
            };
            _d.items.Add(it);
            return it;
        }

        // ---- polyline maths (ported verbatim from tt_11_map) ----------------

        public static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ap = p - a, ab = b - a;
            float ln = ab.sqrMagnitude;
            float t = ln < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(ap, ab) / ln);
            return (ap - ab * t).magnitude;
        }

        /// <summary>Distance from a point to the nearest road, in authored
        /// units. The scatter loops use it to keep props off the tarmac.</summary>
        public static float RoadDist(Vector2 p, IReadOnlyList<Vector2[]> lines)
        {
            float d = 1e9f;
            foreach (var line in lines)
                for (int i = 0; i < line.Length - 1; i++)
                    d = Mathf.Min(d, SegDist(p, line[i], line[i + 1]));
            return d;
        }

        /// <summary>One station from <see cref="Along"/>: authored position plus
        /// the authored heading (a Blender <c>rot_z</c>, ready for
        /// <see cref="Prop"/>).</summary>
        public struct Station
        {
            public float x, y, head;
        }

        /// <summary>
        /// Walk a polyline yielding a station every <paramref name="spacing"/>
        /// authored units. <paramref name="offset"/> shifts sideways (positive
        /// = left of travel), which is how the source places kerb furniture
        /// without a second set of coordinates.
        /// </summary>
        public static List<Station> Along(Vector2[] line, float spacing,
            float offset = 0f, float start = 0f)
        {
            var outp = new List<Station>();
            for (int i = 0; i < line.Length - 1; i++)
            {
                Vector2 a = line[i], d = line[i + 1] - a;
                float ln = d.magnitude;
                if (ln < 1e-6f) continue;
                Vector2 u = d / ln;
                var nrm = new Vector2(-u.y, u.x);
                float head = Mathf.Atan2(u.y, u.x) * Mathf.Rad2Deg;
                float t = start;
                while (t < ln)
                {
                    Vector2 p = a + u * t + nrm * offset;
                    outp.Add(new Station { x = p.x, y = p.y, head = head });
                    t += spacing;
                }
                start = t - ln;
            }
            return outp;
        }

        // ---- floor painting -------------------------------------------------

        /// <summary>Fill the whole map with one surface.</summary>
        public void Fill(int type)
        {
            for (int i = 0; i < _d.floor.Length; i++) _d.floor[i] = type;
        }

        /// <summary>Paint an axis-aligned box given in authored units.</summary>
        public void Rect(float bx0, float by0, float bx1, float by1, int type)
        {
            ForTiles(Mathf.Min(bx0, bx1), Mathf.Min(by0, by1),
                     Mathf.Max(bx0, bx1), Mathf.Max(by0, by1),
                     (tx, tz, wx, wz) => _d.SetFloor(tx, tz, type));
        }

        /// <summary>Paint an ellipse (the toy map's rug, the village green).</summary>
        public void Ellipse(float bcx, float bcy, float bax, float bay, int type)
        {
            ForTiles(bcx - bax, bcy - bay, bcx + bax, bcy + bay, (tx, tz, wx, wz) =>
            {
                float u = (wx - X(bcx)) / (bax * S), v = (wz - Z(bcy)) / (bay * S);
                if (u * u + v * v <= 1f) _d.SetFloor(tx, tz, type);
            });
        }

        /// <summary>
        /// Paint a road: every tile whose centre is within half of
        /// <paramref name="widthB"/> of the polyline. The source maps draw
        /// their roads as flat ribbons at z≈0.04 in a deletable collection, so
        /// painted tiles are the honest equivalent — and unlike a second spline
        /// they cannot steal the bot racing line. The cost is a stair-stepped
        /// edge on a diagonal, which shows from directly overhead and not at
        /// all from the car.
        /// </summary>
        public void Road(Vector2[] line, float widthB, int type)
        {
            float half = widthB * 0.5f;
            float lo = 1e9f, hi = -1e9f, lo2 = 1e9f, hi2 = -1e9f;
            foreach (var p in line)
            {
                lo = Mathf.Min(lo, p.x); hi = Mathf.Max(hi, p.x);
                lo2 = Mathf.Min(lo2, p.y); hi2 = Mathf.Max(hi2, p.y);
            }
            ForTiles(lo - half, lo2 - half, hi + half, hi2 + half, (tx, tz, wx, wz) =>
            {
                var p = new Vector2(wx / S - _shiftX, wz / S - _shiftZ);
                for (int i = 0; i < line.Length - 1; i++)
                    if (SegDist(p, line[i], line[i + 1]) <= half)
                    {
                        _d.SetFloor(tx, tz, type);
                        return;
                    }
            });
        }

        public void Roads(IReadOnlyList<Vector2[]> lines, float widthB, int type)
        {
            foreach (var line in lines) Road(line, widthB, type);
        }

        /// <summary>Visit every tile whose centre falls in an authored-unit box,
        /// handing back both the tile index and its world centre.</summary>
        private void ForTiles(float bx0, float by0, float bx1, float by1,
            System.Action<int, int, float, float> body)
        {
            _d.WorldToTile(new Vector3(X(bx0), 0f, Z(by0)), out int tx0, out int tz0);
            _d.WorldToTile(new Vector3(X(bx1), 0f, Z(by1)), out int tx1, out int tz1);
            tx0 = Mathf.Clamp(tx0, 0, _d.width - 1); tx1 = Mathf.Clamp(tx1, 0, _d.width - 1);
            tz0 = Mathf.Clamp(tz0, 0, _d.length - 1); tz1 = Mathf.Clamp(tz1, 0, _d.length - 1);
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    var c = _d.TileCenter(tx, tz);
                    body(tx, tz, c.x, c.z);
                }
        }
    }
}
