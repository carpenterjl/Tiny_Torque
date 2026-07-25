using AIHWSim.Core;
using AIHWSim.Garage;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Builds the track-builder scene at runtime: lighting, an orbit camera with
    /// a top-down map view (T), a live preview of the working
    /// <see cref="TrackDesign"/> built through <see cref="TrackFactory"/>, and the
    /// <see cref="TrackBuilderUI"/>. Owns the working design and the preview
    /// lifecycle; the UI mutates the design and asks for rebuilds/repaints. Drop
    /// on one empty GameObject (Tools ▸ AIHWSim ▸ Create Track Builder Scene).
    /// </summary>
    public sealed class TrackBuilderBootstrap : MonoBehaviour
    {
        public TrackDesign Design { get; private set; }
        public Camera Cam { get; private set; }
        public OrbitCamera Orbit { get; private set; }
        public BuiltTrack Built { get; private set; }
        public bool TopDown { get; private set; }

        private readonly DesignHistory<TrackDesign> _history = new DesignHistory<TrackDesign>();
        private float _rebuildAt = -1f;

        // Saved orbit pose for the top-down toggle.
        private float _savedYaw, _savedPitch, _savedDistance;

        public bool CanUndo => _history.CanUndo;
        public bool CanRedo => _history.CanRedo;

        private void Awake()
        {
            Design = GameFlow.ActiveTrack != null ? GameFlow.ActiveTrack.Clone() : TrackDesign.Default();
            Design.EnsureFloor();

            BuildLighting();
            BuildCamera();

            var uiGo = new GameObject("TrackBuilderUI");
            var ui = uiGo.AddComponent<TrackBuilderUI>();
            ui.bootstrap = this;

            RebuildAll();
            FrameMap();
        }

        private void BuildLighting()
        {
            RenderSettings.ambientLight = new Color(0.50f, 0.52f, 0.55f);
            if (FindFirstObjectByType<Light>() == null)
            {
                var lightGo = new GameObject("Directional Light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }
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
            Cam.backgroundColor = new Color(0.53f, 0.70f, 0.92f); // outdoor sky
            Cam.farClipPlane = 800f;
            Orbit = Cam.gameObject.GetComponent<OrbitCamera>() ?? Cam.gameObject.AddComponent<OrbitCamera>();
            Orbit.maxPitch = 89.5f;   // allow the straight-down map view
            Orbit.minDistance = 1f;
            Orbit.maxDistance = 60f;  // zoom is proportional — no speed override needed
            Orbit.yaw = 25f;
            Orbit.pitch = 55f;
        }

        /// <summary>Frame the whole map (initial view / F with nothing selected).</summary>
        public void FrameMap()
        {
            Orbit.pivot = new Vector3(0f, 0f, 0f);
            float span = Mathf.Max(Design.WorldWidth, Design.WorldLength);
            Orbit.FocusOn(Vector3.zero, Mathf.Clamp(span * 0.85f, 2.5f, Orbit.maxDistance));
        }

        /// <summary>Snap between the working 3/4 view and a straight-down map view.</summary>
        public void ToggleTopDown()
        {
            if (!TopDown)
            {
                _savedYaw = Orbit.yaw; _savedPitch = Orbit.pitch; _savedDistance = Orbit.distance;
                Orbit.yaw = 0f;
                Orbit.pitch = 89.5f;
                float span = Mathf.Max(Design.WorldWidth, Design.WorldLength);
                Orbit.distance = Mathf.Clamp(span * 0.8f, 2.5f, Orbit.maxDistance);
                Orbit.pivot = Vector3.zero;
            }
            else
            {
                Orbit.yaw = _savedYaw; Orbit.pitch = _savedPitch; Orbit.distance = _savedDistance;
            }
            TopDown = !TopDown;
        }

        // ---- design lifecycle ------------------------------------------------

        /// <summary>Replace the working design (from Load / New) and rebuild.</summary>
        public void SetDesign(TrackDesign d)
        {
            Design = d ?? TrackDesign.Default();
            Design.EnsureFloor();
            RebuildAll();
            FrameMap();
        }

        /// <summary>Queue a rebuild; coalesced so bursts of edits rebuild once.</summary>
        public void RequestRebuild() => _rebuildAt = Time.unscaledTime + 0.15f;

        /// <summary>Snapshot the current design (pre-mutation) for undo, under a change key.</summary>
        public void PushUndo(string changeKey) => _history.Push(Design, changeKey);

        /// <summary>Start a fresh coalesce window (e.g. between paint strokes).</summary>
        public void BreakUndoCoalescing() => _history.BreakCoalescing();

        public void ClearHistory() => _history.Clear();

        public bool TryUndo()
        {
            var d = _history.Undo(Design);
            if (d == null) return false;
            Design = d;
            RebuildAll();
            return true;
        }

        public bool TryRedo()
        {
            var d = _history.Redo(Design);
            if (d == null) return false;
            Design = d;
            RebuildAll();
            return true;
        }

        private void Update()
        {
            if (Core.InputReader.TopDownTogglePressed()) ToggleTopDown();

            if (_rebuildAt > 0f && Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = -1f;
                RebuildAll();
            }
        }

        // ---- preview ---------------------------------------------------------

        /// <summary>Bumped every rebuild so the UI can refresh per-object state.</summary>
        public int RebuildVersion { get; private set; }

        /// <summary>Tear down and rebuild the whole preview from the design.</summary>
        public void RebuildAll()
        {
            if (Built != null && Built.root != null) Destroy(Built.root);
            Built = TrackFactory.Build(Design, interactive: false);
            RebuildVersion++;
        }

        /// <summary>Swap one tile's material after a paint — no rebuild needed.</summary>
        public void RepaintTile(int tx, int tz)
        {
            if (Built?.tileRenderers == null || !Design.InBounds(tx, tz)) return;
            var r = Built.tileRenderers[tx, tz];
            if (r != null) r.sharedMaterial = TrackCatalog.Floors[Design.FloorAt(tx, tz)].Mat;
        }
    }
}
