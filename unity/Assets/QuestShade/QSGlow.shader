Shader "Hidden/QuestShade/Glow"
{
    // Glowing Nullbodies: an additive rim glow drawn on a copy of the NPC's skinned mesh.
    // Brightest at the silhouette, a faint inner glow, and a slow pulse.
    Properties
    {
        _Color ("Color", Color) = (0.72, 0.32, 1, 1)
        _Rim ("Rim power", Float) = 2.4
        _Inflate ("Inflate", Float) = 0.004
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+5" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Back
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
            float _Rim;
            float _Inflate;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 viewWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 n = TransformObjectToWorldNormal(i.normalOS);
                float3 w = TransformObjectToWorld(i.positionOS.xyz) + n * _Inflate;
                o.positionCS = TransformWorldToHClip(w);
                o.normalWS = n;
                o.viewWS = GetWorldSpaceViewDir(w);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 n = normalize(i.normalWS);
                float3 v = normalize(i.viewWS);
                half rim = pow(1 - saturate(abs(dot(n, v))), max(_Rim, 0.5));
                half pulse = 0.85 + 0.15 * sin(_Time.y * 2.2);
                half3 c = _Color.rgb * (rim * 1.7 + 0.06) * pulse * _Color.a;
                return half4(c, 0);
            }
            ENDHLSL
        }
    }
}
