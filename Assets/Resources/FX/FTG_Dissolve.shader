// KO 溶解（手写 HLSL / URP）。
// 思路：世界坐标驱动的程序化 hash 噪声做溶解阈值裁剪（无需噪声贴图），
// 裁剪边缘一圈自发光描边。KO 时整体换材并把 _Threshold 从 0 推到 1，
// 角色化作燃边碎片消散——剪影式溶解，不依赖原材质贴图，任何角色通用。
Shader "FTG/Dissolve"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.08, 0.08, 0.1, 1)
        _EdgeColor ("Edge Color", Color) = (1, 0.55, 0.1, 1)
        _Threshold ("Dissolve Threshold", Range(0, 1)) = 0
        _EdgeWidth ("Edge Width", Range(0.001, 0.2)) = 0.06
        _NoiseScale ("Noise Scale", Float) = 24
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "Dissolve"
            Cull Off // 溶出窟窿后能看见背面，双面渲染

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _EdgeColor;
                float _Threshold;
                float _EdgeWidth;
                float _NoiseScale;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            // 三维 hash 噪声：确定性、无采样、无贴图依赖
            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // 值噪声：hash 的三线性插值，比裸 hash 柔和，溶解边缘成"块状剥落"而非噪点
            float ValueNoise(float3 p)
            {
                float3 cell = floor(p);
                float3 f = frac(p);
                f = f * f * (3.0 - 2.0 * f); // smoothstep 插值

                float n000 = Hash31(cell + float3(0, 0, 0));
                float n100 = Hash31(cell + float3(1, 0, 0));
                float n010 = Hash31(cell + float3(0, 1, 0));
                float n110 = Hash31(cell + float3(1, 1, 0));
                float n001 = Hash31(cell + float3(0, 0, 1));
                float n101 = Hash31(cell + float3(1, 0, 1));
                float n011 = Hash31(cell + float3(0, 1, 1));
                float n111 = Hash31(cell + float3(1, 1, 1));

                return lerp(
                    lerp(lerp(n000, n100, f.x), lerp(n010, n110, f.x), f.y),
                    lerp(lerp(n001, n101, f.x), lerp(n011, n111, f.x), f.y),
                    f.z);
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionHCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float noise = ValueNoise(input.positionWS * _NoiseScale);

                // 阈值推到 1 时必须完全消失：把噪声域压缩进 [edge, 1-edge]
                float cutoff = _Threshold * (1.0 + 2.0 * _EdgeWidth) - _EdgeWidth;
                clip(noise - cutoff);

                // 靠近裁剪线的一圈染成燃烧边
                float edge = smoothstep(cutoff + _EdgeWidth, cutoff, noise);
                return half4(lerp(_BaseColor.rgb, _EdgeColor.rgb, edge), 1);
            }
            ENDHLSL
        }
    }
}
