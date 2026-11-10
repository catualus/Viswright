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

@@TF2@@

- **Seen.** Viswright's hints cut more than Valve's on seven of the ten maps. Three of those seven
  (pl_badwater, pl_goldrush and arena_lumberyard) are partly Valve's choice: their 6 to 10
  func_viscluster entities merge clusters to save VVIS time, and that lets more be seen. On two of them
  Valve's version sees more than Stripped.
- **Drawn.** Viswright is ahead on three maps, level on two and behind on five. The gap is areaportals.
  Valve's designers place 1 to 134 a map, many opened by map logic. Viswright places 2 to 26, and only
  where a door or opening provably divides two areas.

### Garry's Mod

- **Plain**: the map's source compiled as it is.
- **Shipped**: the author's own BSP from the Workshop, measured at the same points.
- **Viswright**: the source run through Viswright with every pass on and every item accepted.

Three of the sources are BSPSource 1.4.8 decompiles: rp_downtown_v4c_v2 (Workshop 110286060),
rp_downtown_tits_v2 (1527403485) and rp_rockford_v2b (622810630). VBSP keeps no hint brushes, so a
decompile has none. Plain is therefore the map without its author's hints, and Shipped shows what those
hints did. rp_evocity_v33x is compiled from its original VMF (MIT,
github.com/SuchtBunker/GMod-DarkRP-Maps); no shipped BSP of it was measured.

@@GMOD@@

- **Seen.** rockford_v2b's and tits_v2's own hints cut what is seen by 34 to 39%. Viswright's hint search
  finds 1 to 7% on these maps. Their streets are walled in up to the sky, so a hint over open ground has
  little to cut off.
- **Drawn.** Viswright cuts what is drawn by 13 to 24% on the two downtown maps and 7% on evocity, with
  the areaportals it adds in doorways and windows. The authors' own hints barely change what is drawn,
  0 to 4%. On rockford_v2b Viswright adds 81 areaportals, but they save under 1%.

### Compile time

VVIS time for each version; Viswright's own run is the search, done once. Once its items are decided,
later compiles are pinned and cost one VBSP run more than compiling without Viswright.

@@TIME@@

### Frame rate

Measured in Garry's Mod at 1600x900 on an RTX 3070 Ti Laptop, with TF2 mounted for the TF2 maps. The
view walks a fixed path over the map's standing points for about 90 seconds; the path is the same for
every version of a map. Each figure is the average frames per second and the 1% low, as the median of
three runs. The compiled versions have no built cubemaps; the shipped ones do.

@@FPS@@
