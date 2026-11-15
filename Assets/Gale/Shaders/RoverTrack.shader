// Rodadas del rover: multiplica el color del suelo (solo oscurece; blanco = sin cambio),
// sin escribir profundidad y con Offset hacia la cámara para que no parpadee sobre el TIN.
// La textura es el dibujo de la banda de rodadura (gris); _Strength mezcla hacia blanco.
// En los extremos de la cinta (V = 0 y 1) se desvanece, y también con la distancia (_FadeStart/_FadeEnd).
Shader "Gale/RoverTrack"
{
    Properties
    {
        _MainTex ("Dibujo de la rodada (gris)", 2D) = "white" {}
        _Strength ("Intensidad", Range(0, 1)) = 0.85
        _FadeStart ("Desvanecer desde (u)", Float) = 150
        _FadeEnd ("Desvanecer hasta (u)", Float) = 600
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-50" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend DstColor Zero
        ZWrite Off
        ZTest LEqual
        Cull Off
        Offset -2, -2
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Strength, _FadeStart, _FadeEnd;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; float age : TEXCOORD2; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float3 w = mul(unity_ObjectToWorld, v.vertex).xyz;
                float d = distance(w, _WorldSpaceCameraPos);
                o.fade = 1.0 - saturate((d - _FadeStart) / max(1e-3, _FadeEnd - _FadeStart));
                o.age = v.color.a;   // 1 = rodada reciente; lo usa el desvanecido opcional por antigüedad
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed g = tex2D(_MainTex, i.uv).r;
                float edge = smoothstep(0.0, 0.18, i.uv.y) * smoothstep(1.0, 0.82, i.uv.y);
                float k = _Strength * edge * i.fade * i.age;
                return fixed4(lerp(1.0.xxx, g.xxx, k), 1);
            }
            ENDCG
        }
    }
}
