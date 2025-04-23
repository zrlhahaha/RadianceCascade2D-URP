Shader "Unlit/2DLighting"
{
    Properties
    {
        _MainTex("Diffuse", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100
        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
        Tags {"Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            #pragma vertex CombinedShapeLightVertex
            #pragma fragment CombinedShapeLightFragment

            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_0 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_1 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_2 __
            #pragma multi_compile USE_SHAPE_LIGHT_TYPE_3 __
            #pragma multi_compile _ DEBUG_DISPLAY

            #pragma enable_d3d11_debug_symbols

            struct i2v
            {
                float3 positionOS   : POSITION;
                float4 color        : COLOR;
                float2  uv          : TEXCOORD0;
            };

            struct v2f
            {
                float4  pos_clip  : SV_POSITION;
                half4   color       : COLOR;
                float2  uv          : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            half4 _MainTex_ST;

            TEXTURE2D(_ScreenSpaceIrradiance);
            float4 _ScreenSpaceIrradianceSize; // (tex_width, tex_height, 1 / tex_width, 1 / tex_height)
            SamplerState sampler_point_clamp;


            v2f CombinedShapeLightVertex(i2v v)
            {
                v2f o = (v2f)0;

                o.pos_clip = TransformObjectToHClip(v.positionOS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                o.color = v.color;
                return o;
            }

            half4 CombinedShapeLightFragment(v2f i) : SV_Target
            {
                half4 base_color = i.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);

                int2 coord = i.pos_clip.xy / _ScreenParams.xy  * _ScreenSpaceIrradianceSize.xy;

                int2 base_probe = floor(coord); // the bottom left probe
                float2 weight = frac(coord); // the fraction part of the coordinate will be used for bilinear interpolation

                float4 irradiance1 = SAMPLE_TEXTURE2D(_ScreenSpaceIrradiance, sampler_point_clamp, base_probe * _ScreenSpaceIrradianceSize.zw);
                float4 irradiance2 = SAMPLE_TEXTURE2D(_ScreenSpaceIrradiance, sampler_point_clamp, (base_probe + int2(1,0)) * _ScreenSpaceIrradianceSize.zw);
                float4 irradiance3 = SAMPLE_TEXTURE2D(_ScreenSpaceIrradiance, sampler_point_clamp, (base_probe + int2(0,1)) * _ScreenSpaceIrradianceSize.zw);
                float4 irradiance4 = SAMPLE_TEXTURE2D(_ScreenSpaceIrradiance, sampler_point_clamp, (base_probe + int2(1,1)) * _ScreenSpaceIrradianceSize.zw);

                float4 x1 = lerp(irradiance1, irradiance2, weight.x);
                float4 x2 = lerp(irradiance3, irradiance4, weight.x);
                float4 irradiance = lerp(x1, x2, weight.y);

                float4 color = irradiance * base_color;
                return float4(color.rgb, 1);
            }
            ENDHLSL
        }
    }
}
