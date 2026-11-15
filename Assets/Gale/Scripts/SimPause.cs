using System;
using UnityEngine;

// Estado de pausa de la simulación, compartido por el rover, la cámara y el minimapa.
// En pausa el tiempo se detiene (Time.timeScale = 0) y nadie lee entrada salvo el menú.
public static class SimPause
{
    public static bool IsPaused { get; private set; }
    public static event Action<bool> Changed;

    static float timeScaleBefore = 1f;

    public static void Set(bool paused)
    {
        if (paused == IsPaused) return;
        IsPaused = paused;
        if (paused)
        {
            timeScaleBefore = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
        }
        else
        {
            Time.timeScale = timeScaleBefore;
        }
        Changed?.Invoke(paused);
    }

    // Al entrar en Play (también sin recarga de dominio) se parte siempre sin pausa.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        IsPaused = false;
        Changed = null;
        timeScaleBefore = 1f;
    }
}
