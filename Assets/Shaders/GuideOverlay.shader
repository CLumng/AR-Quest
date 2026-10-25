// Flat translucent colour for the puncture guide. Premultiplied alpha keeps the alpha channel right over the passthrough.
// _ZTest 8 (Always) draws over the virtual skin so the path stays visible; 4 (LessEqual) lets the skin hide it.
Shader "ModelTarget/GuideOverlay"
{
    Properties
    {
        _Color ("Color", Color) = (0.2, 0.85, 0.4, 0.35)
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest [_ZTest]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _ZTest;
            CBUFFER_END

            struct Attributes { float4 position : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 position : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = TransformObjectToHClip(input.position.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(_Color.rgb * _Color.a, _Color.a);
            }
            ENDHLSL
        }
    }
}
