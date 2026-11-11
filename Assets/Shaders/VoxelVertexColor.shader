Shader "VoxelGeoVR/VoxelVertexColor"
{
    Properties
    {
        _Tint ("Tint", Color) = (1,1,1,1)
        // 2 = Back，0 = Off（剖面双面 / 包络壳材质用）
        _CullMode ("Cull Mode", Float) = 2
        // 1 = 不参与虚化（A 的岩层包络面）；0 = 正常按距离虚化
        _NoFade ("No Fade", Float) = 0
        // 1 = 剖面盖面（不受剖切影响）；0 = 普通 chunk / 包络
        _SectionCap ("Section Cap", Float) = 0
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
                float _NoFade;
                float _SectionCap;
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
                // B：任意方向剖面。只在剖面相机渲染期间打开；盖面材质 _SectionCap=1 不裁。
                if (_SectionEnabled > 0.5 && _SectionCap < 0.5)
                {
                    clip(dot(i.positionWS, _SectionNormal.xyz) + _SectionD);
                }

                // A：普通网格按距离点阵渐隐；包络面 _NoFade=1，保持完全不透明。
                if (_FadeEnabled > 0.5 && _NoFade < 0.5)
                {
                    float dist = distance(_FadeCenter.xyz, i.positionWS);
                    float edge = 1.0 - smoothstep(_FadeRadius - _FadeSoftness, _FadeRadius, dist);
                    float alpha = 1.0 - _FadeStrength * edge;
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
