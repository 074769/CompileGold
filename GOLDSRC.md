# CompilePal — GoldSrc / SDHLT fork

This is a light fork of [ruarai/CompilePal](https://github.com/ruarai/CompilePal) that adds
support for compiling GoldSrc maps with [SDHLT](https://github.com/seedee/SDHLT) instead of
the Source Engine VBSP/VVIS/VRAD toolchain.

CompilePal's compile pipeline is data-driven — every compile step is just a folder under
`CompilePalX/Parameters/<STEP>/` with a `meta.json` (executable path + base args) and a
`parameters.json` (the list of flags shown in the UI). That meant no changes were needed to
the core compiler-invocation code (`CompileExecutable.cs`), lighting/vis logic, etc. — only
additions.

## What was added

- `CompilePalX/Parameters/HLCSG/` — CSG stage (`sdHLCSG.exe`)
- `CompilePalX/Parameters/HLBSP/` — BSP stage (`sdHLBSP.exe`)
- `CompilePalX/Parameters/HLVIS/` — VIS stage (`sdHLVIS.exe`)
- `CompilePalX/Parameters/HLRAD/` — RAD/lighting stage (`sdHLRAD.exe`)
- `CompilePalX/Presets/GoldSrc - SDHLT/` — a preset chaining HLCSG → HLBSP → HLVIS → HLRAD → COPY → GAME

GoldSrc's compile pipeline has four stages instead of Source's three, and its tools take a
raw `.map` file path rather than `-game <moddir> file.vmf`, so each new step's `meta.json`
uses `" $vmfFile$"` as its base argument string (no `-game`) and points its `Path` at
`$binFolder$\sdHL***.exe`. Common flags for each tool (from SDHLT's docs and the Valve
Developer Wiki HLCSG/HLBSP/HLVIS/HLRAD pages) are pre-populated in each `parameters.json`,
including the two SDHLT-specific ones: `-worldextent` (HLCSG) and `-nofixprt` (HLVIS). You
can add any flag not listed via the auto-added "Command Line Argument" field.

## What was changed

Two lines in `CompilePalX/MainWindow.xaml.cs` so `.map` files (not just `.vmf`/`.vmm`/`.bsp`)
can be opened and dropped onto CompilePal:
- the "Add Map" file dialog filter
- the `--add` command-line argument handler

Everything else (packing, error checking, presets UI, launch window, etc.) works unchanged,
though BSPZip/VPK packing and the entity/material/model "Keys" scanning are Source-specific
and won't do anything useful for GoldSrc maps — just don't include the PACK step in your
GoldSrc presets.

## Setting up a GoldSrc game configuration

GoldSrc has no `gameinfo.txt`/`GameConfig.txt` for CompilePal to auto-detect, so add the
configuration by hand: open CompilePal → the "+" / "Edit" button next to the game selector,
and fill in:

| Field          | Value |
|----------------|-------|
| Name           | e.g. `Half-Life (SDHLT)` |
| Bin Folder     | folder containing `sdHLCSG.exe`, `sdHLBSP.exe`, `sdHLVIS.exe`, `sdHLRAD.exe` |
| Game Folder    | your mod folder (e.g. `...\Half-Life\your_mod`) |
| Game EXE       | path to `hl.exe` |
| Map Folder     | the mod's `maps` folder (compiled `.bsp` gets copied here) |
| SDK Map Folder | folder where your `.map` source files live |

Leave VBSP/VVIS/VRAD/BSPZip/VBSPInfo/VPK blank — they're unused by the GoldSrc preset.

If you're on 64-bit Windows, rename/symlink the `sdHLCSG_x64.exe` etc. binaries to the plain
names above, or edit `Path` in `CompilePalX/Parameters/HLCSG/meta.json` (etc.) to match.

Then pick the **GoldSrc - SDHLT** preset before compiling a `.map`.

## Building

I can't produce a compiled `.exe` for you directly — this sandbox has no .NET SDK installed
and no network access to nuget.org, and CompilePal is a self-contained WPF app with several
NuGet dependencies (MahApps.Metro, Newtonsoft.Json, ValveKeyValue, etc.), so package restore
has to happen on a machine with real internet access. The build itself is otherwise trivial:

```
dotnet restore
dotnet publish /p:PublishProfile=PublishRelease
```

(requires the [.NET 10 SDK](https://dotnet.microsoft.com/download); this is exactly what
CompilePal's own CI does, see `.github/workflows/build_release.yml`). The output lands in
`CompilePalX/bin/Release/Deploy/` as a self-contained `win-x86` executable — zip that folder
up and it's ready to run on any Windows machine, no .NET install required on the target.

If you don't have Visual Studio, the .NET SDK's `dotnet` CLI alone is enough — just run the
two commands above from the repo root in a terminal.
