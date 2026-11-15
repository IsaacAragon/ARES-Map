using UnityEngine;

// Elige el contexto de entrada de cada frame (antes que el resto de scripts):
// pausa > mapa interactivo > vista aérea > seguimiento.
[DefaultExecutionOrder(-200)]
public class InputRouter : MonoBehaviour
{
    public ChaseCamera chase;
    public MinimapHUD minimap;

    void Update()
    {
        _ = GaleInput.Controls;   // crea y habilita las acciones la primera vez
        GaleInput.Context c =
            SimPause.IsPaused ? GaleInput.Context.Paused :
            minimap && minimap.Interactive ? GaleInput.Context.Map :
            chase && chase.Aerial ? GaleInput.Context.Aerial :
            GaleInput.Context.Chase;
        GaleInput.SetContext(c);
        GaleInput.SetTyping(ExplorerMenu.Typing);   // buscador del menú con el foco
    }
}
