using UnityEngine;

namespace AIHWSim.BodyEd
{
    /// <summary>Which family a placed part's geometry comes from.</summary>
    public enum PartKind
    {
        /// <summary>Unparseable, or a scheme this build does not have.</summary>
        Unknown,
        /// <summary>A cosmetics-pack FBX (<c>CosmeticCatalog</c>).</summary>
        Cosmetic,
        /// <summary>One renderer group harvested out of a body shell.</summary>
        ShellFeature,
        /// <summary>A procedural aero part (<c>PartVisualFactory.BuildAeroViz</c>).</summary>
        Aero,
        /// <summary>A procedural antenna, by style index.</summary>
        Antenna,
        /// <summary>A procedural light cluster, by style index.</summary>
        Light,
        /// <summary>The battery pack visual.</summary>
        Battery,
        /// <summary>A catalogue wheel worn as decoration — a spare, a trophy rim.
        /// The car's DRIVEN wheels are <c>WheelSpec</c>s on the design and are not
        /// props; this is the tyre bolted to the boot lid.</summary>
        Wheel,
    }

    /// <summary>
    /// The scheme-tagged string a <see cref="PropPlacement"/> names its geometry by,
    /// and the one place that format is written down.
    ///
    /// <code>
    ///   cos:top_crown                      a cosmetics-pack item
    ///   shell:body_patrol/Police_PushBar   a renderer group lifted off a shell
    ///   aero:Wing                          procedural aero, by AeroKind name
    ///   ant:2                              procedural antenna, by style
    ///   light:0                            procedural light cluster, by style
    ///   batt:                              the battery pack visual
    ///   wheel:wheel_redline                a catalogue wheel worn as decoration
    /// </code>
    ///
    /// <b>Strings rather than an enum plus five optional id fields</b>, for the
    /// reason every other key in this project is a string: a layout naming a part
    /// this build has never heard of has to survive being loaded, edited around and
    /// saved again. An unknown scheme parses to <see cref="PartKind.Unknown"/>, the
    /// builder draws nothing, and the row is still in the file when a build that
    /// understands it comes along. It is also the difference between a JSON a human
    /// can read and one they cannot.
    ///
    /// The separator is <c>:</c> for the scheme and <c>/</c> for the one compound
    /// id, so nothing here needs escaping: no catalogue key in the project contains
    /// either character.
    /// </summary>
    public readonly struct PartSource
    {
        public readonly PartKind kind;

        /// <summary>Primary id — cosmetic id, body key, <c>AeroKind</c> name, or
        /// wheel key. Empty for <see cref="PartKind.Battery"/>.</summary>
        public readonly string id;

        /// <summary>Secondary id — the channel name, for a shell feature only.</summary>
        public readonly string channel;

        /// <summary>Style index, for antennas and lights only.</summary>
        public readonly int style;

        private PartSource(PartKind k, string id, string channel, int style)
        {
            kind = k; this.id = id ?? ""; this.channel = channel ?? ""; this.style = style;
        }

        public bool IsValid => kind != PartKind.Unknown;

        // ---- formatting ------------------------------------------------------------

        public static string Cosmetic(string id) => "cos:" + id;
        public static string ShellFeature(string bodyKey, string channel) =>
            "shell:" + bodyKey + "/" + channel;
        public static string Aero(Vehicles.AeroKind kind) => "aero:" + kind;
        public static string Antenna(int style) => "ant:" + style;
        public static string Light(int style) => "light:" + style;
        public static string Battery() => "batt:";
        public static string Wheel(string wheelKey) => "wheel:" + wheelKey;

        // ---- parsing ---------------------------------------------------------------

        /// <summary>
        /// Parse a source key. Never throws and never logs: an unknown scheme is a
        /// datum about the file, not an error in it, and the callers that care say
        /// so themselves at the point where they decide to draw nothing.
        /// </summary>
        public static PartSource Parse(string source)
        {
            if (string.IsNullOrEmpty(source)) return default;
            int colon = source.IndexOf(':');
            if (colon < 0) return default;

            string scheme = source.Substring(0, colon);
            string rest = source.Substring(colon + 1);

            switch (scheme)
            {
                case "cos":
                    return string.IsNullOrEmpty(rest) ? default
                        : new PartSource(PartKind.Cosmetic, rest, null, 0);

                case "shell":
                {
                    int slash = rest.IndexOf('/');
                    if (slash <= 0 || slash == rest.Length - 1) return default;
                    return new PartSource(PartKind.ShellFeature,
                        rest.Substring(0, slash), rest.Substring(slash + 1), 0);
                }

                case "aero":
                    return System.Enum.TryParse(rest, out Vehicles.AeroKind _)
                        ? new PartSource(PartKind.Aero, rest, null, 0)
                        : default;

                case "ant":
                    return int.TryParse(rest, out int a)
                        ? new PartSource(PartKind.Antenna, "", null, a) : default;

                case "light":
                    return int.TryParse(rest, out int l)
                        ? new PartSource(PartKind.Light, "", null, l) : default;

                case "batt":
                    return new PartSource(PartKind.Battery, "", null, 0);

                case "wheel":
                    return string.IsNullOrEmpty(rest) ? default
                        : new PartSource(PartKind.Wheel, rest, null, 0);

                default:
                    return default;
            }
        }

        /// <summary>The <c>AeroKind</c> this names, for
        /// <see cref="PartKind.Aero"/> only. Wing when it somehow is not one —
        /// <see cref="Parse"/> has already refused anything that is not.</summary>
        public Vehicles.AeroKind AeroKindOf =>
            System.Enum.TryParse(id, out Vehicles.AeroKind k) ? k : Vehicles.AeroKind.Wing;

        /// <summary>Round-trip back to the string form. Equal to whatever was
        /// parsed for every valid key, which is what the bench pins.</summary>
        public override string ToString() => kind switch
        {
            PartKind.Cosmetic => Cosmetic(id),
            PartKind.ShellFeature => ShellFeature(id, channel),
            PartKind.Aero => "aero:" + id,
            PartKind.Antenna => Antenna(style),
            PartKind.Light => Light(style),
            PartKind.Battery => Battery(),
            PartKind.Wheel => Wheel(id),
            _ => "",
        };
    }
}
