using System.Collections.Generic;
using AIHWSim.Core;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// Free-play live prop placement: drive somewhere, pick a prop from the
    /// pause menu's PLACE PROPS page, and the ghost rides two metres ahead of
    /// the car until Interact stamps it down. Placements persist to the
    /// per-map layout file immediately and come back next time this map loads.
    /// Holding Interact next to a live-placed prop removes it (scene-authored
    /// and Studio-built props are not removable — they belong to the map).
    ///
    /// Built by TrackBootstrap only for a solo Free Roam: on LAN the layout is
    /// a per-machine file, and syncing map mutations is a bigger feature than
    /// this sensor-playground affordance justifies.
    /// </summary>
    public sealed class PropPlacer : MonoBehaviour
    {
        private const float PlaceAhead = 2f;
        private const float YawSnapDeg = 15f;
        private const float RemoveHoldSec = 0.6f;

        private Transform _propsRoot;
        private CarVehicle _car;
        private PropLayout _layout;
        private string _trackKey;
        private readonly List<MonoBehaviour> _live = new List<MonoBehaviour>();

        // Armed ghost state.
        private string _armedKind;
        private int _presetIndex;
        private GameObject _ghost;

        private float _removeHold;

        /// <summary>True while a ghost is up (the pause menu shows a hint).</summary>
        public bool Armed => _armedKind != null;

        public static PropPlacer Build(Transform propsRoot, CarVehicle localCar,
                                       PropLayout layout, List<MonoBehaviour> live,
                                       string trackKey)
        {
            var placer = new GameObject("PropPlacer").AddComponent<PropPlacer>();
            placer._propsRoot = propsRoot;
            placer._car = localCar;
            placer._layout = layout;
            placer._trackKey = trackKey;
            if (live != null) placer._live.AddRange(live);
            return placer;
        }

        /// <summary>Arm a placement ghost. kind: "speaker" | "mic" | "rf_beacon";
        /// presetIndex indexes SpeakerCatalog for speakers (ignored otherwise).</summary>
        public void Arm(string kind, int presetIndex)
        {
            Disarm();
            _armedKind = kind;
            _presetIndex = presetIndex;

            _ghost = new GameObject("PropGhost");
            switch (kind)
            {
                case "mic": PropRig.BuildMicSkin(_ghost.transform); break;
                case "rf_beacon": PropRig.BuildBeaconSkin(_ghost.transform); break;
                default: PropRig.BuildSpeakerSkin(_ghost.transform); break;
            }
            // See-through preview, and no collider — a ghost you can crash
            // into is a wall that follows you.
            foreach (var col in _ghost.GetComponentsInChildren<Collider>())
                Destroy(col);
            PartVisualFactory.ApplyMaterial(_ghost,
                PartVisualFactory.MakeGhostMat(new Color(0.4f, 0.9f, 1f, 0.45f)));
        }

        public void Disarm()
        {
            _armedKind = null;
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
        }

        private void Update()
        {
            if (_car == null || Time.timeScale <= 0f) return;

            if (Armed) UpdateGhost();
            else UpdateRemoval();
        }

        private (Vector3 pos, float yaw) PlacementPose()
        {
            Transform car = _car.transform;
            Vector3 pos = car.position + car.forward * PlaceAhead;
            // Drop onto whatever is underfoot so a prop lands ON a ramp, not in it.
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out var hit, 6f,
                    ~0, QueryTriggerInteraction.Ignore))
                pos = hit.point;
            float yaw = car.eulerAngles.y + 180f; // face back at the car
            yaw = Mathf.Round(yaw / YawSnapDeg) * YawSnapDeg;
            return (pos, yaw);
        }

        private void UpdateGhost()
        {
            var (pos, yaw) = PlacementPose();
            if (_ghost != null)
                _ghost.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

            if (!InputReader.InteractPressed()) return;

            var row = new PropPlacement
            {
                kind = _armedKind, x = pos.x, y = pos.y, z = pos.z, yawDeg = yaw,
            };
            if (_armedKind == "speaker")
            {
                var entry = SpeakerCatalog.Entries[
                    Mathf.Clamp(_presetIndex, 0, SpeakerCatalog.Entries.Length - 1)];
                row.clipKey = entry.clipKey;
                row.loudness = entry.loudness;
            }

            _layout.props.Add(row);
            PropLayoutStore.Save(_trackKey, _layout);

            var single = new PropLayout();
            single.props.Add(row);
            _live.AddRange(PropLayoutStore.Spawn(single, _propsRoot));
            Disarm();
        }

        private void UpdateRemoval()
        {
            // Hold Interact next to a live-placed prop to take it back.
            int near = -1;
            float bestSq = PropInteraction.InteractRadius * PropInteraction.InteractRadius;
            Vector3 carPos = _car.transform.position;
            for (int i = 0; i < _live.Count; i++)
            {
                if (_live[i] == null) continue;
                float sq = (_live[i].transform.position - carPos).sqrMagnitude;
                if (sq <= bestSq) { bestSq = sq; near = i; }
            }
            if (near < 0) { _removeHold = 0f; return; }

            bool held = KeyTable.Held(KeyBindings.Current.Key(DriveAction.Interact))
                     || PadTable.HeldAny(KeyBindings.Current.Pad(DriveAction.Interact));
            if (!held) { _removeHold = 0f; return; }

            _removeHold += Time.deltaTime;
            if (_removeHold < RemoveHoldSec) return;
            _removeHold = 0f;

            // Row index == live index: both lists are appended in lockstep and
            // pruned together.
            Destroy(_live[near].gameObject);
            _live.RemoveAt(near);
            if (near < _layout.props.Count) _layout.props.RemoveAt(near);
            PropLayoutStore.Save(_trackKey, _layout);
        }
    }
}
