Shader "Hidden/SceneGuard/SceneViewSkyboxFallback"
{
    Properties
    {
        _TopColor ("Top Color", Color) = (0.45, 0.65, 0.95, 1)
        _BottomColor ("Bottom Color", Color) = (0.55, 0.55, 0.58, 1)
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _TopColor;
            fixed4 _BottomColor;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 viewDir : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 viewDir = mul((float3x3)UNITY_MATRIX_I_V, normalize(v.vertex.xyz));
                o.viewDir = viewDir;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float t = saturate(i.viewDir.y * 0.5 + 0.5);
                return lerp(_BottomColor, _TopColor, t);
            }
            ENDCG
        }
    }

    Fallback Off
}
