Shader "Hidden/SceneGuard/SceneViewLitFallback"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.78, 0.78, 0.78, 1)
        _LightDir ("Light Direction", Vector) = (0.3, -0.8, 0.5, 0)
        _LightColor ("Light Color", Color) = (1, 0.96, 0.84, 1)
        _AmbientColor ("Ambient Color", Color) = (0.21, 0.23, 0.26, 1)
        _LightingScale ("Lighting Scale", Range(0, 2)) = 0.55
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _BaseColor;
            float4 _LightDir;
            fixed4 _LightColor;
            fixed4 _AmbientColor;
            half _LightingScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normalWS = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 normal = normalize(i.normalWS);
                float3 lightDir = normalize(_LightDir.xyz);
                // Half-Lambert keeps contrast without blowing out HDR SceneView RT.
                float ndl = dot(normal, -lightDir) * 0.5 + 0.5;
                fixed3 ambient = _AmbientColor.rgb * 0.35;
                fixed3 diffuse = _LightColor.rgb * ndl * 0.65;
                fixed3 lit = _BaseColor.rgb * (ambient + diffuse) * _LightingScale;
                return fixed4(saturate(lit), 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
