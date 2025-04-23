// Upgrade NOTE: replaced 'glstate_matrix_projection' with 'UNITY_MATRIX_P'

Shader "RadianceCascades/LightSource"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Intensity("Intensity", Float) = 1.0
    }
    SubShader
    {
        Name "LightSource"
        Tags { "LightMode"="LightSource" }
        LOD 100
        Cull Off
        ZWrite Off
        ZTest Off

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #pragma enable_d3d11_debug_symbols
            #include "UnityCG.cginc"

            #define NUM_SDF_CASCADES 2

            float _Intensity;
            float4x4 _SDFCameraVP;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : Color;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = mul(_SDFCameraVP, mul(unity_ObjectToWorld, v.vertex));
                o.color = v.color * _Intensity;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return float4(i.color.rgb, 1);
            }
            ENDCG
        }
    }
}
