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
two sides are joined some other way would not divide two areas. The way round is followed to the first
opening in a wall - found and checked as a doorway is - and closed: by that doorway's own portal if it is
one, otherwise by a portal in the opening, then the next, up to six. A window gets a
func_areaportalwindow that fades shut between 1,000 and 1,200 units away, with black fading in over it
(Valve's own distances and stand-in); a passage the floor runs through gets an
always-open func_areaportal. Openings water runs through are left alone. A doorway whose rooms still meet,
through something that is not a framed opening, is dropped. The portal closes with the door when the
door, together with any opaque brushes framing it, fills the opening and the door's materials are
opaque; otherwise it stays open. Unnamed doors are named `vw_door_<id>`, unique across the map.

Garry's Mod loads at most 191 areaportals, windows and the map's own included, so the portals are
ranked by what they stop the engine drawing. With every door shut, the engine floods out from each
standing point through the open portals, narrowing the view to each portal's outline, and only draws
what that leaves in view; a window past its fade distance is shut. The triangles drawn - world faces
and static props, from each model's `.dx90.vtx` - are counted with and without each group of portals:
doors between the same two areas, and a door with the windows and doorways it needs, a window two
doors need paid for once. The groups worth most per portal are kept, and that set is compiled again to
prove it.

**Window pictures** (`-windowpictures`, with `-areaportals`) makes a shut window show the room behind
it instead of black. The room on one side - the more closed-in one, so a shop is seen from the street -
is traced in the first compile and lined with a shell inside the window's stand-in: a back wall, side
walls, floor and ceiling, each a few units behind the real surface it copies and cut into 32-unit cells
so only cells with a wall behind them are kept, plus cards across the room's depth (up to eight, one
per 128 units) for what stands in it. The faces look one way, into the room, and are drawn only while
the window is shut, so the room keeps its depth as you walk past. A window whose room cannot be traced
gets a flat picture on each side. Viswright writes a black placeholder material for every face into
`materials/viswright/<map>/` and a `bake.json` saying where each is seen from; Compile Pal's **BAKE**
step then draws the lit map into each picture after VRAD and packs them into the BSP.

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

Map entity IDs stay stable when the Lua uses them. Garry's Mod numbers map entities by their place in
the BSP's entity list (`MapCreationID`), and a converted entity leaves that list, so every entity after
it would move down a place: DarkRP keeps door titles, owners and job doors by that number, and keypad
addons their links, so saved data would land on the wrong doors. When any scanned Lua uses
`MapCreationID` or `GetMapCreatedEntity` (copying the ID into a field of the same name, as the game's
own duplicator does, does not count), only entities with no other map entity after them in the list are
converted - often few or none - and the compiled entity list is checked against the map's own: if any
entity of the map moved, the pass is undone. The log says so as a warning and names the files, and the
review lists each entity kept for it.

**Hints over open ground** (`-hints`, `-hinttime M`) places hints the way Valve's own maps have them:
few, and large.

- **Caps.** Ground with 384 units or more of sky above where players stand is grouped into regions,
  split where the ground drops by more than 128 units between neighbouring standing points, so a pit and
  the ground round its edge are two. Down from the sky beside each region, the first roof, wall top or
  higher ground met is its rim, and the commonest rim heights (up to three, 96 units apart) are where it
  is capped, just above the roofs. A cap is one horizontal face at one height over everything
  open at that height that joins the region - on the map's own grid of 128 units, cut into as few
  rectangles as cover it, each reaching half a cell into the walls round it - so no part is left out and
  none sits at another height. On an open map one cap can cover the whole map.
- **Cuts.** The plane of every vertical world face - a facade, a corner, a wall's end - carried past each
  end of the face across the open space beside it, traced on a 32-unit grid in the plane, up to the
  face's top and at most 3,072 units along.
- **Boxes.** Once a cap is in, each building standing under it - a run of columns whose top is 128 units
  or more above the ground round it and 48 below the cap - is wrapped in a box of vertical hint faces from
  the ground to the cap, each side moved onto the facade nearest it.

Before anything is compiled, each candidate is ranked by the PVS at stake where it cuts: the clusters
its faces pass through the inside of (probed every 64 units), each weighted by the triangles visible from
every standing point in it, as a share of what all the standing points see. On Valve's TF2 maps that
picked out where Valve hinted with an AUC of 0.82 to 0.85. Then the search goes in rounds, each
compiling candidates alone on top of the hints accepted so far and measuring the whole map: the eight
ranked highest in the first round (caps over the same ground at three heights at most, other kinds two
to a family, and a box always, while one waits), four each round after, and then any measured in an
earlier round whose gain then could still beat the round's best - hints stand in for each other, so a
gain only shrinks as others go in. The round's best goes in if the whole map sees at least 0.3% less;
otherwise the search stops. Several compile at a time, sharing the machine's threads (or the `-threads`
given).

The hints accepted are compiled together with the tools as they are, and kept only if the whole map
sees at least 1% less, VVIS takes at most two and a half times as long (or 30 seconds more; over it, the
last accepted go first), the map stays clear of the face limits, and collision is unchanged. If VVIS
fails on the set - VVIS++ can, on a set whose hints each compiled alone - the hint at fault is found by
halving and left out. A hint is toolshint on the faces that split and toolsskip on every other face: not
solid, never drawn, unable to leak. The search needs VVIS++ and stops when its time (15 minutes by
default) runs out. `-hintsearch tiles` uses the earlier search instead, described in
[Hints the way Valve places them](#hints-the-way-valve-places-them), for comparison.

"More visible" is measured at the points a player can stand: positions at eye height above solid
ground on a 128-unit grid, found once from the first compile, in open space joined to a spawn point or
a teleport destination. A sealed room nobody can reach, such as a box built above a city, is left
out. Two compiles do not share clusters, but they share these points, so the triangles
visible from each can be compared directly.

Triangles are counted from each face's corners: a face with n corners is n - 2 triangles. A compiled
face also lists a vertex wherever a neighbouring face has a corner on its edge - T-junction fixing puts
them there - and counting those made maps compiled with and without `-notjunc` differ by a third, and
made a hint that splits a face cost its neighbours triangles. Since 10 November 2026 neither counts;
figures measured before then, in [Results](#results), counted every vertex.

Checking compiles use VBSP's default block size unless `-blocksize N` gives the one the real compile
uses. It matters: on a city map a block size of 4,096 can give a fifth fewer clusters than 1,024, and a
pass that helps at one can hurt at the other.

## Results

Measured on 2 and 3 November 2026 on fourteen well-known maps: the ten TF2 maps whose sources Valve
released, and four of the most played DarkRP maps. The method is in
[docs/benchmark.md](docs/benchmark.md). Every version of a map was compiled with the same tools,
options and machine, three times over. Each figure below is the median of those three runs. vvis++ gave
the same visibility every time, so only compile times have a range.

- **Seen**: the triangles in the PVS from a standing position, as a mean over every place a player can
  stand (the **Points** column). The same points are used for every version of a map.
- **Drawn**: the world and static-prop triangles the engine draws from those places, with areaportals
  honoured, doors shut and windows shut past their fade. Props are most of it.

Percentages are against the first column; lower is better.

### Team Fortress 2

- **Stripped**: Valve's map with its hints, skips, areaportals, occluders and visclusters taken out.
  func_detail is kept.
- **Valve**: the map as Valve shipped it.
- **Viswright**: Stripped run through Viswright with every pass on and every item accepted
  (`-mode auto`).

| Map | Points | Stripped: seen / drawn | Valve: seen / drawn | Viswright: seen / drawn |
| --- | ---: | ---: | ---: | ---: |
| ctf_2fort | 2,961 | 5,579 / 211,894 | -12.0% / -18.9% | -18.1% / -18.9% |
| cp_dustbowl | 4,885 | 7,283 / 126,830 | -10.1% / -6.3% | -10.9% / -4.7% |
| cp_badlands | 3,807 | 10,582 / 237,346 | -25.3% / -26.1% | -18.8% / -15.9% |
| cp_granary | 5,181 | 9,889 / 218,646 | -12.9% / -23.7% | -16.0% / -17.2% |
| cp_gravelpit | 5,179 | 13,271 / 153,022 | -19.1% / -18.6% | -14.0% / -11.0% |
| tc_hydro | 5,940 | 5,341 / 147,922 | -17.4% / -21.5% | -20.4% / -21.4% |
| pl_badwater | 3,736 | 5,886 / 147,876 | -2.2% / -13.3% | -27.2% / -17.6% |
| pl_goldrush | 6,520 | 8,221 / 168,208 | +15.7% / -7.7% | -9.9% / -8.2% |
| arena_lumberyard | 3,361 | 22,870 / 276,906 | +9.8% / +2.7% | -24.5% / -18.1% |
| arena_ravine | 1,204 | 17,624 / 241,687 | -10.6% / -8.7% | -7.4% / -4.9% |

- **Seen.** Viswright's hints cut more than Valve's on seven of the ten maps. Three of those seven
  (pl_badwater, pl_goldrush and arena_lumberyard) are partly Valve's choice: their 6 to 10
  func_viscluster entities merge clusters to save VVIS time, and that lets more be seen. On two of them
  Valve's version sees more than Stripped.
- **Drawn.** Viswright is ahead on three maps, level on two and behind on five. The gap is areaportals.
  Valve's designers place 1 to 134 a map, many opened by map logic. Viswright places 2 to 26, and only
  where a door or opening provably divides two areas.

### Hints against Valve's

Measured on 10 November 2026, after the hint search was rebuilt: hints alone, against Valve's own, on
the four TF2 maps an 8 November study compared them on. Every version is Stripped with nothing but
hints added, compiled with vbsp++ (`-cullverts`) and vvis++, and measured at the table above's standing
points, with triangles counted by their corners.

- **Valve**: Valve's hint brushes put back into Stripped.
- **Tiles**: the hints the 2 and 3 November run placed with the earlier search (now `-hintsearch tiles`),
  put into Stripped.
- **Regions**: `-hints` alone on Stripped, with the search described in [The passes](#the-passes).

Figures are how much less a standing point sees than in Stripped; higher is better. Runs are the VBSP and
VVIS runs the search made, the final ones included.

| Map | Valve | Tiles: less seen / runs | Regions: less seen / runs |
| --- | ---: | ---: | ---: |
| ctf_2fort | 15.5% | 17.4% / 262 | 19.9% / 132 |
| cp_gravelpit | 19.1% | 11.5% / 270 | 20.1% / 90 |
| arena_ravine | 8.3% | 5.6% / 88 | 9.4% / 102 |
| cp_badlands | 23.9% | 18.6% / 96 | 25.8% / 202 |

- **Regions is ahead of Valve on all four maps**, by 1.0 to 4.5 points, and of Tiles by 2.5 to 8.6.
  Counted by face area instead of triangles it is ahead of Valve on all four too, by 1.5 to 4.9 points.
- **Where it comes from.** One cap does most of it: z 144 over all of Gravelpit (17.7% alone, against
  Valve's z 160), z 480 over Badlands (13.5%), z 656 over 2Fort (10.7%), z 352 over Ravine (3.8%). Then
  more caps at other roof lines, the boxes under a cap (3.3% on Badlands, all ten together), and long
  cuts across the open ground, 0.4 to 1.9% each. Each map kept 6 to 11 hints.
- **Runs.** Tiles ran out of its 15 minutes on three of the maps with wall ends still to measure (on
  Badlands, 39 of 141 batches), so its runs are what fitted in the time. Regions was given 60 minutes and 8
  of the machine's 16 threads, shared with other work; it stopped on its 0.3% rule on 2Fort, Gravelpit and
  Ravine, and ran out of time on Badlands after 15 rounds, then left out the last four it had accepted to
  keep VVIS within its allowance. Viswright's own results in the tables above are still the earlier search.

### Garry's Mod

- **Plain**: the map's source compiled as it is.
- **Shipped**: the author's own BSP from the Workshop, measured at the same points.
- **Viswright**: the source run through Viswright with every pass on and every item accepted.

Three of the sources are BSPSource 1.4.8 decompiles: rp_downtown_v4c_v2 (Workshop 110286060),
rp_downtown_tits_v2 (1527403485) and rp_rockford_v2b (622810630). VBSP leaves hint brushes in the
BSP's brush lump, so the decompiles keep their authors' hints (13, 29 and 149 hint faces): Plain is the
author's map, hints included. rp_evocity_v33x is compiled from its original VMF (MIT,
github.com/SuchtBunker/GMod-DarkRP-Maps); no shipped BSP of it was measured.

| Map | Points | Plain: seen / drawn | Shipped: seen / drawn | Viswright: seen / drawn |
| --- | ---: | ---: | ---: | ---: |
| rp_downtown_v4c_v2 | 4,232 | 7,846 / 34,525 | -0.1% / -0.2% | -4.4% / -23.9% |
| rp_downtown_tits_v2 | 12,218 | 5,636 / 49,288 | -34.3% / -3.8% | -1.3% / -12.8% |
| rp_rockford_v2b | 67,359 | 11,525 / 239,054 | -39.0% / -1.6% | -7.0% / -0.5% |
| rp_evocity_v33x | 27,942 | 4,483 / 137,260 | - | -3.5% / -7.3% |

The Shipped column overstates what the shipped maps save. The shipped rp_downtown_tits_v2 and
rp_rockford_v2b were compiled without T-junction fixing and Plain was not, and fixing T-junctions splits
faces into more triangles; compiling the same source with `-notjunc` reproduces the whole gap. Counted
by faces, Shipped sees 0.1 to 0.3% less than Plain.

- **Seen.** Measured one at a time, the authors' hints are worth 0.2% on tits_v2, 0.4% on v4c_v2 and
  1.9% on rockford_v2b. Viswright's hint search finds 1 to 7% on these maps. Their streets are walled in
  up to the sky, so a hint over open ground has little to cut off.
- **Drawn.** Viswright cuts what is drawn by 13 to 24% on the two downtown maps and 7% on evocity, with
  the areaportals it adds in doorways and windows. Shipped's 0 to 4% carries the same T-junction
  difference. On rockford_v2b Viswright adds 81 areaportals, but they save under 1%.

### Compile time

VVIS time for each version; Viswright's own run is the search, done once. Once its items are decided,
later compiles are pinned and cost one VBSP run more than compiling without Viswright.

| Map | Stripped VVIS | Valve VVIS | Viswright VVIS | Viswright's own run |
| --- | ---: | ---: | ---: | ---: |
| ctf_2fort | 5.9 s (5.9-6.7) | 12.0 s (11.6-12.1) | 16.6 s (15.3-16.7) | 18 min |
| cp_dustbowl | 9.7 s (8.9-9.7) | 22.1 s (20.4-22.3) | 17.0 s (16.9-18.7) | 21 min |
| cp_badlands | 5.9 s (5.8-6.1) | 18.9 s (18.8-19.8) | 17.8 s (16.0-18.6) | 19 min |
| cp_granary | 5.0 s | 13.9 s (13.0-13.9) | 34.3 s (33.6-34.8) | 21 min |
| cp_gravelpit | 2.4 s (2.3-2.5) | 9.4 s (9.1-9.6) | 5.5 s (5.4-5.6) | 18 min |
| tc_hydro | 4.3 s (4.1-4.4) | 7.4 s (7.0-7.9) | 16.6 s (16.5-16.9) | 19 min |
| pl_badwater | 11.7 s (11.4-12.4) | 18.0 s (16.9-18.8) | 52.8 s (42.2-56.7) | 21 min |
| pl_goldrush | 9.6 s (9.5-9.8) | 12.4 s (12.2-12.4) | 21.6 s (21.4-22.0) | 19 min |
| arena_lumberyard | 7.0 s (6.8-7.0) | 9.5 s (9.5-9.7) | 23.8 s (23.8-24.1) | 17 min |
| arena_ravine | 0.5 s | 2.2 s (2.1-2.3) | 1.1 s | 2 min |

| Map | Plain VVIS | Viswright VVIS | Viswright's own run |
| --- | ---: | ---: | ---: |
| rp_downtown_v4c_v2 | 80.0 s (76.3-104.8) | 116.6 s (115.0-120.9) | 33 min |
| rp_downtown_tits_v2 | 35.4 s (34.7-51.5) | 50.8 s (47.4-50.9) | 31 min |
| rp_rockford_v2b | 57.6 s (56.6-57.6) | 85.7 s (85.4-86.1) | 37 min |
| rp_evocity_v33x | 112.1 s (111.4-112.1) | 198.3 s (197.5-198.7) | 43 min |

### Frame rate

Measured in Garry's Mod at 1600x900 on an RTX 3070 Ti Laptop, with TF2 mounted for the TF2 maps. The
view walks a fixed path over the map's standing points for about 90 seconds; the path is the same for
every version of a map. Each figure is the average frames per second and the 1% low, as the median of
three runs. The compiled versions have no built cubemaps; the shipped ones do.

| Map | Stripped: avg / 1% low | Valve: avg / 1% low | Viswright: avg / 1% low |
| --- | ---: | ---: | ---: |
| ctf_2fort | - | - | - |
| cp_dustbowl | - | - | - |
| cp_badlands | - | - | - |
| cp_granary | - | - | - |
| cp_gravelpit | - | - | - |
| tc_hydro | 296 / 128 | - | - |
| pl_badwater | - | - | - |
| pl_goldrush | - | - | - |
| arena_lumberyard | - | - | - |
| arena_ravine | - | - | - |

| Map | Plain: avg / 1% low | Shipped: avg / 1% low | Viswright: avg / 1% low |
| --- | ---: | ---: | ---: |
| rp_downtown_v4c_v2 | - | - | - |
| rp_downtown_tits_v2 | - | - | - |
| rp_rockford_v2b | - | - | - |
| rp_evocity_v33x | - | - | - |

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
the kind.

**PVS slack.** The review also measures how much of what each place's PVS lets through cannot actually be
seen from there. From every standing point, rays are cast in 65,536 directions against the world's
triangles (displacements included; glass and tool faces let them through) and the faces they meet first
are counted. This runs on the GPU through Direct3D 12 - about 230 million rays a second on an RTX 3070 Ti
laptop, ten times the CPU - and on the CPU (16,384 rays a point) when there is no GPU or with `-cpu`.
Slack is split in two: what other points in the same cluster see (what cutting clusters up could save)
and what no point in the cluster sees (VVIS being cautious through portals, which no split inside the
cluster removes). It is a measurement only, shown as a plan per floor and a heatmap layer; no change is
decided by it.

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
  how much the PVS lets through (0.50).
- **Visclusters trade visibility for compile time** on open maps: Lumberyard sees 32% more with them, for a
  VVIS three times faster.
- **Detail buys compile time**: made structural, 2Fort's detail would see 8% less, with VVIS 18 times slower
  (198 s); Lumberyard's would take VVIS 854 s.
- **Areaportals** in these maps are opened and closed by logic and doors' outputs rather than by being
  named as a door's areaportal, so what shutting them hides is not measured here.

## Hints the way Valve places them

Drawn close up in 3D, Valve's hints make three shapes: one cap at roof height over each open area,
vertical faces carrying a wall's end, a building's corner or a facade across the open space beside it,
and boxes round buildings and bridges under a cap. The search above builds those three.

Until 10 November 2026 it worked differently, and `-hintsearch tiles` still does. It tried a hint across
each 1,024-unit square of open ground at heights from 192 to 960 units above that square's own ground,
every square at the same step in one compile, and each square kept the height that cut what its own
standing points saw the most, if by 2%. Then every end of a world wall (a vertical face up to 64 units
wide) was carried across the open space beside it and measured in batches whose surrounding standing
points did not overlap, one kept if the points within 512 units of it saw 2% less. Measured against
Valve's hints on four TF2 maps (in [Results](#results)), that fell short in two ways. The tiles left holes
and sat at different heights where Valve has one face: over Gravelpit's pit, one face over the same
squares cut 18.9% where the tiles cut 11.8%. And each hint was judged where it stands, but 60 to 94% of
a good hint's gain lands more than 512 units away from it, so the credit went to the wrong candidates.

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
viswright optimize <map.vmf> [-source <queued.vmf>] [-dryrun] [-threads N] [-areaportals [-windowpictures]]
                   [-detail] [-nodraw] [-fades [-fadepixels N]] [-slim [-luapath <folder>]...]
                   [-hints [-hinttime M] [-hintsearch regions|tiles]] [-blocksize N] [-review]
                   [-mode auto|review|pinned] [-accept]
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

- [Compile Pal fork](https://github.com/catualus/CompilePal) 1.3.0 or later: it lets a step hand VBSP
  a different VMF (`$sourceVmf$` and a live `COMPILE_PAL_SET file`), shows Viswright's review in the
  map preview, and has the BAKE step that Window pictures need.
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
