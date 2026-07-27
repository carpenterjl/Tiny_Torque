using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// Runtime builder for stylized, compound-primitive part visuals — the single
    /// visual source shared by the garage preview and the driving track. Every
    /// piece is a collider-stripped Unity primitive placed on the built-in
    /// Ignore-Raycast layer (2) so the extra geometry never blocks the garage
    /// placement raycast or interferes with track physics.
    ///
    /// Wheels get a tyre + contrasting rim faces + hub + lug studs (and a motor
    /// can when powered); cameras get a housing + lens barrel + glass; ToF modules
    /// get a small PCB + emitter/receiver dots. Materials are shared and lazily
    /// (re)created so they survive play-mode transitions in the editor.
    /// </summary>
    public static class PartVisualFactory
    {
        /// <summary>Built-in "Ignore Raycast" layer — part viz lives here.</summary>
        public const int VizLayer = 2;

        // ---- shared materials (lazy; Unity destroys runtime materials on play
        // exit, so a null check rebuilds them transparently) ----
        private static Material _tire, _rim, _hub, _can, _housing, _lens, _pcb, _emitter, _stud;

        private static Material Mat(ref Material slot, Color c, float smooth, float metal)
        {
            if (slot == null)
            {
                slot = new Material(Shader.Find("Standard")) { color = c };
                slot.SetFloat("_Glossiness", smooth);
                slot.SetFloat("_Metallic", metal);
            }
            return slot;
        }

        private static Material Tire    => Mat(ref _tire,    new Color(0.09f, 0.09f, 0.10f), 0.15f, 0.0f);
        private static Material Rim     => Mat(ref _rim,     new Color(0.78f, 0.80f, 0.84f), 0.6f,  0.8f);
        private static Material Hub     => Mat(ref _hub,     new Color(0.30f, 0.31f, 0.34f), 0.4f,  0.6f);
        private static Material Can     => Mat(ref _can,     new Color(0.62f, 0.64f, 0.68f), 0.7f,  0.9f);
        private static Material Housing => Mat(ref _housing, new Color(0.16f, 0.17f, 0.19f), 0.3f,  0.2f);
        private static Material Lens    => Mat(ref _lens,    new Color(0.05f, 0.06f, 0.09f), 0.9f,  0.1f);
        private static Material Pcb     => Mat(ref _pcb,     new Color(0.10f, 0.42f, 0.20f), 0.35f, 0.1f);
        private static Material Emitter => Mat(ref _emitter, new Color(0.55f, 0.08f, 0.08f), 0.5f,  0.1f);
        private static Material Stud    => Mat(ref _stud,    new Color(0.55f, 0.56f, 0.60f), 0.6f,  0.8f);

        // ---- TinyTorque show-car accents (build_vehicles.py token set) ----
        // One shared table serves bodies, wheels, light parts and antennas:
        // every exported object is named "<token>_<n>" and the tokens are
        // globally unambiguous. Colors are lifted from the source blends.
        private static Material _dark, _gunmetal, _chrome, _gold, _glassMat,
            _tube, _orangeAcc, _white, _decal,
            _emHead, _emTail, _emAmber, _emRed, _emBlue, _barWhite;

        private static Material Em(ref Material slot, Color baseCol, Color emit)
        {
            if (slot == null)
            {
                slot = new Material(Shader.Find("Standard")) { color = baseCol };
                slot.SetFloat("_Glossiness", 0.9f);
                slot.SetFloat("_Metallic", 0f);
                slot.EnableKeyword("_EMISSION");
                slot.SetColor("_EmissionColor", emit);
            }
            return slot;
        }

        private static Material Glass()
        {
            if (_glassMat == null)
            {
                _glassMat = MakeGhostMat(new Color(0.05f, 0.07f, 0.10f, 0.78f));
                _glassMat.SetFloat("_Glossiness", 0.95f);
            }
            return _glassMat;
        }

        internal static Material DarkTrim => Mat(ref _dark,     new Color(0.014f, 0.014f, 0.016f), 0.38f, 0.10f);
        internal static Material Gunmetal => Mat(ref _gunmetal, new Color(0.075f, 0.080f, 0.090f), 0.70f, 1.00f);
        internal static Material Chrome   => Mat(ref _chrome,   new Color(0.86f, 0.88f, 0.90f),    0.92f, 1.00f);
        internal static Material Gold     => Mat(ref _gold,     new Color(1.00f, 0.766f, 0.336f),  0.86f, 1.00f);
        // "carbon" reuses the aero-section Carbon material (declared below).
        internal static Material Tube     => Mat(ref _tube,     new Color(0.055f, 0.058f, 0.066f), 0.77f, 1.00f);
        internal static Material OrangeAccent => Mat(ref _orangeAcc, new Color(0.78f, 0.19f, 0.01f), 0.60f, 0.40f);
        internal static Material WhiteTrim => Mat(ref _white,   new Color(0.80f, 0.81f, 0.82f),    0.70f, 0.00f);
        internal static Material Decal    => Mat(ref _decal,    new Color(0.02f, 0.075f, 0.44f),   0.75f, 0.00f);
        internal static Material HeadLight => Em(ref _emHead, new Color(0.85f, 0.92f, 1f), new Color(2.1f, 2.3f, 2.5f));
        internal static Material TailLight => Em(ref _emTail, new Color(0.55f, 0.02f, 0.02f), new Color(2.4f, 0.08f, 0.08f));
        internal static Material Amber     => Em(ref _emAmber, new Color(1f, 0.35f, 0.03f), new Color(3.0f, 1.05f, 0.09f));
        internal static Material RedStrobe => Em(ref _emRed,  new Color(0.62f, 0.02f, 0.014f), new Color(2.5f, 0.08f, 0.06f));
        internal static Material BlueStrobe => Em(ref _emBlue, new Color(0.02f, 0.07f, 0.72f), new Color(0.08f, 0.28f, 2.9f));
        internal static Material BarWhite  => Em(ref _barWhite, new Color(0.86f, 0.90f, 1f), new Color(1.3f, 1.35f, 1.5f));

        /// <summary>
        /// The full token→material table for build_vehicles.py exports.
        /// ORDER MATTERS — AssignByName is first-match substring, so "barwhite"
        /// must precede "white", and the emissive tokens precede plain trims.
        /// </summary>
        internal static (string, Material)[] AccentTokens => new (string, Material)[]
        {
            ("em_head", HeadLight), ("em_tail", TailLight), ("em_amber", Amber),
            ("em_red", RedStrobe), ("em_blue", BlueStrobe),
            ("barwhite", BarWhite), ("white", WhiteTrim),
            ("gunmetal", Gunmetal), ("chrome", Chrome), ("gold", Gold),
            ("glass", Glass()), ("carbon", Carbon), ("tube", Tube),
            ("orange", OrangeAccent), ("decal", Decal), ("dark", DarkTrim),
        };

        // ---- primitive helper ----

        private static Transform Piece(PrimitiveType type, Transform parent, Material mat,
                                       Vector3 pos, Vector3 euler, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.layer = VizLayer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go.transform;
        }

        // ==================== WHEEL ====================

        /// <summary>Radius the wheel meshes are authored at (66 mm RC tyre).</summary>
        private const float WheelAuthorRadius = 0.033f;

        /// <summary>Map a wheel-style index to its authored FBX key.</summary>
        private static string WheelStyleKey(int style) => style switch
        {
            1 => "knobby",
            2 => "rally",
            3 => "coupe",     // TinyTorque gold-rim street tyre
            4 => "baja",      // TinyTorque orange-rim balloon tyre
            5 => "patrol",    // TinyTorque chrome steelie
            _ => "slick",
        };

        /// <summary>
        /// Build a stylized wheel inside <paramref name="holder"/>. The holder's
        /// local X is the axle. <paramref name="inboardSign"/> (±1) selects which
        /// axle side the motor can sits on so it faces the vehicle body.
        /// <paramref name="style"/> picks an authored tyre mesh (0 slick / 1 knobby /
        /// 2 rally); when the mesh is absent the primitive tyre is built instead.
        /// </summary>
        public static void BuildWheelViz(Transform holder, float radius, bool powered, float inboardSign, int style = 0)
        {
            radius = Mathf.Max(0.01f, radius);

            // Authored-mesh path: instantiate the tyre+rim FBX, scale to the target
            // radius, and let the shared materials drive its look. Axle stays +X so
            // ballooning (holder Y/Z rescale) and WheelCollider spin remain correct.
            var mesh = PartMeshLibrary.TryInstantiate("wheel_" + WheelStyleKey(style), holder);
            if (mesh != null)
            {
                mesh.transform.localScale = Vector3.one * (radius / WheelAuthorRadius);

                // The wheel is authored with its rim face toward +X and the brake
                // disc behind it, so on the axle side where +X points inboard it
                // would show the brake disc to the world. Spin it half a turn about
                // the vertical instead of mirroring with a negative scale: the tyre
                // and rim are solids of revolution about the axle, so a 180 degree
                // turn lands the face on -X with normals and winding left intact.
                if (inboardSign >= 0f)
                    mesh.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

                // TinyTorque tokens (gold/orange/chrome/dark) ahead of the
                // legacy set; both wheel families bind from this one call.
                PartMeshLibrary.AssignByName(mesh, Tire,
                    ("gold", Gold), ("orange", OrangeAccent), ("chrome", Chrome),
                    ("dark", DarkTrim), ("brake", Hub),
                    ("tire", Tire), ("tyre", Tire), ("rim", Rim), ("hub", Hub),
                    ("stud", Stud));
                if (powered) BuildMotorCan(holder, radius, inboardSign);
                return;
            }

            float d = radius * 2f;              // tyre diameter
            float halfWidth = radius * 0.4f;    // proportional tread (66 mm tyre → 26 mm wide)

            // Tyre: cylinder axis rotated so its length runs along the axle (X).
            Piece(PrimitiveType.Cylinder, holder, Tire,
                Vector3.zero, new Vector3(0f, 0f, 90f), new Vector3(d, halfWidth, d));

            // Rim faces: contrasting discs just inside each tyre face.
            float rimD = d * 0.66f;
            for (int s = -1; s <= 1; s += 2)
            {
                Piece(PrimitiveType.Cylinder, holder, Rim,
                    new Vector3(s * (halfWidth - radius * 0.03f), 0f, 0f), new Vector3(0f, 0f, 90f),
                    new Vector3(rimD, radius * 0.06f, rimD));

                // Five lug studs on a bolt circle on each face.
                float bolt = radius * 0.32f;
                for (int i = 0; i < 5; i++)
                {
                    float a = i * Mathf.PI * 2f / 5f;
                    Piece(PrimitiveType.Cylinder, holder, Stud,
                        new Vector3(s * (halfWidth + radius * 0.015f), Mathf.Sin(a) * bolt, Mathf.Cos(a) * bolt),
                        new Vector3(0f, 0f, 90f),
                        new Vector3(radius * 0.09f, radius * 0.045f, radius * 0.09f));
                }
            }

            // Hub cap through the centre.
            Piece(PrimitiveType.Cylinder, holder, Hub,
                Vector3.zero, new Vector3(0f, 0f, 90f),
                new Vector3(radius * 0.42f, halfWidth + radius * 0.06f, radius * 0.42f));

            if (powered) BuildMotorCan(holder, radius, inboardSign);
        }

        /// <summary>Motor can + output shaft on the inboard axle side (shared by the
        /// primitive and mesh wheel paths).</summary>
        /// <remarks>
        /// Sized as a 2836-class outrunner: about 28 mm across by 36 mm long at the
        /// stock 33 mm wheel radius. The game drives one motor per wheel, so a real
        /// 540 can (36 x 50 mm) would be almost as wide as the tyre it turns and
        /// four of them would swamp the car. Everything stays proportional to
        /// radius so oversized wheels keep sane motors.
        /// </remarks>
        private static void BuildMotorCan(Transform holder, float radius, float inboardSign)
        {
            float halfWidth = radius * 0.4f;
            float sgn = inboardSign >= 0f ? 1f : -1f;
            const float canD = 0.848f;   // x radius -> 28.0 mm diameter at r = 33 mm
            const float canL = 0.545f;   // x radius -> 36.0 mm long  at r = 33 mm

            float canCentre = halfWidth + radius * canL;
            Piece(PrimitiveType.Cylinder, holder, Can,
                new Vector3(sgn * canCentre, 0f, 0f), new Vector3(0f, 0f, 90f),
                new Vector3(radius * canD, radius * canL, radius * canD));

            // Chamfered end bell: a slightly narrower, shorter can capping the
            // outer face so the motor does not read as a bare cylinder.
            Piece(PrimitiveType.Cylinder, holder, Can,
                new Vector3(sgn * (canCentre + radius * canL * 0.92f), 0f, 0f),
                new Vector3(0f, 0f, 90f),
                new Vector3(radius * canD * 0.80f, radius * canL * 0.12f, radius * canD * 0.80f));

            // Output shaft poking back toward the wheel.
            Piece(PrimitiveType.Cylinder, holder, Hub,
                new Vector3(sgn * (halfWidth + radius * 0.12f), 0f, 0f), new Vector3(0f, 0f, 90f),
                new Vector3(radius * 0.16f, radius * 0.45f, radius * 0.16f));
        }

        // ==================== CAMERA ====================

        /// <summary>Camera model: housing box + lens barrel + glass, facing +Z (aim).</summary>
        public static void BuildCameraViz(Transform parent)
        {
            Piece(PrimitiveType.Cube, parent, Housing,
                new Vector3(0f, 0f, -0.003f), Vector3.zero, new Vector3(0.024f, 0.018f, 0.015f));
            // Barrel: cylinder axis rotated to point along +Z.
            Piece(PrimitiveType.Cylinder, parent, Housing,
                new Vector3(0f, 0f, 0.010f), new Vector3(90f, 0f, 0f), new Vector3(0.009f, 0.0045f, 0.009f));
            Piece(PrimitiveType.Cylinder, parent, Lens,
                new Vector3(0f, 0f, 0.015f), new Vector3(90f, 0f, 0f), new Vector3(0.0075f, 0.0006f, 0.0075f));
        }

        // ==================== ToF ====================

        /// <summary>ToF module: small PCB slab + two emitter/receiver dots facing +Z.</summary>
        public static void BuildTofViz(Transform parent)
        {
            Piece(PrimitiveType.Cube, parent, Pcb,
                Vector3.zero, Vector3.zero, new Vector3(0.02f, 0.005f, 0.014f));
            for (int s = -1; s <= 1; s += 2)
                Piece(PrimitiveType.Cylinder, parent, Emitter,
                    new Vector3(s * 0.005f, 0f, 0.006f), new Vector3(90f, 0f, 0f),
                    new Vector3(0.004f, 0.0025f, 0.004f));
        }

        // ==================== ENCODER ====================

        /// <summary>Encoder: a small slotted disc on a stub, facing +Z.</summary>
        public static void BuildEncoderViz(Transform parent)
        {
            Piece(PrimitiveType.Cylinder, parent, Hub,
                new Vector3(0f, 0f, 0.002f), new Vector3(90f, 0f, 0f), new Vector3(0.02f, 0.0025f, 0.02f));
            // A couple of ticks for a coded-wheel read.
            for (int i = 0; i < 3; i++)
            {
                float a = i * Mathf.PI * 2f / 3f;
                Piece(PrimitiveType.Cube, parent, Emitter,
                    new Vector3(Mathf.Sin(a) * 0.0065f, Mathf.Cos(a) * 0.0065f, 0.005f), Vector3.zero,
                    new Vector3(0.0025f, 0.0025f, 0.0035f));
            }
        }

        // ==================== SUSPENSION SENSOR ====================

        /// <summary>Coil-over strut: a silver shock body/rod stood on local Y with a
        /// few accent coil rings — a recognizable little suspension sensor.</summary>
        public static void BuildSuspensionViz(Transform parent)
        {
            // Lower shock body + upper rod (concentric cylinders along +Y).
            Piece(PrimitiveType.Cylinder, parent, Hub,
                new Vector3(0f, 0.006f, 0f), Vector3.zero, new Vector3(0.008f, 0.008f, 0.008f));
            Piece(PrimitiveType.Cylinder, parent, Can,
                new Vector3(0f, 0.018f, 0f), Vector3.zero, new Vector3(0.004f, 0.008f, 0.004f));
            // End caps (mount eyes).
            Piece(PrimitiveType.Cylinder, parent, Stud,
                new Vector3(0f, 0.028f, 0f), Vector3.zero, new Vector3(0.006f, 0.0015f, 0.006f));
            // Coil rings around the body (accent).
            for (int i = 0; i < 4; i++)
                Piece(PrimitiveType.Cylinder, parent, Emitter,
                    new Vector3(0f, 0.001f + i * 0.006f, 0f), Vector3.zero,
                    new Vector3(0.011f, 0.0012f, 0.011f));
        }

        // ==================== SUSPENSION STRUT (per-wheel, visible) ====================

        /// <summary>
        /// Visible coil-over strut authored along +Z with unit length [0,1]. The
        /// caller parents it to the body, points +Z at the wheel hub, and sets
        /// <c>localScale.z</c> to the mount→hub distance, so the whole strut
        /// stretches/compresses with the wheel. x/y stay in real metres (the parent's
        /// x/y scale is 1), so only the length scales. Distinct from the firmware
        /// <see cref="BuildSuspensionViz"/> sensor — this is the wheel's own strut.
        /// </summary>
        public static void BuildStrutViz(Transform parent)
        {
            // Central shock body spanning the full length (dark tube).
            Piece(PrimitiveType.Cylinder, parent, Hub,
                new Vector3(0f, 0f, 0.5f), new Vector3(90f, 0f, 0f),
                new Vector3(0.010f, 0.5f, 0.010f));
            // Piston rod — thinner, toward the hub end (telescoping look).
            Piece(PrimitiveType.Cylinder, parent, Can,
                new Vector3(0f, 0f, 0.72f), new Vector3(90f, 0f, 0f),
                new Vector3(0.005f, 0.28f, 0.005f));
            // Mount eyes at both ends (body mount at z=0, hub at z=1).
            Piece(PrimitiveType.Cylinder, parent, Stud,
                new Vector3(0f, 0f, 0f), new Vector3(90f, 0f, 0f),
                new Vector3(0.008f, 0.02f, 0.008f));
            Piece(PrimitiveType.Cylinder, parent, Stud,
                new Vector3(0f, 0f, 1f), new Vector3(90f, 0f, 0f),
                new Vector3(0.008f, 0.02f, 0.008f));
            // Accent coil rings around the body.
            for (int i = 0; i < 5; i++)
                Piece(PrimitiveType.Cylinder, parent, Emitter,
                    new Vector3(0f, 0f, 0.12f + i * 0.14f), new Vector3(90f, 0f, 0f),
                    new Vector3(0.014f, 0.012f, 0.014f));
        }

        // ==================== BATTERY ====================

        private static Material _lipo;
        private static Material Lipo => Mat(ref _lipo, new Color(0.12f, 0.14f, 0.35f), 0.35f, 0.1f);

        /// <summary>LiPo pack: shrink-wrapped box + two terminal nubs + balance lead stub.</summary>
        public static void BuildBatteryViz(Transform parent)
        {
            var mesh = PartMeshLibrary.TryInstantiate("battery_stick", parent);
            if (mesh != null)
            {
                PartMeshLibrary.AssignByName(mesh, Lipo,
                    ("wrap", Lipo), ("cell", Lipo), ("term", Stud), ("nub", Stud), ("lead", Rim));
                return;
            }

            // Pack body (55×16×30 mm, long axis along Z like a real tray mount).
            Piece(PrimitiveType.Cube, parent, Lipo,
                Vector3.zero, Vector3.zero, new Vector3(0.030f, 0.016f, 0.055f));
            // Terminal nubs on the front face.
            for (int s = -1; s <= 1; s += 2)
                Piece(PrimitiveType.Cylinder, parent, Stud,
                    new Vector3(s * 0.007f, 0.004f, 0.029f), new Vector3(90f, 0f, 0f),
                    new Vector3(0.004f, 0.002f, 0.004f));
            // Balance-lead stub (small white block on a corner).
            Piece(PrimitiveType.Cube, parent, Rim,
                new Vector3(0.012f, 0.004f, 0.026f), Vector3.zero,
                new Vector3(0.006f, 0.004f, 0.006f));
        }

        // ==================== ANTENNA ====================

        private static Material _rubber;
        private static Material Rubber => Mat(ref _rubber, new Color(0.06f, 0.06f, 0.07f), 0.85f, 0.0f);

        /// <summary>
        /// Cosmetic antenna: an SMA base + tapered rubber whip, leaned back by
        /// <paramref name="tiltDeg"/> and scaled by <paramref name="sizeScale"/>.
        /// Uses the authored FBX when present, else a primitive tapered stack.
        /// <paramref name="style"/> picks the mesh: 0 stub, 1 TinyTorque whip
        /// with amber tip, 2 flag whip, 3 twin trunk whips (any lean the show
        /// styles have is baked into the mesh — presets pass tiltDeg 0).
        /// </summary>
        public static void BuildAntennaViz(Transform parent, float tiltDeg, float sizeScale, int style = 0)
        {
            float s = Mathf.Clamp(sizeScale <= 0f ? 1f : sizeScale, 0.6f, 1.6f);
            string key = style switch
            {
                1 => "antenna_whip",
                2 => "antenna_flag",
                3 => "antenna_twin",
                _ => "antenna_stub",
            };
            var mesh = PartMeshLibrary.TryInstantiate(key, parent);
            if (mesh == null && style != 0)
                mesh = PartMeshLibrary.TryInstantiate("antenna_stub", parent);
            if (mesh != null)
            {
                mesh.transform.localRotation = Quaternion.Euler(tiltDeg, 0f, 0f);
                mesh.transform.localScale = Vector3.one * s;
                PartMeshLibrary.AssignByName(mesh, Rubber,
                    ("whip", Rubber), ("base", Can), ("sma", Can),
                    ("em_amber", Amber), ("flag", OrangeAccent));
                return;
            }

            // Primitive fallback: base nub + two-segment tapered whip, tilted about X.
            var holder = new GameObject("antenna").transform;
            holder.SetParent(parent, false);
            holder.gameObject.layer = VizLayer;
            holder.localRotation = Quaternion.Euler(tiltDeg, 0f, 0f);
            holder.localScale = Vector3.one * s;
            Piece(PrimitiveType.Cylinder, holder, Can,
                new Vector3(0f, 0.008f, 0f), Vector3.zero, new Vector3(0.008f, 0.008f, 0.008f));
            Piece(PrimitiveType.Cylinder, holder, Rubber,
                new Vector3(0f, 0.045f, 0f), Vector3.zero, new Vector3(0.007f, 0.030f, 0.007f));
            Piece(PrimitiveType.Cylinder, holder, Rubber,
                new Vector3(0f, 0.090f, 0f), Vector3.zero, new Vector3(0.004f, 0.020f, 0.004f));
        }

        // ==================== LIGHTS ====================

        /// <summary>
        /// Cosmetic light cluster: style 0 = police roof light bar (red/blue
        /// lenses strobe via <see cref="LightBarStrobe"/>), style 1 = off-road
        /// pod cluster (steady glow). Authored FBX when present, else a small
        /// primitive bar with two emissive lenses. Lives on the viz layer like
        /// every part, so the on-car camera never sees it.
        /// </summary>
        public static void BuildLightViz(Transform parent, int style, float sizeScale)
        {
            float s = Mathf.Clamp(sizeScale <= 0f ? 1f : sizeScale, 0.6f, 1.6f);
            string key = style == 1 ? "light_pods" : "light_bar";
            var mesh = PartMeshLibrary.TryInstantiate(key, parent);
            if (mesh != null)
            {
                mesh.transform.localScale = Vector3.one * s;
                PartMeshLibrary.AssignByName(mesh, DarkTrim,
                    ("em_red", RedStrobe), ("em_blue", BlueStrobe),
                    ("em_head", HeadLight), ("barwhite", BarWhite),
                    ("chrome", Chrome), ("dark", DarkTrim));
                if (style == 0) mesh.AddComponent<LightBarStrobe>();
                return;
            }

            // Primitive fallback: dark base + red/blue (bar) or white (pods) lenses.
            var holder = new GameObject("light").transform;
            holder.SetParent(parent, false);
            holder.gameObject.layer = VizLayer;
            holder.localScale = Vector3.one * s;
            Piece(PrimitiveType.Cube, holder, DarkTrim,
                new Vector3(0f, 0.006f, 0f), Vector3.zero, new Vector3(0.11f, 0.010f, 0.028f));
            Piece(PrimitiveType.Cube, holder, style == 0 ? RedStrobe : HeadLight,
                new Vector3(-0.032f, 0.008f, 0f), Vector3.zero, new Vector3(0.036f, 0.009f, 0.024f));
            Piece(PrimitiveType.Cube, holder, style == 0 ? BlueStrobe : HeadLight,
                new Vector3(0.032f, 0.008f, 0f), Vector3.zero, new Vector3(0.036f, 0.009f, 0.024f));
            if (style == 0) holder.gameObject.AddComponent<LightBarStrobe>();
        }

        // ==================== AERO ====================

        private static Material _carbon, _plate;
        private static Material Carbon => Mat(ref _carbon, new Color(0.13f, 0.13f, 0.15f), 0.55f, 0.3f);
        private static Material Plate  => Mat(ref _plate,  new Color(0.24f, 0.25f, 0.28f), 0.45f, 0.4f);

        /// <summary>Dispatch an aero part's stylized visual by kind.</summary>
        public static void BuildAeroViz(Transform parent, AeroKind kind, float angleDeg, float sizeScale)
        {
            switch (kind)
            {
                case AeroKind.Wing: BuildWingViz(parent, angleDeg, sizeScale); break;
                case AeroKind.Splitter: BuildSplitterViz(parent, sizeScale); break;
                case AeroKind.SideDam: BuildSideDamViz(parent, sizeScale); break;
                case AeroKind.Canard: BuildCanardViz(parent, angleDeg, sizeScale); break;
            }
        }

        /// <summary>Rear wing: pitched main plate + endplates + two struts.</summary>
        public static void BuildWingViz(Transform parent, float angleDeg, float s)
        {
            // Main plate, nose-down by the attack angle (downforce airfoil).
            Piece(PrimitiveType.Cube, parent, Carbon,
                Vector3.zero, new Vector3(angleDeg, 0f, 0f),
                new Vector3(0.20f * s, 0.006f * s, 0.05f * s));
            // Endplates.
            for (int e = -1; e <= 1; e += 2)
                Piece(PrimitiveType.Cube, parent, Plate,
                    new Vector3(e * 0.10f * s, 0.004f * s, 0f), Vector3.zero,
                    new Vector3(0.004f * s, 0.035f * s, 0.06f * s));
            // Struts down to the body.
            for (int e = -1; e <= 1; e += 2)
                Piece(PrimitiveType.Cube, parent, Plate,
                    new Vector3(e * 0.05f * s, -0.02f * s, 0.005f * s), Vector3.zero,
                    new Vector3(0.006f * s, 0.04f * s, 0.012f * s));
        }

        /// <summary>Front splitter: a low protruding lip plate.</summary>
        public static void BuildSplitterViz(Transform parent, float s)
        {
            Piece(PrimitiveType.Cube, parent, Carbon,
                Vector3.zero, Vector3.zero,
                new Vector3(0.16f * s, 0.005f * s, 0.04f * s));
            // Small vertical fences at the lip's outer edges.
            for (int e = -1; e <= 1; e += 2)
                Piece(PrimitiveType.Cube, parent, Plate,
                    new Vector3(e * 0.075f * s, 0.008f * s, 0f), Vector3.zero,
                    new Vector3(0.004f * s, 0.014f * s, 0.038f * s));
        }

        /// <summary>Side dam: a vertical fin panel.</summary>
        public static void BuildSideDamViz(Transform parent, float s)
        {
            Piece(PrimitiveType.Cube, parent, Carbon,
                Vector3.zero, Vector3.zero,
                new Vector3(0.005f * s, 0.022f * s, 0.08f * s));
        }

        /// <summary>Canard: a small angled fin pair.</summary>
        public static void BuildCanardViz(Transform parent, float angleDeg, float s)
        {
            for (int e = -1; e <= 1; e += 2)
                Piece(PrimitiveType.Cube, parent, Carbon,
                    new Vector3(e * 0.016f * s, 0f, 0f), new Vector3(angleDeg, 0f, e * 12f),
                    new Vector3(0.03f * s, 0.003f * s, 0.02f * s));
        }

        // ==================== GHOST ====================

        /// <summary>A translucent material for drag-placement ghosts (Standard, fade mode).</summary>
        public static Material MakeGhostMat(Color tint)
        {
            var m = new Material(Shader.Find("Standard")) { color = tint };
            m.SetFloat("_Mode", 3f); // Transparent
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 3000;
            return m;
        }

        /// <summary>Recolour every renderer in a hierarchy (used to tint a ghost).</summary>
        public static void ApplyMaterial(GameObject root, Material mat)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterial = mat;
        }
    }
}
