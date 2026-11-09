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
            float4 _QS_God;
            float4 _QS_SunL;
            float4 _QS_SunR;
            half3 FetchRaw(float2 uv) { return SAMPLE_TEXTURE2D_X_LOD(_SourceTex, sampler_SourceTex, uv, 0).rgb; }

            half3 FetchMain(float2 uv)
            {
                half3 c = FetchSrc(uv);
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
                    UNITY_UNROLL for (int k = 0; k < 12; k++)
                    {
                        float th = k * 0.5235988 + (k & 1) * 0.26;
                        float2 o = float2(cos(th), sin(th)) * rad * (k < 6 ? 0.4 : 1.0);
                        half3 s = FetchRaw(uv + o);
                        half br = max(s.r, max(s.g, s.b));
                        half knee = max(_QS_LiveBloom.y, 1e-4);
                        half soft = clamp(br - _QS_LiveBloom.x + knee, 0, 2 * knee);
                        soft = soft * soft / (4 * knee + 1e-4);
                        b += s * (max(soft, br - _QS_LiveBloom.x) / max(br, 1e-4)) * (k < 6 ? 1.5 : 0.5);
                    }
                    c += b / 12.0 * _QS_Live.x * _QS_LiveTint.rgb;
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
            #define QS_FETCH_MAIN(uv) FetchMain(uv)
            #include "QSEffects.hlsl"

#if defined(QS_PREPASS)
            half4 Prepass(float2 uv)
            {
                half3 c = FetchSrc(uv);
                c = (any(isnan(c)) || any(isinf(c))) ? half3(0, 0, 0) : min(max(c, 0), 32);
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
