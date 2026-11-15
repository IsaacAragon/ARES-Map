// Máscara circular para dibujar la RenderTexture del minimapa con IMGUI (Graphics.DrawTexture).
// Recorta lo que queda fuera del círculo inscrito, con un borde suavizado de _Feather (fracción del radio).
Shader "Hidden/Gale/CircleMask"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Feather ("Feather", Range(0.001, 0.1)) = 0.012
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Feather;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                // IMGUI pinta en espacio gamma: en un proyecto lineal la RenderTexture (lineal)
                // se convierte a gamma, como hace GUI.DrawTexture; si no, el mapa sale oscuro.
                #if !defined(UNITY_COLORSPACE_GAMMA)
                c.rgb = LinearToGammaSpace(c.rgb);
                #endif
                float d = length(i.uv - 0.5) * 2.0;
                c.a = 1.0 - smoothstep(1.0 - _Feather, 1.0, d);
                return c;
            }
            ENDCG
        }
    }
}
