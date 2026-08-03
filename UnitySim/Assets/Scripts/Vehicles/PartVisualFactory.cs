using System.Collections.Generic;
using UnityEngine;

namespace AIHWSim.Vehicles
{
    /// <summary>
    /// A rim re-tint applied over an existing wheel mesh, as opposed to a wheel
    /// that is its own mesh.
    ///
    /// Named so the wheel path can stop asking "is the style between 6 and 8".
    /// That range test is the reason a Legendary wheel nearly shipped neon pink,
    /// and it is the one thing about wheel styles that an FBX key cannot carry —
    /// three of them share the slick's mesh and differ only here.
    /// </summary>
    public enum WheelFinish { None, Chrome, Gold, Neon }

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

        // ---- TinyTorque Legendary cars (rattle/redline/highwing/autopia) ----
        // Same deal one pass later: every number below is the authored
        // Principled value out of the source blend, with smoothness = 1 −
        // roughness. Materials whose authored numbers land close enough to an
        // existing token to be indistinguishable in game were mapped onto that
        // token in the exporter instead of appearing here — M_Toon_Gun onto
        // "gunmetal", M_Toon_Red and M_Auto_Red onto "em_tail" — so this list
        // is only the looks that are genuinely new.
        private static Material _steel, _rust, _rustPaint,
            _coupePaint, _bajaPaint, _patrolPaint, _emLamp, _emAutoLamp,
            _redGold, _redTrim, _hwWhite, _hwTrim,
            _autoGlass, _seat, _hubcap, _whitewall,
            _sclera, _pupil, _emSpec, _tooth, _gum, _tongue, _maw,
            _irisRattle, _irisRedline, _irisHighwing;

        internal static Material Steel     => Mat(ref _steel,     new Color(0.640f, 0.652f, 0.672f), 0.66f, 1.00f);
        internal static Material Rust      => Mat(ref _rust,      new Color(0.230f, 0.086f, 0.040f), 0.13f, 0.25f);
        internal static Material RedGold   => Mat(ref _redGold,   new Color(0.735f, 0.560f, 0.145f), 0.74f, 0.90f);
        internal static Material RedTrim   => Mat(ref _redTrim,   new Color(0.100f, 0.100f, 0.110f), 0.72f, 0.20f);
        internal static Material HwWhite   => Mat(ref _hwWhite,   new Color(0.880f, 0.888f, 0.905f), 0.74f, 0.00f);
        internal static Material HwTrim    => Mat(ref _hwTrim,    new Color(0.620f, 0.640f, 0.680f), 0.72f, 1.00f);
        // The Autopia's wraparound screen is authored OPAQUE (alpha 1, no
        // transmission) — a pale toon windscreen, not glass. Imported as it is
        // modelled; turning it transparent here would be a redesign.
        internal static Material AutoGlass => Mat(ref _autoGlass, new Color(0.820f, 0.870f, 0.885f), 0.96f, 0.00f);
        internal static Material Seat      => Mat(ref _seat,      new Color(0.115f, 0.120f, 0.135f), 0.48f, 0.00f);
        internal static Material Hubcap    => Mat(ref _hubcap,    new Color(0.880f, 0.888f, 0.902f), 0.70f, 0.82f);
        internal static Material Whitewall => Mat(ref _whitewall, new Color(0.880f, 0.876f, 0.855f), 0.56f, 0.00f);
        internal static Material EmLamp     => Em(ref _emLamp,     new Color(0.870f, 0.878f, 0.845f), new Color(0.850f, 0.816f, 0.748f));
        internal static Material EmAutoLamp => Em(ref _emAutoLamp, new Color(0.940f, 0.940f, 0.900f), new Color(3.200f, 3.072f, 2.752f));

        // Face rig. One set serves all three character cars — only the iris
        // colour differs between them, which is why it is the only per-car one.
        internal static Material Sclera => Mat(ref _sclera, new Color(0.940f, 0.940f, 0.920f), 0.66f, 0f);
        internal static Material Pupil  => Mat(ref _pupil,  new Color(0.020f, 0.020f, 0.026f), 0.78f, 0f);
        internal static Material Tooth  => Mat(ref _tooth,  new Color(0.930f, 0.905f, 0.830f), 0.70f, 0f);
        internal static Material Gum    => Mat(ref _gum,    new Color(0.330f, 0.086f, 0.098f), 0.48f, 0f);
        internal static Material Tongue => Mat(ref _tongue, new Color(0.560f, 0.170f, 0.190f), 0.56f, 0f);
        internal static Material Maw    => Mat(ref _maw,    new Color(0.028f, 0.020f, 0.022f), 0.22f, 0f);
        internal static Material EmSpec => Em(ref _emSpec, Color.white, new Color(2.2f, 2.2f, 2.2f));
        internal static Material IrisRattle   => Mat(ref _irisRattle,   new Color(0.340f, 0.500f, 0.260f), 0.80f, 0f);
        internal static Material IrisRedline  => Mat(ref _irisRedline,  new Color(0.140f, 0.440f, 0.800f), 0.80f, 0f);
        internal static Material IrisHighwing => Mat(ref _irisHighwing, new Color(0.300f, 0.460f, 0.300f), 0.80f, 0f);

        /// <summary>
        /// Rattletrap's paint — the one authored material in the project that
        /// is not a set of constants. It is an object-space noise multiplied by
        /// a height ramp, blending faded teal into oxide, so build_vehicles.py
        /// bakes its colour to <c>body_rattle_paint.png</c> and ships that
        /// beside the FBX. Smoothness and metallic are the measured means of
        /// the same mask (roughness 0.6086 → smoothness 0.3914, metallic
        /// 0.1357), printed by the exporter's bake pass.
        ///
        /// This is deliberately NOT the tintable "paint" channel: the body
        /// material carries the livery texture, and one mainTexture cannot be
        /// both. Rattletrap therefore has no repaintable panels — its finish is
        /// the character.
        /// </summary>
        private static Material RustPaint =>
            BakedPaint(ref _rustPaint, "body_rattle_paint", 0.3914f, 0.1357f);

        // The three TinyTorque liveries, Rattletrap-class since the fidelity
        // pass: procedural in the source (candy crimson + graphite stripe +
        // gold pinstripes; acid lime + graphite bands + orange pinstripes; the
        // police black/white with navy flash and gold pinstripe), flattened to
        // 0.8 grey for four passes because the exporter mislabelled them as
        // constants. Baked by build_vehicles.py; smoothness = 1 − the bake's
        // measured mean roughness, metallic the mask-weighted mean noted at
        // each car's exporter config. Like the Rattletrap, these bodies have
        // no tintable panel (CarVehicle.HasPaintableBody).
        private static Material CoupePaint =>
            BakedPaint(ref _coupePaint, "body_coupe_paint", 0.8083f, 0.60f);
        private static Material BajaPaint =>
            BakedPaint(ref _bajaPaint, "body_baja_paint", 0.6894f, 0.15f);
        private static Material PatrolPaint =>
            BakedPaint(ref _patrolPaint, "body_patrol_paint", 0.7905f, 0.20f);

        private static Material BakedPaint(ref Material slot, string texName,
            float smooth, float metal)
        {
            if (slot == null)
            {
                slot = new Material(Shader.Find("Standard")) { color = Color.white };
                slot.SetFloat("_Glossiness", smooth);
                slot.SetFloat("_Metallic", metal);
                var tex = Resources.Load<Texture2D>("PartModels/" + texName);
                if (tex != null) slot.mainTexture = tex;
                else Debug.LogWarning($"[PartVisualFactory] {texName} texture " +
                                      "missing — the body will render flat white.");
            }
            return slot;
        }

        /// <summary>
        /// The full token→material table for build_vehicles.py exports.
        /// ORDER MATTERS, and it is the one thing here nothing else can catch —
        /// AssignByName is first-match substring, the compiler sees a valid
        /// table either way, and a swallowed token renders as a plausible wrong
        /// material rather than as an error. Every COMPOUND token therefore
        /// precedes the token it contains:
        ///
        ///   barwhite / whitewall / hwwhite  before  white
        ///   redgold                          before  gold
        ///   rustpaint                        before  rust
        ///   autoglass                        before  glass
        ///   em_autolamp                      before  em_lamp
        ///
        /// "redgold" and "hwwhite" really were the wrong way round first time:
        /// the Redline's flank flash came out the Coupe's gold and the
        /// Highwing's wing came out generic white trim.
        /// </summary>
        public static (string, Material)[] AccentTokens => new (string, Material)[]
        {
            ("em_autolamp", EmAutoLamp), ("em_lamp", EmLamp), ("em_spec", EmSpec),
            ("em_head", HeadLight), ("em_tail", TailLight), ("em_amber", Amber),
            ("em_red", RedStrobe), ("em_blue", BlueStrobe),
            ("barwhite", BarWhite), ("whitewall", Whitewall), ("hwwhite", HwWhite),
            ("white", WhiteTrim),
            ("rustpaint", RustPaint), ("rust", Rust),
            // The baked liveries. They contain "paint", but the tintable paint
            // channel is matched by StartsWith BEFORE this table runs and no
            // token here is a substring of them, so order is free.
            ("coupepaint", CoupePaint), ("bajapaint", BajaPaint),
            ("patrolpaint", PatrolPaint),
            ("redgold", RedGold), ("gold", Gold),
            ("autoglass", AutoGlass), ("glass", Glass()),
            ("gunmetal", Gunmetal), ("chrome", Chrome),
            ("carbon", Carbon), ("tube", Tube),
            ("orange", OrangeAccent), ("decal", Decal),
            ("redtrim", RedTrim), ("hwtrim", HwTrim),
            ("steel", Steel), ("hubcap", Hubcap), ("seat", Seat),
            ("irisrattle", IrisRattle), ("irisredline", IrisRedline),
            ("irishighwing", IrisHighwing),
            ("sclera", Sclera), ("pupil", Pupil), ("tooth", Tooth),
            ("tongue", Tongue), ("gum", Gum), ("maw", Maw),
            ("dark", DarkTrim),
        };

        /// <summary>
        /// The token→material table for wheel meshes — separate from
        /// <see cref="AccentTokens"/> because a wheel has pieces a body never
        /// does (tyre, brake disc, studs) and because "rim" and "hub" are far
        /// too greedy to live in the body table.
        ///
        /// Ordered by the same rule, and it matters just as much here:
        /// "redtrim" and "hwtrim" both CONTAIN "rim", and "hubcap" contains
        /// "hub". Named rather than inlined so PartModelValidator can check
        /// that ordering against the shipped FBX.
        /// </summary>
        public static (string, Material)[] WheelTokens => new (string, Material)[]
        {
            ("gold", Gold), ("orange", OrangeAccent), ("chrome", Chrome),
            ("whitewall", Whitewall), ("redtrim", RedTrim), ("hwtrim", HwTrim),
            ("hubcap", Hubcap), ("steel", Steel),
            ("dark", DarkTrim), ("brake", Hub),
            ("tire", Tire), ("tyre", Tire), ("rim", Rim), ("hub", Hub),
            ("stud", Stud),
        };

        /// <summary>
        /// The token→material table for the full-scale Tiguan — a THIRD table,
        /// not thirty more rows in <see cref="AccentTokens"/>.
        ///
        /// Two reasons, and both are load-bearing. Its numbers come from a
        /// Blender-probed manifest rather than from the measured C# tables
        /// above, so it is built at runtime and cannot be a static array here.
        /// And leaving the other two tables' contents AND order untouched is
        /// what guarantees this work cannot regress any of the seven arcade
        /// cars — a claim worth having by construction, given the ordering bugs
        /// the AccentTokens comment records.
        ///
        /// One table serves the Tiguan's body and both its wheels: the wheel
        /// needs tigrubber/tigtread/tigrim/tigdisc/tiggloss/tigchrome, all of
        /// which the body already carries, so there is no second split to make.
        /// </summary>
        public static (string, Material)[] TiguanTokens => TiguanMaterials.Tokens;

        /// <summary>
        /// Bind a body shell's renderers by object-name token: the tintable
        /// <paramref name="paintMat"/> for anything named "paint*", the first
        /// <paramref name="accents"/> token contained in the name otherwise, and
        /// <paramref name="paintMat"/> again for a name that matches nothing —
        /// so a renamed export shows up as tintable, not magenta. Every renderer
        /// that ends up on <paramref name="paintMat"/> is added to
        /// <paramref name="paintRenderers"/> (may be null), which is what makes
        /// bodyColor, livery and SetBodyMaterial touch the paint panels only.
        ///
        /// Lifted verbatim out of <c>CarVehicle.AssignBodyAccents</c>, which now
        /// calls it, so that Asset Studio's preview binds a shell through the
        /// SAME code the car does. A preview with its own copy of this loop would
        /// be a preview that can quietly disagree with Play about which panel is
        /// chrome — and disagreeing about exactly that is the failure the tool
        /// exists to catch.
        ///
        /// The paint check is StartsWith and the accent check is Contains, and
        /// that asymmetry is deliberate: it is what lets "coupepaint" be an
        /// accent while "paint_3" is the tintable channel.
        ///
        /// A null <paramref name="accents"/> means "flatten everything onto
        /// <paramref name="paintMat"/>" — the literal behaviour the three legacy
        /// shells need, which is why <c>CarVehicle.BodyAccentTable</c> can
        /// return null instead of the caller branching.
        ///
        /// Returns the <see cref="MaterialBindings"/> the walk produced, with
        /// the paint channel and an unmatched name recorded as DIFFERENT
        /// sources even though both land on <paramref name="paintMat"/>. They
        /// are indistinguishable on screen and mean opposite things: one is an
        /// authored tintable panel, the other is a renamed export nobody has
        /// noticed. No caller consumes this yet.
        ///
        /// <b>A manifest asset never reaches the loop below</b>, for the reason
        /// <see cref="PartMeshLibrary.AssignByName"/> gives at the same seam:
        /// <paramref name="inst"/> carrying a <see cref="PartManifestBinding"/>
        /// sends the whole call to <see cref="AssetManifestBinder"/>, which binds
        /// by object name and slot and consults no token. It is handed the same
        /// <paramref name="paintMat"/> and the same
        /// <paramref name="paintRenderers"/>, because the paint channel means the
        /// same thing on both paths — the difference is only that a manifest says
        /// WHICH SLOT is paint instead of guessing from a name prefix.
        /// </summary>
        public static MaterialBindings BindByToken(GameObject inst, (string, Material)[] accents,
            Material paintMat, ICollection<MeshRenderer> paintRenderers = null)
        {
            var bindings = new MaterialBindings();
            if (inst == null) return bindings;

            var manifest = inst.GetComponent<PartManifestBinding>();
            if (manifest != null)
                return AssetManifestBinder.Bind(manifest, paintMat, paintRenderers);

            foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
            {
                string n = r.gameObject.name.ToLowerInvariant();
                Material hit = null;
                string won = null;
                bool paintName = n.StartsWith("paint");
                if (!paintName && accents != null)
                    foreach (var (token, mat) in accents)
                        if (n.Contains(token)) { hit = mat; won = token; break; }
                if (hit != null)
                {
                    r.sharedMaterial = hit;
                    bindings.Add(r, 0, hit, won, BindSource.Token);
                }
                else
                {
                    r.sharedMaterial = paintMat;
                    paintRenderers?.Add(r);
                    bindings.Add(r, 0, paintMat, null,
                                 paintName ? BindSource.PaintChannel : BindSource.Fallback);
                }
            }
            return bindings;
        }

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

        /// <summary>Radius the wheel meshes are authored at (66 mm RC tyre).
        /// Public because cosmetic rims are baked to the same radius and scale
        /// by the same factor (see CosmeticMounts).</summary>
        public const float WheelAuthorRadius = 0.033f;

        /// <summary>
        /// The tyre material <see cref="BuildWheelViz"/> hands
        /// <c>AssignByName</c> as the fallback on every wheel mesh.
        ///
        /// Exposed so Asset Studio's preview can pass the SAME fallback rather
        /// than a look-alike black. A wheel piece that matches no token coming
        /// out tyre-coloured is a fact about the token table, and a preview that
        /// invented its own grey would turn that fact into a shrug.
        /// </summary>
        public static Material TyreMaterial => Tire;

        // WheelStyleKey lived here until K3c: a switch from the persisted int to
        // an FBX name, with three styles deliberately mapping to the slick
        // because they are FINISHES over it. WheelCatalog.meshKey says the same
        // thing next to the finish that explains it, which is the arrangement
        // that stops a fourth finish being added to one and not the other.

        /// <summary>
        /// The radius at which a wheel mesh renders UNSCALED.
        ///
        /// For every arcade style that is <see cref="WheelAuthorRadius"/>: the
        /// exporter rescales each tyre to 33 mm, so one constant served them
        /// all. The Tiguan is exported 1:1 and is not rescaled at all, so its
        /// author radius is its own — and it is the LOADED centre height
        /// 0.349 m, not the 0.3588 m free radius, because 0.349 is also the
        /// number the design gives the WheelCollider. Defining the constant as
        /// "the radius that means scale 1" keeps those two the same number;
        /// defining it as the free radius would render every Tiguan wheel at
        /// 0.9727 and sit the car 10 mm low on a mesh that needed no scaling.
        ///
        /// Every existing style still divides by the same literal, so this is
        /// bit-identical for the seven arcade cars.
        /// </summary>
        public const float TiguanWheelAuthorRadius = 0.349f;

        // AuthorRadiusFor, IsFullScale and FinishFor were the other three
        // style switches, and K1 had already collapsed "style 13 or 14" out of
        // three copies into one. K3c finishes the job: they are now
        // WheelDef.authorRadius, .fullScale and .finish, read straight off the
        // row. The reasoning each carried lives on those fields.
        //
        // FinishFor is the one worth remembering. It replaced a "style < 6"
        // range test, which would have painted all four Legendary wheels neon
        // pink the moment they shipped — 9-12 are their own authored meshes
        // with their own authored materials and must fall through untouched.

        // Neon rim: hot-pink emissive, the one wheel that glows in the dark maps.
        private static Material _neonRim;
        private static Material NeonRim => Em(ref _neonRim,
            new Color(0.9f, 0.12f, 0.5f), new Color(2.6f, 0.3f, 1.6f));

        /// <summary>
        /// Hide the stock wheel face so a cosmetic rim can take its place —
        /// the pack's "hide Rim_*, RimBarrel_* and RimNut_*" rule, generalised.
        ///
        /// Stated as a KEEP list rather than a hide list, because the two wheel
        /// families name their pieces differently: the legacy tyres carry
        /// rim/hub/stud tokens, while the TinyTorque wheels are separated by
        /// material (chrome/gold/orange/dark) and share no rim token at all.
        /// What both agree on is the tyre and the brake disc, so everything else
        /// inside the wheel mesh goes.
        ///
        /// Scoped to the imported mesh instance so the motor can — a sibling
        /// primitive under the same holder — keeps turning up on powered wheels.
        ///
        /// The primitive fallback needs the other half: its pieces are all called
        /// "Cylinder" (<see cref="Piece"/> never renames), so the name test above
        /// matches nothing and the stock rim discs, studs and hub would stay lit
        /// underneath the cosmetic. They are identified by shared material
        /// instead — the same trick <see cref="ApplyWheelFinish"/> uses — which
        /// leaves the motor can (material <c>Can</c>) alone.
        /// </summary>
        public static void HideStockRim(Transform holder)
        {
            if (holder == null) return;
            for (int i = 0; i < holder.childCount; i++)
            {
                var child = holder.GetChild(i);
                if (child.name.StartsWith("wheel_"))
                {
                    foreach (var r in child.GetComponentsInChildren<Renderer>(true))
                    {
                        string n = r.gameObject.name.ToLowerInvariant();
                        // "whitewall" joined the keep list when the Autopia
                        // shipped: its cream sidewall band is part of the TYRE
                        // (split off only because it is its own material), and
                        // the 3-token list was switching it off with the
                        // hubcap — any cosmetic rim left the car on plain
                        // black tyres with a groove where the wall had been.
                        // CosmeticProbe now hard-FAILs any hidden renderer
                        // whose name reads as tyre-family, so the next
                        // tyre-side material split cannot regress this way.
                        bool keep = n.Contains("tire") || n.Contains("tyre") ||
                                    n.Contains("brake") || n.Contains("whitewall");
                        if (!keep) r.enabled = false;
                    }
                    continue;
                }

                // Primitive fallback: match the rim family by material.
                var pr = child.GetComponent<Renderer>();
                if (pr == null) continue;
                var m = pr.sharedMaterial;
                if (m == Rim || m == Stud) pr.enabled = false;
            }
        }

        /// <summary>
        /// Half the tyre's width along the holder's axle (local X), measured from
        /// the built wheel rather than assumed, so a cosmetic rim can be seated
        /// on the tyre's outer face on any wheel style or size.
        ///
        /// The authored meshes are 27-29 mm wide at the 33 mm author radius and
        /// each style differs, so this reads the instantiated mesh's own bounds.
        /// The primitive fallback has no mesh to read and is built to an exact
        /// proportion (see BuildWheelViz), so that constant is returned instead.
        /// Deliberately ignores everything outside the wheel mesh: the motor can
        /// is a sibling under the same holder and sticks out three times as far.
        /// </summary>
        public static float TyreHalfWidth(Transform holder, float radius)
        {
            const float PrimitiveHalfFrac = 0.4f;   // BuildWheelViz's halfWidth
            if (holder == null) return radius * PrimitiveHalfFrac;

            for (int i = 0; i < holder.childCount; i++)
            {
                var child = holder.GetChild(i);
                if (!child.name.StartsWith("wheel_")) continue;
                var b = LocalRendererBounds(holder, child);
                if (b.size.x > 1e-6f) return Mathf.Max(Mathf.Abs(b.min.x), Mathf.Abs(b.max.x));
            }
            return radius * PrimitiveHalfFrac;
        }

        /// <summary>
        /// A subtree's renderer bounds expressed in <paramref name="frame"/>'s
        /// local space. The eight-corner walk is needed because Renderer.bounds
        /// is a world AABB, not a local one — projecting only its centre and
        /// extents would be wrong the moment the frame is rotated.
        /// </summary>
        public static Bounds LocalRendererBounds(Transform frame, Transform subtree)
        {
            bool any = false;
            var result = new Bounds();
            if (frame == null || subtree == null) return result;

            foreach (var r in subtree.GetComponentsInChildren<Renderer>(true))
            {
                var wb = r.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? wb.min.x : wb.max.x,
                        (c & 2) == 0 ? wb.min.y : wb.max.y,
                        (c & 4) == 0 ? wb.min.z : wb.max.z);
                    var local = frame.InverseTransformPoint(corner);
                    if (!any) { result = new Bounds(local, Vector3.zero); any = true; }
                    else result.Encapsulate(local);
                }
            }
            return result;
        }

        /// <summary>Swap the rim-family materials for a finish (styles 6-8).
        /// Works on both the authored meshes (token-named pieces) and the
        /// primitive fallback (shared Rim/Hub/Stud materials).</summary>
        private static void ApplyWheelFinish(GameObject root, WheelFinish which)
        {
            if (which == WheelFinish.None) return;
            Material finish = which == WheelFinish.Chrome ? Chrome
                            : which == WheelFinish.Gold ? Gold : NeonRim;
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                string n = r.gameObject.name.ToLowerInvariant();
                bool rimFamily = n.Contains("rim") || n.Contains("hub") || n.Contains("stud")
                    || n.Contains("chrome") || n.Contains("gold");
                if (!rimFamily)
                {
                    // Primitive fallback pieces keep primitive names — match by
                    // the shared material instead.
                    var m = r.sharedMaterial;
                    rimFamily = m == Rim || m == Hub || m == Stud;
                }
                if (rimFamily) r.sharedMaterial = finish;
            }
        }

        /// <summary>
        /// Build a stylized wheel inside <paramref name="holder"/>. The holder's
        /// local X is the axle. <paramref name="inboardSign"/> (±1) selects which
        /// axle side the motor can sits on so it faces the vehicle body.
        /// <paramref name="def"/> is the catalogue row: its mesh, its author
        /// radius, its finish. When the mesh is absent the primitive tyre is
        /// built instead and the finish still applies to it.
        /// </summary>
        public static void BuildWheelViz(Transform holder, float radius, bool powered,
            float inboardSign, WheelDef def)
        {
            radius = Mathf.Max(0.01f, radius);

            // Authored-mesh path: instantiate the tyre+rim FBX, scale to the target
            // radius, and let the shared materials drive its look. Axle stays +X so
            // ballooning (holder Y/Z rescale) and WheelCollider spin remain correct.
            def ??= WheelCatalog.Default;
            var mesh = PartMeshLibrary.TryInstantiate(def.meshKey, holder);
            if (mesh != null)
            {
                mesh.transform.localScale =
                    Vector3.one * (radius / WheelCatalog.AuthorRadiusOf(def));

                // The wheel is authored with its rim face toward +X and the brake
                // disc behind it, so on the axle side where +X points inboard it
                // would show the brake disc to the world. Spin it half a turn about
                // the vertical instead of mirroring with a negative scale: the tyre
                // and rim are solids of revolution about the axle, so a 180 degree
                // turn lands the face on -X with normals and winding left intact.
                //
                // COMPOSED, not assigned: TryInstantiate may already have applied
                // a committed asset's authorYawDeg, and overwriting it here would
                // silently un-rotate exactly the wheels that needed it — on one
                // side of the car only, which is the hardest version of that bug
                // to see. Both are rotations about the vertical, so they add.
                if (inboardSign >= 0f)
                    mesh.transform.localRotation =
                        Quaternion.Euler(0f, 180f, 0f) * mesh.transform.localRotation;

                // TinyTorque tokens ahead of the legacy set; all three wheel
                // families bind from this one call. "redtrim", "hwtrim" and
                // "whitewall" MUST come before "rim"/"white", and "hubcap"
                // before "hub" — first-match substring, and "redtrim" really
                // does contain "rim".
                //
                // The Tiguan binds against its own manifest-built table
                // instead: its pieces carry "tig*" names that match nothing in
                // WheelTokens, so it would otherwise take the tyre fallback on
                // every piece and arrive as a solid black wheel.
                bool tiguan = def.fullScale;
                PartMeshLibrary.AssignByName(mesh, tiguan ? null : Tire,
                                             tiguan ? TiguanTokens : WheelTokens);
                if (!tiguan) ApplyWheelFinish(mesh, def.finish);
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

            ApplyWheelFinish(holder.gameObject, def.finish);
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
