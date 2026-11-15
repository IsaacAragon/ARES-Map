using UnityEngine;
using UnityEngine.InputSystem;

// Rover de prueba: suspensión por rayos (una por rueda), tracción y giro arcade.
// WASD / flechas para moverse, Shift para ir rápido, Espacio para frenar, R para enderezar.
// La gravedad es la de Marte (3,71 m/s²) y se aplica solo al rover, sin tocar Physics.gravity.
[RequireComponent(typeof(Rigidbody))]
public class RoverController : MonoBehaviour
{
    [Header("Ruedas (visuales + punto de suspensión)")]
    public Transform[] wheels;
    public float wheelRadius = 0.35f;
    [Tooltip("Mover las ruedas arriba/abajo con la suspensión. Apagado para modelos con brazos rígidos (rocker-bogie del modelo).")]
    public bool moveWheelVisuals = true;

    [Header("Suspensión")]
    public float restLength = 0.55f;
    public float springStrength = 2500f;
    public float springDamper = 700f;
    [Tooltip("Calcula restLength para que, en reposo, las ruedas del modelo toquen el suelo exactamente.")]
    public bool autoRestLength = false;
    public Vector3 centerOfMass = new Vector3(0f, -0.3f, 0f);
    [Tooltip("Pone X/Z del centro de masas sobre el centroide de las ruedas, para que el rover no cabecee en reposo.")]
    public bool centerMassOverWheels = true;
    [Tooltip("Margen (m) por encima del punto de suspensión desde el que se lanza el rayo. " +
             "Si una rueda se hunde en un aterrizaje fuerte, el rayo sigue naciendo por encima del suelo y la recupera.")]
    public float rayHeadroom = 1.5f;
    [Tooltip("Rigidez extra del tope de suspensión cuando la compresión supera el recorrido normal.")]
    public float bumpStopStrength = 40000f;

    [Header("Conducción")]
    public float motorForce = 5000f;
    public float maxSpeed = 12f;
    public float boostMultiplier = 3f;
    public float turnRate = 70f;          // grados por segundo
    public float lateralGrip = 6f;
    public float brakeDrag = 3f;

    [Header("Marte")]
    public float gravity = 3.71f;

    // Lo apaga la vista aérea para que WASD mueva la cámara y no el rover.
    [HideInInspector] public bool inputEnabled = true;

    bool prevR;
    Rigidbody rb;
    Vector3[] wheelRest;
    Quaternion[] wheelRestRot;
    float[] wheelSpin;
    int groundMask;
    int groundedWheels;

    public float Speed => rb ? rb.linearVelocity.magnitude : 0f;
    public bool Grounded => groundedWheels > 0;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.centerOfMass = centerOfMass;
        groundMask = ~(1 << gameObject.layer);
    }

    void Start()
    {
        // Las ruedas se leen en Start: el builder asigna los campos después de AddComponent.

        // Posición y giro de reposo de cada rueda en el espacio del rover (las ruedas pueden estar anidadas en el modelo).
        wheelRest = new Vector3[wheels.Length];
        wheelRestRot = new Quaternion[wheels.Length];
        wheelSpin = new float[wheels.Length];
        for (int i = 0; i < wheels.Length; i++)
        {
            wheelRest[i] = transform.InverseTransformPoint(wheels[i].position);
            wheelRestRot[i] = Quaternion.Inverse(transform.rotation) * wheels[i].rotation;
        }
        // Centro de masas sobre el centroide de las ruedas: con el COM adelantado, las delanteras
        // cargaban más, el rover cabeceaba ~6° y los muelles inclinados lo hacían reptar hacia atrás.
        if (centerMassOverWheels && wheels.Length > 0)
        {
            Vector3 c = Vector3.zero;
            foreach (var w in wheelRest) c += w;
            c /= wheels.Length;
            rb.centerOfMass = new Vector3(c.x, centerOfMass.y, c.z);
        }
        // En equilibrio la compresión es m·g/(n·k); si es igual a restLength la rueda queda en su sitio.
        if (autoRestLength)
            restLength = Mathf.Max(0.12f, rb.mass * gravity / (wheels.Length * springStrength));
        SnapToGround(transform.position);
    }

    public void SnapToGround(Vector3 pos)
    {
        var from = new Vector3(pos.x, 5000f, pos.z);
        if (Physics.Raycast(from, Vector3.down, out var hit, 20000f, groundMask))
            pos.y = hit.point.y + restLength + 0.8f;
        transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    void Update()
    {
        var kb = Keyboard.current;
        bool r = inputEnabled && kb != null && kb.rKey.isPressed;
        if (r && !prevR) SnapToGround(transform.position);
        prevR = r;
    }

    void FixedUpdate()
    {
        if (wheelRest == null) return;
        var kb = Keyboard.current;
        float throttle = 0f, steer = 0f;
        bool boost = false, brake = false;
        if (inputEnabled && kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) throttle += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) throttle -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) steer += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) steer -= 1f;
            boost = kb.leftShiftKey.isPressed;
            brake = kb.spaceKey.isPressed;
        }

        rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);

        // Suspensión: un rayo por rueda.
        groundedWheels = 0;
        Vector3 groundNormal = Vector3.zero;
        for (int i = 0; i < wheels.Length; i++)
        {
            Vector3 origin = transform.TransformPoint(wheelRest[i] + Vector3.up * restLength);
            Vector3 down = -transform.up;
            float maxDist = restLength * 2f + wheelRadius;
            // El rayo nace rayHeadroom más arriba: si el punto de suspensión acaba bajo el suelo
            // (el MeshCollider no se detecta por detrás), antes la rueda dejaba de empujar y el
            // rover quedaba apoyado en el chasis con las ruedas enterradas para siempre.
            Vector3 castFrom = origin - down * rayHeadroom;
            if (Physics.Raycast(castFrom, down, out var hit, maxDist + rayHeadroom, groundMask, QueryTriggerInteraction.Ignore))
            {
                groundedWheels++;
                groundNormal += hit.normal;
                float dist = hit.distance - rayHeadroom;           // negativa si el punto ya está bajo el suelo
                float compression = maxDist - dist;
                float vel = Vector3.Dot(rb.GetPointVelocity(origin), transform.up);
                float force = compression * springStrength - vel * springDamper;
                // Tope: pasado el recorrido normal (rueda hundida) empuja mucho más fuerte.
                float overTravel = compression - restLength * 1.5f;
                if (overTravel > 0f) force += overTravel * bumpStopStrength;
                rb.AddForceAtPosition(transform.up * Mathf.Max(0f, force), origin);
                if (moveWheelVisuals) wheels[i].position = hit.point + transform.up * wheelRadius;
            }
            else if (moveWheelVisuals)
            {
                wheels[i].position = origin + down * (maxDist - wheelRadius);
            }
        }

        if (groundedWheels == 0) return;
        groundNormal.Normalize();

        // Tracción a lo largo del suelo.
        Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, groundNormal).normalized;
        float limit = maxSpeed * (boost ? boostMultiplier : 1f);
        float fwdSpeed = Vector3.Dot(rb.linearVelocity, fwd);
        float traction = (float)groundedWheels / wheels.Length;
        if (Mathf.Abs(fwdSpeed) < limit || Mathf.Sign(fwdSpeed) != Mathf.Sign(throttle))
            rb.AddForce(fwd * throttle * motorForce * (boost ? boostMultiplier : 1f) * traction);

        // Agarre lateral (no derrapa como un hovercraft).
        Vector3 right = Vector3.Cross(groundNormal, fwd);
        float sideSpeed = Vector3.Dot(rb.linearVelocity, right);
        rb.AddForce(-right * sideSpeed * lateralGrip * traction, ForceMode.Acceleration);

        // Freno y rozamiento de rodadura.
        bool idle = Mathf.Approximately(throttle, 0f);
        float drag = brake ? brakeDrag : (idle ? 1.5f : 0f);
        rb.AddForce(-fwd * fwdSpeed * drag * traction, ForceMode.Acceleration);
        // Freno de estacionamiento: sin acelerar, la componente de la gravedad a lo largo del suelo se anula.
        if ((idle || brake) && Mathf.Abs(fwdSpeed) < 1.5f)
            rb.AddForce(-Vector3.ProjectOnPlane(Vector3.down * gravity, groundNormal) * traction, ForceMode.Acceleration);

        // Giro sobre la normal del suelo.
        float dir = fwdSpeed < -0.5f ? -1f : 1f;
        float yaw = steer * turnRate * Mathf.Deg2Rad * dir;
        Vector3 av = rb.angularVelocity;
        float currentYaw = Vector3.Dot(av, groundNormal);
        rb.angularVelocity = av + groundNormal * (yaw - currentYaw) * traction;

        // Giro visual de las ruedas.
        for (int i = 0; i < wheels.Length; i++)
        {
            wheelSpin[i] += fwdSpeed / wheelRadius * Mathf.Rad2Deg * Time.fixedDeltaTime;
            wheels[i].rotation = transform.rotation * Quaternion.AngleAxis(wheelSpin[i], Vector3.right) * wheelRestRot[i];
        }
    }
}
