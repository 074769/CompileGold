# CompileGold

CompileGold is a fork of [ruarai/CompilePal](https://github.com/ruarai/CompilePal), rebuilt as a
standalone GoldSrc-only compile tool using [SDHLT](https://github.com/seedee/SDHLT). It shares no
identity, settings, telemetry, or update channel with the original CompilePal.

## Compile pipeline

CompileGold's input is a **`.rmf`** (Hammer 3.x/J.A.C.K. native format), not a `.vmf` or `.map`.
The pipeline is:

1. **HLFIX** - runs `hlfix.exe` on the selected `.rmf`, writing a `.map` file next to it
2. **HLCSG** (`sdHLCSG.exe`) - CSG stage
3. **HLBSP** (`sdHLBSP.exe`) - BSP stage
4. **HLVIS** (`sdHLVIS.exe`) - VIS stage
5. **HLRAD** (`sdHLRAD.exe`) - lighting stage
6. **COPY** - copies the compiled `.bsp` into your mod's `maps` folder
7. **GAME** - launches `hl.exe` with the map

Steps 2-5 all compile the `.map` that HLFIX generated, not the original `.rmf` - the app
automatically works out that file's path (same name, same folder, `.map` extension) via a new
`$goldMapFile$` substitution token.

**Known gap:** `hlfix.exe`'s exact command-line syntax isn't something I could confirm from public
documentation - `CompilePalX/Parameters/HLFIX/meta.json`'s `BasisString` currently just passes the
file path (the same convention every other tool here uses). If your `hlfix.exe` build expects
something different (an explicit output flag, etc.), edit that `BasisString` to match before
relying on it.

## What's different from CompilePal

- **Renamed**: builds as `CompileGold.exe` (window titles, taskbar, About text all say
  CompileGold)
- **No shared state**: registry settings live under `HKCU\Software\CompileGold` instead of
  `...\CompilePal`, so both can be installed on the same machine without interfering
- **No telemetry**: analytics are disabled outright (the built-in keys belong to the upstream
  project's account)
- **No update checks**: CompileGold won't check itself against, or link to, CompilePal's GitHub
  releases
- **No auto-detection**: the old "read HKCU\Software\Valve\Hammer\General and parse
  GameConfig.txt" flow is gone. Game configs are always added by hand (Game Selector -> `+`)
- **Source Engine tooling removed entirely**: VBSP/VVIS/VRAD, BSPZip, VBSPInfo, VPK, cubemap
  baking, nav mesh generation, and the particle-manifest/soundscape utilities are all gone -
  none of them apply to GoldSrc. The "Add Game" form only asks for GoldSrc-relevant paths now.
- **Gold accent color** in place of CompilePal's red, across the whole theme
- **Checkbox parameter picker**: every option for a tool (HLCSG, HLBSP, etc.) shows as a
  checkbox you can toggle directly - checking/unchecking adds or removes it from the active
  preset immediately, no extra "add" step. A dropdown at the top of that window defaults to
  **Hide Debugging Options** (things like `-chart`, `-leakonly`, `-circus`) and can be switched
  to **Show All Options**.

## Setting up a game configuration

GoldSrc has no file for CompileGold to auto-detect, so add one manually: Game Selector -> `+`,
then fill in:

| Field          | Value |
|----------------|-------|
| Name           | e.g. `Half-Life (SDHLT)` |
| Bin Folder     | folder containing `sdHLCSG.exe`, `sdHLBSP.exe`, `sdHLVIS.exe`, `sdHLRAD.exe`, `hlfix.exe` |
| Game Folder    | your mod folder (e.g. `...\Half-Life\your_mod`) |
| Game EXE       | path to `hl.exe` |
| Map Folder     | the mod's `maps` folder (compiled `.bsp` gets copied here) |
| SDK Map Folder | folder where your `.rmf` source files live |

If you're on 64-bit Windows, rename/symlink the `_x64` SDHLT binaries to the plain names above,
or edit `Path` in the relevant `CompilePalX/Parameters/<TOOL>/meta.json`.

Then pick the **GoldSrc - SDHLT** preset before compiling a `.rmf`.

## Building

Same as before - I can't compile the `.exe` in this sandbox (no .NET SDK, no nuget.org access).
On a machine with internet access:

```
dotnet restore
dotnet publish /p:PublishProfile=PublishRelease
```

requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Output lands in
`CompilePalX/bin/Release/Deploy/` as a self-contained `win-x86` build.

## Not build-tested

This fork was written and reviewed source-only - I don't have a Windows/.NET environment to
actually compile and run it here. I checked every changed file for consistency (removed
tools' references cleaned up, substitution tokens wired through, no dangling class
references), but a first real build may still surface something I missed - if so, paste me
the error and we'll fix it.
