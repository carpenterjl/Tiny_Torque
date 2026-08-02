using UnityEngine;

namespace AIHWSim.Core.Flight
{
    /// <summary>
    /// What a scene builder is allowed to assume about the world it is building
    /// into. The flight builders (<see cref="FlightTestEnvironment"/>,
    /// <see cref="DebugPlaneRig"/>) were written for Play mode, and exactly four
    /// of their habits are illegal or leaky at edit time:
    ///
    /// <list type="bullet">
    /// <item><c>Object.Destroy</c> throws "may not be called from edit mode" —
    /// stripping a primitive's collider needs <c>DestroyImmediate</c> there.</item>
    /// <item><c>renderer.material.color = c</c> instantiates a material per
    /// object; ~40 of those saved into a scene are ~40 dangling sub-assets.</item>
    /// <item><c>new PhysicsMaterial()</c> has no asset backing, so a saved
    /// collider's material reference serializes as nothing.</item>
    /// <item><c>new Material(Shader.Find("Skybox/..."))</c> — same problem, on
    /// the render settings.</item>
    /// </list>
    ///
    /// This struct carries the answer to each, and its <c>default</c> IS the
    /// runtime behaviour: every accessor falls through to the exact statement the
    /// builders contained before it existed. That is the proof obligation — the
    /// scripted flight tests run with <see cref="Runtime"/>, so their path must
    /// reduce to the old code by inspection, not by argument. Only the editor
    /// scene builder ever constructs a non-default one.
    /// </summary>
    public struct SceneBuildContext
    {
        /// <summary>Null → <c>Object.Destroy</c>. The editor passes
        /// <c>DestroyImmediate</c>.</summary>
        public System.Action<Object> destroy;

        /// <summary>Null → <c>r.material.color = c</c> (instanced, Play-only).
        /// The editor passes a delegate that assigns an asset-backed
        /// <c>sharedMaterial</c> keyed by colour.</summary>
        public System.Action<Renderer, Color> paint;

        /// <summary>Null → <see cref="Core.PhysicsTests.PhysicsTestEnvironment.Frictionless"/>
        /// (transient). The editor passes a saved <c>.physicsMaterial</c> asset
        /// with the same values.</summary>
        public PhysicsMaterial surface;

        /// <summary>Null → <c>new Material(Skybox/Procedural)</c>. The editor
        /// passes a saved material asset.</summary>
        public Material skybox;

        /// <summary>The Play-mode context: all nulls, all fall-throughs.</summary>
        public static SceneBuildContext Runtime => default;

        public void Destroy(Object o)
        {
            if (destroy != null) destroy(o);
            else Object.Destroy(o);
        }

        public void Paint(Renderer r, Color c)
        {
            if (paint != null) paint(r, c);
            else r.material.color = c;
        }
    }
}
