// Dependency-free bloom for the Built-in RP (the project deliberately ships no
// post-processing package — same ethos as the synth audio and procedural music).
// Four passes driven by Scripts/Rendering/CameraBloom.cs:
//   0  bright-pass prefilter (threshold + soft knee)
//   1  separable gaussian blur (_Dir selects H or V)
//   2  final composite (scene + _BloomTex * _Intensity)
//   3  additive copy (accumulates the mip chain back up)
// Lives in Resources so Shader.Find resolves in a player build without a
// scene reference — the same guarantee the prop FBX rely on.
Shader "Hidden/AIHWSim/Bloom"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;
        sampler2D _BloomTex;
        float _Threshold;
        float _Knee;
        float _Intensity;
        float2 _Dir;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }
        ENDCG

        // 0 — bright-pass with a quadratic soft knee, so pixels just under the
        // threshold contribute a little instead of popping in and out.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                half br = max(c.r, max(c.g, c.b));
                half soft = clamp(br - _Threshold + _Knee, 0.0h, 2.0h * _Knee);
                soft = soft * soft / (4.0h * _Knee + 1e-4h);
                half contrib = max(soft, br - _Threshold) / max(br, 1e-4h);
                contrib = max(contrib, 0.0h);
                return c * contrib;
            }
            ENDCG
        }

        // 1 — 5-sample separable gaussian along _Dir.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                float2 step = _Dir * _MainTex_TexelSize.xy;
                fixed4 c = tex2D(_MainTex, i.uv) * 0.227027;
                c += tex2D(_MainTex, i.uv + step * 1.384615) * 0.316216;
                c += tex2D(_MainTex, i.uv - step * 1.384615) * 0.316216;
                c += tex2D(_MainTex, i.uv + step * 3.230769) * 0.070270;
                c += tex2D(_MainTex, i.uv - step * 3.230769) * 0.070270;
                return c;
            }
            ENDCG
        }

        // 2 — composite the accumulated bloom over the scene.
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                fixed4 b = tex2D(_BloomTex, i.uv);
                return c + b * _Intensity;
            }
            ENDCG
        }

        // 3 — additive copy (mip-chain upsample accumulate).
        Pass
        {
            Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);
            }
            ENDCG
        }
    }
    Fallback Off
}
