using UnityEngine;
using UnityEngine.InputSystem;

// Cámara con dos modos:
//  - Seguimiento (por defecto): detrás del rover. Rueda del ratón: acercar/alejar.
//  - Vista aérea (Tab): sobre el cráter. WASD desplaza, Q/E gira, rueda = altura,
//    botón derecho + arrastrar = inclinación y giro, F = centrar en el rover, C = centrar en el cráter.
//    Mientras está activa, el rover no recibe teclas (se queda frenado).
// HUD con velocidad y posición en coordenadas reales del DXF (Equirectangular Marte).
public class ChaseCamera : MonoBehaviour
{
    public RoverController target;

    [Header("Seguimiento")]
    public float distance = 9f;
    public float height = 3.5f;
    public float smooth = 6f;

    [Header("Vista aérea")]
    public Key toggleKey = Key.Tab;
    public Vector3 craterCenter = Vector3.zero;   // centro del recorte (Gale) en Unity
    public float aerialHeight = 3200f;            // altura sobre el foco (m Unity)
    public float aerialMinHeight = 50f;
    public float aerialMaxHeight = 6000f;
    [Range(20f, 90f)] public float aerialPitch = 75f;
    public float aerialYaw = 0f;                  // 0 = norte arriba
    public float transitionSpeed = 4f;

    [Header("Atmósfera (polvo en suspensión)")]
    public float chaseFogDensity = 0.0005f;
    public float aerialFogDensity = 0.00008f;

    [Header("Georreferencia (la escribe el builder)")]
    public float worldScale = 0.02f;         // metros Unity por metro real
    public double offsetX = 8168043.885866;  // centro X del DXF (m)
    public double offsetY = -319997.637211;  // centro Y del DXF (m)
    public double marsRadius = 3396190.0;

    public bool Aerial { get; private set; }

    Vector3 aerialFocus;
    bool prevToggle, prevF, prevC;
    Camera cam;
    float baseFar;

    void Awake()
    {
        cam = GetComponent<Camera>();
        baseFar = cam ? cam.farClipPlane : 8000f;
        aerialFocus = craterCenter;
        if (RenderSettings.fog) RenderSettings.fogDensity = chaseFogDensity;
    }

    public void SetAerial(bool on)
    {
        Aerial = on;
        if (target) target.inputEnabled = !on;
        if (cam) cam.farClipPlane = on ? Mathf.Max(baseFar, aerialMaxHeight * 2.5f) : baseFar;
        if (RenderSettings.fog) RenderSettings.fogDensity = on ? aerialFogDensity : chaseFogDensity;
    }

    void LateUpdate()
    {
        var kb = Keyboard.current;
        // Flanco de subida comparando con el frame anterior (fiable también con el Input System en FixedUpdate).
        bool toggle = kb != null && kb[toggleKey].isPressed;
        if (toggle && !prevToggle) SetAerial(!Aerial);
        prevToggle = toggle;

        if (Aerial) UpdateAerial(kb);
        else UpdateChase();
    }

    void UpdateChase()
    {
        if (!target) return;
        var mouse = Mouse.current;
        if (mouse != null)
            distance = Mathf.Clamp(distance - mouse.scroll.ReadValue().y * 0.01f, 4f, 200f);

        Transform t = target.transform;
        Vector3 flatFwd = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
        if (flatFwd.sqrMagnitude < 0.01f) flatFwd = transform.forward;
        Vector3 desired = t.position - flatFwd * distance + Vector3.up * (height + distance * 0.2f);

        // No meter la cámara bajo el terreno.
        int mask = ~(1 << target.gameObject.layer);
        if (Physics.Raycast(desired + Vector3.up * 500f, Vector3.down, out var hit, 1000f, mask)
            && desired.y < hit.point.y + 1.5f)
            desired.y = hit.point.y + 1.5f;

        // Al volver de la vista aérea la transición es más lenta y suave.
        float k = Vector3.Distance(transform.position, desired) > 200f ? transitionSpeed : smooth;
        transform.position = Vector3.Lerp(transform.position, desired, 1f - Mathf.Exp(-k * Time.deltaTime));
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(t.position + Vector3.up * 1.2f - transform.position),
            1f - Mathf.Exp(-k * 2f * Time.deltaTime));
    }

    void UpdateAerial(Keyboard kb)
    {
        float dt = Time.unscaledDeltaTime;
        var mouse = Mouse.current;

        if (kb != null)
        {
            Quaternion yawRot = Quaternion.Euler(0f, aerialYaw, 0f);
            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move += Vector3.forward;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move += Vector3.back;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move += Vector3.right;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move += Vector3.left;
            float speed = aerialHeight * 0.6f * (kb.leftShiftKey.isPressed ? 3f : 1f);
            aerialFocus += yawRot * move.normalized * speed * dt;
            aerialFocus.x = Mathf.Clamp(aerialFocus.x, craterCenter.x - 2000f, craterCenter.x + 2000f);
            aerialFocus.z = Mathf.Clamp(aerialFocus.z, craterCenter.z - 2000f, craterCenter.z + 2000f);

            if (kb.qKey.isPressed) aerialYaw -= 60f * dt;
            if (kb.eKey.isPressed) aerialYaw += 60f * dt;
            bool f = kb.fKey.isPressed, c = kb.cKey.isPressed;
            if (f && !prevF && target) aerialFocus = target.transform.position;
            if (c && !prevC) { aerialFocus = craterCenter; aerialYaw = 0f; }
            prevF = f; prevC = c;
        }
        if (mouse != null)
        {
            // Rueda: zoom proporcional a la altura (rápido lejos, fino cerca).
            float scroll = mouse.scroll.ReadValue().y;
            aerialHeight = Mathf.Clamp(aerialHeight * Mathf.Pow(0.999f, scroll), aerialMinHeight, aerialMaxHeight);
            if (mouse.rightButton.isPressed)
            {
                Vector2 d = mouse.delta.ReadValue();
                aerialYaw += d.x * 0.2f;
                aerialPitch = Mathf.Clamp(aerialPitch - d.y * 0.2f, 20f, 90f);
            }
        }

        Quaternion rot = Quaternion.Euler(aerialPitch, aerialYaw, 0f);
        Vector3 desired = aerialFocus - rot * Vector3.forward * aerialHeight;
        float t = 1f - Mathf.Exp(-transitionSpeed * dt);
        transform.position = Vector3.Lerp(transform.position, desired, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, rot, t);
    }

    void OnGUI()
    {
        if (!target) return;
        Vector3 p = target.transform.position;
        double x = offsetX + p.x / worldScale;
        double y = offsetY + p.z / worldScale;
        double z = p.y / worldScale;
        double lon = x / marsRadius * 180.0 / System.Math.PI;
        double lat = y / marsRadius * 180.0 / System.Math.PI;
        GUI.Box(new Rect(10, 10, 360, 146), GUIContent.none);
        string controls = Aerial
            ? "VISTA AÉREA · WASD mover · Q/E girar · rueda altura\nclic dcho+arrastrar inclinar · F rover · C cráter · Tab volver"
            : "WASD mover · Shift rápido · Espacio freno · R enderezar\nTab vista aérea";
        GUI.Label(new Rect(20, 16, 350, 140),
            $"Gale (MOLA 463 m) — escala 1:{1f / worldScale:0}\n" +
            $"Velocidad: {target.Speed:0.0} m/s (Unity)\n" +
            $"Gravedad: g = {target.gravity:0.00} m/s² (Marte)\n" +
            $"Lon {lon:0.0000}° E   Lat {lat:0.0000}°\n" +
            $"X {x:0} m   Y {y:0} m   Elev. {z:0} m\n" + controls);

        // En vista aérea el rover es diminuto: marcador sobre su posición en pantalla.
        if (Aerial && cam)
        {
            Vector3 sp = cam.WorldToScreenPoint(p);
            if (sp.z > 0f)
            {
                var r = new Rect(sp.x - 9f, Screen.height - sp.y - 9f, 18f, 18f);
                var old = GUI.color;
                GUI.color = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), "◆");
                GUI.color = Color.cyan; GUI.Label(r, "◆");
                GUI.color = old;
                GUI.Label(new Rect(r.xMax, r.y, 60f, 20f), "Rover");
            }
        }
    }
}
