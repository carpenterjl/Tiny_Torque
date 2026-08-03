using UnityEngine;

namespace AIHWSim.Core.Boot
{
    /// <summary>
    /// The three blocks every scene bootstrap in this project had its own copy
    /// of: find-or-make the main camera, put a sun in an unlit scene, and hang a
    /// chase camera off a car.
    ///
    /// <b>These are relocations, not designs.</b> Each method is the body of the
    /// copies it replaces, character for character, with the one thing that
    /// genuinely differed between them passed in. Nothing was generalised on the
    /// way past: where two copies disagreed about far plane, clear flags,
    /// ambient colour or viewport rect, that line stayed at its call site,
    /// because those differences are decisions the scenes made on purpose and
    /// folding them into a parameter list is how a helper starts lying about
    /// what its callers do.
    ///
    /// The reason to have this at all is not line count. It is that a new
    /// setting — an HDR flag, a culling mask, a second audio listener rule —
    /// now has one place to be added, instead of eleven places to be added in
    /// and one to be forgotten.
    ///
    /// <b>Deliberately not here:</b> the flight builders
    /// (<c>FlightTestEnvironment</c>), whose lighting has a tail that runs only
    /// when it created the sun and which is watched by a gate asserting
    /// bit-identical aerodynamics — not worth spending that budget on a
    /// five-line move. (<see cref="BuildLighting"/> returns the light it made
    /// precisely so that case can join later without changing shape.)
    ///
    /// The LAN <b>camera</b> is a caller, because a networked car renders the
    /// same way a local one does; the LAN <b>rig</b> is not, because what a LAN
    /// session decides differently is which car this machine simulates, and that
    /// wants a two-machine test watching it.
    ///
    /// The preview rigs (<c>PartIconFactory</c>, <c>PartPreviewRig</c>) are not
    /// candidates at all — their lights carry <c>hideFlags</c> and a culling
    /// mask, and they are lighting a render texture rather than a scene.
    /// </summary>
    public static class SceneRig
    {
        /// <summary>
        /// The scene's main camera, made if the scene does not already have one.
        ///
        /// The <c>AudioListener</c> rides along because Unity permits exactly
        /// one and this is the camera that should own it — every call site that
        /// made a camera made a listener with it, and the split-screen path's
        /// second and third cameras are built by hand precisely so they do NOT
        /// get one.
        ///
        /// Callers configure what they care about afterwards (far plane, clear
        /// flags, ambience, viewport) — see the class note for why that was not
        /// absorbed.
        /// </summary>
        public static Camera CameraOrCreate()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            return cam;
        }

        /// <summary>
        /// A directional key light, unless the scene already has any light at
        /// all.
        ///
        /// <b>The guard is "any Light", not "any directional light"</b>, and
        /// that is the original behaviour rather than an oversight: a
        /// hand-authored scene that has bothered to place lighting is a scene
        /// whose author has an opinion, and adding a 1.1-intensity sun over the
        /// top of it is the wrong kind of help. <c>MapAmbience.ApplyKeyLight</c>
        /// then retunes whatever sun exists, authored or made here.
        ///
        /// Returns the light it created, or <b>null when the scene already had
        /// one</b> — which is how a caller with follow-up work that belongs only
        /// to a freshly built sun (the flight environment's
        /// <c>ambientIntensity</c>) can still tell the two cases apart.
        /// </summary>
        public static Light BuildLighting(float intensity, Vector3 euler)
        {
            if (Object.FindFirstObjectByType<Light>() != null) return null;
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.transform.rotation = Quaternion.Euler(euler);
            return light;
        }

        /// <summary>
        /// The driving chase camera: 1.1 m up and 2.2 m behind, lerping at 5.
        ///
        /// <b>Reused rather than re-added</b> (<c>GetComponent ?? AddComponent</c>)
        /// because <c>Camera.main</c> may already be carrying one from a
        /// previous rig in the same scene — the split-screen path builds P1's
        /// camera and then the race path can reach the same object again.
        ///
        /// The look-back binding is here for the reason the original comment
        /// gave and it is worth keeping: the camera already knows which car it
        /// follows, and in split-screen that pairing is the only thing that gets
        /// the key press onto the right viewport.
        ///
        /// The offset is NOT a parameter. The four other <c>ChaseCamera</c>
        /// sites in the project (physics tests, flight tests, the debug spawner)
        /// use their own framing and bind no look-back key; they are a different
        /// camera that happens to share a component, and passing their numbers
        /// through here would only put four unrelated framings behind one name.
        /// </summary>
        public static ChaseCamera AttachChase(Camera cam, Transform target)
        {
            var follow = cam.gameObject.GetComponent<ChaseCamera>()
                         ?? cam.gameObject.AddComponent<ChaseCamera>();
            follow.target = target;
            follow.offset = new Vector3(0f, 1.1f, -2.2f);
            follow.followLerp = 5f;
            var lookBackOwner = target != null ? target.GetComponent<CarInput>() : null;
            if (lookBackOwner != null) lookBackOwner.chase = follow;
            return follow;
        }
    }
}
