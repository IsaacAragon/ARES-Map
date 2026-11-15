using UnityEngine;

// Georreferencia de la escena: Equirectangular Marte (R = 3 396 190 m), longitud Este,
// latitud planetocéntrica. El TIN de Gale (y los bloques que comparten su origen, como
// Babati_TIN) quitan el centro XY del DXF de Gale y se importan a 1:50.
//   X Unity = este, Z Unity = norte, Y Unity = cota (m reales × WorldScale).
public static class MarsGeo
{
    public const double Radius = 3396190.0;
    public const float WorldScale = 0.02f;              // unidades Unity por metro real (1:50)
    public const double OffsetX = 8168043.885866;       // centro X del DXF de Gale (m)
    public const double OffsetY = -319997.637211;       // centro Y del DXF de Gale (m)

    /// <summary>Unidades Unity por kilómetro real.</summary>
    public static float UnitsPerKm => WorldScale * 1000f;

    /// <summary>Lon/lat (grados, Este positivo) → posición Unity en el plano (Y = 0).</summary>
    public static Vector3 LonLatToUnity(double lonDeg, double latDeg)
    {
        double x = Radius * lonDeg * System.Math.PI / 180.0 - OffsetX;
        double y = Radius * latDeg * System.Math.PI / 180.0 - OffsetY;
        return new Vector3((float)(x * WorldScale), 0f, (float)(y * WorldScale));
    }

    /// <summary>Posición Unity → (lon, lat) en grados.</summary>
    public static Vector2d UnityToLonLat(Vector3 p)
    {
        double x = OffsetX + p.x / WorldScale;
        double y = OffsetY + p.z / WorldScale;
        return new Vector2d(x / Radius * 180.0 / System.Math.PI, y / Radius * 180.0 / System.Math.PI);
    }

    /// <summary>Cota real (m) de una altura Unity.</summary>
    public static double Elevation(float unityY) => unityY / WorldScale;

    /// <summary>Altura del terreno (Unity) bajo un punto, o null si no hay suelo.</summary>
    public static float? GroundY(Vector3 p, int mask = ~(1 << 2))
    {
        if (Physics.Raycast(new Vector3(p.x, 5000f, p.z), Vector3.down, out var hit, 20000f, mask))
            return hit.point.y;
        return null;
    }

    public readonly struct Vector2d
    {
        public readonly double lon, lat;
        public Vector2d(double lon, double lat) { this.lon = lon; this.lat = lat; }
        public override string ToString() => $"{lon:0.0000}° E, {lat:0.0000}°";
    }
}
