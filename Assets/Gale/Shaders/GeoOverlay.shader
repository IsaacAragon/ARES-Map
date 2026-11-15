// Capa temática (geología, material del suelo) pintada sobre el terreno.
// La textura se proyecta en planta con la posición de mundo (X = este, Z = norte): _GeoRect es el
// rectángulo de la imagen en unidades Unity (xMin, zMin, 1/ancho, 1/alto), así una sola textura
// cubre todos los bloques del mosaico sin tocar sus UV ni sus materiales.
// Se dibuja la misma malla del terreno otra vez (GeoLayers, Graphics.RenderMesh), mezclada por alfa
// y sombreada con la luz principal para que el relieve se siga leyendo bajo el color.
Shader "Gale/GeoOverlay"
{
    Properties
    {
        _GeoTex ("Capa (color, alfa = con dato)", 2D) = "black" {}
        _GeoRect ("Rectángulo en mundo (xMin, zMin, 1/ancho, 1/alto)", Vector) = (0, 0, 1, 1)
        _Opacity ("Opacidad", Range(0, 1)) = 0.72
        _Shade ("Sombreado del relieve", Range(0, 1)) = 0.85
    }
    SubShader
    {
        // Antes que las rodadas del rover (Transparent-50), que así quedan encima de la capa.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Off
        Offset -1, -1
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_GeoTex);
            SAMPLER(sampler_GeoTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _GeoTex_ST;
                float4 _GeoRect;
                half _Opacity;
                half _Shade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                // Misma cadena que URP/Lit (objeto -> mundo -> clip): la profundidad coincide con la del terreno.
                float3 positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.uv = (positionWS.xz - _GeoRect.xy) * _GeoRect.zw;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_GeoTex, sampler_GeoTex, i.uv);
                // Fuera del rectángulo de la imagen no hay dato.
                float2 inside = step(0.0, i.uv) * step(i.uv, 1.0);
                half alpha = c.a * _Opacity * inside.x * inside.y;
                half ndl = saturate(dot(normalize(i.normalWS), _MainLightPosition.xyz));
                half3 rgb = c.rgb * lerp(1.0, 0.45 + 0.75 * ndl, _Shade);
                rgb = MixFog(rgb, i.fogFactor);
                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }
}
