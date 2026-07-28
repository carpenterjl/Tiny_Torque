using System.Collections.Generic;
using AIHWSim.Audio;
using AIHWSim.Core;
using AIHWSim.Garage;
using AIHWSim.Persistence;
using UnityEngine;

namespace AIHWSim.UI
{
    /// <summary>
    /// The crate room: pick an earned crate, open it, and watch the pulls land
    /// one at a time on the turntable.
    ///
    /// This is where the theatre lives, deliberately away from the results
    /// screen. A three-pull Gold Vault takes as long as it takes, and nobody
    /// should be sitting through it while the next race waits.
    ///
    /// The whole opening happens in ONE call to <see cref="CrateSystem.Open"/>:
    /// the profile is written and saved up front, and the reveal walks a list
    /// that already exists. Quitting mid-animation therefore cannot lose a
    /// pull — the alternative, rolling per reveal, would make Alt-F4 a way to
    /// re-roll a legendary.
    /// </summary>
    public sealed class CrateOpenUI
    {
        private const float RevealSeconds = 1.15f;

        private CrateRig _rig;
        private int _idx;                       // selected crate type
        private List<CratePull> _pulls;
        private int _shown = -1;                // index of the pull on the turntable
        private float _revealAt;
        private string _note = "";

        public const int ResultNone = 0;
        public const int ResultBack = 1;

        public void Enter()
        {
            _rig = CrateRig.Create();
            _pulls = null;
            _shown = -1;
            SelectFirstOwned();
            ShowCrate();
        }

        public void Exit()
        {
            _rig?.Dispose();
            _rig = null;
            _pulls = null;
        }

        /// <summary>Per-frame input outside OnGUI: stick/mouse spin.</summary>
        public void Tick()
        {
            if (_rig == null) return;
            float spin = InputReader.RightStick().x;
            if (Mathf.Abs(spin) > 0.15f) _rig.Spin(spin * 260f * Time.unscaledDeltaTime * 60f * 0.02f);
            if (InputReader.RightMouseHeld())
                _rig.Spin(-InputReader.MouseDelta().x * 0.4f);

            // Advance the reveal on the clock. Doing it here rather than in
            // OnGUI keeps the control count stable across a Layout/Repaint pair.
            if (_pulls != null && _shown >= 0 && _shown < _pulls.Count - 1 &&
                Time.unscaledTime >= _revealAt)
                RevealNext();
        }

        private void SelectFirstOwned()
        {
            var crates = CosmeticCatalog.Crates;
            for (int i = 0; i < crates.Length; i++)
                if (Progression.CrateCount(crates[i].id) > 0) { _idx = i; return; }
            _idx = 0;
        }

        private CrateDef Selected =>
            CosmeticCatalog.Crates[Mathf.Clamp(_idx, 0, CosmeticCatalog.Crates.Length - 1)];

        private void ShowCrate()
        {
            _pulls = null;
            _shown = -1;
            _rig?.Show(Selected.id);
        }

        private void OpenSelected()
        {
            var def = Selected;
            var pulls = CrateSystem.Open(def.id);
            if (pulls == null || pulls.Count == 0)
            {
                _note = "No " + def.label + " to open.";
                SfxPlayer.Ensure()?.PlayUi(ProceduralAudio.UiDeny);
                return;
            }
            _pulls = pulls;
            _shown = -1;
            _note = "";
            RevealNext();
        }

        private void RevealNext()
        {
            if (_pulls == null || _shown >= _pulls.Count - 1) return;
            _shown++;
            _revealAt = Time.unscaledTime + RevealSeconds;

            var pull = _pulls[_shown];
            _rig?.Show(pull.item.kind == UnlockKind.Cosmetic ? pull.item.id : Selected.id);
            _rig?.Punch();
            SfxPlayer.Ensure()?.PlayUi(pull.duplicate
                ? ProceduralAudio.UiLevelUp
                : ProceduralAudio.UiUnlock);
        }

        public int Draw()
        {
            int result = ResultNone;
            float h = UIScale.H - 110f;

            // ---- left: the crates you hold ----
            GUILayout.BeginArea(new Rect(10f, 54f, 250f, h), GUI.skin.box);
            GUILayout.Label("CRATES", GarageSkin.Title);
            GUILayout.Label($"Scrap: {Progression.Scrap}", GarageSkin.Header);
            GUILayout.Space(4);

            var crates = CosmeticCatalog.Crates;
            for (int i = 0; i < crates.Length; i++)
            {
                int have = Progression.CrateCount(crates[i].id);
                string label = (i == _idx ? "▸ " : "") + $"{crates[i].label}  ×{have}";
                if (MenuNav.Button(label) && i != _idx)
                {
                    _idx = i;
                    _note = "";
                    ShowCrate();
                }
            }

            GUILayout.Space(6);
            var def = Selected;
            GUILayout.Label(def.description, GarageSkin.StatLabel);
            GUILayout.Label($"Earned by: {def.earn}", GarageSkin.StatLabel);
            GUILayout.Label($"{def.pulls} pull{(def.pulls == 1 ? "" : "s")}" +
                            (def.floor.HasValue
                                ? $" · at least one {CosmeticCatalog.RarityLabel(def.floor.Value)}"
                                : ""),
                            GarageSkin.StatLabel);
            GUILayout.EndArea();

            // ---- right: odds + the pull log ----
            GUILayout.BeginArea(new Rect(UIScale.W - 270f, 54f, 260f, h), GUI.skin.box);
            GUILayout.Label("ODDS", GarageSkin.Header);
            var odds = CrateSystem.TierOdds(def);
            var sizes = CrateSystem.PoolSizes(def);
            for (int t = 0; t < odds.Length; t++)
            {
                if (sizes[t] == 0) continue;
                var col = CosmeticCatalog.RarityColor((Rarity)t);
                var st = new GUIStyle(GarageSkin.StatLabel) { normal = { textColor = col } };
                GUILayout.Label($"{CosmeticCatalog.RarityLabel((Rarity)t),-10} {odds[t] * 100f,5:0.0}%   " +
                                $"({sizes[t]} items)", st);
            }
            GUILayout.Label($"Pity: an Epic or better within {def.pity} pulls\n" +
                            $"({Progression.PityOf(def.id)} since the last one)",
                            GarageSkin.StatLabel);

            if (_pulls != null)
            {
                GUILayout.Space(8);
                GUILayout.Label("THIS CRATE", GarageSkin.Header);
                for (int i = 0; i <= _shown && i < _pulls.Count; i++)
                {
                    var p = _pulls[i];
                    var st = new GUIStyle(GarageSkin.StatLabel)
                        { normal = { textColor = CosmeticCatalog.RarityColor(p.item.rarity) } };
                    GUILayout.Label($"{p.item.display}" +
                                    (p.duplicate ? $"   (dupe +{p.scrap})" : "   NEW!"), st);
                }
            }
            GUILayout.EndArea();

            // ---- centre: what just came out ----
            if (_pulls != null && _shown >= 0 && _shown < _pulls.Count)
            {
                var p = _pulls[_shown];
                var box = new Rect((UIScale.W - 420f) * 0.5f, UIScale.H - 150f, 420f, 60f);
                var st = new GUIStyle(GarageSkin.Title)
                {
                    alignment = TextAnchor.MiddleCenter, fontSize = 20,
                    normal = { textColor = CosmeticCatalog.RarityColor(p.item.rarity) },
                };
                GUI.Label(box, p.duplicate
                    ? $"{p.item.display}\nDuplicate — +{p.scrap} scrap"
                    : $"{p.item.display}\n{CosmeticCatalog.RarityLabel(p.item.rarity)}" +
                      (p.pityForced ? " (pity)" : ""), st);
            }

            // ---- bottom bar ----
            var bottom = new Rect((UIScale.W - 520f) * 0.5f, UIScale.H - 48f, 520f, 40f);
            GUILayout.BeginArea(bottom, GUI.skin.box);
            GUILayout.BeginHorizontal();
            if (MenuNav.Button("← Back", GUILayout.Width(90f), GUILayout.Height(26f)))
                result = ResultBack;

            bool busy = _pulls != null && _shown < _pulls.Count - 1;
            GUI.enabled = !busy && Progression.CrateCount(def.id) > 0;
            if (MenuNav.Button($"Open {def.label} ▶", GUILayout.Width(190f), GUILayout.Height(26f)))
                OpenSelected();
            GUI.enabled = true;

            GUILayout.Label(string.IsNullOrEmpty(_note)
                ? "Right stick / RMB-drag spins"
                : _note, GarageSkin.StatLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            return result;
        }
    }
}
