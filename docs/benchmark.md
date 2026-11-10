# Benchmark method

How Viswright's results in the README are measured, so anyone can repeat them.

## Maps

Well-known maps only, each with a source anyone can get.

| Map | Game | Why | Source |
| --- | --- | --- | --- |
| ctf_2fort, cp_dustbowl, cp_badlands, cp_granary, cp_gravelpit, tc_hydro, pl_badwater, pl_goldrush, arena_lumberyard, arena_ravine | TF2 | Valve's own maps, optimised by Valve's level designers | Valve's official VMFs (TF2 SDK `sdk_` sources) |
| rp_downtown_v4c_v2 | GMod | the most-used DarkRP map (Workshop 110286060, 875k subscribers) | decompiled with BSPSource 1.4.8 |
| rp_downtown_tits_v2 | GMod | widely played downtown variant (Workshop 1527403485) | decompiled with BSPSource 1.4.8 |
| rp_rockford_v2b | GMod | large DarkRP city (Workshop 622810630, 699k subscribers) | decompiled with BSPSource 1.4.8 |
| rp_evocity_v33x | GMod | large DarkRP city | original VMF, MIT, github.com/SuchtBunker/GMod-DarkRP-Maps |

A decompile is not the author's source: clip brushes come back as nodraw. Hint brushes do survive,
because VBSP leaves them in the BSP's brush lump, so a decompile keeps its author's hints. Results on a
decompile are labelled so.

Shipped BSPs are compared by triangles, and that count is only fair between maps compiled the same way.
The shipped rp_downtown_tits_v2 and rp_rockford_v2b were compiled without T-junction fixing, and the
maps compiled here were not: fixing T-junctions splits faces into more triangles. Their "seen" gap of
34 to 39% is those extra triangles, and compiling the source with `-notjunc` reproduces it. Counted by
faces the gap is 0.1 to 0.3%.

cp_badlands' VMF has its hints, skips, clips and 58 areaportals in Hammer's hidden blocks, which VBSP
skips; Valve's shipped BSP has all 58 portals, so they are unhidden before compiling. pl_badwater's
hidden blocks hold no visibility brushes and stay hidden.

## Variants

TF2 maps, where Valve's optimisation is the reference:

- **Valve**: the VMF as Valve shipped it.
- **Stripped**: Valve's hints, skips, areaportals, occluders and visclusters removed. func_detail is
  kept: every real map has it, and with it turned back into world brushes VVIS took 31.7 minutes on
  arena_ravine, the smallest map, against 1 second for Valve's version. So the TF2 comparison is about
  hints and areaportals; Make detail is judged on the GMod maps.
- **Viswright**: Stripped, run through Viswright with every pass on (accepting every item).

GMod maps, where there is no expert source:

- **Shipped**: the author's own BSP from the Workshop (visibility measured on it directly).
- **Plain**: the source compiled as it is.
- **Viswright**: the source run through Viswright with every pass on.

## Compiles

vbsp++ / vvis++ / vrad++ (Tools++ builds of 27 October 2026), each map with the options its game's publish
preset uses: VBSP `-cullverts`, VVIS default, VRAD `-final` (LDR for GMod, HDR for TF2). Block size
is VBSP's default unless stated. The same tools, options and machine for every variant.

Machine: AMD Ryzen 7 6800H (8 cores, 16 threads), 32 GB, RTX 3070 Ti Laptop, Windows 11.

## Measures

1. **Standing points.** Positions a player can stand at: 128-unit grid, eye height (64 units) above
   solid ground, joined through open space to a spawn point. Found once per map from the Plain (or
   Stripped) compile, and the same points are used for every variant, so variants are compared at
   the same places. Each table gives the point count.
2. **PVS triangles.** For each point, the world triangles in the leaves VVIS marks visible from the
   point's cluster. Reported as the mean over all points, with the 90th percentile.
3. **Drawn triangles.** The same, plus static-prop triangles in those leaves, with areaportals
   honoured: the engine's area flood through open portals, narrowed to each portal's outline, doors
   shut, windows shut past their fade distance. A prop counts with its full-detail model (`.dx90.vtx`,
   LOD 0) unless it is past its fade-out distance, where the engine draws nothing. Mean and 90th
   percentile.
4. **Compile time.** VBSP, VVIS and Viswright wall-clock seconds.
5. **Frame rate.** In Garry's Mod, single player, with TF2 mounted for the TF2 maps. Each map gets
   one camera path, the same for every variant: twelve waypoints spread over its standing points,
   visited in a short tour and walked from one to the next over neighbouring standing points (a step
   of up to 128 units), so the view never passes through a wall. A Lua script moves the view along it
   at a constant speed chosen so a run takes about 90 seconds, looking a few steps ahead, and records
   every frame time after a 2-second settle. Average FPS and 1% low (the 99th-percentile frame time).
   Display fixed: 1600x900 windowed, vsync off, `fps_max 0`, `mat_monitorgamma 2.2`, auto-exposure
   1/1, bloom off, HUD and viewmodel hidden. The compiled variants have no built cubemaps; a shipped
   BSP has its author's.

Every compile and every frame-rate run is repeated three times; tables give the median, and a range
where the three differ. On every map vvis++ gave the same visibility all three times; only times vary.

## Reproducing

Nothing here downloads anything; the sources above go in a folder as `tf2/<map>.vmf` and
`gmod/<map>.vmf` (with the shipped `gmod/<map>.bsp`).

1. `bench/run.ps1 -Sources <folder> -Out <out>` strips the TF2 maps, compiles every variant three
   times, runs Viswright once per map, measures, and writes `<out>/results.json`.
2. `python bench/stage.py <folder above out> <garrysmod> bench/path.py` copies each variant's BSP into
   `garrysmod/maps`, writes the camera paths and the plan into `garrysmod/data/vwbench`.
3. Copy `bench/vwbench.lua` to `garrysmod/lua/` and `bench/vwbench_auto.lua` to
   `garrysmod/lua/autorun/`, create `garrysmod/data/vwbench/active.txt`, set the display settings above
   in the console, and load the plan's first map: every variant is flown three times in turn and the
   lines go to `garrysmod/data/vwbench/results.txt`.
4. `python bench/tables.py <out>/results.json <results.txt> bench/readme_results.md` prints the README's
   Results section.
