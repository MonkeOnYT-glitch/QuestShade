Shader "Hidden/QuestShade/FinalBlit"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100

        Pass
        {
            Name "QuestShadeBlit"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment Fragment
            #pragma multi_compile_fragment _ _LINEAR_TO_SRGB_CONVERSION
            #pragma multi_compile_local _ QS_PROCEDURAL
            #if defined(QS_PROCEDURAL)
                #define _USE_DRAW_PROCEDURAL 1
            #endif
            #pragma multi_compile_fragment _ _RECONSTRUCT_VRS_TILES
            #pragma multi_compile_fragment _ _VRS_MASK_MODE
            #pragma multi_compile_local _ QS_STEREO_MULTIVIEW QS_STEREO_INSTANCING
            #pragma multi_compile_local_fragment _ QS_PREPASS
            #if defined(QS_STEREO_MULTIVIEW)
                #define STEREO_MULTIVIEW_ON
            #elif defined(QS_STEREO_INSTANCING)
                #define STEREO_INSTANCING_ON
            #endif

            #include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/Fullscreen.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D_X(_SourceTex);
            SAMPLER(sampler_SourceTex);
            float4 _TileRadii;
            float4 _SourceTex_TexelSize;
#if defined(_RECONSTRUCT_VRS_TILES) && defined(_VRS_MASK_MODE)
            #define QS_VRS 1
            float4 _EyeCenterCoords;

            static const uint4x4 lutX00 = uint4x4(0,0,1,0, 1,0,0,-1, 1,0,0,0, 0,-1,1,0);
            static const int4x4  lutY00 = uint4x4(0,1,0,0, 0,0,0,0, 0,0,0,1, 0,0,0,0);
            static const int4x4  lutX10 = uint4x4(0,1,1,1, 1,1,0,1, 1,1,0,1, 0,1,1,1);
            static const int4x4  lutY10 = uint4x4(0,1,0,0, 0,0,0,-1, 0,0,0,1, 0,-1,0,0);
            static const int4x4  lutX11 = uint4x4(1,1,0,1, 1,1,0,2, 0,1,1,1, 0,2,1,1);
            static const int4x4  lutY11 = uint4x4(1,1,1,0, 1,1,1,1, 1,0,1,1, 1,1,1,1);
            static const int4x4  lutX01 = uint4x4(1,0,0,1, 1,0,0,0, 0,0,1,0, 0,0,1,0);
            static const int4x4  lutY01 = uint4x4(1,1,1,0, 1,1,1,2, 1,0,1,1, 1,2,1,1);

            half4 SampleVRS(float2 uv)
            {
                float2 screenUV = _SourceTex_TexelSize.zw * uv;
                float2 gridUnit2 = 4 * floor(screenUV * 0.25);
                float2 center = unity_StereoEyeIndex == 0 ? _EyeCenterCoords.xy : _EyeCenterCoords.zw;
                center *= _SourceTex_TexelSize.zw;
                float4 radii;
                radii.x = length(gridUnit2 + float2(0.0, 0.0) - center) * _SourceTex_TexelSize.y;
                radii.y = length(gridUnit2 + float2(4.0, 0.0) - center) * _SourceTex_TexelSize.y;
                radii.z = length(gridUnit2 + float2(4.0, 4.0) - center) * _SourceTex_TexelSize.y;
                radii.w = length(gridUnit2 + float2(0.0, 4.0) - center) * _SourceTex_TexelSize.y;
                bool inner = all(radii < _TileRadii.x);
                bool middle = any(radii <= _TileRadii.y);
                bool outer = any(radii > _TileRadii.y);
                uint2 gridCoord = (uint2(screenUV - 0.5));
                bool isCovered = inner && !middle;
                uint2 uv00; uint2 uv10; uint2 uv11; uint2 uv01;
                if (isCovered || outer)
                {
                    uv00 = gridCoord; uv10 = gridCoord + uint2(1, 0); uv11 = gridCoord + uint2(1, 1); uv01 = gridCoord + int2(0, 1);
                    if (outer)
                    {
                        uint4 uvCorners = uint4(uv00.x, uv11.x, uv00.y, uv11.y);
                        uint4 uvGrid0 = ((uvCorners >> 1) + 1) & 1;
                        uint4 uvGrid = (uvCorners + 1) >> 1;
                        uint4 uvGrid2 = (uvCorners + 3) >> 1;
                        int2 offset = ((uvGrid.xz & 1) & uvGrid0.xz) - ((uvGrid2.xz & 1) & uvGrid0.xz); uv00 += offset;
                        offset = ((uvGrid.yz & 1) & uvGrid0.yz) - ((uvGrid2.yz & 1) & uvGrid0.yz); uv10 += offset;
                        offset = ((uvGrid.yw & 1) & uvGrid0.yw) - ((uvGrid2.yw & 1) & uvGrid0.yw); uv11 += offset;
                        offset = ((uvGrid.xw & 1) & uvGrid0.xw) - ((uvGrid2.xw & 1) & uvGrid0.xw); uv01 += offset;
                    }
                }
                else
                {
                    uint2 uvMod = (gridCoord + 1) & 3u;
                    uv00 = gridCoord + uint2(lutX00[uvMod.y][uvMod.x], lutY00[uvMod.y][uvMod.x]);
                    uv10 = gridCoord + uint2(lutX10[uvMod.y][uvMod.x], lutY10[uvMod.y][uvMod.x]);
                    uv11 = gridCoord + uint2(lutX11[uvMod.y][uvMod.x], lutY11[uvMod.y][uvMod.x]);
                    uv01 = gridCoord + uint2(lutX01[uvMod.y][uvMod.x], lutY10[uvMod.y][uvMod.x]);
                }
                half4 col00 = LOAD_TEXTURE2D_X(_SourceTex, uv00);
                half4 col10 = LOAD_TEXTURE2D_X(_SourceTex, uv10);
                half4 col11 = LOAD_TEXTURE2D_X(_SourceTex, uv11);
                half4 colB1 = lerp(col00, col10, frac(screenUV.x - 0.5));
                half4 colB2 = lerp(col10, col11, frac(screenUV.x - 0.5));
                return lerp(colB1, colB2, frac(screenUV.y - 0.5));
            }
#endif
            half3 FetchSrc(float2 uv)
            {
#if QS_VRS
                return SampleVRS(uv).rgb;
#else
                return SAMPLE_TEXTURE2D_X(_SourceTex, sampler_SourceTex, uv).rgb;
#endif
            }

            #include "QSEffectsParams.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            float4 _QS_Dof;
            float4 _QS_DofAF;
            float SceneDist(float2 uv) { return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams); }

            // ---- QuestShade shadow map (world-space map rendered from the sun, applied per pixel from scene depth) ----
            TEXTURE2D(_QS_SMTex); SAMPLER(sampler_QS_SMTex);
            float4x4 _QS_SM;            // world -> shadow map uv (xy)
            float4 _QS_SMOrigin;        // xyz: light-side origin, w: on (darkness 0..1, 0 = off)
            float4 _QS_SMDir;           // xyz: light direction, w: 1 / map texel size in uv (for PCF)
            float4 _QS_SMOpts;          // x: 1 = use CPU eye matrices, y: 1 = flip screen y, z: depth bias (m), w: fade range (m)
            float4x4 _QS_InvVP0, _QS_InvVP1;

            float3 QSWorldFromDepth(float2 uv, float raw)
            {
            #if !UNITY_REVERSED_Z
                raw = lerp(UNITY_NEAR_CLIP_VALUE, 1, raw);
            #endif
                if (_QS_SMOpts.y > 0.5) uv.y = 1 - uv.y;
                UNITY_BRANCH if (_QS_SMOpts.x > 0.5)
                {
                    float4x4 inv = unity_StereoEyeIndex == 0 ? _QS_InvVP0 : _QS_InvVP1;
                    return ComputeWorldSpacePosition(uv, raw, inv);
                }
                return ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
            }

            // 1 = lit, 0 = shadowed, with bilinear blending between the 4 nearest shadow-map texels.
            half QSLit(float2 uv, float d, float t)
            {
                float2 p = uv / t - 0.5;
                float2 f = frac(p);
                float2 b = (floor(p) + 0.5) * t;
                half a0 = SAMPLE_TEXTURE2D_LOD(_QS_SMTex, sampler_QS_SMTex, b, 0).r >= d ? 1 : 0;
                half a1 = SAMPLE_TEXTURE2D_LOD(_QS_SMTex, sampler_QS_SMTex, b + float2(t, 0), 0).r >= d ? 1 : 0;
                half a2 = SAMPLE_TEXTURE2D_LOD(_QS_SMTex, sampler_QS_SMTex, b + float2(0, t), 0).r >= d ? 1 : 0;
                half a3 = SAMPLE_TEXTURE2D_LOD(_QS_SMTex, sampler_QS_SMTex, b + float2(t, t), 0).r >= d ? 1 : 0;
                return lerp(lerp(a0, a1, f.x), lerp(a2, a3, f.x), f.y);
            }

            float4 _QS_SMParams; // x: world size of one shadow-map texel (m)

            // Sun: world-space map along the light direction.
            half QSSunShadow(float3 w, float3 nrm, float vd)
            {
                // Only surfaces that face the light get a cast shadow; it fades out on sides lit at a glancing angle, so a
                // hand over the edge of a crate doesn't smear a long shadow down its side.
                float ndl = dot(nrm, -_QS_SMDir.xyz);
                half face = saturate((ndl - 0.08) * 4);
                if (face <= 0) return 1;
                // Push the lookup off the surface along its normal: stops the surface shadowing itself (light/dark stripes on guns).
                // Close to your eyes (your own body, held items) a shadow-map texel covers many screen pixels and the body copy
                // shadows the body: push further off the surface and blur wider there, so it looks smooth instead of pixely.
                float close = lerp(3.0, 1.0, saturate((vd - 0.4) / 1.6));
                w += nrm * (_QS_SMParams.x * 1.5 * close);
                float2 suv = mul(_QS_SM, float4(w, 1)).xy;
                float2 edge = min(suv, 1 - suv);
                if (edge.x <= 0 || edge.y <= 0) return 1;
                float d = dot(w - _QS_SMOrigin.xyz, _QS_SMDir.xyz) - _QS_SMOpts.z * close;
                float t = _QS_SMDir.w;
                float r = max(_QS_SMOpts.w, 0.5) * t * close;
                // One bilinear-filtered comparison (4 texels): a crisp edge one shadow-map texel wide. The old "4 quick samples,
                // then blur near edges" left a sharp edge with a second, lighter ring around it; this is sharper and 4x cheaper.
                half lit = QSLit(suv, d, t);
                half shade = (1 - lit) * face * saturate(min(edge.x, edge.y) * 10); // fade out near the edge of the map
                return 1 - shade * _QS_SMOrigin.w;
            }

            // Flashlight: a perspective map from the held flashlight, storing distance from the light.
            TEXTURE2D(_QS_FLTex); SAMPLER(sampler_QS_FLTex);
            float4x4 _QS_FLM;     // world -> flashlight clip
            float4 _QS_FLPos;     // xyz: flashlight position, w: strength (0 = off)
            float4 _QS_FLDir;     // xyz: beam direction, w: cos(outer half-angle)
            float4 _QS_FLParams;  // x: range, y: 1/res, z: cos(inner half-angle), w: bias (m)
            float4 _QS_FLParams2; // x: extra flashlight light on every surface (0 = off)
            float _QS_FLURP;      // 1 = the engine is drawing a shadow map for the flashlight (slice 0)
            TEXTURE2D_SHADOW(_AdditionalLightsShadowmapTexture); SAMPLER_CMP(sampler_AdditionalLightsShadowmapTexture);
            float4x4 _AdditionalLightsWorldToShadow[4];

            // x: flashlight shadow (1 = not blocked), y: how strongly the flashlight lights this spot (0..1)
            half QSLitFL(float2 uv, float d, float t)
            {
                float2 p = uv / t - 0.5;
                float2 f = frac(p);
                float2 b = (floor(p) + 0.5) * t;
                half a0 = SAMPLE_TEXTURE2D_LOD(_QS_FLTex, sampler_QS_FLTex, b, 0).r >= d ? 1 : 0;
                half a1 = SAMPLE_TEXTURE2D_LOD(_QS_FLTex, sampler_QS_FLTex, b + float2(t, 0), 0).r >= d ? 1 : 0;
                half a2 = SAMPLE_TEXTURE2D_LOD(_QS_FLTex, sampler_QS_FLTex, b + float2(0, t), 0).r >= d ? 1 : 0;
                half a3 = SAMPLE_TEXTURE2D_LOD(_QS_FLTex, sampler_QS_FLTex, b + float2(t, t), 0).r >= d ? 1 : 0;
                return lerp(lerp(a0, a1, f.x), lerp(a2, a3, f.x), f.y);
            }

            half2 QSFlash(float3 w, float3 nrm)
            {
                UNITY_BRANCH if (_QS_FLPos.w <= 0) return half2(1, 0);
                float3 v = w - _QS_FLPos.xyz;
                float dist = length(v);
                if (dist < 0.3 || dist > _QS_FLParams.x) return half2(1, 0);
                float3 l = v / dist;
                float cosA = dot(l, _QS_FLDir.xyz);
                if (cosA <= _QS_FLDir.w) return half2(1, 0); // outside the beam
                // Gentle angle term: screen-space normals are noisy along edges, a hard cut made the light blotchy.
                half face = saturate(dot(nrm, -l) * 1.2 + 0.15);
                if (face <= 0) return half2(1, 0);
                float3 wo = w + nrm * (0.008 * dist + 0.02); // off the surface (more further away, texels grow with distance): no self-shadow stripes on hands
                float4 c = mul(_QS_FLM, float4(wo, 1));
                if (c.w <= 0) return half2(1, 0);
                float2 fuv = c.xy / c.w * 0.5 + 0.5;
                float2 edge = min(fuv, 1 - fuv);
                if (edge.x <= 0 || edge.y <= 0) return half2(1, 0);
                float d = length(wo - _QS_FLPos.xyz) - _QS_FLParams.w;
                float t = _QS_FLParams.y;
                // One bilinear-filtered comparison: crisp edges, no ring, cheap.
                half lit = QSLitFL(fuv, d, t);
                // Level geometry (walls, floors...) from the engine's own shadow map for this flashlight, so light and shadows
                // don't pass through walls. Our map above covers you, held items and props.
                UNITY_BRANCH if (_QS_FLURP > 0.5)
                {
                    float4 sc = mul(_AdditionalLightsWorldToShadow[0], float4(wo, 1));
                    if (sc.w > 0)
                    {
                        sc.xyz /= sc.w;
                        if (sc.x > 0 && sc.x < 1 && sc.y > 0 && sc.y < 1)
                            lit *= SAMPLE_TEXTURE2D_SHADOW(_AdditionalLightsShadowmapTexture, sampler_AdditionalLightsShadowmapTexture, sc.xyz);
                    }
                }
                // Smooth falloffs: soft beam edge and a light-like fade with distance (bright close, long gentle tail),
                // instead of straight lines that left bright bands and dark patches.
                half cone = smoothstep(_QS_FLDir.w, _QS_FLParams.z, cosA);
                float k = dist / (_QS_FLParams.x * 0.3);
                half near = saturate(1 / (1 + k * k) - 0.05 * dist / _QS_FLParams.x);
                half beam = face * cone * near;
                return half2(1 - (1 - lit) * beam * _QS_FLPos.w, lit * beam);
            }

            half QSShadow(float2 uv)
            {
                UNITY_BRANCH if (_QS_SMOrigin.w <= 0 && _QS_FLPos.w <= 0) return 1;
                // World position and surface direction first (screen derivatives must come before any per-pixel branch).
                float raw = SampleSceneDepth(uv);
                float3 w = QSWorldFromDepth(uv, raw);
            #if UNITY_REVERSED_Z
                float3 wn = QSWorldFromDepth(uv, 1);
            #else
                float3 wn = QSWorldFromDepth(uv, 0);
            #endif
                float3 nrm = normalize(cross(ddy(w), ddx(w)));
                nrm = dot(nrm, wn - w) < 0 ? -nrm : nrm; // face the viewer
                if (Linear01Depth(raw, _ZBufferParams) > 0.995) return 1; // sky / nothing there
                half s = 1;
                UNITY_BRANCH if (_QS_SMOrigin.w > 0) s = QSSunShadow(w, nrm, distance(w, wn));
                half2 f = QSFlash(w, nrm);
                // Where the flashlight reaches, it lights up the sun shadow (a flashlight pointed into a shadow brightens it);
                // things blocking the beam still throw their own flashlight shadow.
                s = lerp(s, 1, saturate(f.y * 1.5));
                // QuestShade's own flashlight light on top: BONELAB's flashlight only lights materials that take extra lights, so whole
                // walls and floors stayed dark. This brightens every surface the beam actually reaches (not through blockers).
                return s * f.x * (1 + f.y * _QS_FLParams2.x);
            }
            float FocusDist()
            {
                float f = _QS_Dof.y;
                UNITY_BRANCH if (f <= 0)
                {
                    float r = _QS_DofAF.z * 0.25;
                    f = min(min(SceneDist(_QS_DofAF.xy), min(SceneDist(_QS_DofAF.xy + float2(r, 0)), SceneDist(_QS_DofAF.xy - float2(r, 0)))),
                            min(SceneDist(_QS_DofAF.xy + float2(0, r)), SceneDist(_QS_DofAF.xy - float2(0, r))));
                }
                return clamp(f, 0.05, 60.0);
            }
            half CoC(float d, float f)
            {
                return d >= f ? pow(saturate((d - f) / d * _QS_Dof.x), max(_QS_Dof.z, 0.1))
                              : pow(saturate((f - d) / f), max(_QS_Dof.w, 0.1)) * _QS_DofAF.w;
            }

            float4 _QS_Live;
            float4 _QS_LiveBloom;
            float4 _QS_LiveTint;
            float4 _QS_LiveFlare;
            float4 _QS_God;
            float4 _QS_SunL;
            float4 _QS_SunR;
            half3 FetchRaw(float2 uv) { return SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_SourceTex, uv, 0).rgb; }

            half3 FetchMainFrom(half3 c, float2 uv)
            {
                UNITY_BRANCH if (_QS_Live.y > 0 && _QS_Dof.x > 0)
                {
                    float f = FocusDist();
                    float R = CoC(SceneDist(uv), f) * _QS_Live.z;
                    UNITY_BRANCH if (R >= 0.75)
                    {
                        half3 acc = c; half wsum = 1;
                        UNITY_LOOP for (int k = 0; k < 12; k++)
                        {
                            float r = sqrt((k + 0.5) / 12.0) * R;
                            float th = k * 2.39996323;
                            float2 o = float2(cos(th), sin(th)) * r * _QS_SrcTexel.xy;
                            half3 s = FetchRaw(uv + o);
                            half w = saturate(CoC(SceneDist(uv + o), f) * _QS_Live.z - r + 1.0);
                            half hl = max(s.r, max(s.g, s.b));
                            w *= 1.0 + min(_QS_Live.w * 2.0 * hl * hl * hl, 3.0);
                            acc += s * w; wsum += w;
                        }
                        c = acc / wsum;
                    }
                }
                UNITY_BRANCH if (_QS_Live.x > 0)
                {
                    half3 b = 0;
                    float2 rad = _QS_LiveBloom.z * float2(_QS_SrcTexel.w * _QS_SrcTexel.x, 1);
                    half knee = max(_QS_LiveBloom.y, 1e-4);
                    float2 cell = floor(frac(uv * _QS_SrcTexel.zw * 0.5) * 2.0);
                    float phase = cell.x + cell.y * 2.0;
                    float sp, cp;
                    sincos(phase * 2.3999632, sp, cp);
                    float2 rx = float2(cp, -sp) * rad.x, ry = float2(sp, cp) * rad.y;
                    UNITY_UNROLL for (int k = 0; k < 16; k++)
                    {
                        float a = k * 9.5998528;
                        float2 dir = float2(cos(a), sin(a));
                        float2 o = float2(dot(dir, rx), dot(dir, ry)) * sqrt((k * 4.0 + phase + 0.5) / 64.0);
                        half3 s = FetchRaw(uv + o);
                        half br = max(s.r, max(s.g, s.b));
                        half soft = clamp(br - _QS_LiveBloom.x + knee, 0, 2 * knee);
                        soft = soft * soft / (4 * knee + 1e-4);
                        b += s * (max(soft, br - _QS_LiveBloom.x) / max(br, 1e-4)) * exp(-2.5 * (k * 4.0 + 0.5) / 64.0);
                    }
                    c += b * (2.72 / 16.0 * exp(-0.0390625 * phase)) * _QS_Live.x * _QS_LiveTint.rgb;
                    UNITY_BRANCH if (_QS_LiveFlare.x > 0)
                    {
                        float2 g = (0.5 - uv) * _QS_LiveFlare.y;
                        half thr = max(_QS_LiveBloom.x, 0.85);
                        half3 f = 0;
                        UNITY_UNROLL for (int k = 1; k <= 4; k++)
                        {
                            float2 p = uv + g * k;
                            half edge = all(p == saturate(p)) ? 1 : 0;
                            half3 s = FetchRaw(saturate(p));
                            half br = max(s.r, max(s.g, s.b));
                            half w = pow(saturate(1 - length(0.5 - p) * 1.414), 4) * edge;
                            half3 tint = k == 1 ? half3(1.0, 0.75, 0.5) : k == 2 ? half3(0.55, 0.8, 1.0) : k == 3 ? half3(1.0, 0.6, 0.85) : half3(0.7, 1.0, 0.75);
                            f += s * (saturate(br - thr) / max(br, 1e-4)) * w * tint;
                        }
                        float gl = length(g);
                        float2 hp = uv + (gl > 1e-5 ? g / gl : float2(0, 0)) * _QS_LiveFlare.z;
                        half hedge = all(hp == saturate(hp)) ? 1 : 0;
                        half3 hs = FetchRaw(saturate(hp));
                        half hb = max(hs.r, max(hs.g, hs.b));
                        half hw = pow(saturate(1 - length(0.5 - hp) * 1.6), 6) * hedge;
                        f += hs * (saturate(hb - thr) / max(hb, 1e-4)) * hw * half3(1.0, 0.9, 0.8);
                        c += f * _QS_LiveFlare.x;
                    }
                }
                UNITY_BRANCH if (_QS_God.x > 0)
                {
                    float4 sun = unity_StereoEyeIndex == 0 ? _QS_SunL : _QS_SunR;
                    UNITY_BRANCH if (sun.z > 0.001)
                    {
                        float2 d = (sun.xy - uv) * (_QS_God.y / 10.0);
                        float j = frac(sin(dot(uv * 431.7, float2(12.9898, 78.233))) * 43758.5453);
                        float2 p = uv + d * j;
                        half3 acc = 0; half w = 1;
                        UNITY_UNROLL for (int g = 0; g < 10; g++)
                        {
                            half3 s = FetchRaw(p);
                            half br = max(s.r, max(s.g, s.b));
                            acc += s * saturate((br - _QS_God.z) * 4) * w;
                            w *= 0.88; p += d;
                        }
                        c += acc * (_QS_God.x * sun.z / 10.0);
                    }
                }
                return c;
            }

            #define QS_FETCH(uv) FetchSrc(uv)
            #define QS_FETCH_MAIN_FROM(c, uv) FetchMainFrom(c, uv)
            #include "QSEffects.hlsl"

#if defined(QS_PREPASS)
            half4 Prepass(float2 uv)
            {
                half3 c = FetchSrc(uv);
                c = (any(isnan(c)) || any(isinf(c))) ? half3(0, 0, 0) : min(max(c, 0), 32);
                c *= QSShadow(uv); // shadows in camera photos/videos (the camera uses this pass when bloom / depth of field are on)
                half coc = 0;
                UNITY_BRANCH if (_QS_Dof.x > 0) coc = CoC(SceneDist(uv), FocusDist());
                coc = isnan(coc) ? 0 : saturate(coc);
                return half4(c, coc);
            }
#endif

            half4 Fragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
#if defined(QS_PREPASS)
                return Prepass(input.uv);
#else
                half4 outc = half4(QSStack(input.uv, input.positionCS.xy), 1);
                outc.rgb *= QSShadow(input.uv);
                #ifdef _LINEAR_TO_SRGB_CONVERSION
                outc = LinearToSRGB(outc);
                #endif
                return outc;
#endif
            }
            ENDHLSL
        }
    }
}
