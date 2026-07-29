using System;
using System.Collections.Generic;
using AIHWSim.Bridge;
using AIHWSim.Core;
using AIHWSim.Sensors;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Garage
{
    /// <summary>
    /// Builds the garage (vehicle assembly) scene at runtime: a showroom floor,
    /// soft lighting, an orbit camera, a frozen preview of the current
    /// <see cref="VehicleDesign"/> built through <see cref="VehicleFactory"/>, and
    /// the <see cref="GarageUI"/>. Owns the working design and the preview
    /// lifecycle; the UI mutates the design and asks for rebuilds. Drop this on
    /// one empty GameObject (Tools ▸ AIHWSim ▸ Create Garage Scene) and press Play.
    /// </summary>
    public sealed class GarageBootstrap : MonoBehaviour
    {
        public VehicleDesign Design { get; private set; }
        public Camera Cam { get; private set; }
        public OrbitCamera Orbit { get; private set; }
        public GameObject PreviewRoot { get; private set; }
        public CarVehicle PreviewCar { get; private set; }
        public SensorComponent[] PreviewSensors { get; private set; } = System.Array.Empty<SensorComponent>();
        public GameObject[] PreviewAero { get; private set; } = System.Array.Empty<GameObject>();
        public GameObject[] PreviewBatteries { get; private set; } = System.Array.Empty<GameObject>();
        public GameObject[] PreviewAntennas { get; private set; } = System.Array.Empty<GameObject>();
        public GameObject[] PreviewLights { get; private set; } = System.Array.Empty<GameObject>();

        /// <summary>Whether part heading/aim lines are drawn (toggled from the UI).</summary>
        public bool ShowAimVectors = true;

        private readonly List<PartMarker> _markers = new List<PartMarker>();
        private readonly DesignHistory<VehicleDesign> _history = new DesignHistory<VehicleDesign>();
        private PartType _selType = PartType.Sensor;
        private int _selIndex = -1;
        private float _rebuildAt = -1f;
        private Material _floorMat;

        public bool CanUndo => _history.CanUndo;
        public bool CanRedo => _history.CanRedo;

        private void Awake()
        {
            Design = GameFlow.ActiveDesign != null ? GameFlow.ActiveDesign.Clone() : VehicleDesign.Default();

            BuildLighting();
            BuildFloor();
            BuildCamera();

            var uiGo = new GameObject("GarageUI");
            var ui = uiGo.AddComponent<GarageUI>();
            ui.bootstrap = this;

            RebuildPreview();
        }

        private void BuildLighting()
        {
            RenderSettings.ambientLight = new Color(0.45f, 0.46f, 0.5f);
            if (FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.05f;
                light.transform.rotation = Quaternion.Euler(45f, 35f, 0f);
            }
        }

        private void BuildFloor()
        {
            _floorMat = new Material(Shader.Find("Standard")) { color = new Color(0.22f, 0.23f, 0.26f) };
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "ShowroomFloor";
            floor.transform.localScale = new Vector3(1f, 1f, 1f);
            floor.transform.position = new Vector3(0f, -0.078f, 0f); // ~ wheel contact
            floor.GetComponent<Renderer>().sharedMaterial = _floorMat;
        }

        private void BuildCamera()
        {
            Cam = Camera.main;
            if (Cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                Cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = new Color(0.12f, 0.13f, 0.16f);
            Cam.farClipPlane = 200f;
            Rendering.CameraBloom.Attach(Cam);
            Orbit = Cam.gameObject.GetComponent<OrbitCamera>() ?? Cam.gameObject.AddComponent<OrbitCamera>();
        }

        /// <summary>Replace the working design (from Load / New) and rebuild.</summary>
        public void SetDesign(VehicleDesign d)
        {
            Design = d ?? new VehicleDesign();
            _selIndex = -1;
            RebuildPreview();
        }

        /// <summary>Queue a preview rebuild; coalesced so slider drags rebuild once on release.</summary>
        public void RequestRebuild() => _rebuildAt = Time.unscaledTime + 0.15f;

        /// <summary>Snapshot the current design (pre-mutation) for undo, under a change key.</summary>
        public void PushUndo(string changeKey) => _history.Push(Design, changeKey);

        /// <summary>Forget all undo/redo history (on Load / New).</summary>
        public void ClearHistory() => _history.Clear();

        /// <summary>Restore the previous design state. Returns true if anything changed.</summary>
        public bool TryUndo()
        {
            var d = _history.Undo(Design);
            if (d == null) return false;
            Design = d; _selIndex = -1;
            RebuildPreview();
            return true;
        }

        public bool TryRedo()
        {
            var d = _history.Redo(Design);
            if (d == null) return false;
            Design = d; _selIndex = -1;
            RebuildPreview();
            return true;
        }

        private void Update()
        {
            if (_rebuildAt > 0f && Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = -1f;
                RebuildPreview();
            }
        }

        /// <summary>Tear down and rebuild the frozen preview vehicle from the design.</summary>
        public void RebuildPreview()
        {
            if (PreviewRoot != null) Destroy(PreviewRoot);
            _markers.Clear();

            var built = VehicleFactory.Build(Design, Vector3.zero, Quaternion.identity, previewKinematic: true);
            PreviewRoot = built.root;
            PreviewCar = built.car;
            PreviewSensors = built.sensors;
            PreviewAero = built.aeroVisuals ?? System.Array.Empty<GameObject>();
            PreviewBatteries = built.batteryVisuals ?? System.Array.Empty<GameObject>();
            PreviewAntennas = built.antennaVisuals ?? System.Array.Empty<GameObject>();
            PreviewLights = built.lightVisuals ?? System.Array.Empty<GameObject>();
            built.rig.Initialize(built.car, built.root.transform); // build cameras + bind sensors

            BuildMarkers(built.sensors);

            Orbit.pivot = new Vector3(0f, Design.bodySize.y * 0.5f, 0f);
            SetHighlight(_selType, _selIndex);
        }

        private void BuildMarkers(SensorComponent[] sensors)
        {
            // Wheel markers (at each wheel centre, with a heading line).
            for (int i = 0; i < PreviewCar.WheelCount; i++)
            {
                var t = PreviewCar.GetWheelTransform(i);
                if (t == null) continue;
                Color col = Design.wheels[i].powered ? new Color(1f, 0.5f, 0.3f) : new Color(0.8f, 0.8f, 0.85f);
                MakeMarker(t, PartType.Wheel, i, col, 0.05f);
            }

            // Sensor markers (with an aim line along the sensor's forward).
            for (int i = 0; i < sensors.Length; i++)
                MakeMarker(sensors[i].transform, PartType.Sensor, i, ColorFor(sensors[i].Type), 0.045f);

            // Aero-part markers.
            for (int i = 0; i < PreviewAero.Length; i++)
                if (PreviewAero[i] != null)
                    MakeMarker(PreviewAero[i].transform, PartType.Aero, i,
                        new Color(0.75f, 0.45f, 1f), 0.045f);

            // Battery markers.
            for (int i = 0; i < PreviewBatteries.Length; i++)
                if (PreviewBatteries[i] != null)
                    MakeMarker(PreviewBatteries[i].transform, PartType.Battery, i,
                        new Color(0.35f, 0.55f, 1f), 0.045f);

            // Antenna markers.
            for (int i = 0; i < PreviewAntennas.Length; i++)
                if (PreviewAntennas[i] != null)
                    MakeMarker(PreviewAntennas[i].transform, PartType.Antenna, i,
                        new Color(0.9f, 0.9f, 0.4f), 0.04f);

            // Light markers.
            for (int i = 0; i < PreviewLights.Length; i++)
                if (PreviewLights[i] != null)
                    MakeMarker(PreviewLights[i].transform, PartType.Light, i,
                        new Color(1f, 0.45f, 0.45f), 0.04f);
        }

        private void MakeMarker(Transform parent, PartType type, int index, Color col, float size)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "marker";
            marker.transform.SetParent(parent, false);
            marker.transform.localScale = Vector3.one * size;
            var mr = marker.GetComponent<Renderer>();
            mr.sharedMaterial = new Material(Shader.Find("Standard")) { color = col };

            var line = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(line.GetComponent<Collider>());
            line.name = "dir";
            line.transform.SetParent(parent, false);
            line.transform.localPosition = new Vector3(0f, 0f, 0.08f);
            line.transform.localScale = new Vector3(0.01f, 0.01f, 0.16f);
            var lineRend = line.GetComponent<Renderer>();
            lineRend.sharedMaterial = mr.sharedMaterial;
            lineRend.enabled = ShowAimVectors;

            var pm = marker.AddComponent<PartMarker>();
            pm.type = type; pm.index = index; pm.rend = mr; pm.dir = lineRend; pm.baseColor = col;
            _markers.Add(pm);
        }

        private static Color ColorFor(SensorType t)
        {
            switch (t)
            {
                case SensorType.Camera: return new Color(0.95f, 0.85f, 0.2f);
                case SensorType.Tof: return new Color(0.2f, 0.8f, 1f);
                case SensorType.Encoder: return new Color(0.6f, 0.9f, 0.4f);
                default: return Color.white;
            }
        }

        /// <summary>Show/hide all part heading/aim lines.</summary>
        public void SetAimVectorsVisible(bool on)
        {
            ShowAimVectors = on;
            foreach (var pm in _markers)
                if (pm != null && pm.dir != null) pm.dir.enabled = on;
        }

        /// <summary>
        /// Show/hide one part's geometry (its stylized viz + marker + aim line),
        /// used to hide a part while its drag ghost stands in for it.
        /// </summary>
        public void SetPartVisible(PartType type, int index, bool visible)
        {
            Transform t = null;
            if (type == PartType.Wheel && PreviewCar != null) t = PreviewCar.GetWheelVisual(index);
            else if (type == PartType.Sensor && PreviewSensors != null &&
                     index >= 0 && index < PreviewSensors.Length && PreviewSensors[index] != null)
                t = PreviewSensors[index].transform;
            else if (type == PartType.Aero && PreviewAero != null &&
                     index >= 0 && index < PreviewAero.Length && PreviewAero[index] != null)
                t = PreviewAero[index].transform;
            else if (type == PartType.Battery && PreviewBatteries != null &&
                     index >= 0 && index < PreviewBatteries.Length && PreviewBatteries[index] != null)
                t = PreviewBatteries[index].transform;
            else if (type == PartType.Antenna && PreviewAntennas != null &&
                     index >= 0 && index < PreviewAntennas.Length && PreviewAntennas[index] != null)
                t = PreviewAntennas[index].transform;
            else if (type == PartType.Light && PreviewLights != null &&
                     index >= 0 && index < PreviewLights.Length && PreviewLights[index] != null)
                t = PreviewLights[index].transform;

            if (t != null)
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;

            foreach (var pm in _markers)
            {
                if (pm == null || pm.type != type || pm.index != index) continue;
                if (pm.rend != null) pm.rend.enabled = visible;
                if (pm.dir != null) pm.dir.enabled = visible && ShowAimVectors;
            }
        }

        /// <summary>Highlight the selected part's marker (brightened + enlarged).</summary>
        public void SetHighlight(PartType type, int index)
        {
            _selType = type;
            _selIndex = index;
            foreach (var pm in _markers)
            {
                if (pm == null || pm.rend == null) continue;
                bool sel = pm.type == type && pm.index == index;
                float baseSize = pm.type == PartType.Wheel ? 0.05f : 0.045f;
                pm.transform.localScale = Vector3.one * (sel ? baseSize * 1.5f : baseSize);
                pm.rend.sharedMaterial.color = sel ? Color.Lerp(pm.baseColor, Color.white, 0.6f) : pm.baseColor;
            }
        }
    }
}
