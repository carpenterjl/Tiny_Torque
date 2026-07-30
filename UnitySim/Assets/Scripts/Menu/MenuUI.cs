using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Net;
using AIHWSim.Persistence;
using AIHWSim.TrackEd;
using AIHWSim.UI;
using UnityEngine;

namespace AIHWSim.Menu
{
    /// <summary>
    /// IMGUI main menu (dark GarageSkin): Single Player (vehicle/track pickers,
    /// Garage, Track Builder), Multiplayer (split-screen setup — enabled in a
    /// later step), Resume Drive (session snapshots — later step), Options
    /// (applied + saved live), Quit.
    /// </summary>
    public sealed class MenuUI : MonoBehaviour
    {
        private enum Page { Root, SinglePlayer, Multiplayer, Championship, Options, Resume, LanHost, LanJoin, Showroom, Crates, Shop, Cheats }

        private Page _page = Page.Root;
        // What this frame DRAWS. Snapshotted from _page on Layout passes only,
        // so a handler that switches pages mid-pass (pad activation happens on
        // a Layout pass — see MenuNav) never desyncs the paired Repaint: the
        // old page draws once more, the new page owns the next frame.
        private Page _pageDraw = Page.Root;
        private string _status = "";

        // Picker state (indices into the option lists; 0 = stock/oval).
        private int _vehicleIdx;
        private int _trackIdx;
        private List<string> _vehicles = new List<string>();
        private List<string> _tracks = new List<string>();

        // Multiplayer setup state. Device choice: 0 = Keyboard, 1+g = Gamepad g.
        private int _mpVeh1, _mpVeh2;
        private int _mpTrackIdx;
        private int _mpDev1 = 0, _mpDev2 = 1;
        private int _mpLaps = 3;
        private int _spLaps;

        // Single-player race setup.
        private int _spBots;        // AI opponents (0..7)
        private int _spDiff = 1;    // 0 Easy / 1 Medium / 2 Hard
        private int _spControl;     // 0 Manual / 1 Autonomous (C firmware) / 2 Autonomous (bot AI)
        private bool _spRubber;
        private int _spCountdown = 3; // race-start countdown seconds (0..60)
        private bool _spArcade;       // power-ups, weapons, arcade board
        private bool _spTrackLimits = true;
        private bool _spArcadeHandling = true;   // false = race the circuits on raw sim physics
        private static readonly List<string> DiffNames = new List<string> { "Easy", "Medium", "Hard" };
        private static readonly List<string> ControlNames =
            new List<string> { "Manual", "Autonomous (firmware)", "Autonomous (bot AI)" };

        // LAN pages state.
        private string _joinIp = "127.0.0.1";
        private bool _connecting;
        private float _connectDeadline;
        private readonly List<LanDiscovery.DiscoveredGame> _discovered =
            new List<LanDiscovery.DiscoveredGame>();
        private Vector2 _lanScroll;

        // Title art (Resources/UI) — the Root page's backdrop. After IdleSeconds
        // without input on Root the panel and backdrop hide so the live attract
        // loop plays full-screen, arcade style; any input brings the menu back.
        private Texture2D _titleTex;
        private const float IdleSeconds = 20f;
        private float _lastInputTime;
        private bool _attractHidden;

        // Showroom state: the 3D turntable + its panels, plus where Select/Back
        // should land (SP page vs the LAN pages).
        private ShowroomUI _showroom;
        private Page _showroomReturn = Page.SinglePlayer;

        // Crate room: the same shape as the Showroom — a full-screen 3D page
        // with its own rig, entered from the root menu and from the Showroom.
        private CrateOpenUI _crates;
        private Page _cratesReturn = Page.Root;

        // Cheats page state.
        private string _cheatEntry = "";
        private string _cheatStatus = "";
        private float _cheatShake;

        private void Start()
        {
            RefreshLists();
            _titleTex = Resources.Load<Texture2D>("UI/TinyTorque_Title");
            _lastInputTime = Time.unscaledTime;

            if (!string.IsNullOrEmpty(NetSession.LastDisconnectReason))
            {
                _status = NetSession.LastDisconnectReason;
                NetSession.LastDisconnectReason = "";
            }

            // Restore last-used picks.
            var s = SettingsStore.Current;
            _vehicleIdx = Mathf.Max(0, _vehicles.IndexOf(s.lastVehicle));
            _trackIdx = Mathf.Max(0, _tracks.IndexOf(s.lastTrack));
            _spLaps = Mathf.Clamp(s.lastLaps, 0, 50);
            _spBots = Mathf.Clamp(s.spBots, 0, 7);
            _spDiff = Mathf.Clamp(s.spDifficulty, 0, 2);
            _spControl = Mathf.Clamp(s.spControl, 0, 2);
            _spRubber = s.spRubberBand;
            _spCountdown = Mathf.Clamp(s.spCountdown, 0, 60);
            _spArcade = s.spArcade;
            _spArcadeHandling = s.spArcadeHandling;
            _spTrackLimits = s.spTrackLimits;
        }

        private void RefreshLists()
        {
            // Progression gating happens HERE and only here — the picker layer.
            // VehiclePresets.Resolve still resolves every name (bots, LAN peers
            // and the headless regression stay progression-blind); a locked car
            // simply isn't offered. If the saved pick is now locked (fresh
            // progress file), the clamp below lands on a legal entry.
            string prevPick = _vehicles.Count > 0
                ? _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)] : null;

            _vehicles = new List<string> { "" };  // "" = stock default
            foreach (var name in VehiclePresets.DisplayNames())
                if (!Persistence.Progression.IsCarLocked(name)) _vehicles.Add(name);
            _vehicles.AddRange(VehicleLibrary.List());   // user saves: never gated
            _tracks = new List<string> { "" };    // "" = classic oval
            _tracks.AddRange(TrackPresets.DisplayNames());
            // ▣ hand-authored scene tracks, between the ★ presets and user saves —
            // they are shipped content like a preset, but resolve down a different
            // path and cannot be opened in the Track Builder.
            _tracks.AddRange(AIHWSim.Track.SceneTrackCatalog.DisplayNames());
            _tracks.AddRange(TrackLibrary.List());

            if (prevPick != null)
            {
                int found = _vehicles.IndexOf(prevPick);
                if (found >= 0) _vehicleIdx = found;
            }
            _vehicleIdx = Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1);
            _trackIdx = Mathf.Clamp(_trackIdx, 0, _tracks.Count - 1);
        }

        /// <summary>Resolve a picker vehicle name: "" = stock, ★-preset, or a
        /// save — then overlay the Showroom loadout. This is the UI layer's
        /// resolver, which is exactly where the loadout is allowed to apply;
        /// VehiclePresets.Resolve and VehicleLibrary.Load themselves stay
        /// byte-identical for bots, LAN internals and the headless regression.</summary>
        private static VehicleDesign ResolveVehicle(string name)
        {
            VehicleDesign d = string.IsNullOrEmpty(name)
                ? null                                        // stock Default()
                : VehiclePresets.Resolve(name) ?? VehicleLibrary.Load(name);

            var l = Persistence.Progression.LoadoutFor(name ?? "");
            bool touched = l.hornStyle >= 0 || l.wheelStyle >= 0 || l.paintIdx >= 0
                || l.topper != 0 || l.aeroKit != 0
                // A loadout that ONLY carries cosmetics is still a loadout: leave
                // these out and a car wearing nothing but a crown races bare.
                || !string.IsNullOrEmpty(l.cosTopper) || !string.IsNullOrEmpty(l.cosRim)
                || !string.IsNullOrEmpty(l.cosOrnament) || !string.IsNullOrEmpty(l.cosBobble)
                || !string.IsNullOrEmpty(l.cosWing);
            if (touched)
            {
                d ??= VehicleDesign.Default();   // stock with a loadout must materialize
                Persistence.Progression.ApplyLoadout(d, name ?? "");
            }
            return d;
        }

        /// <summary>Resolve a picker track name: "" = classic oval, ★-preset, or a save.
        /// Returns null for a ▣ scene track too — those are not TrackDesign data at
        /// all, so every caller must go through <see cref="SelectTrack"/> rather than
        /// assigning the result of this straight onto GameFlow.</summary>
        private static TrackDesign ResolveTrack(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;             // classic oval
            var preset = TrackPresets.Resolve(name);
            return preset ?? TrackLibrary.Load(name);
        }

        /// <summary>
        /// Point GameFlow at whichever of the three track sources a picker name
        /// means. The single funnel for track selection: the two GameFlow
        /// properties are mutually exclusive, and choosing between them in four
        /// places is how they would eventually disagree.
        /// </summary>
        private static void SelectTrack(string name)
        {
            string scene = AIHWSim.Track.SceneTrackCatalog.Resolve(name);
            if (scene != null) { GameFlow.ActiveSceneTrack = scene; return; }
            GameFlow.ActiveTrack = ResolveTrack(name);
        }

        private void Update()
        {
            // Pad B backs out one page. Consumed in Update — state changes here
            // are always pass-safe (MenuNav's rule).
            if (MenuNav.ConsumeBack() && _page != Page.Root)
                GoBack();

            // Idle → full-screen attract, arcade style. Any activity restores.
            bool activity = InputReader.AnyPadActivity()
                || KeyTable.CaptureThisFrame() != KeyCode.None
                || InputReader.LeftMousePressed()
                || InputReader.MouseDelta().sqrMagnitude > 4f;
            if (activity)
            {
                _lastInputTime = Time.unscaledTime;
                _attractHidden = false;
            }
            else if (_page == Page.Root && !_attractHidden
                     && Time.unscaledTime - _lastInputTime > IdleSeconds)
            {
                _attractHidden = true;
            }

            if (_page == Page.Showroom) _showroom?.Tick();
            if (_page == Page.Crates) _crates?.Tick();
            _cheatShake = Mathf.Max(0f, _cheatShake - Time.unscaledDeltaTime * 1.6f);
        }

        /// <summary>One step out of the current page (pad B / ← Back).</summary>
        private void GoBack()
        {
            _status = "";
            switch (_page)
            {
                case Page.LanHost:
                    SettingsStore.Save();
                    GoTo(Page.Multiplayer);
                    break;
                case Page.LanJoin:
                    if (_connecting) { _connecting = false; NetSession.Instance?.Leave(); }
                    LanDiscovery.StopListen();
                    SettingsStore.Save();
                    GoTo(Page.Multiplayer);
                    break;
                case Page.Championship:
                    GoTo(Page.Root);
                    break;
                case Page.Crates:
                    CloseCrates();
                    break;
                case Page.Shop:
                    GoTo(Page.Root);
                    break;
                case Page.Showroom:
                    CloseShowroom(applySelection: false);
                    break;
                case Page.Cheats:
                    GoTo(Page.Options);
                    break;
                default:
                    GoTo(Page.Root);
                    break;
            }
        }

        private void OnGUI()
        {
            GUI.skin = GarageSkin.Skin;
            UIScale.Begin();
            if (Event.current.type == EventType.Layout)
            {
                _pageDraw = _page;
                _attractDraw = _attractHidden;
            }

            // Idle attract: the live loop plays full-screen with just a small
            // "press any button" bug — no panel, no nav.
            if (_attractDraw)
            {
                var bug = new GUIStyle(GarageSkin.Header) { alignment = TextAnchor.MiddleRight };
                GUI.Label(new Rect(0f, UIScale.H - 34f, UIScale.W - 16f, 24f),
                    "TINYTORQUE RC — press any button", bug);
                UIScale.End();
                return;
            }

            MenuNav.BeginFrame("menu:" + _pageDraw);

            // The Showroom owns the whole screen (its 3D camera fills it; its
            // panels sit at the edges) — no centered box, no backdrop.
            if (_pageDraw == Page.Showroom)
            {
                DrawShowroomPage();
                MenuNav.EndFrame();
                UIScale.End();
                return;
            }
            if (_pageDraw == Page.Crates)
            {
                DrawCratesPage();
                MenuNav.EndFrame();
                UIScale.End();
                return;
            }

            bool root = _pageDraw == Page.Root;

            // The Root page IS the title screen: the showroom key art fills the
            // frame and the menu sits low so the gold logo stays clear.
            if (root && _titleTex != null)
                GUI.DrawTexture(Cover(_titleTex.width, _titleTex.height), _titleTex, ScaleMode.StretchToFill);

            float w = 380f, h = _pageDraw == Page.Options
                ? Mathf.Min(680f, UIScale.H - 20f)
                : _pageDraw == Page.SinglePlayer ? Mathf.Min(560f, UIScale.H - 20f)
                : _pageDraw == Page.Multiplayer ? Mathf.Min(560f, UIScale.H - 20f)
                // The shop scrolls ten offers and the championship lists four
                // rounds plus a points table; both drown in the default 430.
                : _pageDraw == Page.Shop ? Mathf.Min(620f, UIScale.H - 20f)
                : _pageDraw == Page.Championship ? Mathf.Min(600f, UIScale.H - 20f) : 430f;
            Rect area;
            if (root && _titleTex != null)
            {
                float y = UIScale.H * 0.42f;
                h = Mathf.Min(h, UIScale.H - y - 12f);
                area = new Rect((UIScale.W - w) * 0.5f, y, w, h);
            }
            else
            {
                area = new Rect((UIScale.W - w) * 0.5f, (UIScale.H - h) * 0.5f, w, h);
            }
            // Wrong-cheat shake: BeginArea's rect is read per pass, and layout
            // inside it is area-relative — offsetting only moves the panel.
            if (_pageDraw == Page.Cheats && _cheatShake > 0f)
                area.x += Mathf.Sin(Time.unscaledTime * 55f) * 9f * _cheatShake;
            GUILayout.BeginArea(area, GUI.skin.box);

            // The key art already carries the logo on Root; every other page
            // gets the text wordmark.
            if (!(root && _titleTex != null))
            {
                GUILayout.Label("TINYTORQUE", GarageSkin.Title);
                var sub = new GUIStyle(GarageSkin.StatLabel)
                    { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                GUILayout.Label("—  R C   S E R I E S  —", sub);
            }
            GUILayout.Space(10);

            switch (_pageDraw)
            {
                case Page.Root: DrawRoot(); break;
                case Page.SinglePlayer: DrawSinglePlayer(); break;
                case Page.Multiplayer: DrawMultiplayer(); break;
                case Page.Championship: DrawChampionship(); break;
                case Page.Shop: DrawShop(); break;
                case Page.Options: DrawOptions(); break;
                case Page.Resume: DrawResume(); break;
                case Page.LanHost: DrawLanHost(); break;
                case Page.LanJoin: DrawLanJoin(); break;
                case Page.Cheats: DrawCheats(); break;
            }

            GUILayout.FlexibleSpace();
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status, GarageSkin.StatLabel);
            GUILayout.EndArea();
            MenuNav.EndFrame();
            UIScale.End();
        }

        // Layout-snapshotted twin of _attractHidden (same rule as _pageDraw:
        // whether the panel exists must not change between Layout and Repaint).
        private bool _attractDraw;

        /// <summary>Smallest rect of the given aspect covering the screen, in UI units.</summary>
        private static Rect Cover(float tw, float th)
        {
            float s = Mathf.Max(UIScale.W / tw, UIScale.H / th);
            float cw = tw * s, ch = th * s;
            return new Rect((UIScale.W - cw) * 0.5f, (UIScale.H - ch) * 0.5f, cw, ch);
        }

        // ---- showroom --------------------------------------------------------

        private void OpenShowroom(Page returnTo)
        {
            _showroomReturn = returnTo;
            string current = _vehicles.Count > 0
                ? _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)] : "";
            GoTo(Page.Showroom, () =>
            {
                _showroom = new ShowroomUI();
                _showroom.Enter(current);
            });
        }

        private void CloseShowroom(bool applySelection)
        {
            if (_showroom != null)
            {
                if (applySelection)
                {
                    int found = _vehicles.IndexOf(_showroom.SelectedName);
                    if (found >= 0) _vehicleIdx = found;
                }
                _showroom.Exit();
                _showroom = null;
            }
            GoTo(_showroomReturn);
        }

        // ---- shop ------------------------------------------------------------

        private Vector2 _shopScroll;

        private void DrawShop()
        {
            GUILayout.Label("SCRAP SHOP", GarageSkin.Header);
            GUILayout.Label($"Scrap: {Persistence.Progression.Scrap}   ·   " +
                            $"stock rotates in {ShopStock.TimeToRotation()}", GarageSkin.StatLabel);
            GUILayout.Space(4);

            _shopScroll = GUILayout.BeginScrollView(_shopScroll);

            GUILayout.Label("TODAY'S ITEMS", GarageSkin.Header);
            foreach (var item in ShopStock.Offers())
            {
                bool owned = Persistence.Progression.IsUnlocked(item.id);
                int price = UnlockCatalog.DirectCost(item);
                var col = CosmeticCatalog.RarityColor(item.rarity);
                GUILayout.Label($"{item.display} · {CosmeticCatalog.RarityLabel(item.rarity)}",
                    new GUIStyle(GarageSkin.StatLabel) { normal = { textColor = col } });
                GUI.enabled = !owned && Persistence.Progression.Scrap >= price;
                if (MenuNav.Button(owned ? "   Owned" : $"   Buy — {price} scrap",
                        GUILayout.Height(22f)) && Persistence.Progression.Buy(item.id))
                    _status = $"{item.display} bought.";
                GUI.enabled = true;
            }

            GUILayout.Space(8);
            GUILayout.Label("CRATES", GarageSkin.Header);
            foreach (var def in CosmeticCatalog.Crates)
            {
                int price = ShopStock.CratePrice(def.id);
                GUILayout.Label($"{def.label} — {def.pulls} pull{(def.pulls == 1 ? "" : "s")}" +
                                $"   (you hold {Persistence.Progression.CrateCount(def.id)})",
                                GarageSkin.StatLabel);
                GUI.enabled = Persistence.Progression.Scrap >= price;
                if (MenuNav.Button($"   Buy — {price} scrap", GUILayout.Height(22f)) &&
                    ShopStock.BuyCrate(def.id))
                    _status = $"{def.label} added to your crates.";
                GUI.enabled = true;
            }

            GUILayout.EndScrollView();

            GUILayout.Space(6);
            if (MenuButton("Crates ▶")) OpenCrates(Page.Shop);
            if (MenuButton("← Back")) GoTo(Page.Root);
        }

        // ---- crate room ------------------------------------------------------

        private void OpenCrates(Page returnTo)
        {
            _cratesReturn = returnTo;
            GoTo(Page.Crates, () =>
            {
                _crates = new CrateOpenUI();
                _crates.Enter();
            });
        }

        private void CloseCrates()
        {
            _crates?.Exit();
            _crates = null;
            GoTo(_cratesReturn);
        }

        private void DrawCratesPage()
        {
            // Null during the exit dip — same rule as the Showroom: Layout
            // registered the panels, this Repaint draws none, and the fade
            // covers the one dark frame.
            if (_crates == null) return;
            if (_crates.Draw() == CrateOpenUI.ResultBack) CloseCrates();
        }

        private void DrawShowroomPage()
        {
            // Null during the exit dip: Layout registered the panels, this
            // Repaint draws none — requesting FEWER cached entries is legal,
            // and the fade hides the one dark frame.
            if (_showroom == null) return;
            switch (_showroom.Draw())
            {
                case ShowroomUI.ResultSelected: CloseShowroom(applySelection: true); break;
                case ShowroomUI.ResultBack: CloseShowroom(applySelection: false); break;
            }
        }

        // ---- cheats ----------------------------------------------------------

        private void DrawCheats()
        {
            GUILayout.Label("CHEAT CODES", GarageSkin.Header);
            GUILayout.Space(6);
            GUILayout.Label("Heard a magic word? Type it here.", GarageSkin.StatLabel);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            _cheatEntry = GUILayout.TextField(_cheatEntry, GUILayout.Width(190));
            bool submit = MenuNav.Button("Redeem", GUILayout.Width(90));
            GUILayout.EndHorizontal();
            if (Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                submit = true;
                Event.current.Use();
            }

            if (submit && !string.IsNullOrWhiteSpace(_cheatEntry))
            {
                var item = Persistence.Progression.Redeem(_cheatEntry, out bool alreadyHad);
                if (item == null)
                {
                    _cheatStatus = "…nothing happened.";
                    _cheatShake = 1f;
                    Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiDeny);
                }
                else if (alreadyHad)
                {
                    _cheatStatus = $"{item.display} — already unlocked.";
                    Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiBack);
                }
                else
                {
                    _cheatStatus = $"UNLOCKED: {item.display}!\n{item.blurb}";
                    Audio.SfxPlayer.Ensure()?.PlayUi(Audio.ProceduralAudio.UiUnlock);
                    RefreshLists();   // an unlocked car appears in the pickers now
                }
                _cheatEntry = "";
            }

            GUILayout.Space(6);
            if (!string.IsNullOrEmpty(_cheatStatus))
                GUILayout.Label(_cheatStatus, GarageSkin.Header);

            var p = Persistence.Progression.Current;
            GUILayout.Space(6);
            GUILayout.Label($"Unlocked {p.unlocked.Count}/{Persistence.UnlockCatalog.All.Length} " +
                            $"· Level {p.level} · {p.wins} wins", GarageSkin.StatLabel);

            GUILayout.Space(8);
            if (MenuButton("← Back")) GoTo(Page.Options);
        }

        // ---- pages -----------------------------------------------------------

        private void DrawRoot()
        {
            if (MenuButton("Single Player")) GoTo(Page.SinglePlayer, RefreshLists);
            if (MenuButton("Multiplayer")) GoTo(Page.Multiplayer, RefreshLists);
            if (MenuButton(Championship.Active ? "Championship (in progress)" : "Championship"))
                GoTo(Page.Championship, RefreshLists);

            int held = Persistence.Progression.Current.crates.Count;
            if (MenuButton(held > 0 ? $"Crates ({held}) ▶" : "Crates"))
                OpenCrates(Page.Root);
            if (MenuButton($"Shop — {Persistence.Progression.Scrap} scrap")) GoTo(Page.Shop);

            GUI.enabled = SaveSystem.ListSnapshots().Count > 0;
            if (MenuButton("Resume Drive")) GoTo(Page.Resume, RefreshSnapshots);
            GUI.enabled = true;

            if (MenuButton("Options")) GoTo(Page.Options);
            if (MenuButton("Quit")) Quit();
        }

        /// <summary>Mode picker labels, in MatchMode order.</summary>
        private static readonly string[] ModeNames =
            { "Race", "Demolition", "Capture the Flag", "Soccer", "Free Roam" };

        /// <summary>The arena each mode is built for. Picking a mode moves the
        /// track selection there, because a derby on a race circuit has no spawn
        /// ring and quietly falls back to a free drive. Free Roam is blank: its
        /// map is not in the picker at all — it cannot be raced on, so it is not
        /// offered as somewhere to race.</summary>
        private static readonly string[] ModeArena =
            { "", "Scrapyard Bowl", "Cargo Yard", "Torque Dome", "" };

        private int _spMode;         // SessionConfig.MatchMode
        private int _spModeDraw;     // Layout snapshot — see DrawSinglePlayer
        private int _spScore = 3;    // captures / goals to win

        /// <summary>Point the track picker at this mode's arena.</summary>
        private void SelectArenaFor(int mode)
        {
            string want = ModeArena[Mathf.Clamp(mode, 0, ModeArena.Length - 1)];
            if (string.IsNullOrEmpty(want)) return;
            int found = _tracks.IndexOf(TrackPresets.Prefix + want);
            if (found < 0) found = _tracks.IndexOf(want);
            if (found >= 0) _trackIdx = found;
        }

        /// <summary>Write the chosen mode's rules into SessionConfig, and split
        /// the roster into two sides when the mode has teams. Called by every
        /// start path, so a mode never half-starts.</summary>
        private void ApplyMatchRules()
        {
            var mode = (MatchMode)Mathf.Clamp(_spMode, 0, (int)MatchMode.FreeRoam);
            SessionConfig.Match = mode;
            SessionConfig.TargetScore = Mathf.Max(1, _spScore);
            if (mode == MatchMode.Race) return;

            // Neither an arena match nor a free roam has laps; leaving them set
            // would compose a RaceDirector's countdown against a mode that
            // never counts one.
            SessionConfig.TargetLaps = 0;
            if (!SessionConfig.IsTeamMatch)
            {
                foreach (var slot in SessionConfig.Players) slot.team = -1;
                return;
            }
            // Alternate sides down the roster: with one human and three bots
            // that is 1v1 plus a bot each, which is the fairest split available
            // without asking the player to assign teams by hand.
            for (int i = 0; i < SessionConfig.Players.Count; i++)
                SessionConfig.Players[i].team = i % 2;
        }

        private void DrawSinglePlayer()
        {
            GUILayout.Label("SINGLE PLAYER", GarageSkin.Header);
            GUILayout.Space(6);

            int wasMode = _spMode;
            _spMode = MenuNav.Cycle("Mode", Mathf.Clamp(_spMode, 0, ModeNames.Length - 1),
                ModeNames.Length, k => ModeNames[k], 60f);
            if (_spMode != wasMode) SelectArenaFor(_spMode);
            // Which controls exist below depends on the mode, so the mode has to
            // be snapshotted on Layout: a click that changes it mid-pass would
            // otherwise offer Repaint a different set of controls than Layout
            // registered, which is the IMGUI error this whole UI avoids.
            if (Event.current.type == EventType.Layout) _spModeDraw = _spMode;
            bool roam = _spModeDraw == (int)MatchMode.FreeRoam;
            bool arena = !roam && _spModeDraw != (int)MatchMode.Race;

            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            if (MenuNav.Button("Showroom — preview & customize ▶"))
                OpenShowroom(Page.SinglePlayer);
            if (roam)
            {
                // No track picker at all. The town is the only free-roam map and
                // it is the only map free roam runs on — offering a choice of
                // one, on a list that deliberately excludes it, would be a
                // control that does nothing.
                GUILayout.Label($"   Map: ★ {TrackPresets.FreeRoamName} — a town to drive around.\n" +
                                "   No laps, no clock, no opponents. R puts you back on a street.",
                                GarageSkin.StatLabel);
            }
            else
            {
                _trackIdx = CyclePicker("Track", _tracks, _trackIdx, t => t == "" ? "Classic Oval" : t);
                if (arena)
                    GUILayout.Label($"   {ModeNames[_spModeDraw]} needs an arena — " +
                                    $"★ {ModeArena[_spModeDraw]} is the one built for it.",
                                    GarageSkin.StatLabel);
            }

            // AI opponents. Not in free roam: with no racing line and no arena
            // policy to hunt anything, a bot dropped into the town would sit at
            // its spawn — an opponent that does nothing is worse than none.
            if (!roam)
            {
                _spBots = MenuNav.Stepper("Opponents", _spBots, 0, 7,
                    v => v == 0 ? "None" : $"{v} bots");

                if (_spBots > 0)
                {
                    _spDiff = CyclePicker("Difficulty", DiffNames, _spDiff, x => x);
                    _spRubber = MenuNav.Toggle(_spRubber, " Rubber-band (keep the pack close)");
                }
            }

            // How the player's own car is driven.
            _spControl = CyclePicker("Driving", ControlNames, _spControl, x => x);

            // Race distance, or the score that ends an arena match.
            if (arena)
            {
                if (_spModeDraw != (int)MatchMode.Derby)
                    _spScore = MenuNav.Stepper("Score to win", _spScore, 1, 15,
                        v => _spModeDraw == (int)MatchMode.Soccer ? $"{v} goals" : $"{v} captures");
                else
                    GUILayout.Label("   Last car still running wins.", GarageSkin.StatLabel);
            }
            else if (!roam)
            {
                _spLaps = MenuNav.Stepper("Race laps", _spLaps, 0, 50,
                    v => v == 0 ? "Free drive" : $"{v} laps");
            }

            if (!roam && (_spBots > 0 || _spLaps > 0 || arena))
                _spCountdown = MenuNav.Stepper("Countdown", _spCountdown, 0, 60,
                    v => v == 0 ? "None" : $"{v} s");

            // Arcade is always offered — hiding it behind "set some laps first"
            // made the whole mode undiscoverable. It still REQUIRES a lap count
            // (item boxes and race positions both need a finish line), so ticking
            // it with a free-drive selected sets a race up rather than silently
            // doing nothing. It must never run on a firmware session: a boost or
            // a spin-out would corrupt the controller-validation run.
            bool firmware = _spControl == 1;
            // Arcade items belong to a race: they need a finish line for their
            // boxes and their positions. The arena modes bring their own, and
            // free roam has no finish line to hang either from.
            GUI.enabled = !firmware && !arena && !roam;
            if (arena || roam) _spArcade = false;
            bool wantArcade = MenuNav.Toggle(_spArcade && !firmware, " Arcade mode (power-ups & weapons)");
            if (wantArcade && !_spArcade && _spLaps == 0) _spLaps = 3;   // arcade needs a race
            _spArcade = wantArcade && !firmware;
            if (_spArcade)
            {
                _spTrackLimits = MenuNav.Toggle(_spTrackLimits, "    Track limits (off-track penalty)");
                GUILayout.Label("    Item boxes on track · use with Left Shift / gamepad X.\n" +
                                "    Built for the ★ themed circuits; works on any map with a finish line.",
                                GarageSkin.StatLabel);
            }
            GUI.enabled = true;
            if (firmware) GUILayout.Label("Arcade is off in firmware sessions.", GarageSkin.StatLabel);

            // The handling mode is its own row, OUTSIDE the arcade-items nest:
            // free roam and the arena modes have no item boxes but very much
            // have physics, and burying the physics choice under a weapons
            // toggle made it unreachable exactly where the car felt worst.
            // Firmware sessions face raw physics regardless (no HandlingFloor),
            // so the control is disabled rather than lying.
            GUI.enabled = !firmware;
            _spArcadeHandling = MenuNav.Toggle(_spArcadeHandling,
                " Arcade handling (extra grip + driving assists)");
            GUILayout.Label(_spArcadeHandling
                    ? "    ARCADE — everyone, bots included, gets grip, stability and assists."
                    : "    SIM — raw brush-tyre physics, no assist floor. The circuits bite.",
                GarageSkin.StatLabel);
            GUI.enabled = true;
            GUILayout.Space(10);

            string go = (arena || roam) ? $"{ModeNames[_spModeDraw]} ▶"
                      : (_spBots > 0 || _spLaps > 0) ? "Race ▶" : "Drive ▶";
            if (MenuButton(go)) StartSinglePlayer();
            if (MenuButton("Garage")) LoadIfBuilt(GameFlow.GarageSceneName, GameFlow.LoadGarage);
            if (MenuButton("Track Builder")) LoadIfBuilt(GameFlow.TrackBuilderSceneName, GameFlow.LoadTrackBuilder);
            GUILayout.Space(8);
            if (MenuButton("← Back")) GoTo(Page.Root);
        }

        private void StartSinglePlayer()
        {
            bool roam = _spMode == (int)MatchMode.FreeRoam;
            string vehicle = _vehicles[_vehicleIdx];
            string track = _tracks[_trackIdx];
            int bots = roam ? 0 : _spBots;
            var s = SettingsStore.Current;

            SessionConfig.SetSinglePlayer();                 // clears roster + rubber-band
            SessionConfig.TargetLaps = roam ? 0 : _spLaps;
            SessionConfig.RubberBand = bots > 0 && _spRubber;
            SessionConfig.CountdownSeconds = (bots > 0 || _spLaps > 0) && !roam ? _spCountdown : 0;
            // Assigned AFTER SetSinglePlayer, which clears them.
            SessionConfig.Arcade = _spArcade && _spLaps > 0 && _spControl != 1 && !roam;
            SessionConfig.TrackLimits = SessionConfig.Arcade && _spTrackLimits;
            SessionConfig.ArcadeHandling = _spArcadeHandling;
            GameFlow.ActiveDesign = ResolveVehicle(vehicle);
            // Free roam owns its map: the town is not in the track picker at
            // all, so it is resolved by name here rather than selected.
            if (roam) GameFlow.ActiveTrack = TrackPresets.Resolve(TrackPresets.FreeRoamName);
            else SelectTrack(track);

            // Slot 0 = the human; slots 1..N = AI opponents.
            string pname = string.IsNullOrWhiteSpace(s.player1Name) ? "Player" : s.player1Name;
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = pname,
                profileId = pname,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                assists = SessionConfig.P1Assists(s),
                isBot = false,
                control = (DriveControl)Mathf.Clamp(_spControl, 0, 2),
            });
            for (int k = 1; k <= bots; k++)
                SessionConfig.Players.Add(MakeBotSlot(k, _spDiff));

            // After the roster exists: a team mode has to split it.
            ApplyMatchRules();

            s.lastVehicle = vehicle;
            s.lastTrack = track;
            s.lastLaps = _spLaps;
            s.spBots = _spBots;
            s.spDifficulty = _spDiff;
            s.spControl = _spControl;
            s.spRubberBand = _spRubber;
            s.spCountdown = _spCountdown;
            s.spArcade = _spArcade;
            s.spTrackLimits = _spTrackLimits;
            s.spArcadeHandling = _spArcadeHandling;
            SettingsStore.Save();

            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        // ---- championship ----------------------------------------------------

        private int _champIdx;      // series picker
        private int _champBots = 3; // opponents, pinned for the whole series

        private void DrawChampionship()
        {
            GUILayout.Label("CHAMPIONSHIP", GarageSkin.Header);
            GUILayout.Space(4);

            if (Championship.Active) DrawChampionshipInProgress();
            else DrawChampionshipSetup();

            GUILayout.Space(8);
            if (MenuButton("← Back")) GoTo(Page.Root);
        }

        private void DrawChampionshipSetup()
        {
            var all = ChampionshipCatalog.All;
            _champIdx = Mathf.Clamp(_champIdx, 0, all.Length - 1);
            var names = new List<string>(all.Length);
            foreach (var s in all) names.Add(s.label);
            _champIdx = CyclePicker("Series", names, _champIdx, x => x);

            var series = all[_champIdx];
            GUILayout.Label(series.blurb, GarageSkin.StatLabel);
            for (int i = 0; i < series.tracks.Length; i++)
                GUILayout.Label($"   Round {i + 1}: " +
                    (series.tracks[i] == "" ? "Classic Oval" : series.tracks[i]),
                    GarageSkin.StatLabel);

            GUILayout.Space(4);
            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            if (MenuNav.Button("Showroom — preview & customize ▶"))
                OpenShowroom(Page.Championship);
            _champBots = MenuNav.Stepper("Opponents", _champBots, 1, 7, v => $"{v} bots");
            _spDiff = CyclePicker("Difficulty", DiffNames, _spDiff, x => x);
            _spLaps = MenuNav.Stepper("Laps per round", Mathf.Max(1, _spLaps), 1, 20, v => $"{v} laps");
            GUILayout.Label("Points: 10-8-6-5-4-3-2-1. The roster is fixed for the\n" +
                            "whole series — win it outright for a Gold Vault.",
                            GarageSkin.StatLabel);

            GUILayout.Space(8);
            if (MenuButton("Start series ▶")) StartChampionship(series);
        }

        private void DrawChampionshipInProgress()
        {
            var series = Championship.Series;
            bool done = Championship.Complete;
            GUILayout.Label(series != null ? series.label : "Series", GarageSkin.Title);
            GUILayout.Label(done
                ? "All rounds raced."
                : $"Round {Championship.RoundNumber} of {Championship.Rounds} — " +
                  (Championship.NextTrack() == "" ? "Classic Oval" : Championship.NextTrack()),
                GarageSkin.StatLabel);

            GUILayout.Space(4);
            int pos = 1;
            foreach (var (name, points, isBot) in Championship.Standings())
                GUILayout.Label($"{pos++}.  {name}{(isBot ? "" : "  (you)")}   {points} pts",
                    GarageSkin.StatLabel);

            GUILayout.Space(8);
            if (!done && MenuButton($"Race round {Championship.RoundNumber} ▶"))
                StartChampionshipRound();
            if (done && MenuButton("Finish series")) Championship.Abandon();
            if (!done && MenuButton("Abandon series")) Championship.Abandon();
        }

        /// <summary>
        /// Open a series and roll straight into round 1. The roster is pinned
        /// here — bot names come from MakeBotSlot so the standings match the
        /// names on the results screen exactly.
        /// </summary>
        private void StartChampionship(SeriesDef series)
        {
            var s = SettingsStore.Current;
            string pname = string.IsNullOrWhiteSpace(s.player1Name) ? "Player" : s.player1Name;
            var botNames = new List<string>(_champBots);
            for (int k = 1; k <= _champBots; k++)
                botNames.Add(MakeBotSlot(k, _spDiff).name);

            Championship.Begin(series.id, pname, botNames, _spDiff);
            StartChampionshipRound();
        }

        /// <summary>Build the session for the next round and drive into it. The
        /// vehicle is whatever the player currently has selected; the track,
        /// opponents and difficulty come from the pinned series.</summary>
        private void StartChampionshipRound()
        {
            if (!Championship.Active || Championship.Complete) return;
            var st = Championship.State;
            var s = SettingsStore.Current;
            string vehicle = _vehicles.Count > 0
                ? _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)] : "";

            SessionConfig.SetSinglePlayer();
            SessionConfig.TargetLaps = Mathf.Max(1, _spLaps);
            SessionConfig.RubberBand = _spRubber;
            SessionConfig.CountdownSeconds = _spCountdown;
            SessionConfig.ChampionshipRound = true;

            GameFlow.ActiveDesign = ResolveVehicle(vehicle);
            Championship.LoadNextRoundTrack();

            string pname = st.driverNames.Count > 0 ? st.driverNames[0]
                : (string.IsNullOrWhiteSpace(s.player1Name) ? "Player" : s.player1Name);
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = pname,
                profileId = pname,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                assists = SessionConfig.P1Assists(s),
                isBot = false,
                // A championship is always driven by hand — a firmware or bot
                // session scoring a points table would be a strange trophy.
                control = DriveControl.Human,
            });
            for (int k = 1; k < st.driverNames.Count; k++)
                SessionConfig.Players.Add(MakeBotSlot(k, st.botDifficulty));

            s.lastVehicle = vehicle;
            SettingsStore.Save();
            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        /// <summary>Build one AI opponent: a preset car with a distinct paint colour.</summary>
        private static PlayerSlot MakeBotSlot(int k, int difficulty)
        {
            var preset = VehiclePresets.All[(k - 1) % VehiclePresets.All.Length];
            var design = preset.build();
            design.liveryPng = "";                                   // show the flat colour
            design.bodyColor = Color.HSVToRGB((k * 0.137f) % 1f, 0.65f, 0.95f);
            return new PlayerSlot
            {
                name = $"Bot {k} · {preset.name}",
                profileId = $"Bot {k}",
                design = design,
                isBot = true,
                control = DriveControl.BotAI,
                botDifficulty = difficulty,
                assists = new AIHWSim.Vehicles.AssistSettings(), // bots race on raw physics
            };
        }

        private void DrawMultiplayer()
        {
            GUILayout.Label("MULTIPLAYER — SPLIT-SCREEN", GarageSkin.Header);
            GUILayout.Space(4);
            var s = SettingsStore.Current;

            DrawPlayerRow(1, ref s.player1Name, ref _mpVeh1, ref _mpDev1);
            DrawPlayerRow(2, ref s.player2Name, ref _mpVeh2, ref _mpDev2);
            GUILayout.Space(4);

            _mpTrackIdx = CyclePicker("Track", _tracks, _mpTrackIdx, t => t == "" ? "Classic Oval" : t);

            _mpLaps = MenuNav.Stepper("Race laps", _mpLaps, 0, 50,
                v => v == 0 ? "Sandbox (no race)" : v.ToString());

            // Same rule as the single-player page: always offered, and ticking it
            // with a sandbox selected sets a race up instead of doing nothing.
            bool wantMpArcade = MenuNav.Toggle(_spArcade, " Arcade mode (power-ups & weapons)");
            if (wantMpArcade && !_spArcade && _mpLaps == 0) _mpLaps = 3;
            _spArcade = wantMpArcade;
            if (_spArcade)
            {
                _spTrackLimits = MenuNav.Toggle(_spTrackLimits, "    Track limits (off-track penalty)");
                GUILayout.Label("    Both players pick up independently; one shared board.",
                                GarageSkin.StatLabel);
            }
            // Physics choice stands on its own — see the single-player page.
            _spArcadeHandling = MenuNav.Toggle(_spArcadeHandling,
                " Arcade handling (extra grip + driving assists)");
            GUILayout.Space(6);

            string problem = ValidateDevices();
#if !ENABLE_INPUT_SYSTEM
            problem = "Split-screen needs the Input System package (Active Input Handling = Both).";
#endif
            GUI.enabled = problem == null;
            if (MenuButton("Start Split-Screen ▶")) StartSplitScreen();
            GUI.enabled = true;
            if (problem != null) GUILayout.Label(problem, GarageSkin.StatLabel);

            GUILayout.Space(6);
            if (MenuButton("Host LAN Game")) GoTo(Page.LanHost, RefreshLists);
            if (MenuButton("Join LAN Game"))
                GoTo(Page.LanJoin, () => { RefreshLists(); LanDiscovery.StartListen(); });
            GUILayout.Space(8);
            if (MenuButton("← Back")) { SettingsStore.Save(); GoTo(Page.Root); }
        }

        private void DrawPlayerRow(int n, ref string name, ref int vehIdx, ref int devChoice)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"P{n}", GUILayout.Width(24));
            name = GUILayout.TextField(name, GUILayout.Width(96));
            // The car pick is a nav-aware cycle; the device button follows it —
            // compact, but every part of the row works from the pad.
            vehIdx = MenuNav.Cycle("", Mathf.Clamp(vehIdx, 0, _vehicles.Count - 1), _vehicles.Count,
                k => _vehicles[k] == "" ? "Stock" : _vehicles[k], 0f);
            if (MenuNav.Button(DeviceLabel(devChoice), GUILayout.Width(86)))
                devChoice = (devChoice + 1) % (1 + PadCount());
            GUILayout.EndHorizontal();
        }

        private static int PadCount()
        {
#if ENABLE_INPUT_SYSTEM
            return Mathf.Max(1, UnityEngine.InputSystem.Gamepad.all.Count);
#else
            return 1;
#endif
        }

        private static string DeviceLabel(int choice) =>
            choice == 0 ? "Keyboard" : $"Gamepad {choice}";

        private string ValidateDevices()
        {
            if (_mpDev1 == _mpDev2) return "Players can't share a device.";
#if ENABLE_INPUT_SYSTEM
            int pads = UnityEngine.InputSystem.Gamepad.all.Count;
            int needed = Mathf.Max(_mpDev1, _mpDev2);
            if (needed > pads) return $"Gamepad {needed} not connected ({pads} detected).";
#endif
            return null;
        }

        private void StartSplitScreen()
        {
            var s = SettingsStore.Current;
            string track = _tracks[Mathf.Clamp(_mpTrackIdx, 0, _tracks.Count - 1)];

            SessionConfig.Mode = SessionMode.SplitScreen;
            // Split-screen has no mode picker, so it races — said explicitly
            // because the single-player page leaves its own choice in
            // SessionConfig, and coming here straight from a soccer match or a
            // free roam would otherwise carry those rules onto a race circuit.
            SessionConfig.Match = MatchMode.Race;
            SessionConfig.TargetLaps = _mpLaps;
            SessionConfig.CountdownSeconds = 0; // split-screen has no countdown control (yet)
            SessionConfig.Arcade = _spArcade && _mpLaps > 0;
            SessionConfig.TrackLimits = SessionConfig.Arcade && _spTrackLimits;
            SessionConfig.ArcadeHandling = _spArcadeHandling;
            SessionConfig.Players.Clear();
            SessionConfig.Players.Add(MakeSlot(s.player1Name, _mpVeh1, _mpDev1, SessionConfig.P1Assists(s)));
            SessionConfig.Players.Add(MakeSlot(s.player2Name, _mpVeh2, _mpDev2, SessionConfig.P2Assists(s)));

            // Split-screen's picker offers saves and scene tracks, not presets.
            if (track == "") GameFlow.ActiveTrack = null;
            else SelectTrack(track);
            GameFlow.ActiveDesign = SessionConfig.Players[0].design;

            s.p1DeviceKind = _mpDev1 == 0 ? (int)InputDeviceKind.Keyboard : (int)InputDeviceKind.Gamepad;
            s.p2DeviceKind = _mpDev2 == 0 ? (int)InputDeviceKind.Keyboard : (int)InputDeviceKind.Gamepad;
            s.p2GamepadIndex = Mathf.Max(0, _mpDev2 - 1);
            s.spArcade = _spArcade;
            s.spTrackLimits = _spTrackLimits;
            s.spArcadeHandling = _spArcadeHandling;
            SettingsStore.Save();

            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        private PlayerSlot MakeSlot(string name, int vehIdx, int devChoice,
            AIHWSim.Vehicles.AssistSettings assists)
        {
            string veh = _vehicles[Mathf.Clamp(vehIdx, 0, _vehicles.Count - 1)];
            return new PlayerSlot
            {
                name = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                profileId = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                design = ResolveVehicle(veh),
                deviceKind = devChoice == 0 ? InputDeviceKind.Keyboard : InputDeviceKind.Gamepad,
                gamepadIndex = Mathf.Max(0, devChoice - 1),
                assists = assists,
            };
        }

        private Vector2 _resumeScroll;
        private readonly List<(string name, SessionSnapshot snap)> _resumeCache =
            new List<(string, SessionSnapshot)>();

        /// <summary>Read snapshot files once on entering the page (not per OnGUI frame).</summary>
        private void RefreshSnapshots()
        {
            _resumeCache.Clear();
            foreach (var name in SaveSystem.ListSnapshots())
            {
                var s = SaveSystem.LoadSnapshot(name);
                if (s != null) _resumeCache.Add((name, s));
            }
        }

        private void DrawResume()
        {
            GUILayout.Label("RESUME DRIVE", GarageSkin.Header);
            GUILayout.Space(6);

            if (_resumeCache.Count == 0) GUILayout.Label("(no saved sessions)", GarageSkin.StatLabel);

            _resumeScroll = GUILayout.BeginScrollView(_resumeScroll, GUILayout.Height(220));
            string deleted = null;
            SessionSnapshot resume = null;
            foreach (var (name, s) in _resumeCache)
            {
                string mode = (SessionMode)s.mode == SessionMode.SplitScreen ? "Split-screen" : "Single player";
                string track = string.IsNullOrEmpty(s.trackName) ? "Classic Oval" : s.trackName;
                GUILayout.BeginHorizontal();
                if (MenuNav.Button($"{mode} · {track} · {name.Replace("snapshot_", "")}"))
                    resume = s;
                if (MenuNav.Button("✕", GUILayout.Width(28)))
                    deleted = name;
                GUILayout.EndHorizontal();
            }
            if (deleted != null)
            {
                SaveSystem.DeleteSnapshot(deleted);
                RefreshSnapshots();
            }
            if (resume != null) ResumeSnapshot(resume);
            GUILayout.EndScrollView();

            GUILayout.Space(8);
            if (MenuButton("← Back")) GoTo(Page.Root);
        }

        private void ResumeSnapshot(SessionSnapshot s)
        {
            SessionConfig.Mode = (SessionMode)s.mode;
            SessionConfig.TargetLaps = s.targetLaps;
            // Snapshots don't capture inventories or effect timers, so resuming
            // into arcade would restore a race with everyone empty-handed and the
            // boxes reset. This path doesn't go through SetSinglePlayer, so clear
            // the flags explicitly rather than inheriting the last session's.
            SessionConfig.Arcade = false;
            SessionConfig.TrackLimits = false;
            SessionConfig.Players.Clear();
            foreach (var ps in s.players)
            {
                // Assists aren't part of saved state — re-read the current options.
                var cur = SettingsStore.Current;
                SessionConfig.Players.Add(new PlayerSlot
                {
                    name = ps.name,
                    profileId = ps.profileId,
                    design = string.IsNullOrEmpty(ps.vehicleJson)
                        ? null : JsonUtility.FromJson<VehicleDesign>(ps.vehicleJson),
                    deviceKind = (InputDeviceKind)ps.deviceKind,
                    gamepadIndex = ps.gamepadIndex,
                    assists = SessionConfig.Players.Count == 0
                        ? SessionConfig.P1Assists(cur) : SessionConfig.P2Assists(cur),
                });
            }
            // A scene track has no trackJson to restore — it is named, and the
            // scene name is what the snapshot carries. Checked first because the
            // ActiveTrack assignment would clear it.
            if (!string.IsNullOrEmpty(s.trackScene))
                GameFlow.ActiveSceneTrack = s.trackScene;
            else
                GameFlow.ActiveTrack = string.IsNullOrEmpty(s.trackJson)
                    ? null : JsonUtility.FromJson<TrackDesign>(s.trackJson);
            GameFlow.ActiveDesign = SessionConfig.Players.Count > 0 ? SessionConfig.Players[0].design : null;
            GameFlow.PendingSnapshot = s;
            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        // ---- LAN pages -------------------------------------------------------

        private void DrawLanHost()
        {
            GUILayout.Label("HOST LAN GAME", GarageSkin.Header);
            GUILayout.Space(4);
            var s = SettingsStore.Current;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(60));
            s.player1Name = GUILayout.TextField(s.player1Name, GUILayout.Width(160));
            GUILayout.EndHorizontal();

            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            if (MenuNav.Button("Showroom ▶")) OpenShowroom(Page.LanHost);
            _trackIdx = CyclePicker("Track", _tracks, _trackIdx, t => t == "" ? "Classic Oval" : t);
            GUILayout.Space(4);

            // The host's arcade rules are the session's — joiners are told them
            // in the welcome and never consult their own settings, so a lobby is
            // never half arcade.
            _spArcade = MenuNav.Toggle(_spArcade, " Arcade mode (power-ups & weapons)");
            if (_spArcade)
            {
                _spTrackLimits = MenuNav.Toggle(_spTrackLimits, "    Track limits (off-track penalty)");
                GUILayout.Label("    Item boxes are live in free roam too, so there is\n" +
                                "    something to do between races.", GarageSkin.StatLabel);
            }
            // Physics choice stands on its own; it crosses the wire in the
            // welcome, so the whole lobby drives one handling mode.
            _spArcadeHandling = MenuNav.Toggle(_spArcadeHandling,
                " Arcade handling (extra grip + driving assists)");

            GUILayout.Space(4);
            GUILayout.Label("Players join into free roam; you start races and\nchange maps from the in-game Esc menu.",
                GarageSkin.StatLabel);
            GUILayout.Space(6);

            if (MenuButton("Start Hosting ▶")) StartLanHost();
            GUILayout.Space(8);
            if (MenuButton("← Back")) { SettingsStore.Save(); GoTo(Page.Multiplayer); }
        }

        private void StartLanHost()
        {
            var s = SettingsStore.Current;
            string vehicle = _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)];
            string track = _tracks[Mathf.Clamp(_trackIdx, 0, _tracks.Count - 1)];
            SettingsStore.Save();

            GameFlow.ActiveDesign = ResolveVehicle(vehicle);
            SelectTrack(track);

            SessionConfig.Mode = SessionMode.LanHost;
            SessionConfig.TargetLaps = 0; // free roam; races start in-game
            // LAN arcade is gated on the map having a finish line rather than on
            // a lap target: free roam has no laps, but item boxes still need a
            // racing line to be laid out along.
            SessionConfig.Arcade = _spArcade;
            SessionConfig.TrackLimits = _spArcade && _spTrackLimits;
            SessionConfig.ArcadeHandling = _spArcadeHandling;
            s.spArcade = _spArcade;
            s.spTrackLimits = _spTrackLimits;
            s.spArcadeHandling = _spArcadeHandling;
            SettingsStore.Save();
            SessionConfig.Players.Clear();
            SessionConfig.Players.Add(new PlayerSlot
            {
                name = s.player1Name,
                profileId = s.player1Name,
                design = GameFlow.ActiveDesign,
                deviceKind = InputDeviceKind.MergedKeyboardGamepad,
                isLocal = true,
                assists = SessionConfig.P1Assists(s),
            });

            var session = NetSession.Create();
            if (!session.StartHost())
            {
                _status = "Failed to start hosting (port in use?).";
                session.Leave();
                return;
            }
            LanDiscovery.SetAnnounce(new LanDiscovery.Announce
            {
                gameName = s.player1Name,
                players = 1,
                trackName = track == "" ? "Classic Oval" : track,
            });
            LoadIfBuilt(GameFlow.TrackSceneName, GameFlow.LoadTrack);
        }

        private void DrawLanJoin()
        {
            GUILayout.Label("JOIN LAN GAME", GarageSkin.Header);
            GUILayout.Space(4);
            var s = SettingsStore.Current;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(60));
            s.player1Name = GUILayout.TextField(s.player1Name, GUILayout.Width(160));
            GUILayout.EndHorizontal();
            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            if (MenuNav.Button("Showroom ▶")) OpenShowroom(Page.LanJoin);
            GUILayout.Space(4);

            if (_connecting)
            {
                GUILayout.Label("Connecting…", GarageSkin.Header);
                if (Time.unscaledTime > _connectDeadline)
                {
                    _connecting = false;
                    _status = "Connection timed out.";
                    NetSession.Instance?.Leave();
                }
                if (MenuButton("Cancel"))
                {
                    _connecting = false;
                    NetSession.Instance?.Leave();
                }
                return;
            }

            LanDiscovery.Drain(_discovered);
            GUILayout.Label($"GAMES ON YOUR NETWORK ({_discovered.Count})", GarageSkin.Header);
            _lanScroll = GUILayout.BeginScrollView(_lanScroll, GUILayout.Height(120));
            foreach (var g in _discovered)
            {
                string track = string.IsNullOrEmpty(g.info.trackName) ? "Classic Oval" : g.info.trackName;
                if (MenuNav.Button($"{g.info.gameName} · {track} · {g.info.players}/{g.info.maxPlayers}"))
                    Connect(g.address, g.info.port);
            }
            if (_discovered.Count == 0)
                GUILayout.Label("(listening for hosts…)", GarageSkin.StatLabel);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(24));
            _joinIp = GUILayout.TextField(_joinIp, GUILayout.Width(150));
            if (MenuNav.Button("Connect", GUILayout.Width(80)))
                Connect(_joinIp.Trim(), NetSession.DefaultPort);
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            if (MenuButton("← Back"))
            {
                LanDiscovery.StopListen();
                SettingsStore.Save();
                GoTo(Page.Multiplayer);
            }
        }

        private void Connect(string ip, ushort port)
        {
            SettingsStore.Save();
            string vehicle = _vehicles[Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1)];
            GameFlow.ActiveDesign = ResolveVehicle(vehicle);

            LanDiscovery.StopListen();
            var session = NetSession.Create();
            if (!session.StartClient(ip, port))
            {
                _status = "Failed to start connecting.";
                session.Leave();
                return;
            }
            // The scene loads when aihw.welcome arrives (NetSession drives it);
            // we just show progress + a timeout here.
            _connecting = true;
            _connectDeadline = Time.unscaledTime + 10f;
        }

        private void OnDestroy()
        {
            // Scene changed (drive/garage/track): stop the join-page listener.
            LanDiscovery.StopListen();
        }

        private Vector2 _optionsScroll;

        private void DrawOptions()
        {
            GUILayout.Label("OPTIONS", GarageSkin.Header);
            GUILayout.Space(6);
            var s = SettingsStore.Current;
            bool changed = false;

            // Scroll so the page never clips in small (editor) game views.
            _optionsScroll = GUILayout.BeginScrollView(_optionsScroll);

            changed |= MenuNav.Slider01("Master volume", ref s.masterVolume);
            changed |= MenuNav.Slider01("Sound effects", ref s.sfxVolume);
            changed |= MenuNav.Slider01("Engine + tyres", ref s.engineVolume);
            changed |= MenuNav.Slider01("Music", ref s.musicVolume);

            GUILayout.Space(4);
            string[] quality = QualitySettings.names;
            int qShown = Mathf.Clamp(s.qualityLevel >= 0 ? s.qualityLevel : QualitySettings.GetQualityLevel(),
                0, quality.Length - 1);
            int qNew = MenuNav.Cycle("Quality", qShown, quality.Length, i => quality[i], 60f);
            if (qNew != qShown) { s.qualityLevel = qNew; changed = true; }

            bool fs = MenuNav.Toggle(s.fullscreen, " Fullscreen");
            if (fs != s.fullscreen) { s.fullscreen = fs; changed = true; }
            bool vs = MenuNav.Toggle(s.vSync, " VSync");
            if (vs != s.vSync) { s.vSync = vs; changed = true; }
            bool bl = MenuNav.Toggle(s.bloom, " Bloom (neon glow)");
            if (bl != s.bloom) { s.bloom = bl; changed = true; }
            bool ms = MenuNav.Toggle(s.mouseSteer, " Mouse steering (single player)");
            if (ms != s.mouseSteer) { s.mouseSteer = ms; changed = true; }

            // Keyboard-only feel: ramps the digital A/D step like a transmitter
            // stick. Gamepad sticks are never shaped. 0% = old instant response.
            changed |= AssistSlider("KB steer smoothing", ref s.kbSteerSmoothing);
            // Same idea on the other axis: W is an instant step to full voltage,
            // which is why the car lights up the rears out of every slow corner.
            changed |= AssistSlider("KB throttle smoothing", ref s.kbThrottleSmoothing);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("P1 name", GUILayout.Width(60));
            string n1 = GUILayout.TextField(s.player1Name, GUILayout.Width(140));
            if (n1 != s.player1Name) { s.player1Name = n1; changed = true; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("P2 name", GUILayout.Width(60));
            string n2 = GUILayout.TextField(s.player2Name, GUILayout.Width(140));
            if (n2 != s.player2Name) { s.player2Name = n2; changed = true; }
            GUILayout.EndHorizontal();

            // Arcade assists — 0% is the pure physics model. Per-player; in LAN
            // your own values travel with you to the host.
            GUILayout.Space(6);
            GUILayout.Label("ASSISTS — P1  (0% = realistic)", GarageSkin.Header);

            // Preset row. The sliders below stay live: touching any of them flips
            // the preset to Custom, so this is a shortcut rather than a cage.
            string[] presetNames = { "Off", "Standard", "Full", "Custom" };
            int preset = Mathf.Clamp(s.assistPreset, 0, 3);
            int presetNew = MenuNav.Cycle("Preset", preset, presetNames.Length, i => presetNames[i], 60f);
            if (presetNew != preset)
            {
                s.assistPreset = presetNew;
                changed = true;
            }

            bool sliderMoved = false;
            sliderMoved |= AssistSlider("Steering help", ref s.p1AssistSteer);
            sliderMoved |= AssistSlider("Stability", ref s.p1AssistStability);
            sliderMoved |= AssistSlider("Traction ctrl", ref s.p1AssistTraction);
            sliderMoved |= AssistSlider("ABS", ref s.p1AssistAbs);
            sliderMoved |= AssistSlider("Launch ctrl", ref s.p1AssistLaunch);
            GUILayout.Space(4);
            GUILayout.Label("ASSISTS — P2 (split-screen)", GarageSkin.Header);
            sliderMoved |= AssistSlider("Steering help", ref s.p2AssistSteer);
            sliderMoved |= AssistSlider("Stability", ref s.p2AssistStability);
            sliderMoved |= AssistSlider("Traction ctrl", ref s.p2AssistTraction);
            sliderMoved |= AssistSlider("ABS", ref s.p2AssistAbs);
            sliderMoved |= AssistSlider("Launch ctrl", ref s.p2AssistLaunch);
            if (sliderMoved)
            {
                s.assistPreset = (int)SessionConfig.AssistPreset.Custom;
                changed = true;
            }

            // Simulation realism — deterministic noise + control-loop latency.
            GUILayout.Space(6);
            GUILayout.Label("SIM REALISM", GarageSkin.Header);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Noise seed (0 = random)", GUILayout.Width(150));
            string seedStr = GUILayout.TextField(s.noiseSeed.ToString(), GUILayout.Width(90));
            if (int.TryParse(seedStr, out int seed) && seed != s.noiseSeed)
            { s.noiseSeed = seed; changed = true; }
            GUILayout.EndHorizontal();
            GUILayout.Label("Seed used is stamped into the telemetry sidecar.", GarageSkin.StatLabel);
            int del = MenuNav.Stepper("Actuation delay", s.actuationDelayTicks, 0, 5,
                v => $"{v} ticks", 110f);
            if (del != s.actuationDelayTicks) { s.actuationDelayTicks = del; changed = true; }

            // Telemetry — opt-in CSV logging (off by default). Also toggleable
            // mid-drive from pause ▸ Settings, where it starts after the menu closes.
            GUILayout.Space(6);
            GUILayout.Label("TELEMETRY", GarageSkin.Header);
            bool lg = MenuNav.Toggle(s.logTelemetry, " Log sensor/telemetry data (CSV)");
            if (lg != s.logTelemetry) { s.logTelemetry = lg; changed = true; }
            GUILayout.Label("Off by default; writes to TelemetryLogs/ on Save.", GarageSkin.StatLabel);

            // Extras.
            GUILayout.Space(6);
            GUILayout.Label("EXTRAS", GarageSkin.Header);
            if (MenuNav.Button("Cheat Codes…")) GoTo(Page.Cheats);

            GUILayout.EndScrollView();

            if (changed)
            {
                SettingsStore.Apply();
                SettingsStore.Save();
            }

            GUILayout.Space(8);
            if (MenuButton("← Back")) GoTo(Page.Root);
        }

        /// <summary>The shared 0..1 row, now through MenuNav so the pad can
        /// focus it and nudge left/right. Kept as a thin alias rather than
        /// rewriting eleven call sites for no behaviour change.</summary>
        private static bool AssistSlider(string label, ref float value) =>
            MenuNav.Slider01(label, ref value);

        // ---- helpers -----------------------------------------------------------

        // Routed through MenuNav so every page's plain buttons are gamepad
        // focusable/activatable. The other control shapes (cycle pickers,
        // toggles, sliders) convert in the menu-shell pass.
        private static bool MenuButton(string label) =>
            MenuNav.MenuButton(label);

        // The nav-aware "◀ value ▶" row; drawing lives in MenuNav so the pad
        // can focus it and left/right through the options.
        private int CyclePicker(string label, List<string> options, int index,
            System.Func<string, string> display)
        {
            int i = Mathf.Clamp(index, 0, options.Count - 1);
            return MenuNav.Cycle(label, i, options.Count, k => display(options[k]), 60f);
        }

        private void LoadIfBuilt(string sceneName, System.Action load)
        {
            if (Application.CanStreamedLevelBeLoaded(sceneName)) ScreenFade.To(load);
            else _status = $"Scene '{sceneName}' missing — run Tools ▸ AIHWSim ▸ Create it first.";
        }

        /// <summary>Page change through a quick fade dip. The mutation happens
        /// inside the fade's coroutine — between frames, outside any OnGUI
        /// pass — which is the safest possible timing.</summary>
        private void GoTo(Page p, System.Action also = null)
        {
            ScreenFade.Dip(() =>
            {
                _page = p;
                also?.Invoke();
            });
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
