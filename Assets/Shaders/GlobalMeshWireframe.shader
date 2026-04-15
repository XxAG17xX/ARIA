// GlobalMeshWireframe.shader — shows the Global Mesh as green triangle outlines
// ported from Meta's Phanto sample (which uses BiRP) to work with URP.
// EffectMesh stores barycentric coords in vertex colors when BarycentricCoordinatesEnabled
// is on — each vertex gets (1,0,0), (0,1,0), or (0,0,1). when a coord is near 0
// we're on an edge, so we draw the wireframe color there and discard the fill.
// distance scaling keeps edges visible even far away.

Shader "ARIA/GlobalMeshWireframe"
{
    Properties
    {
        _WireframeColor("Wireframe Color", Color) = (0, 1, 0.3, 0.8)
        _Color("Fill Color", Color) = (0, 0, 0, 0)
        _DistanceMultiplier("Distance Multiplier", Range(1, 10)) = 2
        _LineWidth("Line Width", Range(0.001, 0.05)) = 0.01
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "GlobalMeshWireframe"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _WireframeColor;
                half4 _Color;
                float _DistanceMultiplier;
                float _LineWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR; // barycentric coordinates from EffectMesh
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 barycentric : TEXCOORD0;
                float3 positionVS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.positionVS = TransformWorldToView(TransformObjectToWorld(input.positionOS.xyz));
                output.barycentric = input.color.xyz;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Find closest edge — barycentric coord near 0 = on edge
                float closest = min(input.barycentric.x, min(input.barycentric.y, input.barycentric.z));

                // Distance-based line width (thicker lines farther away for readability)
                float dist = length(input.positionVS) * _DistanceMultiplier * 0.02;
                float edge = closest / max(dist, 0.001);

                // Sharp edge: wireframe color on edges, fill elsewhere
                half4 color = lerp(_WireframeColor, _Color, saturate(edge));

                // Discard fully transparent pixels (fill with alpha=0)
                if (color.a < 0.01) discard;

                return color;
            }
            ENDHLSL
        }
    }
    Fallback "Universal Render Pipeline/Unlit"
}
