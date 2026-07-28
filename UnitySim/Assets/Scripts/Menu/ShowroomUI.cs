using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Persistence;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Menu
{
    /// <summary>
    /// The Showroom's 2D half, drawn by MenuUI on Page.Showroom: vehicle list
    /// on the left (locked cars shown greyed with a padlock — a tease beats a
    /// mystery), stats bars + cosmetic customization on the right, the live
    /// turntable (ShowroomRig) filling the screen between them.
    ///
    /// Customization writes a per-vehicle <see cref="VehicleLoadout"/> into
    /// progress.json and re-previews immediately. Locked entries deny with a
    /// buzz and the standing hint that a magic word might exist.
    /// </summary>
    public sealed class ShowroomUI
    {
        private readonly List<string> _all = new List<string>();
        private int _idx;
        private ShowroomRig _rig;
        private Vector2 _listScroll, _rightScroll;
        private VehicleDesign _preview;   // loadout-applied — the stats source
        private string _note = "";

        // Cosmetics panel: which slot's grid is open, and the locked item being
        // TRIED ON. A try-on is written onto the previewed design only — never
        // into the saved loadout — which is what "viewable but not selectable"
        // means: you get to see the crown on your car, you just don't keep it.
        private CosmeticSlot _slot = CosmeticSlot.Topper;
        private string _tryOn = "";

        private static readonly string[] HornNames =
            { "Standard", "Police siren", "Air horn", "Musical", "Clown" };
        private static readonly string[] WheelNames =
            { "Slick", "Knobby", "Rally", "Coupe", "Baja", "Steelie", "Chrome", "Gold", "Neon" };
        private static readonly string[] TopperNames =
            { "As designed", "Light bar", "Off-road pods", "Flag antenna", "Twin whips", "Whip", "Clean deck" };
        private static readonly string[] AeroNames =
            { "As designed", "Street kit", "Track kit" };

        public string SelectedName =>
            _all.Count > 0 ? _all[Mathf.Clamp(_idx, 0, _all.Count - 1)] : "";

        public void Enter(string currentDisplayName)
        {
            _all.Clear();
            _all.Add("");                                   // stock engineering design
            _all.AddRange(VehiclePresets.DisplayNames());   // ALL presets, locked included
            _all.AddRange(VehicleLibrary.List());
            _idx = Mathf.Max(0, _all.IndexOf(currentDisplayName ?? ""));

            _rig = ShowroomRig.Create();
            Refresh();
        }

        public void Exit()
        {
            _rig?.Dispose();
            _rig = null;
        }

        /// <summary>Per-frame input outside OnGUI: stick spin + rev.</summary>
        public void Tick()
        {
            if (_rig == null) return;
            float spin = InputReader.RightStick().x;
            if (Mathf.Abs(spin) > 0.15f) _rig.Spin(spin * 240f * Time.unscaledDeltaTime * 60f * 0.02f);
            if (InputReader.RightMouseHeld())
                _rig.Spin(-InputReader.MouseDelta().x * 0.35f);
            _rig.Rev(Mathf.Clamp01(InputReader.Throttle()));
        }

        private static VehicleDesign Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return VehicleDesign.Default();
            return VehiclePresets.Resolve(name) ?? VehicleLibrary.Load(name) ?? VehicleDesign.Default();
        }

        private void Refresh()
        {
            string name = SelectedName;
            _preview = Resolve(name);
            Progression.ApplyLoadout(_preview, name);
            ApplyTryOn(_preview);
            _rig?.Show(_preview);
        }

        /// <summary>Fit the locked item being inspected. ApplyLoadout has
        /// already stripped anything unowned, so this is the only way an
        /// unowned cosmetic ever reaches a car — and it reaches exactly one,
        /// the one on the turntable.</summary>
        private void ApplyTryOn(VehicleDesign d)
        {
            if (d == null || string.IsNullOrEmpty(_tryOn)) return;
            var item = CosmeticCatalog.ById(_tryOn);
            if (item == null) return;
            switch (item.slot)
            {
                case CosmeticSlot.Topper: d.cosTopper = item.id; break;
                case CosmeticSlot.Rim: d.cosRim = item.id; break;
                case CosmeticSlot.Ornament: d.cosOrnament = item.id; break;
                case CosmeticSlot.Bobble: d.cosBobble = item.id; break;
                default: d.cosWing = item.id; break;
            }
        }

        private static string EquippedIn(VehicleLoadout l, CosmeticSlot slot) => slot switch
        {
            CosmeticSlot.Topper => l.cosTopper,
            CosmeticSlot.Rim => l.cosRim,
            CosmeticSlot.Ornament => l.cosOrnament,
            CosmeticSlot.Bobble => l.cosBobble,
            _ => l.cosWing,
        };

        private static void EquipIn(VehicleLoadout l, CosmeticSlot slot, string id)
        {
            switch (slot)
            {
                case CosmeticSlot.Topper: l.cosTopper = id; break;
                case CosmeticSlot.Rim: l.cosRim = id; break;
                case CosmeticSlot.Ornament: l.cosOrnament = id; break;
                case CosmeticSlot.Bobble: l.cosBobble = id; break;
                default: l.cosWing = id; break;
            }
        }

        private bool SelectedLocked => Progression.IsCarLocked(SelectedName);

        public const int ResultNone = 0;
        public const int ResultSelected = 1;
        public const int ResultBack = 2;

        /// <summary>Draw. Returns ResultSelected when the player confirmed a
        /// (legal) selection, ResultBack for the on-screen back button.</summary>
        public int Draw()
        {
            int result = ResultNone;

            // ---- left: the car list ----
            float h = UIScale.H - 110f;
            var left = new Rect(10f, 54f, 230f, h);
            GUILayout.BeginArea(left, GUI.skin.box);
            GUILayout.Label("SHOWROOM", GarageSkin.Title);
            _listScroll = GUILayout.BeginScrollView(_listScroll);
            for (int i = 0; i < _all.Count; i++)
            {
                string display = _all[i] == "" ? "Stock Default" : _all[i];
                if (Progression.IsCarLocked(_all[i])) display = "🔒 " + display;
                bool on = i == _idx;
                string label = on ? "▸ " + display : display;
                if (MenuNav.Button(label))
                {
                    if (i != _idx)
                    {
                        _idx = i;
                        _note = "";
                        Refresh();
                    }
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // ---- right: stats + customize ----
            var right = new Rect(UIScale.W - 280f, 54f, 270f, h);
            GUILayout.BeginArea(right, GUI.skin.box);
            _rightScroll = GUILayout.BeginScrollView(_rightScroll);
            DrawStats();
            GUILayout.Space(8);
            DrawCosmetics();
            GUILayout.Space(8);
            DrawCustomize();
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // ---- bottom bar ----
            var bottom = new Rect((UIScale.W - 520f) * 0.5f, UIScale.H - 48f, 520f, 40f);
            GUILayout.BeginArea(bottom, GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (MenuNav.Button("← Back", GUILayout.Width(90f), GUILayout.Height(26f)))
                result = ResultBack;
            if (MenuNav.Button("Honk 🔊", GUILayout.Width(90f), GUILayout.Height(26f)))
                _rig?.Honk();
            GUILayout.Label("Right stick / RMB-drag spins · hold throttle to rev",
                GarageSkin.StatLabel);
            GUILayout.FlexibleSpace();
            bool locked = SelectedLocked;
            GUI.enabled = !locked;
            if (MenuNav.Button("Select ▶", GUILayout.Width(110f), GUILayout.Height(26f)))
                result = ResultSelected;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (locked)
            {
                var hint = new Rect((UIScale.W - 460f) * 0.5f, UIScale.H - 84f, 460f, 24f);
                var st = new GUIStyle(GarageSkin.StatLabel) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(hint, "Locked — win races to unlock it (or know the magic word…)", st);
            }
            if (!string.IsNullOrEmpty(_note))
            {
                var note = new Rect((UIScale.W - 460f) * 0.5f, 58f, 460f, 24f);
                var st = new GUIStyle(GarageSkin.StatLabel) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(note, _note, st);
            }

            return result;
        }

        // ---- stats ----------------------------------------------------------

        private void DrawStats()
        {
            if (_preview == null) return;
            var r = VehicleStats.Compute(_preview);

            GUILayout.Label(SelectedName == "" ? "Stock Default" : SelectedName, GarageSkin.Header);
            GUILayout.Space(2);

            // Mean powered-wheel radius for the thrust estimate.
            float rw = 0.033f;
            int powered = 0;
            if (_preview.wheels != null)
            {
                float sum = 0f;
                foreach (var w in _preview.wheels)
                    if (w.powered) { sum += w.radius; powered++; }
                if (powered > 0) rw = sum / powered;
            }

            float speed = Mathf.Clamp01(r.estTopSpeedMs / 18f);
            float accel = Mathf.Clamp01((r.totalStallTorqueNm / Mathf.Max(0.005f, rw))
                                        / Mathf.Max(0.1f, r.totalMass) / 30f);
            float agility = r.composite
                ? Mathf.Clamp01(0.030f * r.totalMass / Mathf.Max(1e-4f, r.yawInertia))
                : 0.55f;
            float balance = r.composite
                ? Mathf.Clamp01(1f - Mathf.Abs(r.frontWeightPct - 50f) / 35f)
                : 0.5f;
            float response = Mathf.Clamp01((r.rideFreqHz - 1.5f) / 3.5f);
            float steerAuthority = Mathf.Clamp01(r.steered / 2f);
            float handling = Mathf.Clamp01(0.35f * agility + 0.25f * balance +
                                           0.20f * response + 0.20f * steerAuthority);

            Bar("SPEED", speed, $"{r.estTopSpeedMs:0.0} m/s");
            Bar("ACCEL", accel, $"{r.totalStallTorqueNm:0.00} N·m");
            Bar("HANDLING", handling, $"{r.totalMass:0.00} kg");

            GUILayout.Space(2);
            GUILayout.Label($"Special: {SpecialFor(SelectedName)}", GarageSkin.StatLabel);
        }

        /// <summary>Flavor placeholders — per-car arcade abilities are a later
        /// pass; the row exists so the layout is already honest about them.</summary>
        private static string SpecialFor(string name) => name switch
        {
            "★ TT Patrol" => "Siren's Call (coming soon)",
            "★ TT Baja" => "Big Air (coming soon)",
            "★ TT Coupe" => "Slipstream Star (coming soon)",
            "★ Opus Vector" => "Precision Ghost (coming soon)",
            "★ Real Twin 1/10" => "Calibrated (coming soon)",
            _ => "???",
        };

        private static void Bar(string label, float v01, string detail)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70f));
            var rect = GUILayoutUtility.GetRect(120f, 14f, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(rect, GarageSkin.Solid(new Color(0.03f, 0.045f, 0.08f)));
            var fill = new Rect(rect.x + 1f, rect.y + 1f,
                (rect.width - 2f) * Mathf.Clamp01(v01), rect.height - 2f);
            GUI.DrawTexture(fill, GarageSkin.Solid(GarageSkin.Accent));
            for (int i = 1; i < 10; i++)   // notch ticks
            {
                var tick = new Rect(rect.x + rect.width * i / 10f, rect.y, 1f, rect.height);
                GUI.DrawTexture(tick, GarageSkin.Solid(new Color(0f, 0f, 0f, 0.45f)));
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("   " + detail, GarageSkin.StatLabel);
        }

        // ---- customize ------------------------------------------------------

        // ---- cosmetics ------------------------------------------------------

        private static readonly CosmeticSlot[] Slots =
        {
            CosmeticSlot.Topper, CosmeticSlot.Rim, CosmeticSlot.Ornament,
            CosmeticSlot.Bobble, CosmeticSlot.Wing,
        };

        /// <summary>
        /// The unlockable-cosmetics panel: a slot strip, a cycle that a gamepad
        /// can drive, and an icon grid a mouse can. Both write the same one
        /// string, so neither is a second source of truth.
        ///
        /// Locked entries are shown, not hidden. A padlock and a price beat a
        /// blank space: you cannot want the phoenix if you have never seen it.
        /// </summary>
        private void DrawCosmetics()
        {
            GUILayout.Label("COSMETICS", GarageSkin.Header);
            GUILayout.Label($"Scrap: {Progression.Scrap}", GarageSkin.StatLabel);

            var l = Progression.LoadoutFor(SelectedName);
            bool changed = false;

            // Slot strip.
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Slots.Length; i++)
            {
                string tab = (Slots[i] == _slot ? "▸" : "") + CosmeticCatalog.SlotLabel(Slots[i]);
                if (MenuNav.Button(tab, GUILayout.Height(20f)) && Slots[i] != _slot)
                {
                    _slot = Slots[i];
                    _tryOn = "";
                    Refresh();
                }
            }
            GUILayout.EndHorizontal();

            var items = CosmeticCatalog.InSlot(_slot);
            string equipped = EquippedIn(l, _slot);
            string shown = !string.IsNullOrEmpty(_tryOn) &&
                           CosmeticCatalog.ById(_tryOn)?.slot == _slot ? _tryOn : equipped;

            // Cycle over None + every item in the slot — the gamepad path.
            int cur = 0;
            for (int i = 0; i < items.Count; i++)
                if (items[i].id == shown) { cur = i + 1; break; }
            int now = MenuNav.Cycle(CosmeticCatalog.SlotLabel(_slot), cur, items.Count + 1,
                k => k == 0 ? "None" : items[k - 1].label, 70f);
            if (now != cur) changed |= Choose(l, now == 0 ? null : items[now - 1]);

            // Icon grid — the mouse path. Same rule as the paint swatches: raw
            // MouseDown hit-testing, because a 48 px thumbnail is not a button.
            const int cols = 4;
            for (int row = 0; row * cols < items.Count + 1; row++)
            {
                GUILayout.BeginHorizontal();
                for (int c = 0; c < cols; c++)
                {
                    int k = row * cols + c;
                    if (k > items.Count) { GUILayout.FlexibleSpace(); continue; }
                    var cell = GUILayoutUtility.GetRect(52f, 46f);
                    if (k == 0) DrawNoneCell(cell, string.IsNullOrEmpty(shown));
                    else changed |= DrawItemCell(cell, l, items[k - 1], shown);
                    if (k == 0 && Event.current.type == EventType.MouseDown &&
                        cell.Contains(Event.current.mousePosition))
                    {
                        changed |= Choose(l, null);
                        Event.current.Use();
                    }
                }
                GUILayout.EndHorizontal();
            }

            // Detail line for whatever is under the cursor of attention.
            var sel = CosmeticCatalog.ById(shown);
            if (sel != null)
            {
                var rc = CosmeticCatalog.RarityColor(sel.rarity);
                GUILayout.Label($"{sel.label} · {CosmeticCatalog.RarityLabel(sel.rarity)}",
                    new GUIStyle(GarageSkin.StatLabel) { normal = { textColor = rc } });
                GUILayout.Label("   " + sel.description, GarageSkin.StatLabel);
                if (!Progression.IsUnlocked(sel.id))
                {
                    GUILayout.Label("   Locked — trying it on only.", GarageSkin.StatLabel);
                    bool afford = Progression.Scrap >= sel.directCost;
                    GUI.enabled = afford;
                    if (MenuNav.Button($"Buy for {sel.directCost} scrap", GUILayout.Height(22f)))
                    {
                        if (Progression.Buy(sel.id))
                        {
                            _tryOn = "";
                            EquipIn(l, sel.slot, sel.id);
                            changed = true;
                            _note = $"{sel.label} bought.";
                            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiUnlock);
                        }
                    }
                    GUI.enabled = true;
                    if (!afford)
                        GUILayout.Label($"   Need {sel.directCost - Progression.Scrap} more scrap.",
                            GarageSkin.StatLabel);
                }
            }

            if (changed)
            {
                Progression.Save();
                Refresh();
            }
        }

        /// <summary>Pick an item (or None). Owned items are equipped and saved;
        /// locked ones only go on the turntable.</summary>
        private bool Choose(VehicleLoadout l, CosmeticItem item)
        {
            if (item == null)
            {
                _tryOn = "";
                if (string.IsNullOrEmpty(EquippedIn(l, _slot))) { Refresh(); return false; }
                EquipIn(l, _slot, "");
                return true;
            }
            if (Progression.IsUnlocked(item.id))
            {
                _tryOn = "";
                EquipIn(l, item.slot, item.id);
                _note = item.label;
                return true;
            }
            // Locked: fit it, say so, and leave the profile alone.
            _tryOn = item.id;
            _note = $"{item.label} is locked — win crates or buy it.";
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiDeny);
            Refresh();
            return false;
        }

        private static void DrawNoneCell(Rect cell, bool selected)
        {
            GUI.DrawTexture(cell, GarageSkin.Solid(new Color(0.04f, 0.05f, 0.09f)));
            var st = new GUIStyle(GarageSkin.StatLabel) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(cell, "none", st);
            if (selected)
                GUI.Box(new Rect(cell.x - 2f, cell.y - 2f, cell.width + 4f, cell.height + 4f),
                    GUIContent.none, GarageSkin.FocusRing);
        }

        private bool DrawItemCell(Rect cell, VehicleLoadout l, CosmeticItem item, string shown)
        {
            bool owned = Progression.IsUnlocked(item.id);
            var tint = CosmeticCatalog.RarityColor(item.rarity);
            GUI.DrawTexture(cell, GarageSkin.Solid(new Color(tint.r * 0.18f, tint.g * 0.18f,
                                                            tint.b * 0.18f)));
            var icon = CosmeticIcons.Get(item.id);
            if (icon != null)
            {
                var inner = new Rect(cell.x + 3f, cell.y + 2f, cell.width - 6f, cell.height - 12f);
                var prev = GUI.color;
                // Locked items render dim, so the grid reads as a collection
                // with holes in it rather than a shop.
                GUI.color = owned ? Color.white : new Color(0.55f, 0.55f, 0.6f, 0.75f);
                GUI.DrawTexture(inner, icon, ScaleMode.ScaleToFit);
                GUI.color = prev;
            }
            var lbl = new GUIStyle(GarageSkin.StatLabel)
                { alignment = TextAnchor.LowerCenter, fontSize = 9 };
            GUI.Label(cell, owned ? "" : "🔒", lbl);
            if (item.id == shown)
                GUI.Box(new Rect(cell.x - 2f, cell.y - 2f, cell.width + 4f, cell.height + 4f),
                    GUIContent.none, GarageSkin.FocusRing);

            if (Event.current.type == EventType.MouseDown && cell.Contains(Event.current.mousePosition))
            {
                bool changed = Choose(l, item);
                Event.current.Use();
                return changed;
            }
            return false;
        }

        private void DrawCustomize()
        {
            GUILayout.Label("CUSTOMIZE", GarageSkin.Header);
            string name = SelectedName;
            var l = Progression.LoadoutFor(name);
            bool changed = false;

            changed |= LockedCycle("Horn", HornNames,
                v => v < 0 ? "As designed" : HornNames[v],
                l.hornStyle, v => l.hornStyle = v,
                v => v switch { 1 => "horn_siren", 2 => "horn_truck", 3 => "horn_musical", 4 => "horn_clown", _ => null });

            changed |= LockedCycle("Wheels", WheelNames,
                v => v < 0 ? "As designed" : WheelNames[v],
                l.wheelStyle, v => l.wheelStyle = v,
                v => v >= 6 ? $"wheel_style_{v}" : null);

            // "Roof kit", not "Topper": the cosmetics panel above owns that word
            // now, and this row swaps the light bar and antennas rather than
            // bolting a crown on the roof.
            changed |= LockedSlotCycle("Roof kit", TopperNames, l.topper, v => l.topper = v,
                v => v switch { 1 => "light_style_0", 2 => "light_style_1",
                                3 => "antenna_style_2", 4 => "antenna_style_3", _ => null });

            changed |= LockedSlotCycle("Aero", AeroNames, l.aeroKit, v => l.aeroKit = v,
                v => v == 1 ? "aero_kit_street" : v == 2 ? "aero_kit_track" : null);
            if (l.aeroKit > 0)
                GUILayout.Label("   Aero kits change mass + downforce —\n   the bars above move for real.",
                    GarageSkin.StatLabel);

            // Paint swatch row(s).
            GUILayout.Label("Paint", GUILayout.Width(70f));
            var palette = Progression.PaintPalette;
            for (int row = 0; row < palette.Length; row += 5)
            {
                GUILayout.BeginHorizontal();
                for (int i = row; i < Mathf.Min(row + 5, palette.Length); i++)
                {
                    var (color, pname, lockId) = palette[i];
                    bool locked = lockId != null && !Progression.IsUnlocked(lockId);
                    var swatch = GUILayoutUtility.GetRect(30f, 22f);
                    GUI.DrawTexture(swatch, GarageSkin.Solid(color));
                    if (l.paintIdx == i)
                        GUI.Box(new Rect(swatch.x - 2f, swatch.y - 2f, swatch.width + 4f, swatch.height + 4f),
                            GUIContent.none, GarageSkin.FocusRing);
                    if (locked)
                        GUI.Label(swatch, "🔒", new GUIStyle(GUI.skin.label)
                            { alignment = TextAnchor.MiddleCenter });
                    if (Event.current.type == EventType.MouseDown &&
                        swatch.Contains(Event.current.mousePosition))
                    {
                        if (locked) Deny(pname);
                        else { l.paintIdx = i; changed = true; _note = pname; }
                        Event.current.Use();
                    }
                }
                GUILayout.EndHorizontal();
            }
            if (MenuNav.Button("Paint: as designed", GUILayout.Height(22f)) && l.paintIdx != -1)
            {
                l.paintIdx = -1;
                changed = true;
            }

            if (changed)
            {
                Progression.Save();
                Refresh();
            }
        }

        /// <summary>A cycle over values −1 (as designed) + 0..N−1, skipping
        /// nothing but DENYING locked entries: the pick snaps back and buzzes,
        /// so the player sees what exists and learns it is winnable.</summary>
        private bool LockedCycle(string label, string[] names,
            System.Func<int, string> display, int value, System.Action<int> set,
            System.Func<int, string> lockIdOf)
        {
            int shown = Mathf.Clamp(value, -1, names.Length - 1);
            // Map −1..N−1 onto 0..N for the cycle: 0 = as designed.
            int cur = shown + 1;
            int now = MenuNav.Cycle(label, cur, names.Length + 1,
                k => k == 0 ? "As designed" : names[k - 1], 70f);
            if (now == cur) return false;
            int chosen = now - 1;
            string lockId = chosen >= 0 ? lockIdOf(chosen) : null;
            if (lockId != null && !Progression.IsUnlocked(lockId))
            {
                Deny(chosen >= 0 && chosen < names.Length ? names[chosen] : label);
                return false;
            }
            set(chosen);
            return true;
        }

        /// <summary>Same, for the slot-style values (0 = as designed, no −1).</summary>
        private bool LockedSlotCycle(string label, string[] names, int value,
            System.Action<int> set, System.Func<int, string> lockIdOf)
        {
            int cur = Mathf.Clamp(value, 0, names.Length - 1);
            int now = MenuNav.Cycle(label, cur, names.Length, k => names[k], 70f);
            if (now == cur) return false;
            string lockId = lockIdOf(now);
            if (lockId != null && !Progression.IsUnlocked(lockId))
            {
                Deny(names[now]);
                return false;
            }
            set(now);
            return true;
        }

        private void Deny(string what)
        {
            _note = $"{what} is locked — win races (or find the magic word).";
            Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiDeny);
        }
    }
}
