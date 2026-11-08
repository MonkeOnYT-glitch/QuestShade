Shader "Hidden/QuestShade/Post"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

        float4x4 unity_ObjectToWorld;
        float4x4 unity_MatrixVP;
        TEXTURE2D(_MainTex);       SAMPLER(sampler_MainTex);       float4 _MainTex_TexelSize;
        TEXTURE2D(_QS_BloomLow);   SAMPLER(sampler_QS_BloomLow);   float4 _QS_BloomLow_TexelSize;
        TEXTURE2D(_QS_BloomTex);   SAMPLER(sampler_QS_BloomTex);
        float4 _QS_Bloom;
        float4 _QS_BloomTint;
        float4 _QS_DofPx;

        struct A { float4 pos : POSITION; float2 uv : TEXCOORD0; };
        struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        V Vert(A a)
        {
            V o;
            o.pos = mul(unity_MatrixVP, mul(unity_ObjectToWorld, float4(a.pos.xyz, 1)));
            o.uv = a.uv;
            return o;
        }

        half3 Safe(half3 c)
        {
            c = (any(isnan(c)) || any(isinf(c))) ? half3(0, 0, 0) : c;
            return min(max(c, 0), 32);
        }

        half3 Box4(float2 uv, float2 texel)
        {
            float4 d = texel.xyxy * float4(-1, -1, 1, 1);
            return Safe((SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + d.xy, 0).rgb +
                    SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + d.zy, 0).rgb +
                    SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + d.xw, 0).rgb +
                    SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + d.zw, 0).rgb) * 0.25);
        }
        ENDHLSL

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(V i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv, 0);
                c.rgb = Safe(c.rgb); c.a = saturate(c.a);
                float R = c.a * _QS_DofPx.x;
                UNITY_BRANCH if (R < 0.5) return c;
                half3 acc = c.rgb; half wsum = 1;
                int N = _QS_DofPx.z > 0 ? (int)_QS_DofPx.z : 48;
                float2 px = i.uv * _MainTex_TexelSize.zw;
                float rot = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))) * 6.2831853;
                UNITY_LOOP for (int k = 0; k < N; k++)
                {
                    float r = sqrt((k + 0.5) / N) * R;
                    float th = k * 2.39996323 + rot;
                    float2 o = float2(cos(th), sin(th)) * r * _MainTex_TexelSize.xy;
                    half4 s = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv + o, 0);
                    s.rgb = Safe(s.rgb);
                    half w = saturate(saturate(s.a) * _QS_DofPx.x - r + 1.0);
                    half hl = max(s.r, max(s.g, s.b));
                    w *= 1.0 + min(_QS_DofPx.y * 2.0 * hl * hl * hl, 3.0) * s.a;
                    acc += s.rgb * w; wsum += w;
                }
                return half4(acc / wsum, c.a);
            }
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(V i) : SV_Target
            {
                half3 c = Box4(i.uv, _MainTex_TexelSize.xy);
                half br = max(c.r, max(c.g, c.b));
                half knee = max(_QS_Bloom.z, 1e-4);
                half soft = clamp(br - _QS_Bloom.y + knee, 0, 2 * knee);
                soft = soft * soft / (4 * knee + 1e-4);
                half contrib = max(soft, br - _QS_Bloom.y) / max(br, 1e-4);
                return half4(c * contrib, 1);
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(V i) : SV_Target { return half4(Box4(i.uv, _MainTex_TexelSize.xy), 1); }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(V i) : SV_Target
            {
                float4 d = _QS_BloomLow_TexelSize.xyxy * float4(1, 1, -1, 0);
                half3 s;
                s  = SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv - d.xy, 0).rgb;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv - d.wy, 0).rgb * 2;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv - d.zy, 0).rgb;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv + d.zw, 0).rgb * 2;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv, 0).rgb * 4;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv + d.xw, 0).rgb * 2;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv + d.zy, 0).rgb;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv + d.wy, 0).rgb * 2;
                s += SAMPLE_TEXTURE2D_LOD(_QS_BloomLow, sampler_QS_BloomLow, i.uv + d.xy, 0).rgb;
                s *= 1.0 / 16;
                half3 high = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv, 0).rgb;
                return half4(high + s * _QS_Bloom.w, 1);
            }
            ENDHLSL
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 _QS_SS;
            half3 FetchPost(float2 uv)
            {
                half3 c;
                UNITY_BRANCH if (_QS_SS.x > 0)
                {
                    c = (SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + float2(-_QS_SS.y, -_QS_SS.z), 0).rgb +
                         SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + float2( _QS_SS.y, -_QS_SS.z), 0).rgb +
                         SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + float2(-_QS_SS.y,  _QS_SS.z), 0).rgb +
                         SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv + float2( _QS_SS.y,  _QS_SS.z), 0).rgb) * 0.25;
                }
                else c = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).rgb;
                c = Safe(c);
                UNITY_BRANCH if (_QS_Bloom.x > 0)
                    c += SAMPLE_TEXTURE2D_LOD(_QS_BloomTex, sampler_QS_BloomTex, uv, 0).rgb * _QS_Bloom.x * _QS_BloomTint.rgb;
                return c;
            }
            #define QS_FETCH(uv) FetchPost(uv)
            #include "QSEffects.hlsl"
            half4 Frag(V i) : SV_Target { return half4(QSStack(i.uv, i.pos.xy), 1); }
            ENDHLSL
        }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(V i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv, 0);
                float R = c.a * _QS_DofPx.x;
                UNITY_BRANCH if (R < 1.5) return c;
                float2 t = _MainTex_TexelSize.xy * clamp(R * 0.18, 0.75, 2.5);
                half3 s = c.rgb * 4;
                s += (SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv + float2(t.x, 0), 0).rgb +
                      SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv - float2(t.x, 0), 0).rgb +
                      SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv + float2(0, t.y), 0).rgb +
                      SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv - float2(0, t.y), 0).rgb) * 2;
                s += SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv + t, 0).rgb +
                     SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv - t, 0).rgb +
                     SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv + float2(t.x, -t.y), 0).rgb +
                     SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, i.uv + float2(-t.x, t.y), 0).rgb;
                return half4(lerp(c.rgb, s / 16, saturate((R - 1.5) / 3)), c.a);
            }
            ENDHLSL
        }    }
}
