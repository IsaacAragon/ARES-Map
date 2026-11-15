using UnityEngine;

// Cámara con dos modos:
//  - Seguimiento (por defecto): detrás del rover. Rueda / cruceta: acercar y alejar.
//  - Vista aérea (Tab / Select): sobre el cráter. WASD / stick izq. desplaza, Q/E / LB-RB gira, rueda / stick dcho. = altura,
//    clic dcho. + arrastrar / stick dcho. X + cruceta = mirar, F / X = rover, C / Y = cráter (acciones del mapa Aerial).
//    Mientras está activa, el rover no recibe teclas (se queda frenado).
// Los datos del rover se muestran en NavInstrumentHUD (alrededor del minimapa).
public class ChaseCamera : MonoBehaviour
{
    public RoverController target;

    [Header("Seguimiento")]
    public float distance = 9f;
    public float height = 3.5f;
    public float smooth = 6f;

    [Header("Vista aérea")]
    public Vector3 craterCenter = Vector3.zero;   // centro del recorte (Gale) en Unity
    public float aerialHeight = 3200f;            // altura sobre el foco (m Unity)
    public float aerialMinHeight = 50f;
    public float aerialMaxHeight = 6000f;
    [Range(20f, 90f)] public float aerialPitch = 75f;
    public float aerialYaw = 0f;                  // 0 = norte arriba
    public float transitionSpeed = 4f;
    [Tooltip("Hasta dónde puede desplazarse la vista aérea hacia el norte (u Unity desde el centro de Gale).")]
    public float aerialNorthLimit = 3000f;
    [Tooltip("Límites de la vista aérea al sur, este y oeste (u Unity desde el centro de Gale); cubren los bloques del mosaico.")]
    public float aerialSouthLimit = 2150f;
    public float aerialEastLimit = 5000f;
    public float aerialWestLimit = 6800f;

    [Header("Atmósfera (polvo en suspensión)")]
    public float chaseFogDensity = 0.0005f;
    public float aerialFogDensity = 0.00008f;

    [Header("Georreferencia (la escribe el builder)")]
    public float worldScale = 0.02f;         // metros Unity por metro real
    public double offsetX = 8168043.885866;  // centro X del DXF (m)
    public double offsetY = -319997.637211;  // centro Y del DXF (m)
    public double marsRadius = 3396190.0;

    public bool Aerial { get; private set; }

    // Lo activa el minimapa interactivo: la cámara no lee teclado ni ratón mientras tanto.
    [HideInInspector] public bool inputBlocked;
    bool InputOff => inputBlocked || SimPause.IsPaused;

    Vector3 aerialFocus;
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
        var k = GaleInput.Controls;
        if (!InputOff && k.Global.ToggleAerial.WasPressedThisFrame()) SetAerial(!Aerial);

        if (Aerial) UpdateAerial(!InputOff);
        else UpdateChase();
    }

    void UpdateChase()
    {
        if (!target) return;
        // Rueda del ratón (unidades de rueda) o cruceta del mando (≈6 u por segundo).
        if (!InputOff)
            distance = Mathf.Clamp(distance - GaleInput.Delta(GaleInput.Controls.Camera.Zoom, 600f) * 0.01f, 4f, 200f);

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

    void UpdateAerial(bool readInput)
    {
        float dt = Time.unscaledDeltaTime;

        if (readInput)
        {
            var a = GaleInput.Controls.Aerial;
            Quaternion yawRot = Quaternion.Euler(0f, aerialYaw, 0f);
            Vector2 pan = Vector2.ClampMagnitude(a.Pan.ReadValue<Vector2>(), 1f);
            float speed = aerialHeight * 0.6f * (a.Fast.IsPressed() ? 3f : 1f);
            aerialFocus += yawRot * new Vector3(pan.x, 0f, pan.y) * speed * dt;
            // Límites: la extensión del mosaico de terreno (los fija el builder).
            aerialFocus.x = Mathf.Clamp(aerialFocus.x, craterCenter.x - aerialWestLimit, craterCenter.x + aerialEastLimit);
            aerialFocus.z = Mathf.Clamp(aerialFocus.z, craterCenter.z - aerialSouthLimit, craterCenter.z + aerialNorthLimit);

            aerialYaw += a.Rotate.ReadValue<float>() * 60f * dt;
            if (a.FocusRover.WasPressedThisFrame() && target) aerialFocus = target.transform.position;
            if (a.FocusCrater.WasPressedThisFrame()) { aerialFocus = craterCenter; aerialYaw = 0f; }

            // Altura: rueda (zoom proporcional a la altura) o stick derecho Y del mando.
            float scroll = GaleInput.Delta(a.Height, 900f);
            aerialHeight = Mathf.Clamp(aerialHeight * Mathf.Pow(0.999f, scroll), aerialMinHeight, aerialMaxHeight);
            // Mirar: clic derecho + arrastrar (píxeles) o stick derecho X + cruceta (≈250 px/s).
            Vector2 d = GaleInput.Delta2(a.Look, 250f);
            aerialYaw += d.x * 0.2f;
            aerialPitch = Mathf.Clamp(aerialPitch - d.y * 0.2f, 20f, 90f);
        }

        Quaternion rot = Quaternion.Euler(aerialPitch, aerialYaw, 0f);
        Vector3 desired = aerialFocus - rot * Vector3.forward * aerialHeight;
        float t = 1f - Mathf.Exp(-transitionSpeed * dt);
        transform.position = Vector3.Lerp(transform.position, desired, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, rot, t);
    }

    void OnGUI()
    {
        // Los datos (velocidad, rumbo, elevación, coordenadas, g) y las ayudas los muestra
        // NavInstrumentHUD alrededor del minimapa.
        if (!target || SimPause.IsPaused) return;
        Vector3 p = target.transform.position;

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
