# Mal.Raycaster

A Wolfenstein-style **DDA raycaster** for [Space Engineers](https://www.spaceengineers.com/),
rendered as a grid of dot sprites on an in-game LCD / text surface. It is a pure
software renderer: it marches a ray per screen column through a 2D grid map and
emits one round `Circle` sprite per logical output pixel: a literal dot per pixel.

Pick it from a text surface's script dropdown as **"Raycaster"**. On a block that
can receive menu input (a cockpit/seat) the six `CUBE_ROTATE` inputs drive the
camera: Up/Down = forward/back, Left/Right = turn, RollLeft/RollRight = strafe.
On a plain wall LCD it just renders the scene.

## ⚠️ Performance prohibits this from being a real thing in its current form

**This is a spike, not a usable mod.** The problem is not the visual frame rate;
the scene renders fine. The problem is CPU cost: all of this work runs synchronously
inside the game's update loop, so every ray march, per-dot lighting calculation,
per-light shadow march, and the building of ten-thousand-plus sprites is charged
against a single game frame's CPU budget on the main simulation thread. One LCD
running this eats far more of that per-frame budget than any mod has a right to,
starving the actual game simulation.

There is no incremental tuning that rescues the current approach; the
sprite-per-pixel design itself is the ceiling. Making this practical would require
a different output path entirely (e.g. drawing into a sprite/texture buffer rather
than thousands of individual sprites) and moving the heavy compute off the main
thread. Treat everything here as an experiment in "does the full thing even run
in-game," not as something to ship.

## What it does (when it runs)

- **DDA wall casting** with textured walls, distance fog, and a per-column depth buffer
- **Textured floors and ceilings** via per-row plane casting (with a cheap gradient fallback)
- **Dynamic colored lighting**: omni point lights and a spotlight, diffuse `N·L` shading
- **Per-light shadows** via a 2D grid march (Amanatides-Woo) toward each light
- **A player flashlight** (spotlight re-aimed to the view each frame)
- **Auto-doors** that slide open near the player and occlude both rendering and light
- **Billboard props** (pillar, barrel, slime) with depth-correct occlusion
- **Procedural textures**: no image files; every wall/door/prop texture is generated
  from a base colour plus a pattern at startup
- **Supersampling + a backlight glow** pass for a softer look

All of it is resolution-agnostic: the dot grid fills the full width of any surface
and as many rows as fit at the same square dot size, with the FOV adjusted per
aspect so pixels stay square.

## Layout

| File | Role |
|------|------|
| `Raycaster.cs` | The renderer: map, DDA, texturing, lighting, shadows, doors, billboards |
| `Textures.cs` | Procedural texture generation (walls, door, floor/ceiling, props) |
| `RaycasterElement.cs` | Hosts the renderer in Ion's element tree; feeds it ticks and input |
| `RaycasterApp.cs` | The `MyTextSurfaceScript` entry point ("Raycaster") |
| `RaycasterSession.cs` | Session wiring (render pass + focus input) |

## Building

Targets `netframework48`, C# 6, x64, via the MDK2 toolchain. It consumes the
shared Ion + Utilities sources from a sibling `Mal.Terminal` checkout (a deliberate
spike shortcut, see the note in the `.csproj`), and the shared `.shproj` projects
require full MSBuild / Visual Studio rather than the `dotnet` CLI.

## Status

Experimental spike. Intentionally untested. If it ever graduates beyond the
performance wall above, it would move to its own repo with proper dependency pins
and a test suite for the deterministic seams.
