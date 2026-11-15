"""
Genera los mapas de acciones de ARES Map dentro de Assets/InputSystem_Actions.inputactions.
Sustituye el mapa "Player" de la plantilla por Global, Rover, Camera, Aerial y Map, y conserva
el mapa "UI" (lo usa la navegación de UI Toolkit). Cada acción tiene enlace de teclado/ratón
(Keyboard&Mouse) y de mando (Gamepad). Los IDs son deterministas (uuid5), así que volver a
ejecutarlo no cambia el asset si no cambian las acciones.

Reparto del mando (resuelve los choques de la propuesta original):
  Global : Start pausa · Select vista aérea · R3 mapa · cruceta → ayuda · cruceta ← capa del mapa
  Rover  : stick izq. X girar · RT/LT acelerar/retroceder · A rápido · B freno · Y enderezar
  Camera : cruceta ↑/↓ zoom de la cámara de seguimiento
  Aerial : stick izq. mover · LB/RB girar · stick dcho. Y altura · stick dcho. X + cruceta ↑/↓ mirar
           L3 rápido · X rover · Y cráter
  Map    : stick izq. mover · gatillos zoom · LB/RB girar · stick dcho. cursor virtual · A seleccionar
           cruceta ↑/↓ zoom por pasos · X rover · Y cráter · B salir · L3 rápido

Uso:  python Tools/build_input_actions.py
"""
import json, uuid, os

PATH = os.path.join(os.path.dirname(__file__), "..", "Assets", "InputSystem_Actions.inputactions")
NS = uuid.UUID("6f1d3c9e-2a0b-4c55-9e0e-a7e5a0a4d001")
KM, GP = "Keyboard&Mouse", "Gamepad"


def uid(*parts):
    return str(uuid.uuid5(NS, "/".join(parts)))


class Map:
    def __init__(self, name):
        self.name, self.actions, self.bindings = name, [], []

    def action(self, name, kind, ctrl=""):
        t = {"button": ("Button", "Button"), "axis": ("Value", "Axis"), "vec2": ("Value", "Vector2"),
             "pass2": ("PassThrough", "Vector2")}[kind]
        self.actions.append({"name": name, "type": t[0], "id": uid(self.name, name), "expectedControlType": t[1],
                             "processors": "", "interactions": "", "initialStateCheck": t[0] == "Value"})
        return name

    def bind(self, action, path, group, processors=""):
        self.bindings.append({"name": "", "id": uid(self.name, action, path, group, processors), "path": path,
                              "interactions": "", "processors": processors, "groups": group, "action": action,
                              "isComposite": False, "isPartOfComposite": False})

    def composite(self, action, kind, parts, group, name="", processors=""):
        cid = uid(self.name, action, kind, group, name, *[p for _, p in parts])
        self.bindings.append({"name": name or kind, "id": cid, "path": kind, "interactions": "",
                              "processors": processors, "groups": "", "action": action,
                              "isComposite": True, "isPartOfComposite": False})
        for part, path in parts:
            self.bindings.append({"name": part, "id": uid(cid, part, path), "path": path, "interactions": "",
                                  "processors": "", "groups": group, "action": action,
                                  "isComposite": False, "isPartOfComposite": True})

    def wasd(self, action):
        self.composite(action, "2DVector", [("up", "<Keyboard>/w"), ("down", "<Keyboard>/s"),
                                            ("left", "<Keyboard>/a"), ("right", "<Keyboard>/d")], KM, "WASD")
        self.composite(action, "2DVector", [("up", "<Keyboard>/upArrow"), ("down", "<Keyboard>/downArrow"),
                                            ("left", "<Keyboard>/leftArrow"), ("right", "<Keyboard>/rightArrow")], KM, "Flechas")

    def axis_keys(self, action, neg, pos, group=KM, name=""):
        self.composite(action, "1DAxis", [("negative", neg), ("positive", pos)], group, name)

    def to_json(self):
        return {"name": self.name, "id": uid("map", self.name), "actions": self.actions, "bindings": self.bindings}


def build():
    g = Map("Global")
    g.action("Pause", "button"); g.bind("Pause", "<Keyboard>/escape", KM); g.bind("Pause", "<Gamepad>/start", GP)
    g.action("ToggleAerial", "button"); g.bind("ToggleAerial", "<Keyboard>/tab", KM); g.bind("ToggleAerial", "<Gamepad>/select", GP)
    g.action("ToggleMap", "button"); g.bind("ToggleMap", "<Keyboard>/m", KM); g.bind("ToggleMap", "<Gamepad>/rightStickPress", GP)
    g.action("Help", "button"); g.bind("Help", "<Keyboard>/h", KM); g.bind("Help", "<Gamepad>/dpad/right", GP)
    # Capas del mapa (geología, material del suelo): pasa a la siguiente.
    g.action("CycleLayer", "button"); g.bind("CycleLayer", "<Keyboard>/l", KM); g.bind("CycleLayer", "<Gamepad>/dpad/left", GP)
    g.action("MinimapZoom", "axis")   # pasos −/+ del minimapa fuera del modo mapa
    g.axis_keys("MinimapZoom", "<Keyboard>/minus", "<Keyboard>/equals", name="MenosMas")
    g.axis_keys("MinimapZoom", "<Keyboard>/numpadMinus", "<Keyboard>/numpadPlus", name="Numpad")

    r = Map("Rover")
    r.action("Throttle", "axis")
    r.axis_keys("Throttle", "<Keyboard>/s", "<Keyboard>/w", name="WS")
    r.axis_keys("Throttle", "<Keyboard>/downArrow", "<Keyboard>/upArrow", name="Flechas")
    r.axis_keys("Throttle", "<Gamepad>/leftTrigger", "<Gamepad>/rightTrigger", GP, "Gatillos")
    r.action("Steer", "axis")
    r.axis_keys("Steer", "<Keyboard>/a", "<Keyboard>/d", name="AD")
    r.axis_keys("Steer", "<Keyboard>/leftArrow", "<Keyboard>/rightArrow", name="Flechas")
    r.bind("Steer", "<Gamepad>/leftStick/x", GP)
    r.action("Boost", "button"); r.bind("Boost", "<Keyboard>/leftShift", KM); r.bind("Boost", "<Gamepad>/buttonSouth", GP)
    r.action("Brake", "button"); r.bind("Brake", "<Keyboard>/space", KM); r.bind("Brake", "<Gamepad>/buttonEast", GP)
    r.action("Reset", "button"); r.bind("Reset", "<Keyboard>/r", KM); r.bind("Reset", "<Gamepad>/buttonNorth", GP)

    c = Map("Camera")
    c.action("Zoom", "axis")   # ratón: unidades de rueda; mando: −1..1 por segundo
    c.bind("Zoom", "<Mouse>/scroll/y", KM)
    c.axis_keys("Zoom", "<Gamepad>/dpad/down", "<Gamepad>/dpad/up", GP, "Cruceta")

    a = Map("Aerial")
    a.action("Pan", "vec2"); a.wasd("Pan"); a.bind("Pan", "<Gamepad>/leftStick", GP)
    a.action("Rotate", "axis"); a.axis_keys("Rotate", "<Keyboard>/q", "<Keyboard>/e", name="QE")
    a.axis_keys("Rotate", "<Gamepad>/leftShoulder", "<Gamepad>/rightShoulder", GP, "Hombros")
    a.action("Height", "axis"); a.bind("Height", "<Mouse>/scroll/y", KM); a.bind("Height", "<Gamepad>/rightStick/y", GP)
    a.action("Look", "vec2")   # ratón: píxeles con el botón derecho pulsado; mando: −1..1 por segundo
    a.composite("Look", "OneModifier", [("modifier", "<Mouse>/rightButton"), ("binding", "<Mouse>/delta")], KM, "ClicDchoArrastrar")
    a.composite("Look", "2DVector(mode=2)", [("left", "<Gamepad>/rightStick/left"), ("right", "<Gamepad>/rightStick/right"),
                                             ("up", "<Gamepad>/dpad/up"), ("down", "<Gamepad>/dpad/down")], GP, "StickCruceta")
    a.action("Fast", "button"); a.bind("Fast", "<Keyboard>/leftShift", KM); a.bind("Fast", "<Gamepad>/leftStickPress", GP)
    a.action("FocusRover", "button"); a.bind("FocusRover", "<Keyboard>/f", KM); a.bind("FocusRover", "<Gamepad>/buttonWest", GP)
    a.action("FocusCrater", "button"); a.bind("FocusCrater", "<Keyboard>/c", KM); a.bind("FocusCrater", "<Gamepad>/buttonNorth", GP)

    m = Map("Map")
    m.action("Pan", "vec2"); m.wasd("Pan"); m.bind("Pan", "<Gamepad>/leftStick", GP)
    m.action("Zoom", "axis"); m.bind("Zoom", "<Mouse>/scroll/y", KM)
    m.axis_keys("Zoom", "<Gamepad>/leftTrigger", "<Gamepad>/rightTrigger", GP, "Gatillos")
    m.action("ZoomStep", "axis"); m.axis_keys("ZoomStep", "<Keyboard>/minus", "<Keyboard>/equals", name="MenosMas")
    m.axis_keys("ZoomStep", "<Keyboard>/numpadMinus", "<Keyboard>/numpadPlus", name="Numpad")
    m.axis_keys("ZoomStep", "<Gamepad>/dpad/down", "<Gamepad>/dpad/up", GP, "Cruceta")
    m.action("Rotate", "axis"); m.axis_keys("Rotate", "<Keyboard>/q", "<Keyboard>/e", name="QE")
    m.axis_keys("Rotate", "<Gamepad>/leftShoulder", "<Gamepad>/rightShoulder", GP, "Hombros")
    m.action("Drag", "button"); m.bind("Drag", "<Mouse>/rightButton", KM)
    m.action("PointerDelta", "pass2"); m.bind("PointerDelta", "<Mouse>/delta", KM)
    m.action("Point", "pass2"); m.bind("Point", "<Mouse>/position", KM)
    m.action("CursorMove", "vec2"); m.bind("CursorMove", "<Gamepad>/rightStick", GP)
    m.action("Select", "button"); m.bind("Select", "<Mouse>/leftButton", KM); m.bind("Select", "<Gamepad>/buttonSouth", GP)
    m.action("Fast", "button"); m.bind("Fast", "<Keyboard>/leftShift", KM); m.bind("Fast", "<Gamepad>/leftStickPress", GP)
    m.action("FocusRover", "button"); m.bind("FocusRover", "<Keyboard>/f", KM); m.bind("FocusRover", "<Gamepad>/buttonWest", GP)
    m.action("FocusCrater", "button"); m.bind("FocusCrater", "<Keyboard>/c", KM); m.bind("FocusCrater", "<Gamepad>/buttonNorth", GP)
    m.action("Exit", "button"); m.bind("Exit", "<Keyboard>/escape", KM); m.bind("Exit", "<Gamepad>/buttonEast", GP)

    return [g, r, c, a, m]


def main():
    path = os.path.abspath(PATH)
    d = json.load(open(path, encoding="utf-8-sig"))
    ui = [mp for mp in d["maps"] if mp["name"] == "UI"]
    d["maps"] = [mp.to_json() for mp in build()] + ui
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(d, f, indent=4, ensure_ascii=False)
        f.write("\n")
    for mp in d["maps"]:
        print(mp["name"], len(mp["actions"]), "acciones,", len(mp["bindings"]), "enlaces")


if __name__ == "__main__":
    main()
