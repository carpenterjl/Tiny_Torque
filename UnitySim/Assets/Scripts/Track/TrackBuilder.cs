using UnityEngine;

namespace AIHWSim.Track
{
    /// <summary>
    /// Low-level primitive/material helpers for building the track from code.
    /// No imported assets: shapes are Unity primitives and materials/textures
    /// are generated at runtime.
    /// </summary>
    public static class TrackBuilder
    {
        public static Material StandardMat(Color color)
        {
            // "Standard" exists in the Built-in Render Pipeline (this project's default).
            var shader = Shader.Find("Standard");
            var mat = new Material(shader) { color = color };
            return mat;
        }

        public static Texture2D CheckerTexture(int tiles = 8, int pixelsPerTile = 16)
        {
            int size = tiles * pixelsPerTile;
            var tex = new Texture2D(size, size) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool on = ((x / pixelsPerTile) + (y / pixelsPerTile)) % 2 == 0;
                    tex.SetPixel(x, y, on ? Color.white : Color.black);
                }
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Speckled two-tone noise texture (dirt, grass, sand...). Deterministic
        /// per (a, b) so repeated calls look identical.
        /// </summary>
        public static Texture2D NoiseTexture(Color a, Color b, int size = 64, float bAmount = 0.35f)
        {
            var tex = new Texture2D(size, size) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            var rng = new System.Random(a.GetHashCode() ^ b.GetHashCode());
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float t = (float)rng.NextDouble();
                    tex.SetPixel(x, y, t < bAmount ? b : Color.Lerp(a, b, t * 0.25f));
                }
            tex.Apply();
            return tex;
        }

        /// <summary>Alternating stripes along X (rumble strips, hazard edges).</summary>
        public static Texture2D StripeTexture(Color a, Color b, int stripes = 8, int pixelsPerStripe = 8)
        {
            int size = stripes * pixelsPerStripe;
            var tex = new Texture2D(size, size) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, (x / pixelsPerStripe) % 2 == 0 ? a : b);
            tex.Apply();
            return tex;
        }

        /// <summary>Chevron arrows pointing +Y of the texture (boost pads).</summary>
        public static Texture2D ChevronTexture(Color bg, Color arrow, int size = 64)
        {
            var tex = new Texture2D(size, size) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    // Two chevrons per tile: distance from the vertical centerline
                    // added to y makes a ^ shape band.
                    int dx = Mathf.Abs(x - size / 2);
                    int band = (y + dx) % (size / 2);
                    tex.SetPixel(x, y, band < size / 8 ? arrow : bg);
                }
            tex.Apply();
            return tex;
        }

        public static GameObject Box(string name, Vector3 pos, Vector3 size, Quaternion rot,
            Material mat, Transform parent, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            if (!collider) Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = size;
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        public static GameObject Cylinder(string name, Vector3 pos, Vector3 scale, Quaternion rot,
            Material mat, Transform parent, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            if (!collider) Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = scale;
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>
        /// A takeoff ramp: a box tilted up along its local +Z. yawDeg orients it
        /// in the world; the low edge rests near ground so a car can drive up it.
        /// </summary>
        public static GameObject Ramp(string name, Vector3 groundPos, float yawDeg,
            float length, float width, float thickness, float angleDeg, Material mat, Transform parent)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            var rot = Quaternion.Euler(-angleDeg, yawDeg, 0f);
            // Lift so the ramp's lower end is roughly at ground level.
            Vector3 pos = groundPos + Vector3.up * (0.5f * length * Mathf.Sin(a));
            return Box(name, pos, new Vector3(width, thickness, length), rot, mat, parent);
        }

        // ---- procedural prop meshes (cached; regenerated per play session) ----

        private static readonly System.Collections.Generic.Dictionary<string, Mesh> _meshCache
            = new System.Collections.Generic.Dictionary<string, Mesh>();

        private static Material _bandWhite;
        private static Material BandWhite =>
            _bandWhite != null ? _bandWhite : _bandWhite = StandardMat(new Color(0.93f, 0.93f, 0.93f));

        /// <summary>
        /// Flip triangle winding if the first face's normal disagrees with the
        /// expected outward direction at its first vertex (guards the whole
        /// class of inverted-winding bugs — backfaces are invisible AND
        /// non-raycastable).
        /// </summary>
        private static void EnsureOutwardWinding(Vector3[] verts, int[] tris, Vector3 expectedOutward)
        {
            var n = Vector3.Cross(verts[tris[1]] - verts[tris[0]], verts[tris[2]] - verts[tris[0]]);
            if (Vector3.Dot(n, expectedOutward) >= 0f) return;
            for (int i = 0; i < tris.Length; i += 3)
                (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
        }

        /// <summary>Torus lying flat in XZ (tube circling the Y axis), centered at the origin.</summary>
        public static Mesh TorusMesh(float majorR, float minorR, int segMajor = 20, int segMinor = 10)
        {
            string key = $"torus_{majorR:0.###}_{minorR:0.###}_{segMajor}_{segMinor}";
            if (_meshCache.TryGetValue(key, out var cached) && cached != null) return cached;

            var verts = new Vector3[segMajor * segMinor];
            var tris = new int[segMajor * segMinor * 6];
            for (int i = 0; i < segMajor; i++)
            {
                float phi = i * Mathf.PI * 2f / segMajor;
                float cp = Mathf.Cos(phi), sp = Mathf.Sin(phi);
                for (int j = 0; j < segMinor; j++)
                {
                    float th = j * Mathf.PI * 2f / segMinor;
                    float r = majorR + minorR * Mathf.Cos(th);
                    verts[i * segMinor + j] = new Vector3(r * cp, minorR * Mathf.Sin(th), r * sp);
                }
            }
            int t = 0;
            for (int i = 0; i < segMajor; i++)
                for (int j = 0; j < segMinor; j++)
                {
                    int a = i * segMinor + j;
                    int b = ((i + 1) % segMajor) * segMinor + j;
                    int c = i * segMinor + (j + 1) % segMinor;
                    int d = ((i + 1) % segMajor) * segMinor + (j + 1) % segMinor;
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }
            // Outward at vertex 0 (phi=0, theta=0) is +X.
            EnsureOutwardWinding(verts, tris, Vector3.right);

            var mesh = new Mesh { name = key, vertices = verts, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _meshCache[key] = mesh;
            return mesh;
        }

        /// <summary>Closed cone, base circle on y = 0, apex at y = height.</summary>
        public static Mesh ConeMesh(float height, float baseRadius, int segments = 20)
        {
            string key = $"cone_{height:0.###}_{baseRadius:0.###}_{segments}";
            if (_meshCache.TryGetValue(key, out var cached) && cached != null) return cached;

            // Side ring + per-segment apex copies (hard normals at the rim),
            // then a separate base ring + center (hard edge at the base).
            var verts = new Vector3[segments * 2 + segments + 1];
            var tris = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var rim = new Vector3(Mathf.Cos(a) * baseRadius, 0f, Mathf.Sin(a) * baseRadius);
                verts[i] = rim;                                    // side ring
                verts[segments + i] = new Vector3(0f, height, 0f); // apex copies
                verts[segments * 2 + i] = rim;                     // base ring
            }
            int centerIdx = segments * 3;
            verts[centerIdx] = Vector3.zero;

            int t = 0;
            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                // Side: rim_i -> rim_next -> apex_i
                tris[t++] = i; tris[t++] = next; tris[t++] = segments + i;
                // Base fan: baseRim_i -> center -> baseRim_next (faces down).
                tris[t++] = segments * 2 + i; tris[t++] = centerIdx; tris[t++] = segments * 2 + next;
            }
            // Check side winding at vertex 0 (outward ≈ +X); the base fan was
            // wound opposite the side on purpose, so one global flip keeps both
            // consistent.
            EnsureOutwardWinding(verts, tris, Vector3.right);

            var mesh = new Mesh { name = key, vertices = verts, triangles = tris };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _meshCache[key] = mesh;
            return mesh;
        }

        /// <summary>A mesh-based object with an optional convex MeshCollider (props).</summary>
        public static GameObject MeshObj(string name, Mesh mesh, Material mat, Transform parent,
            Vector3 pos, Quaternion rot, bool convexCollider = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (convexCollider)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = true;
            }
            return go;
        }

        /// <summary>
        /// A traffic cone: square base slab + tapered cone body + white band.
        /// One convex MeshCollider (the cone body) on the returned root, so a
        /// Rigidbody added to it carries all the visuals.
        /// </summary>
        public static GameObject Cone(string name, Vector3 groundPos, float height, float radius,
            Material mat, Transform parent)
        {
            var root = MeshObj(name, ConeMesh(height, radius), mat, parent,
                groundPos, Quaternion.identity, convexCollider: true);

            // Square base slab (visual only — the hull's flat bottom does the physics).
            Box("Base", groundPos + Vector3.up * 0.006f,
                new Vector3(radius * 2.4f, 0.012f, radius * 2.4f),
                Quaternion.identity, mat, root.transform, collider: false);

            // Reflective band, slightly proud of the cone surface at ~55% height.
            float bandY = height * 0.55f;
            float bandR = baseRadiusAt(bandY) * 1.12f;
            Cylinder("Band", groundPos + Vector3.up * bandY,
                new Vector3(bandR * 2f, height * 0.06f, bandR * 2f),
                Quaternion.identity, BandWhite, root.transform, collider: false);

            return root;

            float baseRadiusAt(float y) => radius * (1f - y / height);
        }

        /// <summary>A single tire: flat torus + convex collider (rounded-disc hull).</summary>
        public static GameObject Tire(string name, Vector3 pos, float outerRadius, float tubeRadius,
            Material mat, Transform parent)
        {
            return MeshObj(name, TorusMesh(outerRadius - tubeRadius, tubeRadius), mat, parent,
                pos, Quaternion.identity, convexCollider: true);
        }
    }
}
