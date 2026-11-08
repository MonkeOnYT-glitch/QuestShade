Shader "Hidden/QuestShade/YUV"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            float4x4 unity_ObjectToWorld;
            float4x4 unity_MatrixVP;
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _QS_YUVSize;
            float4 _QS_YUVLayout;

            struct A { float4 pos : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            V Vert(A a)
            {
                V o;
                o.pos = mul(unity_MatrixVP, mul(unity_ObjectToWorld, float4(a.pos.xyz, 1)));
                o.uv = a.uv;
                return o;
            }

            half3 Src(float2 uvTopDown)
            {
                float2 uv = float2(uvTopDown.x, _QS_YUVSize.w > 0.5 ? 1 - uvTopDown.y : uvTopDown.y);
                return LinearToSRGB(saturate(SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).rgb));
            }

            float Frag(V i) : SV_Target
            {
                float W = _QS_YUVSize.x, H = _QS_YUVSize.y;
                float S = max(_QS_YUVLayout.x, H);
                float stride = max(_QS_YUVLayout.y, W);
                float row = floor(i.uv.y * _QS_YUVSize.z);
                float col = floor(i.uv.x * stride);
                if (col >= W) return row < S ? 16.0 / 255.0 : 128.0 / 255.0;
                if (row < S)
                {
                    if (row >= H) return 16.0 / 255.0;
                    half3 c = Src(float2((col + 0.5) / W, (row + 0.5) / H));
                    return (16.0 + 65.481 * c.r + 128.553 * c.g + 24.966 * c.b) / 255.0;
                }
                float k = row - S;
                if (k >= H * 0.5) return 128.0 / 255.0;
                float pairX = floor(col * 0.5);
                half3 c = Src(float2((pairX * 2 + 1) / W, (k * 2 + 1) / H));
                bool isV = fmod(col, 2) > 0.5;
                return isV ? (128.0 + 112.0 * c.r - 93.786 * c.g - 18.214 * c.b) / 255.0
                           : (128.0 - 37.797 * c.r - 74.203 * c.g + 112.0 * c.b) / 255.0;
            }
            ENDHLSL
        }
    }
}
