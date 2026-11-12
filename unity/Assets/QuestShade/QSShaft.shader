Shader "Hidden/QuestShade/Shaft"
{
    Properties
    {
        _Color ("Color", Color) = (1, 0.9, 0.7, 0.2)
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Off
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
            half4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; half4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 viewWS : TEXCOORD2; half glow : TEXCOORD3; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 w = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(w);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.viewWS = GetWorldSpaceViewDir(w);
                o.uv = i.uv;
                o.glow = i.color.a;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float d = length(i.viewWS);
                float3 v = i.viewWS / max(d, 1e-4);
                half facing = abs(dot(normalize(i.normalWS), v));
                facing *= facing;
                half across = saturate(1 - abs(i.uv.x));
                across = across * across * (3 - 2 * across);
                half along = sin(saturate(i.uv.y) * 3.14159265);
                along *= saturate(i.uv.y * 6);
                half nearFade = saturate((d - 0.35) / 1.6);
                half k = _Color.a * i.glow * facing * across * along * nearFade;
                return half4(_Color.rgb * k, 0);
            }
            ENDHLSL
        }
    }
}
