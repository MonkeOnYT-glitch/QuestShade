Shader "Hidden/QuestShade/Shadow"
{
    Properties
    {
        _QS_ShadowTex ("Silhouette", 2D) = "black" {}
        _QS_ShadowK ("Darkness", Float) = 0.5
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "AlphaTest+49" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend Zero OneMinusSrcAlpha, Zero One
            ZWrite Off
            ZTest LEqual
            Cull Off
            Offset -1, -1
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

            TEXTURE2D(_QS_ShadowTex); SAMPLER(sampler_QS_ShadowTex);
            CBUFFER_START(UnityPerMaterial)
            float4 _QS_ShadowTex_TexelSize;
            half _QS_ShadowK;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.uv = i.uv;
                return o;
            }

            half Tap(float2 uv) { return SAMPLE_TEXTURE2D_LOD(_QS_ShadowTex, sampler_QS_ShadowTex, uv, 0).r; }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 t = _QS_ShadowTex_TexelSize.xy * 1.6;
                half a = Tap(i.uv) * 0.28;
                a += (Tap(i.uv + float2(t.x, t.y)) + Tap(i.uv + float2(-t.x, t.y)) + Tap(i.uv + float2(t.x, -t.y)) + Tap(i.uv + float2(-t.x, -t.y))) * 0.12;
                a += (Tap(i.uv + float2(t.x * 2, 0)) + Tap(i.uv + float2(-t.x * 2, 0)) + Tap(i.uv + float2(0, t.y * 2)) + Tap(i.uv + float2(0, -t.y * 2))) * 0.06;
                float2 e = 0.5 - abs(i.uv - 0.5);
                a *= saturate(min(e.x, e.y) * 25.0);
                return half4(0, 0, 0, saturate(a) * _QS_ShadowK);
            }
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode" = "QSShadowCaster" }
            ZWrite Off
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target { return half4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
