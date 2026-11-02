# Viswright

Automatic visibility optimisation for Source maps, as a Compile Pal step. It does the work Hammer
mappers do by hand - func_detail, nodraw on hidden faces, areaportals in doorways, hint brushes, prop
fade distances - on a copy of the map, proves each change with the real compilers, and backs out
anything that does not pay for itself.

**Status: phase 5.** Six passes, all opt-in while in beta: **Areaportals in doorways**, **Make detail**,
**Hide covered faces**, **Fade static props**, **Fewer entities** and **Hints over open ground**.
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
and it is compiled again. Detail merges the leaves its brushes no longer cut, and a merged leaf sees
everything any of its parts saw, so the street beside a run of trim can end up seeing more. After
VVIS, every detailed brush whose standing points within 384 units see more in total goes back to being
world, and the map is compiled again, up to three times. The pass is undone if the leaks will not stop,
if any collision brush is lost, added, moved or changes contents, or if more is still visible from where
a player stands.

**Hide covered faces** (`-nodraw`) makes a face nodraw when all of it is pressed against faces of solid
brushes whose every material is opaque, so it can only be seen from inside a brush. VBSP already does
this between two world brushes and between two detail brushes; this covers world against detail and
detail against world, which it does not. A material counts as opaque only when its VMT says so; one
that cannot be found never hides anything.

**Fade static props** (`-fades`, `-fadepixels N`) gives a prop_static that never fades a fade distance:
where its bounding sphere shrinks to N pixels on a 1920-wide screen at a 90 degree field of view (6 by
default, a size nobody notices; 10 balanced, 16 aggressive). Props in the 3D skybox, landmarks over 512
units across, models marked never to fade, and props whose fade would lie beyond the map are left
alone. Every model and its distance is written to `fades.txt` in the work folder.

**Fewer entities** (`-slim`, `-luapath <folder>`) turns a prop_dynamic that nothing names, parents, fires,
animates, breaks, recolours or lets the physgun move into a prop_static, and an always-solid, unnamed
func_brush that already casts lightmap shadows into func_detail. Each frees an edict and the network
traffic of an entity. A prop is converted only if VBSP will take its model as a static prop - compiled
with `$staticprop`, and with no `prop_data` unless it says `allowstatic` - and is given
`disableshadows 1`, so the lightmaps stay as they were. Any class the game's gamemodes and addons, or the
Lua folders named, look entities up by is left alone. After compiling, the entity count must fall and
the static prop count rise by exactly the number converted, or the pass is undone; a prop VBSP still
refuses (its copy of a model can differ from the one Viswright reads) is put back.

**Hints over open ground** (`-hints`, `-hinttime M`) tries a hint brush across each 1,024-unit square of
open ground - streets, courtyards, anywhere with 384 units or more of sky above where players stand - at
heights from 192 to 960 units above the ground, below the sky. Every square is tried at the same step
above its own ground in one compile, so each height costs one VBSP and one VVIS run; a square keeps the
height that cut what its own standing points see the most, if by 2% or more. The squares kept are
compiled together and any that stopped helping lose their hint. The set is kept only if the whole map
sees at least 1% less, VVIS has no more than 15% more portals to work through, and collision is
unchanged. A hint is toolshint on top and toolsskip on every other face: not solid, never drawn, unable
to leak. The search needs VVIS++ and stops when its time (15 minutes by default) runs out.

"More visible" is measured at the points a player can stand: positions at eye height above solid
ground on a 128-unit grid, found once from the first compile, in open space joined to a spawn point or
a teleport destination. A sealed room nobody can reach - prominence has a 16,384-unit box above the
city - is left out. Two compiles do not share clusters, but they share these points, so the triangles
visible from each can be compared directly.

Checking compiles use VBSP's default block size unless `-blocksize N` gives the one the real compile
uses. It matters: on these maps a block size of 4,096 gives a fifth fewer clusters than 1,024, and a
pass that helps at one can hurt at the other.

Measured on 2026-11-01 with vbsp++ and vvis++ (default VBSP options, every floor counted):

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

Fades and Fewer entities (2026-11-01, 6 pixels):

| | rp_downtown_prominence_d | rp_downtown_meowy |
| --- | --- | --- |
| Static props given a fade | 374 (mostly rebar debris, 5,000-10,000 units) | 29 (it already fades nearly all) |
| prop_dynamic made prop_static | 30 | 23 |
| Left as entities | named, parented, physgun-movable, physics models | the same, and 51 models not compiled for static use |
| func_brush made func_detail | 0 (all never solid, named, or without lightmap shadows) | 0 |

Every pass, measured as the maps really compile (2026-11-01, `-blocksize 4096`, only places a player
can reach):

| | rp_downtown_prominence_d | rp_downtown_meowy |
| --- | --- | --- |
| Before | 5,338 clusters, 13,489 portals, 6,472 triangles visible, VVIS 22.6 s | 5,169 clusters, 12,925 portals, 6,941 triangles, VVIS 20.5 s |
| Areaportals | 69 added; 6,484 triangles visible, 6,327 with the new doors shut | 74 added; 6,972, 6,798 with them shut |
| Make detail, all candidates | 455 brushes: 6,626 triangles visible against 6,484 | 310 brushes: 7,084 against 6,972 |
| Make detail, kept where no more is seen | 27 brushes: 6,473 | 33 brushes: 6,945 |
| Hide covered faces | 219 faces | 92 faces |
| Fades / Fewer entities | 374 props / 30 entities | 29 props / 23 entities |
| Hints over open ground | undone: 0.3% less seen (6,441 against 6,460), under the 1% bar | undone: 0.8% less (6,913 against 6,968) |
| Time | 12 minutes, 6 of them the hint search | 11 minutes |

At the block size these maps are really compiled with, detailing every candidate makes more visible
from the streets: only about a fifth of what newly appears lies behind a brush that became detail, the
rest is leaves merging. Keeping detail only where the points around each brush see no more leaves
under a tenth of the candidates and changes almost nothing - VVIS++ already takes about 22 seconds on
these maps, so there is little for detail to save. The figures from default options above do not carry
over. Hints do
not pay on either map: about 15 of 120 squares see 2% less on their own, but together they save under
1%. The streets are walled in up to the sky, so there is little over the rooftops for a hint to cut
off, and VVIS++'s leaves are already tight.

A full run on rp_downtown_meowy with every pass, hints and the review takes about 8 minutes (it took 11
before compiles were reused and Viswright's own work was spread over the cores); about nine tenths of it
is VBSP and VVIS. Each step logs its time and how much of it was compiling. Hint heights compile two at
a time, which finishes two compiles in 50 seconds rather than 60.

In Garry's Mod, the optimised prominence loads with all 191 portal numbers in use. At a storefront door
given a portal, wireframe rendering shows nothing behind the shut door and the whole plaza once it opens.

The optimised prominence was compiled in full and loaded in Garry's Mod. From open space around each of the 246
nodraw faces, 18,048 rays were traced at it; every one hit the brush covering it first. The other faces had no
open space in front of them at all.

## Reviewing what it did

With **Write a review** (`-review`), a run writes a `review` folder beside the copy:

- `<map>_review.vmf`, the optimised map for Hammer++. A **Viswright** visgroup holds every item:
  - **Applied**, by pass: the real changes;
  - **Markers**: boxes for applied changes that are not objects of their own, such as a nodraw face or a fade on a prop;
  - **Rejected**: a named box per refused candidate, with the reason in its comments;
  - **Heatmap**: flat cells coloured by how much is visible from where players stand, before, after and the change, seen in the 2D views and flat-shaded 3D.

  Markers and heatmap start hidden, and VBSP leaves hidden objects out, so the file still compiles to the optimised map. The camera list has one camera per top item, taken from each pass in turn, for the Camera tool to step through.
- Plans of each floor, drawn from the BSP: walls, detail, indoor and outdoor floor, each item outlined (applied solid, refused faint), and what is visible from each standing place before and after, and the change.
- A picture of each top item, `items.json`, and `review.html`, which puts them together with the reason for every item and a `setpos` to go and look in game.
- `viswright_tour.lua`, the same top items as a tour in Garry's Mod: copy it to `garrysmod/lua`, load the compiled map on a listen server with `sv_cheats 1`, run `lua_openscript_cl viswright_tour.lua`, then `viswright_next`, `viswright_prev` or `viswright_go N` to stand where each item is seen from, its reason on screen.

`viswright render <map.bsp> [-vmf <map.vmf>] [-from x,y,z] [-slack]` draws the plans of any compiled map,
or with `-from`, what that spot's PVS lets it see.

**In 3D.** The top 24 items are also drawn in 3D, ray-traced from the compiled map: the world as
compiled, doors and other brush entities from the VMF (glass see-through), cut away a little above the
item so its room opens from above, with the item in its status colour and items near it faint.
`viswright view <map.bsp> [-vmf <map.vmf>] [-game <folder>] [-target x,y,z] [-yaw D] [-pitch D]
[-distance N] [-cut z|none]` draws any spot of any compiled map the same way, outlining its hints,
areaportals and occluders.

For a doorway refused because its two rooms meet another way, the review shows *where*: the openings
between leaves that join the rooms when every other portal is shut, drawn in red, and the reason names
the kind. On rp_downtown_meowy, 109 of 228 framed doorways are refused this way: 50 through an opening
beside the door in the same wall (a shop window, mostly), 13 through one above it, 32 through another
doorway or gap close by, and 14 a long way round through other rooms.

**PVS slack.** The review also measures how much of what each place's PVS lets through cannot actually be
seen from there. From every standing point, rays are cast in 65,536 directions against the world's
triangles (displacements included; glass and tool faces let them through) and the faces they meet first
are counted. This runs on the GPU through Direct3D 12 - about 230 million rays a second on an RTX 3070 Ti
laptop, ten times the CPU - and on the CPU (16,384 rays a point) when there is no GPU or with `-cpu`.
Slack is split in two: what other points in the same cluster see (what cutting clusters up could save)
and what no point in the cluster sees (VVIS being cautious through portals, which no split inside the
cluster removes). It is a measurement only, shown as a plan per floor and a heatmap layer; no change is
decided by it.

On the downtown maps a standing point really sees about 1,180 of the 6,940 triangles its PVS lets through
(80% slack): 24% within clusters, 56% through portals. Open plazas are near 0%, streets and rooms 80-90%.
Against the hint search's results, the half of the squares with the most slack held 17 of meowy's 21 and
10 of prominence's 14 squares that a hint helped - a weak guide, and the slack within clusters pointed
the wrong way. Hints that helped did so by giving VVIS better portals, not by separating places that see
different things.

## What Valve's own maps teach

Valve released the sources of ten TF2 maps in 2009. Each was compiled with vbsp++ and vvis++ as Valve left
it, then with one kind of placement taken out, and measured the same way as Viswright measures (triangles
visible from where players can stand). Two sources were saved with their hints and portals hidden in
Hammer, which VBSP leaves out; they were brought back out of hiding first. The sources are not part of
this repository.

| Map | Hint faces | Without hints | Without visclusters | VVIS with / without hints |
| --- | --- | --- | --- | --- |
| cp_badlands | 542 | +37.0% | -0.3%, VVIS 14 s -> 22 s | 14.2 s / 3.6 s |
| pl_badwater | 192 | +26.0% | -15.3% | 18.5 s / 12.1 s |
| cp_gravelpit | 124 | +23.7% | no change | 7.5 s / 3.1 s |
| tc_hydro | 74 | +21.4% | no change | 6.9 s / 4.9 s |
| ctf_2fort | 202 | +19.5% | -3.8% | 10.9 s / 4.9 s |
| cp_granary | 585 | +16.1% | no change | 8.7 s / 4.0 s |
| cp_dustbowl | 137 | +11.1% | no change | 17.4 s / 7.0 s |
| arena_ravine | 83 | +10.5% | no change | 2.4 s / 0.7 s |
| arena_lumberyard | 305 | +8.0% | -31.9%, VVIS 10 s -> 27 s | 10.1 s / 2.3 s |
| pl_goldrush | 90 | +1.1% | -17.0% | 12.1 s / 9.2 s |

- **Hints pay on Valve's maps** - a median of about 17% less visible - at two to four times the VVIS time.
  Two thirds of Valve's hint faces are vertical and axis-aligned (median 200 units across), a third
  horizontal (median 350). Drawn on the maps, they make three shapes: a cap at roof height across open
  ground (2Fort's courtyard, all of Badlands, Gravelpit's pit), vertical walls cutting open terrain into
  cells, and boxes wrapping rooms, buildings and bridges. Across 621 squares of ground, the squares Valve
  hinted are picked out by slack through portals (AUC 0.64), not by slack within clusters (0.39) or by
  how much the PVS lets through (0.50) - the same as on the downtown maps.
- **Visclusters trade visibility for compile time** on open maps: Lumberyard sees 32% more with them, for a
  VVIS three times faster.
- **Detail buys compile time**: made structural, 2Fort's detail would see 8% less, with VVIS 18 times slower
  (198 s); Lumberyard's would take VVIS 854 s.
- **Areaportals** in these maps are opened and closed by logic and doors' outputs rather than by being
  named as a door's areaportal, so what shutting them hides is not measured here.

The downtown maps differ from these where it matters: their streets are walled in up to the sky, so a
cap over open ground has nothing to cut off, which is why the hint search finds so little there.

## Hints the way Valve places them

Drawn close up in 3D, most of Valve's vertical hints carry the end of a wall - a doorway's jamb, a
building's corner, the mouth of a passage - straight across the open space beside it. VBSP already has
that plane among its candidates, as the wall's end face; a hint makes it split there. **Hints over open
ground** now tries those too, after the roof caps: every end of a world wall (a vertical face up to 64
units wide) is carried along its own plane across the open space beside it until it meets solid again.
They are measured in batches whose surrounding standing points do not overlap, two batches compiling at
a time; one is kept if the points within 512 units of it see 2% less, and the set is kept if the whole map
sees 1% less and VVIS takes at most two and a half times as long (or 30 seconds more). Over budget, the
weakest half goes and the rest is compiled again. Both kinds of hint are now judged on VVIS time rather
than on a portal count.

Tested on Valve's maps with their hints taken out, then on the downtown maps:

| Map | Valve's hints | Viswright's: caps | + wall ends | Total | VVIS |
| --- | --- | --- | --- | --- | --- |
| ctf_2fort | 16.3% less visible | 11.5% | 4.8% | 15.8% (97% of Valve's) | 5 s -> 13 s (Valve's: 10.9 s) |
| cp_gravelpit | 19.2% | 9.3% | 3.5% | 12.4% (65%) | 3 s -> 21 s (Valve's: 7.5 s) |
| rp_downtown_meowy | - | undone (0.9%) | 13.2% (433 kept) | 13.2% | 14 s -> 37 s |

On meowy the search measured 60 of 240 batches of wall ends in its 20 minutes; more time finds more.

## Deciding, then compiling the same way every time

A map's first compile with any pass ticked is a **review**: everything is searched and measured, the
review above is written, and `<map>.viswright.json` appears beside the map with every item Viswright
would apply, waiting for a decision. The compile itself uses the map as it is.

Decide the items any of three ways:

- in Compile Pal's map preview, which draws the review over the compiled map: the panel lists every
  item with its reason and pictures, a click flies the camera to it (a refused doorway's leak in red),
  and **Accept** or **Deny** writes the decision straight into `<map>.viswright.json`;
- edit `<map>.viswright.json`: set an item's `decision` to `accept` or `deny`;
- in Hammer++, in the review VMF: delete an applied change you do not want, move or resize a hint or
  areaportal brush to where you want it, or put a refused item's marker into **Viswright/Accept**, and
  save;
- tick **Accept the review** (`-accept`), which takes in your Hammer edits and then accepts every item
  still waiting.

Once nothing is waiting, compiles are **pinned**: the accepted items are put back exactly as decided,
with no search, and checked by one VBSP compile - a leak, an areaportal that no longer divides two
areas, a model VBSP refuses as a static prop, or changed collision takes the item out and says so,
it is never forced in. The same map and the same decisions give the same copy every time, and a
pinned compile costs one VBSP run on top of compiling without Viswright (the first compile of the map
as it is is kept from the review while the map is unchanged). Items a later review no longer finds,
because the map changed around them, are marked stale and left out until reviewed again.

On rp_downtown_meowy with every pass: the review took about 7 minutes and left 251 items waiting; with
them accepted, each pinned compile takes about 20 seconds and gives a byte-identical copy every time.

Each pinned compile also writes `<map>_adopt.vmf` beside the copy: your map with the accepted changes
in a **Viswright accepted** visgroup, to open in Hammer and save over your map if you want them to be
your own. Viswright never writes your map.

`-mode auto` searches and applies whatever proves itself on every compile, as before reviews existed;
`-mode review` and `-mode pinned` force one or the other.

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
viswright optimize <map.vmf> [-source <queued.vmf>] [-dryrun] [-threads N] [-areaportals]
                   [-detail] [-nodraw] [-fades [-fadepixels N]] [-slim [-luapath <folder>]...]
                   [-hints [-hinttime M]] [-blocksize N] [-review] [-mode auto|review|pinned] [-accept]
                   [-cpu] -vbsp <exe> -vvis <exe> -game <folder>
viswright audit <map.vmf>
viswright report <map.bsp>
viswright diff <a.vmf> <b.vmf>
viswright collision <a.bsp> <b.bsp>
viswright model <models/x.mdl> -game <folder>
viswright render <map.bsp> [-vmf <map.vmf>] [-from x,y,z] [-slack] [-out <folder>]
viswright view <map.bsp> [-vmf <map.vmf>] [-game <folder>] [-target x,y,z] [-yaw D] [-pitch D] [-distance N] [-cut z|none]
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
