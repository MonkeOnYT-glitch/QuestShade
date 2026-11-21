// Draws shadow casters (copies of the player body, held items, nearby NPCs/props) into QuestShade's own shadow map.
// Rendered only by the sun camera. Writes the distance along the light direction in metres; the closest caster wins.
Shader "Hidden/QuestShade/ShadowCaster"
{
    Properties
    {
        _MainTex ("Cutout texture", 2D) = "white" {}
        _QS_Clip ("Cutout", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            half _QS_Clip;
            CBUFFER_END
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _QS_SMOrigin;   // xyz: shadow map origin (light-side end of the box)
            float4 _QS_SMDir;      // xyz: light direction (normalized)
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float dist : TEXCOORD1; };
            Varyings Vert(Attributes i)
            {
                Varyings o;
                float3 w = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(w);
                o.uv = i.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                o.dist = dot(w - _QS_SMOrigin.xyz, _QS_SMDir.xyz);
                return o;
            }
            float4 Frag(Varyings i) : SV_Target
            {
                UNITY_BRANCH if (_QS_Clip > 0) clip(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a - _QS_Clip);
                return float4(i.dist, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}
