# ARES Map

**English** · [Español](README-es.md)

A simulator of Gale crater for planning a traverse on the surface of Mars. It brings together terrain, geology, surface material, official place names, and Curiosity's route, and you drive it with a rover.

Built by [Salomonsters](https://www.spaceappschallenge.org/2026/find-a-team/salomonsters/) (Universidad Americana, Managua) for the [Interplanetary Survival Guide: Martian Map](https://www.spaceappschallenge.org/2026/challenges/interplanetary-survival-guide-martian-map/) challenge at the 2026 NASA Space Apps Challenge.

## Download

Ready-to-run build for **Windows**, no Unity needed:

1. Go to the [Releases page](https://github.com/IsaacAragon/ARES-Map/releases) and open the latest release (**ARES Map v1**).
2. Download `ARES.Map.zip` (about 200 MB).
3. Extract the zip to any folder.
4. Open the `ARES Map` folder and run **`ARES Map.exe`**.

![Aerial view of Farah Vallis, with the place search and the valley's info card](docs/screenshots/vista-aerea-farah-vallis.png)

![Gale crater info card over the regional map](docs/screenshots/ficha-crater-gale.png)

![Surface-material layer (THEMIS) in the rover view](docs/screenshots/capa-material-del-suelo.png)

## Open it in Unity

Requires **Unity 6000.6.8f1** (Unity 6.6).

1. Clone the repository.
2. In Unity Hub, choose **Add** → **Add project from disk** and select this folder.
3. Wait for the import to finish.
4. Open `Assets/Scenes/SampleScene.unity`.
5. Press **Play**.

## Controls

| | Keyboard | Gamepad |
|---|---|---|
| Drive | W/S throttle, A/D steer. Shift boost, Space brake, R level the rover | Triggers, left stick. A boost, B brake, Y level |
| Rover camera | Mouse wheel | D-pad up/down |
| Aerial view | Tab. WASD pan, Q/E rotate, wheel changes height, right-drag to look, Shift moves faster. F focuses the rover, C the crater | Select. Left stick, LB/RB, right stick. L3 faster. X rover, Y crater |
| Map | M. Click sets a destination, Shift+click adds a stop. Enter confirms, Backspace cancels, Delete removes a point, Ctrl+Z / Ctrl+Y, P cycles the profile, V shows the route in the aerial view. Wheel zooms, right-drag pans, Q/E rotates. Esc leaves | R3. Left stick pans, triggers zoom, right stick moves the cursor, A selects. B leaves |
| General | G menu, L cycles the map layer, H help, Esc pauses | D-pad left cycles the layer, D-pad right opens help, Start pauses |
