using AIHWSim.Track;
using UnityEngine;

namespace AIHWSim.Props
{
    /// <summary>
    /// Shared plumbing for the world props (speaker / microphone / RF beacon):
    /// primitive skins, and the position-hash identity that lets LAN peers
    /// agree on which prop is which with zero sync traffic — scene and catalog
    /// props are built identically on every machine, so a hash of the world
    /// position IS a shared id (the BillboardPoster trick).
    ///
    /// Skins follow the TrackCatalog contract: geometry built under a parent
    /// assumed to be at the origin, with ONE solid collider so props are
    /// physical obstacles. No triggers, no rigidbodies — proximity is polled,
    /// not collided (Flag.cs's reasons). The local piece helper exists instead
    /// of TrackBuilder because these builds also run at EDIT time (the [PRP]
    /// gate), where Object.Destroy on the unwanted collider is an error and
    /// DestroyImmediate is required.
    /// </summary>
    public static class PropRig
    {
        public const byte KindSpeaker = 0;
        public const byte KindMic = 1;
        public const byte KindBeacon = 2;

        /// <summary>Deterministic prop identity: world position quantized to
        /// 5 cm, hashed with the kind byte.</summary>
        public static int PropId(Vector3 pos, byte kind)
        {
            int x = Mathf.RoundToInt(pos.x * 20f);
            int y = Mathf.RoundToInt(pos.y * 20f);
            int z = Mathf.RoundToInt(pos.z * 20f);
            unchecked
            {
                int h = 17;
                h = h * 31 + x;
                h = h * 31 + y;
                h = h * 31 + z;
                h = h * 31 + kind;
                return h;
            }
        }

        /// <summary>True if a "skin" child already exists (built by a catalog
        /// ItemDef or a previous Attach) — the signal not to build again.</summary>
        public static Transform ExistingSkin(Transform root) => root.Find("skin");

        private static Transform SkinRoot(Transform parent)
        {
            var skin = new GameObject("skin").transform;
            skin.SetParent(parent, false);
            return skin;
        }

        private static GameObject Piece(string name, PrimitiveType type, Transform parent,
            Vector3 localPos, Vector3 scale, Quaternion rot, Material mat, bool collider)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (!collider)
            {
                var col = go.GetComponent<Collider>();
                if (Application.isPlaying) Object.Destroy(col);
                else Object.DestroyImmediate(col);
            }
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Speaker cabinet: dark box, grille discs, emissive power dot.</summary>
        public static Transform BuildSpeakerSkin(Transform parent)
        {
            var skin = SkinRoot(parent);
            var box = TrackBuilder.StandardMat(new Color(0.13f, 0.13f, 0.15f));
            var grille = TrackBuilder.StandardMat(new Color(0.30f, 0.30f, 0.33f));
            var dot = TrackBuilder.StandardMat(new Color(0.2f, 0.9f, 0.4f));
            dot.EnableKeyword("_EMISSION");
            dot.SetColor("_EmissionColor", new Color(0.1f, 0.6f, 0.25f));

            Piece("cab", PrimitiveType.Cube, skin, new Vector3(0f, 0.11f, 0f),
                new Vector3(0.14f, 0.22f, 0.12f), Quaternion.identity, box, collider: true);
            Piece("cone_lo", PrimitiveType.Cylinder, skin, new Vector3(0f, 0.075f, 0.061f),
                new Vector3(0.10f, 0.004f, 0.10f), Quaternion.Euler(90f, 0f, 0f),
                grille, collider: false);
            Piece("cone_hi", PrimitiveType.Cylinder, skin, new Vector3(0f, 0.165f, 0.061f),
                new Vector3(0.055f, 0.004f, 0.055f), Quaternion.Euler(90f, 0f, 0f),
                grille, collider: false);
            Piece("power", PrimitiveType.Cube, skin, new Vector3(0.05f, 0.02f, 0.061f),
                new Vector3(0.012f, 0.012f, 0.004f), Quaternion.identity, dot, collider: false);
            return skin;
        }

        /// <summary>Microphone: base puck, stand, windscreen cube.</summary>
        public static Transform BuildMicSkin(Transform parent)
        {
            var skin = SkinRoot(parent);
            var dark = TrackBuilder.StandardMat(new Color(0.16f, 0.16f, 0.18f));
            var steel = TrackBuilder.StandardMat(new Color(0.60f, 0.62f, 0.66f));
            var foam = TrackBuilder.StandardMat(new Color(0.35f, 0.37f, 0.42f));

            Piece("base", PrimitiveType.Cylinder, skin, new Vector3(0f, 0.01f, 0f),
                new Vector3(0.10f, 0.01f, 0.10f), Quaternion.identity, dark, collider: true);
            Piece("stand", PrimitiveType.Cylinder, skin, new Vector3(0f, 0.10f, 0f),
                new Vector3(0.012f, 0.09f, 0.012f), Quaternion.identity, steel, collider: false);
            Piece("wind", PrimitiveType.Cube, skin, new Vector3(0f, 0.21f, 0f),
                new Vector3(0.05f, 0.05f, 0.05f), Quaternion.Euler(0f, 45f, 0f), foam,
                collider: false);
            return skin;
        }

        /// <summary>RF beacon: base box, mast, emissive tip lamp (named "lamp"
        /// — RfBeaconProp drives its colour with the enabled state).</summary>
        public static Transform BuildBeaconSkin(Transform parent)
        {
            var skin = SkinRoot(parent);
            var box = TrackBuilder.StandardMat(new Color(0.20f, 0.21f, 0.24f));
            var mast = TrackBuilder.StandardMat(new Color(0.55f, 0.57f, 0.61f));
            var lamp = TrackBuilder.StandardMat(new Color(0.3f, 1f, 0.45f));
            lamp.EnableKeyword("_EMISSION");
            lamp.SetColor("_EmissionColor", new Color(0.15f, 0.8f, 0.3f));

            Piece("base", PrimitiveType.Cube, skin, new Vector3(0f, 0.04f, 0f),
                new Vector3(0.12f, 0.08f, 0.12f), Quaternion.identity, box, collider: true);
            Piece("mast", PrimitiveType.Cylinder, skin, new Vector3(0f, 0.20f, 0f),
                new Vector3(0.010f, 0.13f, 0.010f), Quaternion.identity, mast, collider: false);
            Piece("lamp", PrimitiveType.Cube, skin, new Vector3(0f, 0.345f, 0f),
                new Vector3(0.03f, 0.03f, 0.03f), Quaternion.Euler(0f, 45f, 0f), lamp,
                collider: false);
            return skin;
        }
    }
}
