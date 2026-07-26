using AIHWSim.Track;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.Arcade
{
    /// <summary>
    /// Visuals for the arcade objects. Each builder tries the authored Blender
    /// mesh under <c>Resources/TrackProps/</c> first and falls back to runtime
    /// primitives — the same idiom the vehicle parts use, so arcade mode is fully
    /// playable before a single FBX ships.
    ///
    /// Nothing built here carries a collider: the item box, missile and banana own
    /// their own trigger volumes, sized in code so gameplay never depends on what
    /// an artist did to the mesh.
    /// </summary>
    public static class ArcadeVfx
    {
        private static Material _box, _glow, _banana, _missile, _fin, _shield;

        private static Material Mat(ref Material slot, Color c, float smooth, Color emission)
        {
            if (slot == null)
            {
                slot = TrackBuilder.StandardMat(c);
                slot.SetFloat("_Glossiness", smooth);
                if (emission.maxColorComponent > 0f)
                {
                    slot.EnableKeyword("_EMISSION");
                    slot.SetColor("_EmissionColor", emission);
                }
            }
            return slot;
        }

        private static Material BoxShell => Mat(ref _box, new Color(0.95f, 0.72f, 0.10f), 0.75f,
            new Color(0.45f, 0.30f, 0.02f));
        private static Material Glow => Mat(ref _glow, new Color(1f, 0.95f, 0.55f), 0.9f,
            new Color(1.0f, 0.80f, 0.25f));
        private static Material BananaSkin => Mat(ref _banana, new Color(0.96f, 0.86f, 0.16f), 0.5f, Color.black);
        private static Material MissileBody => Mat(ref _missile, new Color(0.85f, 0.22f, 0.16f), 0.6f,
            new Color(0.35f, 0.05f, 0.02f));
        private static Material MissileFin => Mat(ref _fin, new Color(0.30f, 0.31f, 0.34f), 0.6f, Color.black);
        private static Material ShieldSkin => Mat(ref _shield, new Color(0.35f, 0.80f, 1.0f), 0.9f,
            new Color(0.15f, 0.55f, 0.85f));

        /// <summary>Collider-free primitive piece on the parent's layer.</summary>
        private static Transform Piece(PrimitiveType type, Transform parent, Material mat,
            Vector3 pos, Vector3 euler, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            return go.transform;
        }

        private static GameObject TryMesh(string key, Transform parent, Material fallback,
            params (string token, Material mat)[] tokens)
        {
            var go = PartMeshLibrary.TryInstantiate(key, parent, parent.gameObject.layer,
                PartMeshLibrary.PropRoot);
            if (go != null && tokens.Length > 0) PartMeshLibrary.AssignByName(go, fallback, tokens);
            return go;
        }

        /// <summary>The floating power-up box (0.24 m, drivable through).</summary>
        public static void BuildItemBox(Transform parent)
        {
            if (TryMesh("arc_item_box", parent, BoxShell,
                    ("box", BoxShell), ("frame", BoxShell), ("glyph", Glow), ("core", Glow)) != null)
                return;

            Piece(PrimitiveType.Cube, parent, BoxShell, Vector3.zero, new Vector3(0f, 45f, 0f),
                new Vector3(0.22f, 0.22f, 0.22f));
            Piece(PrimitiveType.Sphere, parent, Glow, Vector3.zero, Vector3.zero,
                new Vector3(0.13f, 0.13f, 0.13f));
        }

        /// <summary>A dropped banana peel (~0.12 m across).</summary>
        public static void BuildBanana(Transform parent)
        {
            if (TryMesh("arc_banana", parent, BananaSkin,
                    ("peel", BananaSkin), ("inner", BananaSkin), ("stem", BananaSkin)) != null)
                return;

            Piece(PrimitiveType.Sphere, parent, BananaSkin, Vector3.zero, Vector3.zero,
                new Vector3(0.11f, 0.035f, 0.07f));
            for (int i = 0; i < 3; i++)
                Piece(PrimitiveType.Capsule, parent, BananaSkin,
                    new Vector3(0f, 0.012f, 0f), new Vector3(78f, i * 60f - 60f, 0f),
                    new Vector3(0.022f, 0.05f, 0.022f));
        }

        /// <summary>A homing missile (~0.16 m long, nose along +Z).</summary>
        public static void BuildMissile(Transform parent)
        {
            if (TryMesh("arc_missile", parent, MissileBody,
                    ("body", MissileBody), ("nose", MissileBody),
                    ("fin", MissileFin), ("nozzle", MissileFin)) != null)
                return;

            Piece(PrimitiveType.Capsule, parent, MissileBody, Vector3.zero, new Vector3(90f, 0f, 0f),
                new Vector3(0.045f, 0.065f, 0.045f));
            Piece(PrimitiveType.Sphere, parent, Glow, new Vector3(0f, 0f, -0.07f), Vector3.zero,
                new Vector3(0.05f, 0.05f, 0.05f));
            for (int i = 0; i < 3; i++)
                Piece(PrimitiveType.Cube, parent, MissileFin,
                    new Vector3(0f, 0f, -0.045f), new Vector3(0f, 0f, i * 120f),
                    new Vector3(0.006f, 0.075f, 0.03f));
        }

        /// <summary>One of the three orbs that make up an active shield.</summary>
        public static void BuildShieldOrb(Transform parent)
        {
            if (TryMesh("arc_shield_orb", parent, ShieldSkin, ("orb", ShieldSkin), ("gem", ShieldSkin)) != null)
                return;

            Piece(PrimitiveType.Sphere, parent, ShieldSkin, Vector3.zero, Vector3.zero,
                new Vector3(0.05f, 0.05f, 0.05f));
        }
    }
}
