// ARIA/ShadowReceiver
// Applied to MRUK EffectMesh surfaces (floor, walls) so virtual objects cast
// visible shadows onto real surfaces through the passthrough feed.
//
// How it works:
//   - The mesh is fully transparent (alpha = 0 where no shadow)
//   - Where a shadow falls, renders a dark semi-transparent overlay on passthrough
//   - Combined with Quest's passthrough compositing, this creates the illusion of
//     virtual shadows landing on real surfaces
//
// Requires URP with shadow keywords enabled in the pipeline asset.
// Queue: Transparent-1 so it renders before other transparent objects but after opaque.

Shader "ARIA/ShadowReceiver"
{
    Properties
    {
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.7
        _ShadowColor    ("Shadow Color",    Color)       = (0, 0, 0, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType"       = "Transparent"
            "Queue"            = "Transparent-1"
            "RenderPipeline"   = "UniversalPipeline"
            "IgnoreProjector"  = "True"
        }

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back

        Pass
        {
            Name "ShadowReceiver"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            // Shadow keyword variants
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float  _ShadowStrength;
                float4 _ShadowColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // Sample the main light shadow map at this world position
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light  mainLight   = GetMainLight(shadowCoord);

                // shadowAttenuation: 0 = fully in shadow, 1 = fully lit
                float inShadow = 1.0 - mainLight.shadowAttenuation;

                // Output: dark overlay with alpha proportional to shadow depth
                float alpha = inShadow * _ShadowStrength;
                return half4(_ShadowColor.rgb, alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/InternalErrorShader"
}
