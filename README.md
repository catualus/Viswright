# Viswright

Automatic visibility optimisation for Source maps, as a Compile Pal step. It does the work Hammer
mappers do by hand - func_detail, nodraw on hidden faces, areaportals in doorways, hint brushes, prop
fade distances - on a copy of the map, proves each change with the real compilers, and backs out
anything that does not pay for itself.

**Status: phase 3.** Three passes, all opt-in while in beta: **Areaportals in doorways**, **Make detail**
and **Hide covered faces**.
Every compile audits the map, runs the ticked passes, checks the copy differs only in what they are
allowed to change, and hands it to VBSP; a second step measures the compiled map's visibility after
VVIS.

## The passes

Each pass is checked by compiling the map with the VBSP and VVIS the game is set up with, in a work
folder beside the copy. The map as it is gets compiled first - once, and again only when the map,
the compilers or Viswright change - and every pass is measured against it.

**Areaportals in doorways** (`-areaportals`) puts a func_areaportal in the doorway of each door that has
none, closing with the door. The opening is found by walking out from the door through the first
compile's world brushes - left, right, up, down - and only a clean rectangle framed by world brushes on
all four sides is used; the portal sits inside the wall's thickness, a unit into the frame all round.
Before compiling, VBSP's own area flood is repeated over the first compile's portal file: a doorway whose
two sides are joined some other way (an open window, a gap, a door with no portal) would not divide two
areas and is dropped. The portal closes with the door when the door, together with any opaque brushes
framing it, fills the opening and the door's materials are opaque; otherwise it stays open. Unnamed
doors are named `vw_door_<id>`, unique across the map. Garry's Mod loads at most 191 areaportals, the
map's own included, so the portals are ranked by what shutting their door hides from where players
stand, and the best that fit are kept; that set is compiled again to prove it.

**Make detail** (`-detail`) turns small and complex world brushes into func_detail: trim, railings,
pillars, stairs, frames. Left out: brushes that face the void (checked against the first compile),
frame an areaportal, look into two different areas (they keep an areaportal's sides apart), are water,
are a unit or two thin, or are in a visgroup named `viswright_ignore`.
The map is then compiled; if it leaks, every brush the leak path runs through goes back to being world
and it is compiled again. The pass is undone if the leaks will not stop, if any collision brush is lost,
added, moved or changes contents, or if more becomes visible from where a player stands.

**Hide covered faces** (`-nodraw`) makes a face nodraw when all of it is pressed against faces of solid
brushes whose every material is opaque, so it can only be seen from inside a brush. VBSP already does
this between two world brushes and between two detail brushes; this covers world against detail and
detail against world, which it does not. A material counts as opaque only when its VMT says so; one
that cannot be found never hides anything.

"More visible" is measured at about 30,000 standing positions, found once from the first compile:
points at eye height above solid ground on a 128-unit grid. Two compiles do not share clusters, but
they share these points, so the triangles visible from each can be compared directly.

Measured on 2026-11-01 with vbsp++ and vvis++ (default VBSP options):

| | rp_downtown_prominence_d | rp_downtown_meowy |
| --- | --- | --- |
| Brushes made func_detail | 507 of 515 candidates | 362 of 375 |
| Clusters | 6,677 -> 6,289 | 6,174 -> 5,923 |
| Portals | 16,310 -> 15,269 | 14,942 -> 14,471 |
| Triangles visible from a standing position | 6,023 -> 6,021 | 6,701 -> 6,714 |
| VVIS | 59.3 s -> 47.1 s | 55.1 s -> 51.2 s |
| Faces made nodraw | 246 | 89 |
| Collision | identical | identical |
| Time the passes add | about 3.5 minutes | about 3.5 minutes |

With all three passes (2026-11-01):

| | rp_downtown_prominence_d | rp_downtown_meowy |
| --- | --- | --- |
| Areaportals added | 69, all closing with their door | 76, 70 closing |
| Areaportal numbers used | 191 of 191 | 187 of 191 |
| Brushes made func_detail | 452 | 310 |
| Faces made nodraw | 246 | 89 |
| VVIS portals | 16,310 -> 15,691 | 14,942 -> 15,019 |
| VVIS | 62.8 s -> 44.8 s | 49.4 s -> 53.0 s |
| Triangles visible from a standing position | 6,023 -> 6,016; 5,956 with the new doors shut | 6,701 -> 6,693; 6,626 with them shut |
| Time the passes add | about 6.5 minutes | about 6.5 minutes |

The world triangles a shut door saves are few, because VVIS already hides most interiors from the street.
What a closed areaportal also does - stop drawing the props and entities behind it, and stop the server
sending them - is not in that figure.

In Garry's Mod, the optimised prominence loads with all 191 portal numbers in use. At a storefront door
given a portal, wireframe rendering shows nothing behind the shut door and the whole plaza once it opens.

The optimised prominence was compiled in full and loaded in Garry's Mod. From open space around each of the 246
nodraw faces, 18,048 rays were traced at it; every one hit the brush covering it first. The other faces had no
open space in front of them at all.

## How it runs

1. Compile Pal runs `viswright optimize` before VBSP (order 0.8, after VMFFIX).
2. Viswright reads the VMF the compile is using: your map, or VMFFIX's fixed copy of it.
3. It writes its copy under `%TEMP%\CompilePal\Viswright\<map>-<hash>\<map>.vmf`, keeping the map's
   file name so the BSP reaches the game under the same name. `func_instance` paths are re-pointed so
   the copy finds the same instance files.
4. It compares the copy with what it read, block by block. A difference no pass declared stops the
   compile with an error rather than shipping a map that changed for no stated reason.
5. Its last line, `COMPILE_PAL_SET file "<copy>"`, hands the copy to the rest of the compile. Compile Pal
   copies the `.bsp`, `.prt` and `.lin` back beside your map after each step.

Your `.vmf` is never written.

## What it measures

**Audit** (every compile, from the VMF, changes nothing): areaportals used against Garry's Mod's
ceiling, doors no areaportal closes with, static props that never fade, func_viscluster, world brushes
shaped like detail, hint faces, named lights nothing switches, and prop_dynamic that need not be
entities.

**Report** (the Viswright Report step, after VVIS, from the BSP): clusters, how many each can see
(equal to VVIS's "Total clusters visible" minus its "Optimized" count, which is the PVS the map ships
with), the world and displacement triangles each cluster can see, the worst places by position, and
areaportal and face headroom.

**Garry's Mod loads at most 191 areaportals**, windows included: a map with an areaportal numbered 192
stops at load with `ClientSetupVisibility: portal number (192) too large`. Measured in game; the area
count is not the limit (the tools accept 1024). The map card shows how many are used, and blocks a
compile that would go over.

## Rules

- **The source VMF is never written.**
- **Failures are loud.** A failed check, a malformed map or a crash prints `error:` and exits non-zero,
  which stops the compile. Nothing half-done reaches VBSP.
- **Only declared changes.** Every pass states the kinds of change it makes; anything else is refused.
- **Deterministic.** The same map gives the same copy at any thread count.

## Commands

```
viswright optimize <map.vmf> [-source <queued.vmf>] [-dryrun] [-threads N]
                   [-detail] [-nodraw] -vbsp <exe> -vvis <exe> -game <folder>
viswright audit <map.vmf>
viswright report <map.bsp>
viswright diff <a.vmf> <b.vmf>
viswright collision <a.bsp> <b.bsp>
viswright status <map.vmf>
```

## Requirements

- The Compile Pal fork from the version that lets a step hand VBSP a different VMF (`$sourceVmf$`
  and a live `COMPILE_PAL_SET file`).
- Garry's Mod for the first release.

## Building

[SourceKit](https://github.com/catualus/SourceKit) is a submodule in `external/SourceKit`; set
`SourceKitRoot` to build against another copy.

```
git clone --recurse-submodules https://github.com/catualus/Viswright.git
dotnet test Viswright.slnx
./build-plugin.ps1
```

Licensed GPL-3.0, see [LICENSE](LICENSE). See [NOTICE.md](NOTICE.md) for what may be taken from the
Source SDK.
