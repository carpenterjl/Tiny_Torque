using System.Collections.Generic;
using System.IO;
using AIHWSim.Audio;
using AIHWSim.Core;
using AIHWSim.Props;
using AIHWSim.TrackEd;
using UnityEditor;
using UnityEngine;

namespace AIHWSim.EditorTools
{
    /// <summary>
    /// [PRP] — the world-prop gate: the Track Studio rows build cleanly, the
    /// append-only enums have not moved, the speaker catalog's clips resolve
    /// and loop cleanly, the per-map layout JSON round-trips with additive
    /// defaults, each prop class lives in a file named after itself (the
    /// scene-serialization rule), and Attach on an existing skin does not
    /// duplicate it.
    ///
    /// Batch: Unity.exe -batchmode -quit -projectPath &lt;UnitySim&gt;
    ///   -executeMethod AIHWSim.EditorTools.PropValidator.Report
    /// </summary>
    public static class PropValidator
    {
        private const string Tag = "[PRP]";

        private static readonly List<string> Fails = new List<string>();
        private static int _checks;

        [MenuItem("Tools/AIHWSim/Validate Props [PRP]", priority = 405)]
        public static void RunFromMenu() => Run(exitWhenDone: false);

        public static void Report() => Run(exitWhenDone: true);

        private static void Run(bool exitWhenDone)
        {
            Fails.Clear();
            _checks = 0;

            CheckCatalogRows();
            CheckAppendOnly();
            CheckSpeakerCatalog();
            CheckLayoutRoundTrip();
            CheckClassFiles();
            CheckAttachIdempotence();

            foreach (string f in Fails) Debug.LogError($"{Tag} FAIL {f}");
            string line = Fails.Count == 0
                ? $"{Tag} RESULT ALL PASS ({_checks} checks)"
                : $"{Tag} RESULT {Fails.Count} FAILED of {_checks} checks";
            if (Fails.Count == 0) Debug.Log(line); else Debug.LogError(line);

            if (exitWhenDone) EditorApplication.Exit(Fails.Count == 0 ? 0 : 1);
        }

        private static void True(string what, bool cond)
        {
            _checks++;
            if (!cond) Fails.Add(what);
        }

        private static readonly string[] PropIds =
            { "prop_speaker_loop", "prop_speaker_button", "prop_mic", "prop_rf_beacon" };

        private static void CheckCatalogRows()
        {
            foreach (string id in PropIds)
            {
                var def = TrackCatalog.Item(id);
                True($"catalog row '{id}' exists", def != null);
                if (def == null) continue;
                True($"'{id}' is themed Electronics", def.theme == TrackCatalog.Electronics);
                True($"'{id}' has a behavior (exempts it from batching)",
                    def.behavior != ItemBehavior.None);

                // The build must run clean on a temp root and leave a skin +
                // at least one collider (props are physical obstacles).
                var root = new GameObject("prp_probe");
                try
                {
                    def.build(root.transform);
                    True($"'{id}' build makes a skin child",
                        root.transform.Find("skin") != null);
                    True($"'{id}' build makes a collider",
                        root.GetComponentInChildren<Collider>() != null);
                }
                catch (System.Exception e)
                {
                    True($"'{id}' build threw: {e.Message}", false);
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }
        }

        private static void CheckAppendOnly()
        {
            // ItemBehavior ordinals 0-5 predate the props and must never move.
            True("ItemBehavior.None == 0", (int)ItemBehavior.None == 0);
            True("ItemBehavior.Finish == 1", (int)ItemBehavior.Finish == 1);
            True("ItemBehavior.Checkpoint == 2", (int)ItemBehavior.Checkpoint == 2);
            True("ItemBehavior.Light == 3", (int)ItemBehavior.Light == 3);
            True("ItemBehavior.Spawn == 4", (int)ItemBehavior.Spawn == 4);
            True("ItemBehavior.ItemBox == 5", (int)ItemBehavior.ItemBox == 5);
            True("ItemBehavior.Speaker == 6", (int)ItemBehavior.Speaker == 6);
            True("ItemBehavior.Microphone == 7", (int)ItemBehavior.Microphone == 7);
            True("ItemBehavior.RfBeacon == 8", (int)ItemBehavior.RfBeacon == 8);
            // The Interact action's ordinal is on disk in settings.json.
            True("DriveAction.Interact == 14", (int)DriveAction.Interact == 14);
        }

        private static void CheckSpeakerCatalog()
        {
            True("speaker catalog is non-empty", SpeakerCatalog.Entries.Length > 0);
            foreach (var e in SpeakerCatalog.Entries)
            {
                var clip = ProceduralAudio.Get(e.clipKey);
                True($"speaker clip '{e.clipKey}' resolves", clip != null);
                True($"speaker entry '{e.clipKey}' has a tone", e.toneHz > 0f);
                if (clip == null) continue;

                // Loop-clean: first and last samples must nearly meet, or the
                // loop clicks once per cycle. Asserted only for the pure tones
                // this feature added — the horn loops predate it and handle
                // their seams their own way (LoopFade), and a siren SWEEP has
                // no sample-level seam to measure.
                if (!e.clipKey.StartsWith("tone_")) continue;
                var data = new float[clip.samples];
                clip.GetData(data, 0);
                if (data.Length > 1)
                    True($"speaker clip '{e.clipKey}' is loop-clean",
                        Mathf.Abs(data[0] - data[data.Length - 1]) < 0.15f);
            }
            True("unknown clip key falls back, never null",
                ProceduralAudio.Get(SpeakerCatalog.Find("no_such_key").clipKey) != null);
        }

        private static void CheckLayoutRoundTrip()
        {
            var layout = new PropLayout();
            layout.props.Add(new PropPlacement
            {
                kind = "speaker", x = 1.25f, z = -3.5f, yawDeg = 90f,
                mode = (int)SpeakerMode.Interact, clipKey = "tone_b",
                loudness = 1.4f, startOn = false,
            });
            layout.props.Add(new PropPlacement { kind = "rf_beacon", beaconId = 4, txPowerDbm = -3f });

            var back = JsonUtility.FromJson<PropLayout>(JsonUtility.ToJson(layout));
            True("layout round-trips row count", back.props.Count == 2);
            True("layout round-trips speaker fields",
                back.props[0].mode == (int)SpeakerMode.Interact
                && back.props[0].clipKey == "tone_b"
                && Mathf.Approximately(back.props[0].loudness, 1.4f)
                && !back.props[0].startOn);
            True("layout round-trips beacon fields",
                back.props[1].beaconId == 4
                && Mathf.Approximately(back.props[1].txPowerDbm, -3f));

            // Additive migration: a fragment missing every new field keeps the
            // initializers, the same contract every save file in the project has.
            var legacy = JsonUtility.FromJson<PropPlacement>("{\"kind\":\"speaker\",\"x\":2.0}");
            True("missing fields keep their defaults",
                legacy.clipKey == SpeakerCatalog.DefaultKey
                && legacy.loudness == 1f && legacy.startOn
                && legacy.mode == (int)SpeakerMode.Loop);
        }

        private static void CheckClassFiles()
        {
            // A MonoBehaviour authored into a saved scene whose filename does
            // not match its class reloads as a Missing Script.
            foreach (string cls in new[] { "SpeakerProp", "WorldMicProp", "RfBeaconProp" })
                True($"{cls}.cs exists under Scripts/Props",
                    File.Exists(Path.Combine(Application.dataPath, "Scripts", "Props", cls + ".cs")));
        }

        private static void CheckAttachIdempotence()
        {
            var root = new GameObject("prp_attach_probe");
            try
            {
                PropRig.BuildSpeakerSkin(root.transform);
                SpeakerProp.Attach(root, new SpeakerConfig());
                // Attach must adopt the existing skin, not stack a second one
                // (Awake's build-if-missing is gated on Find("skin")).
                int skins = 0;
                foreach (Transform child in root.transform)
                    if (child.name == "skin") skins++;
                True("Attach adopts an existing skin", skins == 1);
                True("Attach is component-idempotent",
                    ReferenceEquals(SpeakerProp.Attach(root, null),
                                    root.GetComponent<SpeakerProp>()));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
