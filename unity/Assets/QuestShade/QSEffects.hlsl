#ifndef QS_EFFECTS_INCLUDED
#define QS_EFFECTS_INCLUDED

#include "QSEffectsParams.hlsl"
#ifndef QS_FETCH_MAIN
#define QS_FETCH_MAIN(uv) QS_FETCH(uv)
#endif

float Hash12(float2 p) { float3 p3 = frac(float3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return frac((p3.x + p3.y) * p3.z); }

float2 DistortUV(float2 uv)
{
    UNITY_BRANCH if (_QS_Curve.x > 0)
    {
        float2 c = uv * 2 - 1;
        c *= 1 + _QS_Curve.x * dot(c.yx, c.yx) * float2(0.25, 0.35);
        uv = c * 0.5 + 0.5;
    }
    UNITY_BRANCH if (_QS_Distort.x != 0)
    {
        float2 c = (uv - 0.5) * _QS_Distort.y;
        float r2 = dot(c, c);
        uv = 0.5 + c * (1 + _QS_Distort.x * r2 * 1.5);
    }
    return uv;
}

half3 Tonemap(half3 c)
{
    half3 r = c;
    UNITY_BRANCH if (_QS_Tone.y > 1.5)
        return saturate((c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14));
    UNITY_BRANCH if (_QS_Tone.y > 0.5)
    {
        const half A = 0.2, B = 0.29, C = 0.24, D = 0.272, E = 0.02, F = 0.3, W = 5.3;
        half3 x = c * 1.31;
        half3 m = ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
        half w = ((W * (A * W + C * B) + D * E) / (W * (A * W + B) + D * F)) - E / F;
        r = saturate(m / w);
    }
    return r;
}

half3 ApplyLut(half3 lin)
{
    half3 s = saturate(LinearToSRGB(lin));
    float3 sc = s * _QS_LutParams.z;
    float slice = floor(sc.b);
    float f = sc.b - slice;
    float2 uv = float2((sc.r + 0.5 + slice * (_QS_LutParams.z + 1)) * _QS_LutParams.x, (sc.g + 0.5) * _QS_LutParams.y);
    half3 a = SAMPLE_TEXTURE2D_LOD(_QS_Lut, sampler_QS_Lut, uv, 0).rgb;
    half3 b = SAMPLE_TEXTURE2D_LOD(_QS_Lut, sampler_QS_Lut, uv + float2((_QS_LutParams.z + 1) * _QS_LutParams.x, 0), 0).rgb;
    return SRGBToLinear(lerp(a, b, f));
}

half3 QSStack(float2 uv, float2 pixelPos)
{
    float t = _QS_Time.x;

    UNITY_BRANCH if (_QS_Glitch.x > 0)
    {
        float row = floor(uv.y * _QS_Glitch.y);
        float tt = floor(t * _QS_Glitch.z);
        float h = Hash12(float2(row, tt));
        float on = step(1 - _QS_Glitch.x * 0.35, h);
        uv.x += on * (Hash12(float2(tt, row + 7)) - 0.5) * 0.08 * _QS_Glitch.x;
    }
    float2 suv = DistortUV(uv);

    half3 col;
    UNITY_BRANCH if (_QS_CA.x > 0)
    {
        float2 d = (suv - 0.5) * _QS_CA.x * 0.02;
        half3 mainC = QS_FETCH_MAIN(suv);
        half3 baseC = QS_FETCH(suv);
        col = half3(QS_FETCH(suv - d).r, baseC.g, QS_FETCH(suv + d).b) + (mainC - baseC);
    }
    else col = QS_FETCH_MAIN(suv);

    UNITY_BRANCH if (_QS_Hurt.x > 0.001)
    {
        float r = _QS_Hurt.x * 0.006 * (0.85 + 0.15 * sin(_QS_Time.x * 3.1));
        half3 acc = col;
        [unroll] for (int hi = 0; hi < 8; hi++)
        {
            float a = hi * 0.785398;
            acc += QS_FETCH(suv + float2(cos(a), sin(a) * 1.7) * r);
        }
        col = acc / 9.0;
    }

    UNITY_BRANCH if (_QS_Sharpen.x > 0)
    {
        float2 px = _QS_SrcTexel.xy;
        half3 n = QS_FETCH(suv + float2(0, px.y)), s = QS_FETCH(suv - float2(0, px.y));
        half3 e = QS_FETCH(suv + float2(px.x, 0)), w = QS_FETCH(suv - float2(px.x, 0));
        half3 mn = min(col, min(min(n, s), min(e, w)));
        half3 mx = max(col, max(max(n, s), max(e, w)));
        half3 amp = saturate(min(mn, 2.0 - mx) / max(mx, 1e-4));
        amp = sqrt(amp);
        half peak = -1.0 / lerp(8.0, 5.0, _QS_Sharpen.x);
        half3 wgt = amp * peak;
        col = saturate((col + (n + s + e + w) * wgt) / (1.0 + 4.0 * wgt));
    }

    col *= _QS_Tone.x;
    col = Tonemap(col);
    UNITY_BRANCH if (_QS_Tone.z > 0.001)
    {
        half3 c = saturate(col);
        half k = 1.0 - _QS_Tone.z;
        col += c / (c + k * (1.0 - c) + 1e-5) - c;
    }
    UNITY_BRANCH if (_QS_LutParams.w > 0) col = ApplyLut(col);

    UNITY_BRANCH if (_QS_Vignette.x > 0)
    {
        UNITY_BRANCH if (_QS_Vignette.w > 0.5)
        {
            float2 dd = (uv - _QS_VignetteCenter.xy) * float2(_QS_Vignette.z, _QS_Vignette.y) * _QS_Vignette.x;
            col *= 1.0 + pow(max(dot(dd, dd), 1e-6), _QS_VignetteCenter.w * 0.5) * _QS_VignetteCenter.z;
        }
        else
        {
            float2 d = abs(uv - _QS_VignetteCenter.xy) * _QS_Vignette.x;
            d.x *= _QS_Vignette.z;
            half v = pow(saturate(1.0 - dot(d, d)), _QS_Vignette.y);
            col *= lerp(_QS_VignetteColor.rgb, (1.0).xxx, v);
        }
    }

    UNITY_BRANCH if (_QS_Hurt.x > 0.001)
    {
        float2 hd = (uv - 0.5) * float2(1.25, 1.0);
        half edge = saturate(dot(hd, hd) * 3.2 - 0.1);
        half beat = 0.8 + 0.2 * sin(_QS_Time.x * 5.5);
        half k = saturate(_QS_Hurt.x * (0.3 + edge * 1.3) * beat);
        half3 red = col * half3(1.0, 0.22, 0.18) + half3(0.22, 0.0, 0.0) * edge;
        col = lerp(col, red, k);
    }

    UNITY_BRANCH if (_QS_Scan.x > 0)
    {
        float y = uv.y * _QS_Scan.y + t * _QS_Scan.z;
        half scanWave = 0.5 + 0.5 * sin(y * 6.2831853);
        col *= 1.0 - _QS_Scan.x * (1.0 - scanWave);
        UNITY_BRANCH if (_QS_Scan.w > 0)
        {
            half bar = smoothstep(0.0, 0.08, frac(uv.y * 0.6 - t * 0.12)) * smoothstep(0.16, 0.08, frac(uv.y * 0.6 - t * 0.12));
            col *= 1.0 + bar * _QS_Scan.w * 0.25;
        }
    }
    UNITY_BRANCH if (_QS_Grain.x > 0 || _QS_Noise.x > 0)
    {
        float2 p = floor(pixelPos / max(_QS_Grain.z, 1.0));
        float fr = _QS_Time.y;
        half n = Hash12(p + fr * 13.37) - 0.5;
        half3 nc = _QS_Grain.w > 0 ? half3(n, Hash12(p + fr * 7.1) - 0.5, Hash12(p + fr * 3.3) - 0.5) : n.xxx;
        half lum = dot(col, half3(0.2126, 0.7152, 0.0722));
        half resp = lerp(1.0, saturate(1.0 - lum), _QS_Grain.y);
        col += nc * (_QS_Grain.x * 0.12 * resp + _QS_Noise.x * 0.2);
        col = max(col, 0);
    }
    UNITY_BRANCH if (_QS_Posterize.x > 1)
    {
        half3 s = LinearToSRGB(saturate(col));
        s = floor(s * _QS_Posterize.x + 0.5) / _QS_Posterize.x;
        col = SRGBToLinear(s);
    }
    UNITY_BRANCH if (_QS_Curve.x > 0)
    {
        col *= step(0.0, suv.x) * step(suv.x, 1.0) * step(0.0, suv.y) * step(suv.y, 1.0);
    }
    return col;
}

#endif
