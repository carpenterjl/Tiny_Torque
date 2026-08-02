using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// The navball: a real textured sphere, rotated by the aircraft's attitude,
    /// photographed each frame by a dedicated camera into a small RenderTexture
    /// that the HUD draws.
    ///
    /// <b>Why a sphere and not a rotated 2D horizon.</b> The flat artificial
    /// horizon this replaces was honest only near level. It slid a quad by pitch,
    /// so it had no answer for vertical, for inverted, or for heading at all —
    /// and "which way am I pointing" is half of what a navball is for. A sphere
    /// has no such regions: every attitude is just a rotation.
    ///
    /// Built on the <c>PartPreviewRig</c> pattern — hidden geometry far from the
    /// world, one disabled camera rendered on demand, a persistent RT handed
    /// straight to IMGUI. Two rules it inherits and must keep: the camera never
    /// gets an <c>AudioListener</c> (Unity warns every frame about the second one),
    /// and it sees only its own layer.
    ///
    /// <b>Nothing here is a tuned constant.</b> The ball's rotation follows from
    /// the attitude, and the texture's alignment to the mesh is MEASURED off the
    /// sphere's own UVs at build time (see <see cref="MeasureSeam"/>) rather than
    /// guessed at and nudged until it looks right.
    /// </summary>
    public sealed class NavballRig
    {
        /// <summary>Render size. The HUD draws it at 220 UI units, so 256 is a
        /// touch of headroom without paying for a texture nobody can see.</summary>
        public const int Size = 256;

        /// <summary>A high, unnamed user layer. Unnamed on purpose: naming it would
        /// mean editing <c>ProjectSettings/TagManager.asset</c>, and this feature is
        /// not worth a diff in a file every scene in the project depends on.
        /// <c>PartVisualFactory.VizLayer</c> could not be reused — the flight
        /// camera renders every layer, so it would have the ball hanging in the
        /// sky.</summary>
        public const int NavballLayer = 31;

        /// <summary>Half-height of the orthographic view, in world units. The ball
        /// is a unit sphere (r = 0.5), so it fills 0.5/this of the picture — a
        /// sliver of margin so the limb is never clipped by the RT edge.</summary>
        private const float OrthoSize = 0.52f;

        /// <summary>Fraction of the drawn square the ball's radius actually spans.
        /// Markers are placed against the BALL, not the texture, so every offset
        /// goes through this.</summary>
        public const float BallFill = 0.5f / OrthoSize;

        /// <summary>Turns a world direction into the ball's frame. The nose must
        /// land in the middle of the visible disc, which is local −Z for a camera
        /// looking along +Z; with the ball carrying the inverse of the aircraft's
        /// attitude, that takes exactly one half turn to arrange.</summary>
        private static readonly Quaternion Flip = Quaternion.Euler(0f, 180f, 0f);

        private GameObject _root, _pivot, _ball, _camGo;
        private Camera _cam;
        private RenderTexture _rt;
        private Texture2D _skin;
        private Material _mat;

        /// <summary>The live ball image.</summary>
        public Texture Texture => _rt;

        /// <summary>Point the ball at the aircraft's attitude and take the picture.
        /// Called once a frame from the HUD.</summary>
        public void Tick(Quaternion attitude)
        {
            EnsureRig();
            _pivot.transform.localRotation = BallRotation(attitude);
            _cam.Render();
        }

        /// <summary>
        /// Where a world direction lands on the drawn ball, in GUI pixels from its
        /// centre — or null when it is round the back.
        ///
        /// The camera is orthographic and looks down +Z, so the projection is just
        /// "drop the z": a unit direction at local (x, y, z) sits at (x, y) across
        /// the disc. Y is negated because GUI counts downward. The near side is
        /// z &lt; 0, and a marker on the far side must be HIDDEN rather than drawn
        /// at the mirrored position — that is the classic navball bug, and it shows
        /// up as a prograde marker that swims to the wrong side in a steep climb.
        /// </summary>
        /// <param name="halfSizePx">Half the width of the square the ball is drawn
        /// into (not the ball's radius — <see cref="BallFill"/> handles that).</param>
        public Vector2? MarkerOffset(Vector3 worldDir, Quaternion attitude, float halfSizePx)
        {
            if (worldDir.sqrMagnitude < 1e-8f) return null;
            Vector3 d = BallRotation(attitude) * worldDir.normalized;
            if (d.z >= 0f) return null;
            return new Vector2(d.x, -d.y) * (halfSizePx * BallFill);
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
            if (_camGo != null) Object.Destroy(_camGo);
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); }
            if (_skin != null) Object.Destroy(_skin);
            if (_mat != null) Object.Destroy(_mat);
            _root = _pivot = _ball = _camGo = null;
            _cam = null; _rt = null; _skin = null; _mat = null;
        }

        private static Quaternion BallRotation(Quaternion attitude) =>
            Flip * Quaternion.Inverse(attitude);

        private void EnsureRig()
        {
            if (_root != null) return;

            // Far from anything, so even a mis-set culling mask shows a sphere in
            // empty space rather than one wedged inside the aeroplane.
            _root = new GameObject("navball_rig") { hideFlags = HideFlags.HideAndDontSave };
            _root.transform.position = new Vector3(0f, -620f, 0f);

            _pivot = new GameObject("navball_pivot") { hideFlags = HideFlags.HideAndDontSave };
            _pivot.transform.SetParent(_root.transform, false);

            _ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _ball.name = "navball";
            _ball.hideFlags = HideFlags.HideAndDontSave;
            _ball.transform.SetParent(_pivot.transform, false);
            Object.Destroy(_ball.GetComponent<Collider>());
            SetLayer(_root);

            // The sphere's own seam decides where longitude 0 sits; measure it and
            // rotate the mesh so the texture's N ends up dead ahead at zero yaw.
            var mesh = _ball.GetComponent<MeshFilter>().sharedMesh;
            _ball.transform.localRotation = Quaternion.Euler(0f, 180f + MeasureSeam(mesh), 0f);

            _skin = NavballTexture.Build();
            // Unlit: a navball is a diagram, not an object in a scene. Shading it
            // would put a dark limb exactly where the pitch numbers are, and it
            // saves the rig a light.
            _mat = new Material(Shader.Find("Unlit/Texture"))
            {
                mainTexture = _skin,
                hideFlags = HideFlags.HideAndDontSave,
            };
            _ball.GetComponent<Renderer>().sharedMaterial = _mat;

            _rt = new RenderTexture(Size, Size, 16, RenderTextureFormat.ARGB32)
            {
                name = "navball_rt",
                hideFlags = HideFlags.HideAndDontSave,
            };

            _camGo = new GameObject("navball_cam") { hideFlags = HideFlags.HideAndDontSave };
            _camGo.transform.SetParent(_root.transform, false);
            _camGo.transform.localPosition = new Vector3(0f, 0f, -2f);
            _camGo.transform.localRotation = Quaternion.identity;   // looks along +Z
            _camGo.layer = NavballLayer;

            _cam = _camGo.AddComponent<Camera>();
            _cam.enabled = false;                     // rendered on demand from Tick
            _cam.orthographic = true;
            _cam.orthographicSize = OrthoSize;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane = 8f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.cullingMask = 1 << NavballLayer;
            _cam.targetTexture = _rt;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;

            // Belt and braces on top of the 620 m drop: the scene camera renders
            // every layer by default, so take this one away from it explicitly.
            if (Camera.main != null)
                Camera.main.cullingMask &= ~(1 << NavballLayer);
        }

        private static void SetLayer(GameObject go)
        {
            go.layer = NavballLayer;
            foreach (Transform t in go.transform) SetLayer(t.gameObject);
        }

        /// <summary>
        /// Finds where the sphere mesh puts texture longitude 0, in degrees around
        /// +Y measured from +Z.
        ///
        /// Every equatorial vertex knows both its texture longitude (<c>uv.x</c>)
        /// and its geometric bearing, and the difference between them is constant —
        /// that constant IS the seam. Averaging it as a unit vector rather than a
        /// number keeps the wrap at 360° from dragging the mean to the middle of
        /// nowhere, which is the whole difficulty with averaging angles.
        /// </summary>
        public static float MeasureSeam(Mesh mesh)
        {
            if (mesh == null) return 0f;
            Vector3[] verts = mesh.vertices;
            Vector2[] uv = mesh.uv;
            if (verts.Length == 0 || uv.Length != verts.Length) return 0f;

            float sx = 0f, sy = 0f;
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 p = verts[i];
                // Near the poles the bearing is numerically meaningless, and at the
                // seam u is duplicated 0 and 1 — the equator away from both is where
                // the reading is clean.
                if (Mathf.Abs(uv[i].y - 0.5f) > 0.08f) continue;
                if (uv[i].x < 0.02f || uv[i].x > 0.98f) continue;
                if (new Vector2(p.x, p.z).sqrMagnitude < 1e-6f) continue;

                float bearing = Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg;
                float offset = uv[i].x * 360f - bearing;
                sx += Mathf.Cos(offset * Mathf.Deg2Rad);
                sy += Mathf.Sin(offset * Mathf.Deg2Rad);
            }
            if (sx == 0f && sy == 0f) return 0f;
            return Mathf.Atan2(sy, sx) * Mathf.Rad2Deg;
        }
    }
}
