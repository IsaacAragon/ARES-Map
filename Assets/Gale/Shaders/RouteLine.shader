// Línea de la ruta de Curiosity apoyada en el terreno: color con transparencia, sin escribir
// profundidad y con Offset hacia la cámara para que no parpadee sobre el TIN.
// V (0..1) cruza la cinta: borde suavizado. U avanza por la ruta: rayas suaves opcionales (_Dash).
// La ruta planificada usa además: _Reveal (U revelada, para trazarla animada; por defecto todo visible),
// color por vértice (_UseVertexColor: precaución, no segura) y rayas que avanzan (_DashSpeed).
Shader "Gale/RouteLine"
{
    Properties
    {
        _Color ("Color", Color) = (0.25, 0.9, 1, 0.75)
        _Dash ("Rayas por unidad de U (0 = continua)", Float) = 0
        _HalfWidth ("Medio ancho de la cinta (u)", Float) = 0.25
        _RefDist ("Distancia a partir de la que la cinta se ensancha (u)", Float) = 40
        _Reveal ("U revelada (trazado animado)", Float) = 1000000
        _UseVertexColor ("Multiplicar por el color del vértice", Float) = 0
        _DashSpeed ("Avance de las rayas (U por segundo)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-40" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        Offset -3, -3
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Dash, _HalfWidth, _RefDist, _Reveal, _UseVertexColor, _DashSpeed;

            // normal = dirección lateral hacia fuera (horizontal, unitaria) de cada borde de la cinta.
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                // De cerca, los bordes van apoyados en el suelo (ancho real); de lejos se ensancha
                // con la distancia para mantener un grosor mínimo en pantalla.
                float3 w = mul(unity_ObjectToWorld, v.vertex).xyz;
                float k = max(1.0, distance(w, _WorldSpaceCameraPos) / _RefDist);
                w += normalize(mul((float3x3)unity_ObjectToWorld, v.normal)) * _HalfWidth * (k - 1.0);
                o.pos = mul(UNITY_MATRIX_VP, float4(w, 1.0));
                o.uv = v.uv;
                o.color = _UseVertexColor > 0.5 ? v.color : fixed4(1, 1, 1, 1);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                clip(_Reveal - i.uv.x);
                float edge = smoothstep(0.0, 0.25, i.uv.y) * smoothstep(1.0, 0.75, i.uv.y);
                float dash = _Dash > 0 ? step(0.35, frac((i.uv.x - _Time.y * _DashSpeed) * _Dash)) : 1.0;
                fixed4 c = _Color * i.color;
                return fixed4(c.rgb, c.a * edge * lerp(0.45, 1.0, dash));
            }
            ENDCG
        }
    }
}
