using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Destroys the runtime-generated Mesh when its GameObject goes away.
    ///
    /// <b>Its own file, and that matters.</b> This lived inside
    /// <c>RibbonMeshBuilder.cs</c> until a ribbon was first baked at EDIT time and
    /// saved into a scene. Unity creates exactly one <c>MonoScript</c> asset per
    /// .cs file, named after the file, so a class that does not match its
    /// filename has no script asset to reference: the scene serialized
    /// <c>m_Script</c> as a stub embedded in the scene itself instead of
    /// <c>{fileID: 11500000, guid: …}</c>, and the component reloaded as a
    /// Missing Script — invisible to <c>GetComponentsInChildren</c>, and
    /// therefore unable to do the one job it has.
    ///
    /// It was harmless for as long as every ribbon was built at Play, because a
    /// scene built by <c>AddComponent</c> at runtime is never serialized. The
    /// same rule <c>TrackMarker</c> documents for the track markers applies here
    /// the moment Track Studio's bake became a thing an author saves.
    /// </summary>
    public sealed class RibbonMeshMarker : MonoBehaviour
    {
        public Mesh mesh;

        private void OnDestroy() { if (mesh != null) Destroy(mesh); }
    }
}
