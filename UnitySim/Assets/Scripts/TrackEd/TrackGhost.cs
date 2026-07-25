using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.TrackEd
{
    /// <summary>
    /// Translucent stand-in shown while placing or moving a track item (same
    /// pattern as the garage's PartGhost): built from the real catalog visuals,
    /// collider-stripped, moved to the ignore-raycast viz layer, tinted green
    /// (valid) or red (invalid).
    /// </summary>
    public sealed class TrackGhost
    {
        private static readonly Color ValidTint = new Color(0.4f, 1f, 0.55f, 0.45f);
        private static readonly Color InvalidTint = new Color(1f, 0.4f, 0.4f, 0.4f);

        public GameObject Root { get; private set; }
        public float Yaw;

        private Material _mat;
        private bool _valid = true;

        public static TrackGhost For(ItemDef def)
        {
            var g = new TrackGhost { Root = new GameObject($"ghost_{def.id}") };
            TrackFactory.BuildItemVisual(def, g.Root.transform);

            foreach (var col in g.Root.GetComponentsInChildren<Collider>(true))
                Object.Destroy(col);
            foreach (var t in g.Root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = PartVisualFactory.VizLayer;

            g._mat = PartVisualFactory.MakeGhostMat(ValidTint);
            PartVisualFactory.ApplyMaterial(g.Root, g._mat);
            return g;
        }

        public void SetPose(Vector3 pos, Quaternion rot) =>
            Root.transform.SetPositionAndRotation(pos, rot);

        public void SetValid(bool ok)
        {
            if (ok == _valid) return;
            _valid = ok;
            if (_mat != null) _mat.color = ok ? ValidTint : InvalidTint;
        }

        public void SetVisible(bool on)
        {
            if (Root != null && Root.activeSelf != on) Root.SetActive(on);
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root);
            Root = null;
        }
    }
}
