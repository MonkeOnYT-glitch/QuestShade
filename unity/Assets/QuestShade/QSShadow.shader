Shader "Hidden/QuestShade/Shadow"
{
    Properties
    {
        _QS_ShadowK ("Darkness", Float) = 0.5
        _QS_Mask ("Color mask", Float) = 15
        _QS_Comp ("Stencil compare", Float) = 6
        _QS_Pass ("Stencil pass", Float) = 2
        _QS_ZTest ("Depth test", Float) = 4
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend Zero OneMinusSrcAlpha, Zero One
            ZWrite Off
            ZTest [_QS_ZTest]
            Cull Off
            ColorMask [_QS_Mask]
            Offset -1, -1
            Stencil
            {
                Ref 64
                ReadMask 64
                WriteMask 64
                Comp [_QS_Comp]
                Pass [_QS_Pass]
            }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ QS_STEREO_MULTIVIEW QS_STEREO_INSTANCING
            #if defined(QS_STEREO_MULTIVIEW)
                #define STEREO_MULTIVIEW_ON
            #elif defined(QS_STEREO_INSTANCING)
                #define STEREO_INSTANCING_ON
            #endif
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
            half _QS_ShadowK;
            half _QS_Mask;
            half _QS_Comp;
            half _QS_Pass;
            half _QS_ZTest;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                return half4(0, 0, 0, _QS_ShadowK);
            }
            ENDHLSL
        }
    }
}
