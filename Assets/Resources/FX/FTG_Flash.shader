// 受击闪白（手写 HLSL / URP）。
// 用法：命中瞬间把角色所有 Renderer 的材质整体换成本材质 2~4 帧再换回——
// 经典街霸式"打击定格闪"。整体换材对原 Shader（Toon 等）零侵入，任何角色通用。
// SRP Batcher 兼容：材质属性全部收进 UnityPerMaterial CBUFFER。
Shader "FTG/Flash"
{
    Properties
    {
        _FlashColor ("Flash Color", Color) = (1, 1, 1, 1)
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
            Name "Flash"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _FlashColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return _FlashColor;
            }
            ENDHLSL
        }
    }
}
