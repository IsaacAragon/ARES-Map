# ARES Map

[English](README.md) · **Español**

Simulador del cráter Gale para planear un recorrido en la superficie de Marte. Junta relieve, geología, material del suelo, nombres oficiales y la ruta de Curiosity, y se recorre con un rover.

Proyecto del equipo [Salomonsters](https://www.spaceappschallenge.org/2026/find-a-team/salomonsters/) (Universidad Americana, Managua) para el reto [Interplanetary Survival Guide: Martian Map](https://www.spaceappschallenge.org/2026/challenges/interplanetary-survival-guide-martian-map/) del NASA Space Apps Challenge 2026.

## Descarga

Versión lista para ejecutar en **Windows**, sin necesidad de Unity:

1. Ve a la [página de releases](https://github.com/IsaacAragon/ARES-Map/releases) y abre el release más reciente (**ARES Map v1**).
2. Descarga `ARES.Map.zip` (unos 200 MB).
3. Extrae el zip en la carpeta que quieras.
4. Abre la carpeta `ARES Map` y ejecuta **`ARES Map.exe`**.

![Vista aérea de Farah Vallis, con el buscador de lugares y la ficha del valle](docs/screenshots/vista-aerea-farah-vallis.png)

![Ficha del cráter Gale sobre el mapa de la región](docs/screenshots/ficha-crater-gale.png)

![Capa de material del suelo (THEMIS) en la vista del rover](docs/screenshots/capa-material-del-suelo.png)

## Abrirlo en Unity

Hace falta **Unity 6000.6.8f1** (Unity 6.6).

1. Clona el repositorio.
2. En Unity Hub, **Add** → **Add project from disk** y elige esta carpeta.
3. Espera a que termine de importar.
4. Abre `Assets/Scenes/SampleScene.unity`.
5. Pulsa **Play**.

## Controles

| | Teclado | Mando |
|---|---|---|
| Conducir | W/S acelerar, A/D girar. Shift rápido, Espacio freno, R enderezar | Gatillos, stick izquierdo. A rápido, B freno, Y enderezar |
| Cámara del rover | Rueda del ratón | Cruceta arriba/abajo |
| Vista aérea | Tab. WASD mover, Q/E girar, rueda altura, clic derecho arrastrar para mirar, Shift rápido. F al rover, C al cráter | Select. Stick izquierdo, LB/RB, stick derecho. L3 rápido. X al rover, Y al cráter |
| Mapa | M. Clic elige destino, Shift+clic añade parada. Enter confirma, Retroceso cancela, Supr quita un punto, Ctrl+Z / Ctrl+Y, P cambia el perfil, V muestra la ruta en vista aérea. Rueda zoom, clic derecho arrastra, Q/E gira. Esc sale | R3. Stick izquierdo mueve, gatillos zoom, stick derecho cursor, A selecciona. B sale |
| General | G menú, L cambia la capa, H ayuda, Esc pausa | Cruceta izquierda cambia la capa, cruceta derecha ayuda, Start pausa |
