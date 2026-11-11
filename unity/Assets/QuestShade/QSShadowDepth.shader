Shader "Hidden/QuestShade/ShadowDepth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4x4 _QS_ShadowVPGpu;
            float4 _QS_ShadowO;
            float4 _QS_ShadowD;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float depth : TEXCOORD0; };

            Varyings Vert(Attributes i)
            {
                Varyings o;
                float3 w = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = mul(_QS_ShadowVPGpu, float4(w, 1));
                o.depth = dot(w - _QS_ShadowO.xyz, _QS_ShadowD.xyz) * _QS_ShadowO.w;
                return o;
            }

            float4 Frag(Varyings i) : SV_Target { return float4(i.depth, 0, 0, 1); }
            ENDHLSL
        }
    }
}
