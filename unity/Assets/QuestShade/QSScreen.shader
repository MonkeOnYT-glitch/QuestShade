Shader "Hidden/QuestShade/Screen"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            ZTest LEqual

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

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float4 _QS_Portrait;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 uv = i.uv;
                UNITY_BRANCH if (_QS_Portrait.x > 0.5)
                {
                    bool fx = _MainTex_ST.x < 0, fy = _MainTex_ST.y < 0;
                    float2 s = float2(fx ? 1 - uv.x : uv.x, fy ? 1 - uv.y : uv.y);
                    float qx = (s.x - 0.5) / max(_QS_Portrait.y, 1e-3) + 0.5;
                    if (qx < 0 || qx > 1) return half4(0, 0, 0, 1);
                    float2 t = float2(s.y, 1 - qx);
                    uv = float2(fy ? 1 - t.x : t.x, fx ? 1 - t.y : t.y);
                }
                half3 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb * _Color.rgb;
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
