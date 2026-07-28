// Gradient sky dome for the themed maps (see MapAmbience).
//
// It lives in Resources/ rather than anywhere tidier for one reason: assets
// under Resources are never stripped from a player build, and a sky that only
// exists in the editor is exactly the class of bug the vehicle pass hit with
// emissive shader variants. MapAmbience loads it with Resources.Load and
// silently falls back to a flat camera background if it is ever missing.
//
// Drawn on the INSIDE of a sphere (Cull Front) at the background queue, with
// depth writes off, so all real geometry draws over it. Fog is off: fogging
// the sky toward the fog colour would flatten the very gradient the fog colour
// was picked to match.
Shader "AIHWSim/SkyGradient"
{
    Properties
    {
        _TopColor     ("Zenith", Color)      = (0.10, 0.15, 0.30, 1)
        _HorizonColor ("Horizon", Color)     = (0.40, 0.40, 0.50, 1)
        _GroundColor  ("Nadir", Color)       = (0.05, 0.05, 0.06, 1)
        // Alpha is the wedge's strength, so a def with no wedge just leaves it 0.
        _WedgeColor   ("Horizon wedge", Color) = (0, 0, 0, 0)
        _WedgeDir     ("Wedge direction (xz)", Vector) = (0, 0, 1, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Front
        ZWrite Off
        ZTest LEqual
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            fixed4 _TopColor, _HorizonColor, _GroundColor, _WedgeColor;
            float4 _WedgeDir;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Unity's sphere primitive is centred on its own origin, so the
                // object-space position IS the view direction for that fragment.
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);

                // Sky above, ground below, both easing out of the horizon band.
                // The exponents are what keep the horizon a band rather than a
                // hard line: a linear ramp puts all the colour change in the
                // top of frame where nothing is looking.
                fixed3 col = d.y >= 0.0
                    ? lerp(_HorizonColor.rgb, _TopColor.rgb, saturate(pow(d.y, 0.55)))
                    : lerp(_HorizonColor.rgb, _GroundColor.rgb, saturate(pow(-d.y, 0.35)));

                // One compass side gets a low glow: the sunset behind the
                // arcade volcano, the aurora over the enchanted castle.
                float3 flat_d = normalize(float3(d.x, 0.0, d.z) + float3(0.0, 0.0, 1e-5));
                float3 flat_w = normalize(float3(_WedgeDir.x, 0.0, _WedgeDir.z) + float3(0.0, 0.0, 1e-5));
                float side = saturate(dot(flat_d, flat_w));
                float band = saturate(1.0 - abs(d.y) * 4.0);
                col = lerp(col, _WedgeColor.rgb, _WedgeColor.a * side * side * band);

                return fixed4(col, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
