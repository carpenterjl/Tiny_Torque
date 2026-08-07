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
        // SinglePlayer is the submenu LIST; Sp* are its leaves. Appended rather
        // than inserted so existing values keep their meaning — and the nav page
        // id is "menu:" + the name, not the number.
        private enum Page
        {
            Root, SinglePlayer, Multiplayer, Championship, Options, Resume,
            LanHost, LanJoin, Showroom, Crates, Shop, Cheats,
            SpRace, SpFreeRoam, SpDerby, SpCtf, SpSoccer, SpController,
        }

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
        /// <summary>The Simulate Controller screen's own map pick, into
        /// <c>_roamMaps</c>. Separate from <see cref="_trackIdx"/> so choosing a
        /// test track to validate a DLL on cannot silently change which circuit
        /// the race page starts. Defaults to
        /// <c>SceneTrackCatalog.ControllerMap</c>; see <see cref="ControllerMapIndex"/>.</summary>
        private int _ctlTrackIdx;
        private List<string> _vehicles = new List<string>();
        private List<string> _tracks = new List<string>();

        // Free roam's own map list and index. Separate from _tracks because the
        // two sets differ at both ends: free roam's own maps are hidden from
        // _tracks (nothing to race there) and everything in _tracks is drivable
        // once the rules come off, so this is the roam maps followed by all of
        // _tracks. _roamBuilt is how many of the head entries are purpose-built,
        // which is the only thing the page has to say about the choice.
        private int _roamIdx;
        private int _roamBuilt;
        private List<string> _roamMaps = new List<string>();

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
        private int _spResultsWait = 30; // seconds after the first finisher (0 = wait for all)
        private bool _spArcade;       // power-ups, weapons, arcade board
        private bool _spTrackLimits = true;
        private bool _spArcadeHandling = true;   // false = race the circuits on raw sim physics
        private bool _spArcadeTyreThermal;       // keep tyre warm-up under the arcade floor
        private static readonly List<string> DiffNames = new List<string> { "Easy", "Medium", "Hard" };
        private static readonly List<string> ControlNames =
            new List<string> { "Manual", "Autonomous (firmware)", "Autonomous (bot AI)" };

        /// <summary>
        /// The "Driving" picker index as a <see cref="DriveControl"/>.
        ///
        /// The index order is frozen — <c>GameSettings.spControl</c> persists it, and
        /// every guard in this file reads <c>_spControl == 1</c> as "firmware". The enum
        /// happens to be { Human, BotAI, Firmware }, so the straight cast that used to
        /// live at the call site mapped index 1 to <c>BotAI</c>: picking "Autonomous
        /// (firmware)" silently ran the bot AI and never loaded a DLL, while "Autonomous
        /// (bot AI)" was the option that did. Map the two explicitly rather than relying
        /// on two unrelated orderings agreeing.
        /// </summary>
        private static DriveControl ControlFor(int idx) => idx switch
        {
            1 => DriveControl.Firmware,
            2 => DriveControl.BotAI,
            _ => DriveControl.Human,
        };

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
        // Fallback only — every caller passes a page explicitly. Points at a
        // setup screen rather than the Single Player LIST, which has no vehicle
        // picker for a selection to land in.
        private Page _showroomReturn = Page.SpRace;

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
            // Max(0, …) lands on the first entry when the saved name is gone or
            // was never written — which for the roam list is the town, i.e. the
            // map free roam has always started on.
            _roamIdx = Mathf.Max(0, _roamMaps.IndexOf(s.lastRoamMap));
            _ctlTrackIdx = ControllerMapIndex(s.lastControllerMap);
            _spLaps = Mathf.Clamp(s.lastLaps, 0, 50);
            _spBots = Mathf.Clamp(s.spBots, 0, 7);
            _spDiff = Mathf.Clamp(s.spDifficulty, 0, 2);
            _spControl = Mathf.Clamp(s.spControl, 0, 2);
            _spRubber = s.spRubberBand;
            _spCountdown = Mathf.Clamp(s.spCountdown, 0, 60);
            _spResultsWait = Mathf.Clamp(s.spResultsWait, 0, 300);
            _spArcade = s.spArcade;
            _spArcadeHandling = s.spArcadeHandling;
            _spArcadeTyreThermal = s.spArcadeTyreThermal;
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
            string prevRoam = _roamMaps.Count > 0
                ? _roamMaps[Mathf.Clamp(_roamIdx, 0, _roamMaps.Count - 1)] : null;
            string prevCtl = _roamMaps.Count > 0
                ? _roamMaps[Mathf.Clamp(_ctlTrackIdx, 0, _roamMaps.Count - 1)] : null;

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

            // Free roam's list: the maps built for it first — those are exactly
            // the ones _tracks leaves out — then every race map, which is a
            // perfectly good place to drive once there is nothing to win.
            _roamMaps = TrackPresets.RoamNames();
            _roamMaps.AddRange(AIHWSim.Track.SceneTrackCatalog.RoamNames());
            _roamBuilt = _roamMaps.Count;
            _roamMaps.AddRange(_tracks);

            if (prevPick != null)
            {
                int found = _vehicles.IndexOf(prevPick);
                if (found >= 0) _vehicleIdx = found;
            }
            if (prevRoam != null)
            {
                int found = _roamMaps.IndexOf(prevRoam);
                if (found >= 0) _roamIdx = found;
            }
            if (prevCtl != null) _ctlTrackIdx = ControllerMapIndex(prevCtl);
            _vehicleIdx = Mathf.Clamp(_vehicleIdx, 0, _vehicles.Count - 1);
            _trackIdx = Mathf.Clamp(_trackIdx, 0, _tracks.Count - 1);
            _roamIdx = Mathf.Clamp(_roamIdx, 0, _roamMaps.Count - 1);
            _ctlTrackIdx = Mathf.Clamp(_ctlTrackIdx, 0, _roamMaps.Count - 1);
        }

        /// <summary>
        /// Where the Simulate Controller screen's picker should sit for a saved
        /// map name.
        ///
        /// Unlike the other pickers this does NOT fall to index 0 when the saved
        /// name is gone: index 0 of the roam list is the town, and a controller
        /// screen quietly opening on the town instead of the test track is the
        /// one outcome this whole default exists to prevent. The test track is
        /// the fallback; 0 is only the fallback's fallback, for a build where the
        /// scene has been removed from the catalogue.
        /// </summary>
        private int ControllerMapIndex(string want)
        {
            // want == "" is NOT "unset" — it is the classic oval, a legitimate
            // pick. Only a name that is absent from the list falls through.
            int i = want == null ? -1 : _roamMaps.IndexOf(want);
            if (i < 0) i = _roamMaps.IndexOf(AIHWSim.Track.SceneTrackCatalog.ControllerMap);
            return Mathf.Max(0, i);
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
            // wheelKey counts on its own: a wheel with no legacy int — anything
            // Asset Studio commits — leaves wheelStyle at −1 and would otherwise
            // read as an untouched loadout.
            bool touched = l.hornStyle >= 0 || l.wheelStyle >= 0
                || !string.IsNullOrEmpty(l.wheelKey) || l.paintIdx >= 0
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
                // The setup screens are one level below the Single Player list.
                // Without these they would hit `default` and jump two levels.
                case Page.SpRace:
                case Page.SpFreeRoam:
                case Page.SpDerby:
                case Page.SpCtf:
                case Page.SpSoccer:
                case Page.SpController:
                    BackFromSetup();
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

            float w = 380f, h = Mathf.Min(PanelHeight(_pageDraw), UIScale.H - 20f);
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
                case Page.SpRace: DrawSpRace(); break;
                case Page.SpFreeRoam: DrawSpFreeRoam(); break;
                case Page.SpDerby: DrawSpArena(MatchMode.Derby); break;
                case Page.SpCtf: DrawSpArena(MatchMode.Ctf); break;
                case Page.SpSoccer: DrawSpArena(MatchMode.Soccer); break;
                case Page.SpController: DrawSpController(); break;
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

        /// <summary>
        /// Unclamped panel height per page; the caller clamps to the window. A
        /// page missing from here is not an error and draws nothing wrong — it
        /// just clips silently at 430, which is why the two tallest screens also
        /// sit in a scroll view rather than trusting this number to be right.
        /// </summary>
        private static float PanelHeight(Page p) => p switch
        {
            Page.Options => 680f,
            // The shop scrolls ten offers and the championship lists four rounds
            // plus a points table; both drown in the default 430.
            Page.Shop => 620f,
            Page.Championship => 600f,
            // Tallest page in the game: the scripts list, the build panel with its
            // log, and a full race setup underneath. It scrolls, so this is only
            // deciding how much is visible without scrolling.
            Page.SpController => 690f,
            // The five setup screens all carry the temporary dev row (~90) below
            // their footer; drop 90 from each of these when it goes.
            Page.SpRace => 690f,
            Page.Multiplayer => 560f,
            Page.SpDerby or Page.SpCtf or Page.SpSoccer => 610f,
            Page.SpFreeRoam => 560f,
            _ => 430f,
        };

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
        /// ring and quietly falls back to a free drive. Free Roam is blank: it
        /// does not use the race track picker at all — it has its own list
        /// (<see cref="_roamMaps"/>), which is where its maps live.</summary>
        private static readonly string[] ModeArena =
            { "", "Scrapyard Bowl", "Cargo Yard", "Torque Dome", "" };

        private int _spScore = 3;    // captures / goals to win

        /// <summary>
        /// Which mode a setup page is for. Derived from the page rather than held
        /// in a field, because <see cref="_pageDraw"/> is already the Layout
        /// snapshot every draw method is required to read from — a separate mode
        /// field would need its own twin, and would be one more thing that can
        /// disagree with the page you are looking at.
        /// </summary>
        private static MatchMode ModeOf(Page p) => p switch
        {
            Page.SpDerby => MatchMode.Derby,
            Page.SpCtf => MatchMode.Ctf,
            Page.SpSoccer => MatchMode.Soccer,
            Page.SpFreeRoam => MatchMode.FreeRoam,
            _ => MatchMode.Race,     // SpRace and SpController both race
        };

        private static Page PageOf(MatchMode m) => m switch
        {
            MatchMode.Derby => Page.SpDerby,
            MatchMode.Ctf => Page.SpCtf,
            MatchMode.Soccer => Page.SpSoccer,
            MatchMode.FreeRoam => Page.SpFreeRoam,
            _ => Page.SpRace,
        };

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
        private void ApplyMatchRules(MatchMode mode)
        {
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

        /// <summary>
        /// The Single Player page is a LIST now, not a setup form: one row per
        /// mode, each opening a screen that shows only the controls that mode
        /// actually has. The setup form it used to be carried five modes' worth of
        /// rows behind `if (roam)` / `if (arena)` gates, which is how the firmware
        /// option ended up as one ◀ ▶ row two thirds of the way down a page most
        /// people never scrolled.
        /// </summary>
        private void DrawSinglePlayer()
        {
            GUILayout.Label("SINGLE PLAYER", GarageSkin.Header);
            GUILayout.Space(6);

            if (MenuButton("Race")) GoToMode(MatchMode.Race);
            if (MenuButton("Free Roam")) GoToMode(MatchMode.FreeRoam);
            if (MenuButton("Demolition")) GoToMode(MatchMode.Derby);
            if (MenuButton("Capture the Flag")) GoToMode(MatchMode.Ctf);
            if (MenuButton("Soccer")) GoToMode(MatchMode.Soccer);
            if (MenuButton("Simulate Controller"))
                GoTo(Page.SpController, () =>
                {
                    RefreshLists();
                    RefreshControllerDlls();
                    _scriptsAt = -99f;   // force a fresh scan on the first Layout pass
                });
            GUILayout.Space(8);
            if (MenuButton("Garage")) LoadIfBuilt(GameFlow.GarageSceneName, GameFlow.LoadGarage);
            if (MenuButton("Vehicle Studio"))
                LoadIfBuilt(GameFlow.BodyEditorSceneName, GameFlow.LoadBodyEditor);
            if (MenuButton("Track Builder")) LoadIfBuilt(GameFlow.TrackBuilderSceneName, GameFlow.LoadTrackBuilder);
            GUILayout.Space(8);
            if (MenuButton("← Back")) GoTo(Page.Root);
        }

        /// <summary>
        /// Open a mode's setup screen. <see cref="SelectArenaFor"/> runs on entry
        /// now that there is no Mode cycle to change — and it must run AFTER
        /// RefreshLists, because it searches <c>_tracks</c>. Get that order wrong
        /// and the picker silently keeps the previous track, which for an arena
        /// mode means a derby on a race circuit quietly degrading to a free drive.
        /// </summary>
        private void GoToMode(MatchMode mode)
        {
            GoTo(PageOf(mode), () => { RefreshLists(); SelectArenaFor((int)mode); });
        }

        /// <summary>One step back out of any setup screen. Shared by the pad-Back
        /// handler and the "← Back" row so the two cannot drift apart — they were
        /// two independent copies of the same decision before.</summary>
        private void BackFromSetup()
        {
            SettingsStore.Save();
            GoTo(Page.SinglePlayer);
        }

        // ---- shared setup rows ----------------------------------------------
        // Each interlock lives in exactly one of these. Five screens copying the
        // firmware↔arcade rules by hand is how they would eventually disagree.

        private void DrawVehicleRow(Page returnTo)
        {
            _vehicleIdx = CyclePicker("Vehicle", _vehicles, _vehicleIdx, v => v == "" ? "Stock Default" : v);
            if (MenuNav.Button("Showroom — preview & customize ▶"))
                OpenShowroom(returnTo);
        }

        private void DrawTrackRow(MatchMode mode)
        {
            _trackIdx = CyclePicker("Track", _tracks, _trackIdx, t => t == "" ? "Classic Oval" : t);
            if (mode != MatchMode.Race)
                GUILayout.Label($"   {ModeNames[(int)mode]} needs an arena — " +
                                $"★ {ModeArena[(int)mode]} is the one built for it.",
                                GarageSkin.StatLabel);
        }

        /// <summary>AI opponents. Never drawn in free roam: with no racing line and
        /// no arena policy to hunt anything, a bot dropped into the town would sit
        /// at its spawn, and an opponent that does nothing is worse than none.</summary>
        private void DrawOpponentsRows()
        {
            _spBots = MenuNav.Stepper("Opponents", _spBots, 0, 7,
                v => v == 0 ? "None" : $"{v} bots");

            if (_spBots > 0)
            {
                _spDiff = CyclePicker("Difficulty", DiffNames, _spDiff, x => x);
                _spRubber = MenuNav.Toggle(_spRubber, " Rubber-band (keep the pack close)");
            }
        }

        /// <summary>How the player's own car is driven.</summary>
        private void DrawDrivingRow()
        {
            _spControl = CyclePicker("Driving", ControlNames, _spControl, x => x);
        }

        private void DrawCountdownRow(bool show)
        {
            if (!show) return;
            _spCountdown = MenuNav.Stepper("Countdown", _spCountdown, 0, 60,
                v => v == 0 ? "None" : $"{v} s");
        }

        /// <summary>
        /// Arcade items and their track-limits sub-toggle. Race screen only: the
        /// boxes and the position board both need a finish line, the arena modes
        /// bring their own scoring, and free roam has nothing to hang either from.
        ///
        /// Arcade must never run on a firmware session — a boost or a spin-out
        /// would corrupt a controller-validation run — so the whole block is
        /// disabled rather than hidden when firmware is selected.
        /// </summary>
        private void DrawArcadeRows()
        {
            bool firmware = _spControl == 1;
            GUI.enabled = !firmware;
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
        }

        /// <summary>
        /// The handling mode, deliberately OUTSIDE the arcade-items nest and drawn
        /// on every setup screen: free roam and the arena modes have no item boxes
        /// but very much have physics, and burying the physics choice under a
        /// weapons toggle made it unreachable exactly where the car felt worst.
        /// Firmware sessions face raw physics regardless (no HandlingFloor), so the
        /// control is disabled rather than lying.
        /// </summary>
        private void DrawHandlingRows()
        {
            GUI.enabled = _spControl != 1;
            _spArcadeHandling = MenuNav.Toggle(_spArcadeHandling,
                " Arcade handling (extra grip + driving assists)");
            GUILayout.Label(_spArcadeHandling
                    ? "    ARCADE — everyone, bots included, gets grip, stability and assists."
                    : "    SIM — raw brush-tyre physics, no assist floor. The circuits bite.",
                GarageSkin.StatLabel);
            DrawTyreThermalRow();
            GUI.enabled = true;
        }

        /// <summary>
        /// The arcade sub-choice: keep tyre warm-up under the arcade grip floor.
        ///
        /// Only shown when arcade handling is on, because in sim it is not a choice
        /// — a sim session always runs whatever thermal model the car's own tyre
        /// pressures ask for. Drawn from three pages, so it lives in one method
        /// rather than three copies that can drift apart.
        /// </summary>
        private void DrawTyreThermalRow()
        {
            if (!_spArcadeHandling) return;
            _spArcadeTyreThermal = MenuNav.Toggle(_spArcadeTyreThermal,
                "    Tyre temperature + pressure (cold tyres at the start)");
        }

        // ══════════════ TEMPORARY DEV SWITCH — delete before shipping ═════════════
        /// <summary>Loud enough to be remembered. A test switch that blends in is
        /// a test switch that ships.</summary>
        private static readonly Color DevTint = new Color(1f, 0.30f, 0.72f);

        /// <summary>
        /// Unlock everything, for testing. Drawn on every single-player setup
        /// screen because that is where the things it unlocks get picked, and in
        /// exactly one method so removing the feature is removing two call-free
        /// blocks. See <c>Progression.DevUnlockAll</c> for what it actually does
        /// and the full removal list.
        /// </summary>
        private void DrawDevRow()
        {
            var prevColor = GUI.color;
            GUI.color = DevTint;

            GUILayout.Space(10);
            GUILayout.Label("▼ ▼ ▼   D E V   —   T E M P O R A R Y   ▼ ▼ ▼", GarageSkin.Header);

            bool was = Persistence.Progression.DevUnlockAll;
            bool dev = MenuNav.Toggle(was, " Dev mode — unlock every car, cosmetic and paint");
            if (dev != was)
            {
                SettingsStore.Current.devUnlockAll = dev;
                SettingsStore.Save();
                // The vehicle picker is built from the gate, so the locked cars
                // appear (or vanish) on the very next pass. Safe to call from a
                // draw method: it changes list CONTENTS, and a Cycle row is one
                // control however many entries it holds.
                RefreshLists();
            }

            // Both branches are two lines on purpose — the toggle is consumed on
            // the Layout pass, so this label's text can differ between Layout and
            // its Repaint, and only its height has to agree.
            GUILayout.Label(dev
                    ? "   ON — nothing is locked anywhere. Your actual collection is\n" +
                      "   untouched: turn this off and it is exactly as you left it."
                    : "   Unlocks every car, cosmetic and paint, for testing.\n" +
                      "   Temporary — remove this row before shipping.",
                GarageSkin.StatLabel);

            GUI.color = prevColor;
        }
        // ═════════════════════════ end temporary dev switch ═══════════════════════

        private void DrawSetupFooter(string goLabel, MatchMode mode)
        {
            DrawDevRow();       // TEMPORARY — goes with the rest of the dev switch
            GUILayout.Space(10);
            if (MenuButton(goLabel)) StartSinglePlayer(mode);
            GUILayout.Space(8);
            if (MenuButton("← Back")) BackFromSetup();
        }

        // ---- per-mode setup screens -----------------------------------------

        private void DrawSpRace()
        {
            GUILayout.Label("RACE", GarageSkin.Header);
            GUILayout.Space(6);

            // The scroll view is the actual fix for "does this page fit": with it,
            // PanelHeight only has to be reasonable rather than exactly right.
            _raceScroll = GUILayout.BeginScrollView(_raceScroll);
            DrawVehicleRow(Page.SpRace);
            DrawTrackRow(MatchMode.Race);
            DrawOpponentsRows();
            DrawDrivingRow();

            _spLaps = MenuNav.Stepper("Race laps", _spLaps, 0, 50,
                v => v == 0 ? "Free drive" : $"{v} laps");
            // How long after the LEADER finishes before the results screen. Without
            // this the race waited for the last car, and a bot wedged in scenery
            // never arrives — so the race simply never ended.
            _spResultsWait = MenuNav.Stepper("Results wait", _spResultsWait, 0, 300,
                v => v == 0 ? "Wait for everyone" : $"{v} s after 1st");

            DrawCountdownRow(_spBots > 0 || _spLaps > 0);
            DrawArcadeRows();
            DrawHandlingRows();
            GUILayout.EndScrollView();
            DrawSetupFooter((_spBots > 0 || _spLaps > 0) ? "Race ▶" : "Drive ▶", MatchMode.Race);
        }

        private void DrawSpFreeRoam()
        {
            GUILayout.Label("FREE ROAM", GarageSkin.Header);
            GUILayout.Space(6);

            DrawVehicleRow(Page.SpFreeRoam);
            // Free roam's own picker, not DrawTrackRow: the race list deliberately
            // excludes the maps built for roaming, and this one deliberately
            // includes both. See _roamMaps.
            _roamIdx = CyclePicker("Map", _roamMaps, _roamIdx, m => m == "" ? "Classic Oval" : m);
            GUILayout.Label(_roamIdx < _roamBuilt
                    ? "   Built for roaming — somewhere to be, nothing to win.\n" +
                      "   No laps, no clock, no opponents. R puts you back on the road."
                    : "   A race map with the rules taken off: no laps, no clock,\n" +
                      "   no opponents. R puts you back at the start.",
                GarageSkin.StatLabel);
            DrawDrivingRow();
            DrawHandlingRows();
            DrawSetupFooter("Free Roam ▶", MatchMode.FreeRoam);
        }

        // ---- simulate controller ---------------------------------------------

        private readonly List<string> _ctlDlls = new List<string>();
        private int _ctlDllIdx;
        private int _ctlSeenBuild = -1;

        /// <summary>
        /// The controller DLLs sitting in the plugin folder, refreshed on page
        /// entry rather than per OnGUI pass — the row count must not change
        /// between a Layout pass and its Repaint, and a folder scan every frame
        /// would be both a census hazard and pointless disk traffic.
        /// </summary>
        private void RefreshControllerDlls()
        {
            // Keep whatever is on screen selected across a re-list; only fall back
            // to the saved choice when nothing was selected yet.
            string keep = SelectedControllerDll;
            if (string.IsNullOrEmpty(keep)) keep = SettingsStore.Current.simControllerDll;

            _ctlDlls.Clear();
            try
            {
                string dir = System.IO.Path.Combine(Application.dataPath, "Plugins", "x86_64");
                if (System.IO.Directory.Exists(dir))
                    foreach (var f in System.IO.Directory.GetFiles(dir, "*.dll"))
                        _ctlDlls.Add(System.IO.Path.GetFileName(f));
            }
            catch (System.Exception e)
            {
                // A missing or unreadable plugin folder is a "nothing built yet"
                // state, not a crash — the page says so below.
                Debug.LogWarning($"[Menu] Could not list controller DLLs: {e.Message}");
            }
            _ctlDlls.Sort();

            int want = _ctlDlls.IndexOf(keep);
            _ctlDllIdx = want >= 0 ? want : 0;
        }

        private string SelectedControllerDll =>
            _ctlDlls.Count > 0 ? _ctlDlls[Mathf.Clamp(_ctlDllIdx, 0, _ctlDlls.Count - 1)] : "";

        /// <summary>
        /// The car the selected controller DLL asks for through its optional
        /// <c>ctrl_get_vehicle()</c> export, as a picker display name — or null
        /// when it asks for nothing, which is every controller written before
        /// ABI v5.
        ///
        /// <paramref name="note"/> is a sentence for the screen, and it is not
        /// empty only when there is something the player would otherwise have to
        /// work out from a car they did not choose appearing on the grid. The
        /// answer is checked against <c>_vehicles</c> — the picker's own list —
        /// so a number naming a car this player has not unlocked, or a preset
        /// that has been renamed out from under the table, is refused here
        /// rather than resolved into a surprise.
        /// </summary>
        private string ControllerVehiclePick(out string note)
        {
            note = "";
            string dll = SelectedControllerDll;
            if (string.IsNullOrEmpty(dll)) return null;

            string path = System.IO.Path.Combine(
                AIHWSim.Build.ControllerWorkspace.PluginDir(), dll);
            var want = AIHWSim.Bridge.ControllerVehicleProbe.Read(path);
            if (want == AIHWSim.Bridge.ControllerVehicle.Menu) return null;

            string name = VehiclePresets.DisplayFor(want);
            if (name == null)
            {
                note = $"{dll} asks for vehicle #{(int)want}, which this build does " +
                       "not have. Using the pick above.";
                return null;
            }
            if (!_vehicles.Contains(name))
            {
                // Locked, or a preset renamed without its table. Either way the
                // picker is the authority on what may be driven; compiling a
                // number is not a way around it.
                note = $"{dll} asks for {(name == "" ? "Stock Default" : name)}, " +
                       "which is not available to you. Using the pick above.";
                return null;
            }

            note = $"{dll} drives {(name == "" ? "Stock Default" : name)} — " +
                   "the controller chose it, so the Vehicle row above is ignored.";
            return name;
        }

        private Vector2 _ctlScroll;
        private Vector2 _raceScroll;

        // ---- the player's own scripts ----------------------------------------

        /// <summary>
        /// One Layout-built string describing every folder in UserScripts/ and
        /// whether the DLL beside the game still matches what is on disk.
        ///
        /// One Label, not one per script: the list is read off the filesystem and
        /// can change while this page is open, and a row count that moves between a
        /// Layout pass and its Repaint is the census bug this whole UI is written to
        /// avoid. A Label's height may vary; how many controls exist may not.
        /// </summary>
        private string _scriptsInfo = "";
        private float _scriptsAt = -99f;

        /// <summary>Layout-snapshotted, because it is a disk check: doing it inline
        /// would stat the file twice a frame, and a differing answer across the
        /// Layout/Repaint pair would leave the pad's focus ring out of step with
        /// which rows are actually enabled.</summary>
        private bool _guideDraw;

        /// <summary>The script names as of the last scan, joined. Only used to spot
        /// that the set changed, so the build picker can be re-derived then and not
        /// on every poll.</summary>
        private string _scriptNames = "";

        /// <summary>How often the script list is re-derived from disk. Frequent
        /// enough that saving a file shows up as "edited since build" while you are
        /// still looking at the screen; rare enough that it is not a directory walk
        /// every frame.</summary>
        private const float ScriptsPollSeconds = 1f;

        private void RefreshScriptsInfo()
        {
            _scriptsAt = Time.unscaledTime;
            AIHWSim.Build.UserScriptCatalog.Invalidate();
            var all = AIHWSim.Build.UserScriptCatalog.All;
            _guideDraw = AIHWSim.Build.UserScriptCatalog.GuidePath != null;

            if (AIHWSim.Build.UserScriptCatalog.Root == null)
            {
                _scriptsInfo = "   No UserScripts folder found beside the game.\n" +
                               "   The Controllers folder setting below decides where it is looked for.";
                return;
            }
            if (all.Count == 0)
            {
                _scriptsInfo = "   UserScripts/ is empty. A folder with a .c file in it\n" +
                               "   becomes a controller named after the folder.";
                return;
            }

            // A folder created while the game is running should appear in the BUILD
            // picker too, not just in the list below it — otherwise the page names a
            // script it cannot offer to compile. Only when the set actually changed:
            // re-scraping CMakeLists.txt once a second for no reason is not free.
            var names = new System.Text.StringBuilder();
            foreach (var s in all) names.Append(s.name).Append('|');
            if (names.ToString() != _scriptNames)
            {
                _scriptNames = names.ToString();
                AIHWSim.Build.ControllerWorkspace.RefreshTargets();
            }

            var sb = new System.Text.StringBuilder();
            foreach (var s in all)
            {
                sb.Append("   ").Append(s.name);
                if (s.sources.Length == 0)
                    sb.Append(" — no .c file in the folder\n");
                else if (!AIHWSim.Build.UserScriptCatalog.IsBuilt(s))
                    sb.Append(" — never built. Build & Reload to compile it.\n");
                else if (AIHWSim.Build.UserScriptCatalog.IsStale(s))
                    sb.Append(" — EDITED SINCE THE LAST BUILD. Your changes are not in\n" +
                              "     the DLL yet; Build & Reload before you drive it.\n");
                else
                    sb.Append(" — built and up to date\n");
            }
            _scriptsInfo = sb.ToString().TrimEnd('\n');
        }

        private void DrawSpController()
        {
            GUILayout.Label("SIMULATE CONTROLLER", GarageSkin.Header);
            GUILayout.Label("Drive a car with your compiled C controller instead of\n" +
                            "the keyboard. The DLL runs closed-loop from the green flag;\n" +
                            "press M in the drive to take over by hand.",
                            GarageSkin.StatLabel);
            GUILayout.Space(6);

            // A build that just finished may have produced the first DLL there has
            // ever been. Re-listing changes which rows exist below, so it happens on
            // a Layout pass and nowhere else.
            var build = AIHWSim.Build.ControllerBuildRunner.Instance;
            if (Event.current.type == EventType.Layout)
            {
                if (build != null && _ctlSeenBuild != build.BuildGeneration)
                {
                    _ctlSeenBuild = build.BuildGeneration;
                    RefreshControllerDlls();
                }
                // Layout only, and on a timer: this walks the UserScripts folder, and
                // the string it produces has to be identical in the paired Repaint.
                if (Time.unscaledTime - _scriptsAt > ScriptsPollSeconds) RefreshScriptsInfo();
            }

            _ctlScroll = GUILayout.BeginScrollView(_ctlScroll);

            // Your own code first — this screen exists for it, and the game's four
            // shipped controllers are reference material by comparison.
            GUILayout.Label("YOUR SCRIPTS", GarageSkin.Header);
            GUILayout.Label(_scriptsInfo, GarageSkin.StatLabel);
            GUI.enabled = _guideDraw;
            if (MenuNav.Button("Open the guide (in your browser)")) OpenUserScriptGuide();
            GUI.enabled = true;

            GUILayout.Space(6);
            if (_ctlDlls.Count == 0)
            {
                GUILayout.Label("No controller DLL built yet — use Build & Reload below.",
                                GarageSkin.StatLabel);
            }
            else
            {
                _ctlDllIdx = CyclePicker("Controller", _ctlDlls, _ctlDllIdx, x => x);
            }

            GUILayout.Space(6);
            // Compile the C sources and hot-swap the result, without leaving the
            // game. Same panel the pause menu shows mid-drive.
            //
            // followDll ties the Build picker to the Run picker: pick the controller
            // you want to drive and the build target follows it, so the screen cannot
            // quietly compile one thing and race another.
            ControllerBuildPanel.Draw(logHeight: 120f, followDll: SelectedControllerDll);
            GUILayout.Space(6);
            DrawVehicleRow(Page.SpController);
            // A controller may name its own car (ctrl_get_vehicle, ABI v5). Said
            // here rather than left to be discovered on the grid: the Vehicle row
            // is right above, and a picker that is being overruled without a word
            // is worse than no picker at all.
            string ctlVehNote;
            ControllerVehiclePick(out ctlVehNote);
            if (!string.IsNullOrEmpty(ctlVehNote))
                GUILayout.Label("   " + ctlVehNote, GarageSkin.StatLabel);
            // Not DrawTrackRow: this screen picks from free roam's list, which is
            // the union of all three track sources. The race list deliberately
            // hides the maps with no finish line, and a map with no finish line is
            // exactly what a controller test wants — laps below default to "Free
            // drive". Its own index, so a pick here never moves the race page's.
            _ctlTrackIdx = CyclePicker("Map", _roamMaps, _ctlTrackIdx,
                                       m => m == "" ? "Classic Oval" : m);
            DrawOpponentsRows();
            _spLaps = MenuNav.Stepper("Race laps", _spLaps, 0, 50,
                v => v == 0 ? "Free drive" : $"{v} laps");
            DrawCountdownRow(true);

            // Not a choice here: a firmware session gets no assist floor either
            // way (TrackBootstrap skips HandlingFloor for it), so the row is shown
            // disabled rather than removed — removing it would change the control
            // census against the other setup screens for no gain.
            GUI.enabled = false;
            DrawHandlingRows();
            GUI.enabled = true;
            GUILayout.EndScrollView();

            GUILayout.Space(10);
            GUI.enabled = _ctlDlls.Count > 0;
            if (MenuButton("Run controller ▶")) StartController();
            GUI.enabled = true;
            GUILayout.Space(8);
            if (MenuButton("← Back")) BackFromSetup();
        }

        /// <summary>
        /// Open the HTML guide in the system browser. A file path, not a URL:
        /// Application.OpenURL needs a real file:// URI, and building one by hand
        /// fails on the first space in "EE Projects".
        /// </summary>
        private void OpenUserScriptGuide()
        {
            string path = AIHWSim.Build.UserScriptCatalog.GuidePath;
            if (path == null) return;
            try { Application.OpenURL(new System.Uri(path).AbsoluteUri); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Menu] Could not open the guide: {e.Message}");
            }
        }

        /// <summary>
        /// Start a race with the selected DLL driving. Nothing new is plumbed: the
        /// firmware slot and the per-car DLL name are both existing session
        /// concepts, so this only picks them.
        /// </summary>
        private void StartController()
        {
            _spControl = 1;                    // "Autonomous (firmware)" — see ControlFor
            _spArcade = false;                 // never on a controller-validation run
            _pendingControllerDll = SelectedControllerDll;

            var s = SettingsStore.Current;
            s.simControllerDll = _pendingControllerDll;

            string map = _roamMaps.Count > 0
                ? _roamMaps[Mathf.Clamp(_ctlTrackIdx, 0, _roamMaps.Count - 1)] : "";
            s.lastControllerMap = map;

            // The DLL's own choice of car, if it made one. Only this screen asks:
            // it is the one place where the controller is the thing under test and
            // the car is its subject. A race entered from any other page runs the
            // car that page picked, whatever DLL the design happens to name.
            string veh = ControllerVehiclePick(out string why);
            if (!string.IsNullOrEmpty(why)) Debug.Log("[ControllerVehicle] " + why);
            StartSinglePlayer(MatchMode.Race, map, veh);
        }

        /// <summary>DLL the Simulate Controller screen wants this car to load, or
        /// null for whatever the vehicle design already names. Consumed and cleared
        /// by <see cref="StartSinglePlayer"/>.</summary>
        private string _pendingControllerDll;

        /// <summary>One body for all three arena modes; they differ only in how the
        /// match is won.</summary>
        private void DrawSpArena(MatchMode mode)
        {
            GUILayout.Label(ModeNames[(int)mode].ToUpperInvariant(), GarageSkin.Header);
            GUILayout.Space(6);

            DrawVehicleRow(PageOf(mode));
            DrawTrackRow(mode);
            DrawOpponentsRows();
            DrawDrivingRow();

            if (mode == MatchMode.Derby)
                GUILayout.Label("   Last car still running wins.", GarageSkin.StatLabel);
            else
                _spScore = MenuNav.Stepper("Score to win", _spScore, 1, 15,
                    v => mode == MatchMode.Soccer ? $"{v} goals" : $"{v} captures");

            DrawCountdownRow(true);
            DrawHandlingRows();
            DrawSetupFooter($"{ModeNames[(int)mode]} ▶", mode);
        }

        /// <summary>
        /// Start a single-player session in <paramref name="mode"/>. The mode is a
        /// parameter rather than a field read: each setup screen passes its own
        /// literal, so what starts can never disagree with the page you started it
        /// from — and no handler has to read live page state, which
        /// <see cref="GoTo"/> is allowed to change mid-pass.
        /// </summary>
        /// <param name="trackOverride">A picker display name from a screen that
        /// keeps its own map pick — today only Simulate Controller. Non-null also
        /// suppresses the <c>lastTrack</c> write below, so that screen cannot
        /// overwrite the race page's saved circuit with a test track.</param>
        /// <param name="vehicleOverride">A picker vehicle name chosen by something
        /// other than the player — today only a controller DLL's
        /// <c>ctrl_get_vehicle()</c>. Like the track override it is deliberately
        /// NOT saved: the vehicle index is shared across every setup screen, and a
        /// controller that asks for a Baja must not leave the race page in one.</param>
        private void StartSinglePlayer(MatchMode mode, string trackOverride = null,
                                       string vehicleOverride = null)
        {
            bool roam = mode == MatchMode.FreeRoam;
            bool arena = mode != MatchMode.Race && !roam;
            string picked = _vehicles[_vehicleIdx];
            string vehicle = vehicleOverride ?? picked;
            string track = trackOverride ?? _tracks[_trackIdx];
            string roamMap = _roamMaps.Count > 0
                ? _roamMaps[Mathf.Clamp(_roamIdx, 0, _roamMaps.Count - 1)] : "";
            int bots = roam ? 0 : _spBots;
            var s = SettingsStore.Current;

            SessionConfig.SetSinglePlayer();                 // clears roster + rubber-band
            SessionConfig.TargetLaps = roam ? 0 : _spLaps;
            SessionConfig.RubberBand = bots > 0 && _spRubber;
            SessionConfig.CountdownSeconds = (bots > 0 || _spLaps > 0) && !roam ? _spCountdown : 0;
            SessionConfig.ResultsWaitSeconds = _spResultsWait;
            // Assigned AFTER SetSinglePlayer, which clears them.
            // Arcade is decided from the MODE here rather than by the arena and
            // free-roam screens clearing _spArcade on their way past: that flag is
            // shared with the split-screen and LAN-host pages, so merely visiting
            // a Soccer setup screen used to wipe their arcade toggle too.
            SessionConfig.Arcade = !roam && !arena
                && _spArcade && _spLaps > 0 && _spControl != 1;
            SessionConfig.TrackLimits = SessionConfig.Arcade && _spTrackLimits;
            SessionConfig.ArcadeHandling = _spArcadeHandling;
            SessionConfig.ArcadeTyreThermal = _spArcadeTyreThermal;
            GameFlow.ActiveDesign = ResolveVehicle(vehicle);
            // The Simulate Controller screen picks the DLL by name; TrackBootstrap
            // reads it straight off the design (through SafeDllName). ResolveVehicle
            // hands back a freshly built design every call, so writing here cannot
            // leak the choice into a later session.
            if (!string.IsNullOrEmpty(_pendingControllerDll))
            {
                GameFlow.ActiveDesign ??= VehicleDesign.Default();
                GameFlow.ActiveDesign.controllerDll = _pendingControllerDll;
                _pendingControllerDll = null;
            }
            // Free roam picks from its own list, which spans all three track
            // sources — so it goes through the same funnel as everything else
            // rather than resolving a preset by name the way it did when the town
            // was the only place it could go.
            SelectTrack(roam ? roamMap : track);

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
                control = ControlFor(_spControl),
            });
            for (int k = 1; k <= bots; k++)
                SessionConfig.Players.Add(MakeBotSlot(k, _spDiff));

            // After the roster exists: a team mode has to split it.
            ApplyMatchRules(mode);

            // The PICK, never the override — see the vehicleOverride doc above.
            s.lastVehicle = picked;
            if (trackOverride == null) s.lastTrack = track;
            s.lastRoamMap = roamMap;
            s.lastLaps = _spLaps;
            s.spBots = _spBots;
            s.spDifficulty = _spDiff;
            s.spControl = _spControl;
            s.spRubberBand = _spRubber;
            s.spCountdown = _spCountdown;
            s.spResultsWait = _spResultsWait;
            s.spArcade = _spArcade;
            s.spTrackLimits = _spTrackLimits;
            s.spArcadeHandling = _spArcadeHandling;
            s.spArcadeTyreThermal = _spArcadeTyreThermal;
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

        /// <summary>Build one AI opponent: a preset car with a distinct paint
        /// colour. The body moved to <see cref="SessionConfig.MakeBotSlot"/> when a
        /// second caller appeared (a scene asking for opponents on a direct Play);
        /// this stays as the name every start path here already calls.</summary>
        private static PlayerSlot MakeBotSlot(int k, int difficulty) =>
            SessionConfig.MakeBotSlot(k, difficulty);

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
            DrawTyreThermalRow();
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
            SessionConfig.ArcadeTyreThermal = _spArcadeTyreThermal;
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
            s.spArcadeTyreThermal = _spArcadeTyreThermal;
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
            DrawTyreThermalRow();

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
            SessionConfig.ArcadeTyreThermal = _spArcadeTyreThermal;
            s.spArcade = _spArcade;
            s.spTrackLimits = _spTrackLimits;
            s.spArcadeHandling = _spArcadeHandling;
            s.spArcadeTyreThermal = _spArcadeTyreThermal;
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
