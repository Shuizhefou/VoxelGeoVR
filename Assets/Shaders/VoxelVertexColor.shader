Shader "VoxelGeoVR/VoxelVertexColor"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        // 2 = Back（默认单面），0 = Off（剖面双面 / 近处封闭壳材质用）
        _CullMode ("Cull Mode", Float) = 2
        // 1 = 普通 chunk 网格（会被近处球形挖空裁掉）；0 = 近处封闭壳网格（不裁）
        _CarveReceiver ("Carve Receiver", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_CullMode]
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                float _CullMode;
                float _CarveReceiver;
            CBUFFER_END

            // ------------------------------------------------------------------
            // 全局参数（VoxelDisplayController 每帧用 Shader.SetGlobal* 推送）
            // ------------------------------------------------------------------
            float4 _SectionNormal;      // 世界空间平面法线（xyz）
            float  _SectionD;           // 平面方程 dot(worldPos, n) + d >= 0 保留
            float  _SectionEnabled;     // 0/1

            float  _FadeEnabled;        // 0/1
            float  _FadeRadius;         // 格
            float  _FadeStrength;       // 0..1，1 = 完全透明
            float  _FadeSoftness;       // 边缘过渡宽度（格）
            float4 _FadeCenter;         // 主相机世界坐标

            float  _CarveEnabled;       // 0/1，A 功能开启时把普通网格在半径内裁掉
            float4 _CarveCenter;        // 主相机位置
            float  _CarveRadius;        // 与 A 的虚化半径一致

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float  fogFactor  : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionWS = positionWS;
                o.color = v.color * _Tint;
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            // 4x4 Bayer 阈值。用递推公式生成，避免移动端动态索引常量数组。
            float Bayer2(uint x, uint y)
            {
                return (float)(((x ^ y) << 1) + y);
            }

            float Bayer4(float2 pixel)
            {
                uint x = (uint)pixel.x & 3u;
                uint y = (uint)pixel.y & 3u;
                return (4.0 * Bayer2(x >> 1, y >> 1) + Bayer2(x & 1u, y & 1u)) / 16.0;
            }

            half4 frag (Varyings i) : SV_Target
            {
                // B：任意方向剖面。按片元世界坐标裁剪，切面不会出现整块三角形缺失。
                if (_SectionEnabled > 0.5)
                {
                    clip(dot(i.positionWS, _SectionNormal.xyz) + _SectionD);
                }

                // A：普通 chunk 网格在相机球形半径内让位给"近处封闭壳"网格。
                // 壳网格（_CarveReceiver=0）自己不被裁，负责产生新的内壁面。
                if (_CarveEnabled > 0.5 && _CarveReceiver > 0.5)
                {
                    if (distance(_CarveCenter.xyz, i.positionWS) < _CarveRadius + 1.0)
                        discard;
                }

                // A：普通网格按距离渐隐；近处封闭壳整层按"透明程度"抖动，
                // 否则壳刚好在半径处，按距离算会永远是不透明的。
                if (_FadeEnabled > 0.5)
                {
                    float alpha;
                    if (_CarveReceiver < 0.5)
                    {
                        alpha = 1.0 - _FadeStrength;
                    }
                    else
                    {
                        float dist = distance(_FadeCenter.xyz, i.positionWS);
                        float edge = 1.0 - smoothstep(_FadeRadius - _FadeSoftness, _FadeRadius, dist);
                        alpha = 1.0 - _FadeStrength * edge;
                    }
                    float threshold = Bayer4(i.positionCS.xy);
                    clip(alpha - threshold);
                }

                half3 c = MixFog(i.color.rgb, i.fogFactor);
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
