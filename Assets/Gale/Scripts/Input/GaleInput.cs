using UnityEngine;
using UnityEngine.InputSystem;

// Acceso único a las acciones de ARES Map (clase generada GaleControls desde
// Assets/InputSystem_Actions.inputactions). Ningún script lee Keyboard.current / Mouse.current.
//   Contextos: Global siempre; Rover + Camera en seguimiento; Aerial en vista aérea; Map en el
//   mapa interactivo. En pausa solo Global (y la UI). Lo cambia InputRouter en cada frame.
//   UsingGamepad: el último dispositivo con el que se actuó (para mostrar la ayuda adecuada).
public static class GaleInput
{
    public enum Context { Chase, Aerial, Map, Paused }

    static GaleControls controls;
    public static Context Current { get; private set; } = Context.Chase;
    public static bool UsingGamepad { get; private set; }

    public static GaleControls Controls
    {
        get
        {
            if (controls == null)
            {
                controls = new GaleControls();
                controls.Global.Enable();
                Apply(Current, force: true);
                InputSystem.onActionChange += OnActionChange;
            }
            return controls;
        }
    }

    /// <summary>Escribiendo en un campo de texto (buscador): ninguna acción del juego lee el teclado.</summary>
    public static bool Typing { get; private set; }

    public static void SetTyping(bool on)
    {
        if (on == Typing) return;
        Typing = on;
        if (controls == null) return;
        if (on) controls.Disable();
        else { controls.Global.Enable(); Apply(Current, force: true); }
    }

    public static void SetContext(Context c)
    {
        if (c == Current && controls != null) return;
        Current = c;
        if (controls != null && !Typing) Apply(c, force: false);
    }

    static void Apply(Context c, bool force)
    {
        var k = controls;
        Toggle(k.Rover.Get(), c == Context.Chase);
        Toggle(k.Camera.Get(), c == Context.Chase);
        Toggle(k.Aerial.Get(), c == Context.Aerial);
        Toggle(k.Map.Get(), c == Context.Map);
    }

    // Al desactivar un mapa sus acciones vuelven a reposo: no quedan entradas "pegadas".
    static void Toggle(InputActionMap m, bool on)
    {
        if (on && !m.enabled) m.Enable();
        else if (!on && m.enabled) m.Disable();
    }

    static void OnActionChange(object obj, InputActionChange change)
    {
        if (change != InputActionChange.ActionPerformed || !(obj is InputAction a)) return;
        var dev = a.activeControl?.device;
        if (dev is Gamepad) UsingGamepad = true;
        else if (dev is Keyboard || dev is Mouse) UsingGamepad = false;
    }

    /// <summary>Eje que con ratón es un incremento por evento (rueda, delta) y con mando un valor
    /// continuo −1..1: con mando se convierte en incremento por frame a `gamepadPerSecond`.</summary>
    public static float Delta(InputAction a, float gamepadPerSecond)
    {
        float v = a.ReadValue<float>();
        return a.activeControl?.device is Pointer ? v : v * gamepadPerSecond * Time.unscaledDeltaTime;
    }

    public static Vector2 Delta2(InputAction a, float gamepadPerSecond)
    {
        Vector2 v = a.ReadValue<Vector2>();
        return a.activeControl?.device is Pointer ? v : v * gamepadPerSecond * Time.unscaledDeltaTime;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Typing = false;
        if (controls != null)
        {
            InputSystem.onActionChange -= OnActionChange;
            controls.Dispose();
        }
        controls = null;
        Current = Context.Chase;
        UsingGamepad = false;
    }
}
