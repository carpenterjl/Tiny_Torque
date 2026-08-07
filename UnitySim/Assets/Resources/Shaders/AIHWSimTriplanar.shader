// World-space triplanar body surface, for the runtime deformation editor.
//
// WHY IT EXISTS. A body being sculpted has no stable UV layout: pulling a vertex
// stretches whatever texel spacing the exporter baked in, and the blendshape
// morphs move whole regions relative to each other. Triplanar projection sidesteps
// the question entirely by deriving texture coordinates from position rather than
// from a UV channel — so a panel that has just been dragged out by 30 % shows the
// same texel density as the one beside it, and the merged mesh that
// BodyMeshSource produces (whose per-part UVs no longer relate to each other at
// all) still reads as one surface.
//
// WORLD SPACE, NOT OBJECT SPACE, and that is a decision this shader can only make
// because the editor's body never moves. On a driving car, world-space projection
// makes the texture swim across the bodywork as the car translates; the fix there
// is to sample in object space. That is the one line to change when this is ported
// into the garage, and it is why this shader is BodyEd's rather than a replacement
// for Standard on CarVehicle.
//
// NO NORMAL MAP, deliberately. DeformableBody calls RecalculateNormals after every
// edit, so the geometric normal is always honest; a tangent-space bump map would
// cost three more samples and need a whitening blend to combine across the three
// planes, and it would be describing detail the sculpting tool cannot author.
// Adding one later means three more tex2D calls and a UDN blend — the property
// block leaves room.

Shader "AIHWSim/TriplanarBody"
{
    Properties
    {
        _Color          ("Tint", Color)               = (0.62, 0.66, 0.72, 1)
        _MainTex        ("Albedo (triplanar)", 2D)    = "white" {}
        _TileScale      ("Tiles per metre", Float)    = 12
        _BlendSharpness ("Blend sharpness", Range(1, 8)) = 4
        _Glossiness     ("Smoothness", Range(0, 1))   = 0.45
        _Metallic       ("Metallic", Range(0, 1))     = 0.15
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;
        half _TileScale;
        half _BlendSharpness;
        half _Glossiness;
        half _Metallic;

        // No uv_MainTex: that is the point. Position and normal are the whole
        // input, so the mesh needs no usable UV channel at all.
        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
        };

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Blend weights from how much each axis the surface faces. Raising
            // them to a power tightens the transition: at 1 the three projections
            // smear into each other everywhere, at 8 the seams turn hard.
            half3 blend = pow(abs(IN.worldNormal), _BlendSharpness);
            blend /= max(1e-4h, blend.x + blend.y + blend.z);

            float3 p = IN.worldPos * _TileScale;

            // One sample per plane, each using the two axes that plane spans.
            fixed4 cx = tex2D(_MainTex, p.zy);   // faces ±X
            fixed4 cy = tex2D(_MainTex, p.xz);   // faces ±Y
            fixed4 cz = tex2D(_MainTex, p.xy);   // faces ±Z

            fixed4 c = (cx * blend.x + cy * blend.y + cz * blend.z) * _Color;

            o.Albedo = c.rgb;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }

    FallBack "Diffuse"
}
