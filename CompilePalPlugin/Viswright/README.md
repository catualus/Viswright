# Viswright for Compile Pal

Copy this folder into Compile Pal's `Plugins` directory and restart Compile Pal. A **Viswright**
step appears; add it to a preset and tick it. It runs before VBSP.

Viswright optimises a copy of your map and VBSP compiles the copy. Your `.vmf` is never written.
The `.bsp`, `.prt` and `.lin` still appear beside your map as before, so Hammer's pointfile and
portal file loading keep working.

This build has no optimisation passes yet. It audits the map on every compile, and the separate
**Viswright Report** step (copy that folder in too) measures the compiled map's visibility after VVIS.
The map card shows how many of Garry's Mod's 191 areaportals the map uses.

Needs the Compile Pal fork from the version that lets a step hand VBSP a different VMF. On an older
Compile Pal the step runs but VBSP compiles your map as it is.

Source and documentation: https://github.com/catualus/Viswright
