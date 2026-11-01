# Viswright

Automatic visibility optimisation for Source maps, as a Compile Pal step. It does the work Hammer
mappers do by hand - func_detail, nodraw on hidden faces, areaportals in doorways, hint brushes, prop
fade distances - on a copy of the map, proves each change with the real compilers, and backs out
anything that does not pay for itself.

**Status: phase 0.** The plumbing works and no optimisation passes exist yet. A run copies the map,
checks the copy differs only in what it is allowed to, and hands it to VBSP.

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

## Rules

- **The source VMF is never written.**
- **Failures are loud.** A failed check, a malformed map or a crash prints `error:` and exits non-zero,
  which stops the compile. Nothing half-done reaches VBSP.
- **Only declared changes.** Every pass states the kinds of change it makes; anything else is refused.
- **Deterministic.** The same map gives the same copy at any thread count.

## Commands

```
viswright optimize <map.vmf> [-source <queued.vmf>] [-dryrun] [-threads N]
viswright diff <a.vmf> <b.vmf>
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
