using System.Collections.Generic;
using AIHWSim.Garage;
using AIHWSim.Vehicles;
using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>
    /// Everything the studio can bolt onto a vehicle, and the one place that knows
    /// how to build any of it.
    ///
    /// <b>Four sources, one key space.</b> Cosmetics come from
    /// <c>CosmeticCatalog</c>, shell features are harvested by
    /// <see cref="ShellFeatureSource"/>, aero/antenna/light/battery come out of
    /// <c>PartVisualFactory</c>'s procedural builders, and wheels come from
    /// <c>WheelCatalog</c>. Nothing here duplicates any of them: the catalogue is a
    /// list of <see cref="PartSource"/> keys and the builder is a switch that
    /// forwards to the code the game already ships. That is what keeps a spoiler in
    /// the studio identical to the same spoiler on a race car.
    ///
    /// <b>Every category is enumerated from data.</b> Not one entry is typed out
    /// here — the shell rows come from probing each FBX, the cosmetics from the
    /// pack, the wheels from the catalogue — so a body or a cosmetic that Asset
    /// Studio commits reaches this palette with no edit at all.
    /// </summary>
    public static class StudioPartLibrary
    {
        /// <summary>One palette row.</summary>
        public struct Entry
        {
            public string source;    // PartSource key
            public string label;
            public string desc;      // the hover tooltip's one-liner
            public int triangles;    // 0 where it is not meaningful (procedural parts)
        }

        public struct Category
        {
            public string title;
            public Entry[] items;
        }

        private static Category[] _cats;

        /// <summary>Forget the palette. Sibling of the catalogue resets — a body
        /// or cosmetic committed by Asset Studio adds rows here.</summary>
        public static void ResetCache()
        {
            _cats = null;
            ShellFeatureSource.ResetCache();
        }

        /// <summary>
        /// The palette, grouped. Built once: probing thirteen shells for their
        /// features means instantiating thirteen prefabs, which is cheap enough
        /// once and not cheap enough per frame.
        /// </summary>
        public static IReadOnlyList<Category> Categories() => _cats ??= Compose();

        private static Category[] Compose()
        {
            var cats = new List<Category>();

            // ---- procedural parts the game already builds ----
            var aero = new List<Entry>();
            foreach (AeroKind k in System.Enum.GetValues(typeof(AeroKind)))
                aero.Add(new Entry
                {
                    source = PartSource.Aero(k), label = AeroLabel(k), desc = AeroDesc(k),
                });
            cats.Add(new Category { title = "AERO", items = aero.ToArray() });

            var misc = new List<Entry>
            {
                new Entry { source = PartSource.Light(0), label = "Light bar",
                            desc = "Roof light bar — six lenses, red and blue, strobing." },
                new Entry { source = PartSource.Light(1), label = "Light pods",
                            desc = "Off-road pod cluster — four lamps, steady glow." },
                new Entry { source = PartSource.Battery(), label = "Battery",
                            desc = "The 2S pack visual. Decoration here: mass lives on the design." },
            };
            for (int s = 0; s < 4; s++)
                misc.Add(new Entry
                {
                    source = PartSource.Antenna(s), label = AntennaLabel(s),
                    desc = "Aerial — cosmetic, mirrors like any part.",
                });
            cats.Add(new Category { title = "FITTINGS", items = misc.ToArray() });

            // ---- wheels worn as decoration ----
            var wheels = new List<Entry>();
            foreach (WheelDef w in WheelCatalog.All)
            {
                if (w == null || w.debugOnly || string.IsNullOrEmpty(w.meshKey)) continue;
                wheels.Add(new Entry
                {
                    source = PartSource.Wheel(w.id), label = w.label,
                    desc = "A spare, a trophy, a bonnet decoration. The wheels the car " +
                           "drives on are set on the design, not here.",
                });
            }
            if (wheels.Count > 0)
                cats.Add(new Category { title = "WHEELS", items = wheels.ToArray() });

            // ---- the cosmetics pack ----
            foreach (CosmeticSlot slot in System.Enum.GetValues(typeof(CosmeticSlot)))
            {
                var items = new List<Entry>();
                foreach (CosmeticItem c in CosmeticCatalog.InSlot(slot))
                    items.Add(new Entry
                    {
                        source = PartSource.Cosmetic(c.id), label = c.label,
                        desc = c.description,
                    });
                if (items.Count > 0)
                    cats.Add(new Category
                    {
                        title = CosmeticCatalog.SlotLabel(slot).ToUpperInvariant(),
                        items = items.ToArray(),
                    });
            }

            // ---- features harvested off every shell ----
            foreach (BodyDef def in BodyCatalog.All)
            {
                if (def == null || string.IsNullOrEmpty(def.meshKey)) continue;
                var items = new List<Entry>();
                foreach (ShellFeatureSource.Feature f in ShellFeatureSource.Features(def))
                {
                    // A group of a handful of triangles is a seam or a stud, not a
                    // part somebody wants to place. The floor is stated rather than
                    // tuned: below it the palette fills with rows nobody can see.
                    if (f.triangles < MinHarvestTriangles) continue;
                    items.Add(new Entry
                    {
                        source = PartSource.ShellFeature(def.id, f.channel),
                        label = f.label,
                        triangles = f.triangles,
                        desc = $"From the {def.label}. {f.sizeM.x * 100f:0} × " +
                               $"{f.sizeM.y * 100f:0} × {f.sizeM.z * 100f:0} cm, " +
                               $"{f.triangles} tris.",
                    });
                }
                if (items.Count > 0)
                    cats.Add(new Category
                    {
                        title = def.label.ToUpperInvariant() + " PARTS",
                        items = items.ToArray(),
                    });
            }

            return cats.ToArray();
        }

        /// <summary>Smallest group offered as a part. Twelve triangles is one box:
        /// below that a group is a seam, a stud or a decal plane.</summary>
        public const int MinHarvestTriangles = 12;

        /// <summary>Find a palette row by source key, for labels and tooltips on a
        /// part that is already placed.</summary>
        public static bool TryFind(string source, out Entry entry)
        {
            foreach (Category c in Categories())
                foreach (Entry e in c.items)
                    if (e.source == source) { entry = e; return true; }
            entry = default;
            return false;
        }

        /// <summary>The name a freshly placed part takes.</summary>
        public static string LabelFor(string source) =>
            TryFind(source, out Entry e) ? e.label : source;

        // ==================== building ====================

        /// <summary>
        /// Build a part's geometry under <paramref name="parent"/>, at the identity
        /// local pose. The caller poses the parent; nothing here reads a
        /// <see cref="PropPlacement"/>, so the same call serves a placed part, a
        /// drag ghost and a palette thumbnail.
        ///
        /// Returns null for a source this build does not have — a missing FBX, a
        /// scheme from a newer version, a body that has been retired. The caller
        /// keeps the row and draws nothing, which is what lets a layout survive
        /// being opened by a build that is missing one of its parts.
        /// </summary>
        public static GameObject Build(Transform parent, string source,
                                       int layer = PartVisualFactory.VizLayer)
        {
            PartSource ps = PartSource.Parse(source);
            switch (ps.kind)
            {
                case PartKind.Cosmetic:
                    return CosmeticCatalog.Build(parent, ps.id, layer);

                case PartKind.ShellFeature:
                    return ShellFeatureSource.Build(parent, ps.id, ps.channel, layer);

                case PartKind.Aero:
                {
                    var go = Holder("aero", parent, layer);
                    PartVisualFactory.BuildAeroViz(go.transform, ps.AeroKindOf, 8f, 1f);
                    return go;
                }

                case PartKind.Antenna:
                {
                    var go = Holder("antenna", parent, layer);
                    PartVisualFactory.BuildAntennaViz(go.transform, 15f, 1f, ps.style);
                    return go;
                }

                case PartKind.Light:
                {
                    var go = Holder("light", parent, layer);
                    PartVisualFactory.BuildLightViz(go.transform, ps.style, 1f);
                    return go;
                }

                case PartKind.Battery:
                {
                    var go = Holder("battery", parent, layer);
                    PartVisualFactory.BuildBatteryViz(go.transform);
                    return go;
                }

                case PartKind.Wheel:
                {
                    WheelDef w = WheelCatalog.ById(ps.id);
                    if (w == null) return null;
                    var go = Holder("wheel", parent, layer);
                    // The design's own wheel radius, so a spare is the size of the
                    // wheels the car runs. Unpowered and unmarked: this is a
                    // decoration, and a drive stripe on it would be a lie.
                    PartVisualFactory.BuildWheelViz(go.transform, DecorWheelRadius,
                                                    false, -1f, w);
                    return go;
                }

                default:
                    return null;
            }
        }

        /// <summary>Radius a decorative wheel is built at (m) — the stock RC wheel,
        /// which is what <c>WheelSpec</c> defaults to.</summary>
        private const float DecorWheelRadius = 0.033f;

        private static GameObject Holder(string name, Transform parent, int layer)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = layer;
            return go;
        }

        private static string AeroLabel(AeroKind k) => k switch
        {
            AeroKind.Wing => "Wing",
            AeroKind.Splitter => "Splitter",
            AeroKind.SideDam => "Side dam",
            _ => "Canard",
        };

        private static string AeroDesc(AeroKind k) => k switch
        {
            AeroKind.Wing => "Rear wing. Placed here it is decoration — downforce comes " +
                             "from the aero parts on the design.",
            AeroKind.Splitter => "Front lip.",
            AeroKind.SideDam => "Sill skirt.",
            _ => "Nose winglet.",
        };

        private static string AntennaLabel(int style) => style switch
        {
            0 => "Stub aerial",
            1 => "Whip aerial",
            2 => "Flag aerial",
            _ => "Twin aerial",
        };
    }
}
