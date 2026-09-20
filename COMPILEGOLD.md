# CompileGold

CompileGold is a fork of [ruarai/CompilePal](https://github.com/ruarai/CompilePal), rebuilt as a
standalone GoldSrc-only compile tool. It works with any HLT-family compiler (Vluzacn's ZHLT,
SDHLT, etc. - anything shipping `hlcsg.exe`/`hlbsp.exe`/`hlvis.exe`/`hlrad.exe`/`hlfix.exe` under
those standard names). It shares no identity, settings, telemetry, or update channel with the
original CompilePal.

## HLFIX auto-skip

If your input file is already a `.map` (TrenchBroom saves/exports directly to `.map`, no `.rmf`
involved), HLFIX now skips itself automatically instead of running and failing with "input file
can't be the same as output file" (since the intended output would be identical to the input in
that case) - HLCSG onward already reads from the same file either way, so there's nothing lost.
You'll see a one-line "Skipping HLFIX" log note instead of hlfix's usage text dumped into the
compile log.

## Compile pipeline

CompileGold's input is a **`.rmf`** (Hammer 3.x/J.A.C.K. native format), not a `.vmf` or `.map`.
The pipeline is:

1. **HLFIX** - runs `hlfix.exe` on the selected `.rmf`, writing a `.map` file next to it (via an
   explicit `-o` flag, using hlfix's confirmed CLI syntax: `hlfix <mapname>[.rmf] -o <outfile>`)
2. **HLCSG** (`hlcsg.exe`) - CSG stage
3. **HLBSP** (`hlbsp.exe`) - BSP stage
4. **HLVIS** (`hlvis.exe`) - VIS stage
5. **HLRAD** (`hlrad.exe`) - lighting stage
6. **COPY** - copies the compiled `.bsp` into your mod's `maps` folder
7. **GAME** - launches `hl.exe` with the map

Steps 2-5 all compile the `.map` that HLFIX generated, not the original `.rmf` - the app works out
that file's path (same name, same folder, `.map` extension) via a `$goldMapFile$` substitution
token.

Each tool's exe path is now built from a single dedicated substitution token
(`$hlfixExe$`, `$hlcsgExe$`, `$hlbspExe$`, `$hlvisExe$`, `$hlradExe$` - each resolving to
`Path.Combine(BinFolder, "<tool>.exe")`, quoted as one unit) rather than concatenating
`$binFolder$` with a literal `\toolname.exe` suffix in each `meta.json`'s `Path` field. The
earlier version of this fork had that concatenation, which broke the quoting whenever
`BinFolder` needed quotes (e.g. contained spaces or an apostrophe) - the literal suffix ended up
outside the quoted section, producing exactly the `"...\tools"\hlfix.exe` mangled path some users
hit. Fixed now.

Flags marked "SDHLT-specific" in the UI (`-worldextent`, `-nowadautodetect` in HLCSG;
`-nofixprt` in HLVIS; `-pre25`, `-nostudioshadow`, and the whole `-ao*` ambient occlusion family
in HLRAD) are SDHLT additions that vanilla ZHLT doesn't have - skip them if your tools don't
recognise them.

All four tools' option lists (HLCSG: 33, HLBSP: 26, HLVIS: 16, HLRAD: 76) were rebuilt from
each tool's own `-?` help output rather than guessed, so they should match your actual binaries'
supported flags exactly.

## Dark mode

The "always white" bug (everything except the compile log staying white regardless of the
toggle) came from `CompilePalTheme.xaml` hardcoding `ThemeForeground`, `ThemeBackground`
(literally `White`), and `IdealForeground` - independent of whichever base MahApps theme
(light/dark) was active, so those fixed colors always won. Fixed by splitting that file into
`CompilePalTheme.Light.xaml` / `CompilePalTheme.Dark.xaml`, neither of which touches those three
tokens anymore (they're left to the active base theme), and giving the dark variant its own
dark-appropriate grid-row and disabled-checkbox colors.

The toggle is now a **live** switch, no restart. That required converting every place the app
consumes a theme brush/color (`MahApps.Brushes.*`, `MahApps.Colors.*`, `CompilePal.Brushes.*`)
from `StaticResource` to `DynamicResource` across `App.xaml`, `MainWindow.xaml`,
`ParameterAdder.xaml`, `ProcessAdder.xaml`, `LaunchWindow.xaml`, and `PresetDialog.xaml` -
`StaticResource` resolves once at load and won't react to a dictionary swap, `DynamicResource`
does. Style resources (`MahApps.Styles.*`, `GroupListBoxStyle`, etc.) were left as
`StaticResource` since only colors need to react to the swap. `App.ApplyTheme()` then simply
replaces both the base MahApps dictionary and the `CompilePalTheme.Light/Dark.xaml` overlay in
`Application.Resources.MergedDictionaries` - this is the same technique MahApps' own
`ThemeManager` uses internally (some of its own control templates, like window buttons, were
already `DynamicResource` for exactly this reason).

**Unverified**: this relies on MahApps shipping `dark.red.xaml` alongside the `light.red.xaml`
this project already uses (very standard convention, but I can't build here to confirm), and I
haven't been able to actually run the live swap to watch it repaint.

## Resource packaging (RESGEN / PACK)

GoldSrc doesn't have a pak lump the way Source's BSPs do, so CompileGold never packs anything
into the `.bsp`. Instead there are two separate steps, both sharing the same scan logic:

- **RESGEN** (on by default) - writes only the `.res` file
- **PACK** (off by default, tick it to enable) - writes the `.res` **and** a distributable zip

Both run right after HLRAD and:

1. Parse the freshly compiled `.bsp` directly (GoldSrc BSPVERSION 30 - header, lump directory,
   entities text lump, miptex lump) rather than shelling out to another tool
2. Scan every entity's key/value pairs for anything that looks like a resource path (`.mdl`,
   `.spr`, `.wav`, `.tga`, `.bmp`, `.wad`, `.txt`, etc. by extension - not an exhaustive
   FGD-driven per-entity-class table, since I don't have one I can fully verify), plus a special
   case for `skyname` (expands to the 6 `gfx/env/<name><suffix>.tga` files)
3. Check the miptex lump for any texture with no embedded pixel data; if any is found, read
   worldspawn's `wad` key and add those WAD filenames as dependencies too (skipped entirely if
   every texture is embedded, e.g. you compiled with `-nowadtextures`)
4. Look for `<mapname>_detail.txt` next to your `.rmf`/`.map` (SDHLT detail props) - included in
   PACK's zip if present, and scanned with the same extension heuristic for further dependencies
5. Resolve every discovered path against your mod folder, then (unless disabled) against the
   base game folder next to it (e.g. `valve` next to `your_mod`) as a fallback
6. Skip `models/player.mdl` (and anything under `models/player/`) - it ships with every GoldSrc
   install, never worth bundling
7. Group everything into categories (Models, Sound, Sprites, GFX, WADs, Other) and write
   `<mapname>.res` in the mod's **maps folder** (next to where the bsp ends up), each category
   under a `// CategoryName` comment header, blank line between sections. **The bsp itself is
   never listed in the .res** - it's downloaded through the normal map-change mechanism, so
   listing it there would have the server tell clients to download the very map they're already
   loading.

PACK additionally zips everything into `<mapname>.zip` (named to match the bsp, also in the maps
folder) - the bsp, the `.res`, the detail.txt if present, and every resolved dependency at its
correct relative path. The zip *does* include the bsp (unlike the `.res` file) since it's meant
as a single archive you can hand off or extract straight into a server's mod folder.

Anything unresolvable gets logged as missing rather than silently dropped. Use the **Extra
File** option (repeatable - check it again for each additional path) to add dependencies the
scan can't catch on its own: things referenced only through non-standard entity keys, resources
loaded dynamically rather than declared in the map, or anything from inside a `.mdl`'s own
internal texture/sound references (the scan doesn't parse model files themselves).

This is new code, not a place I could point at prior art in the original CompilePal, and I
can't run it against a real compiled map to confirm the BSP parsing is exactly right - the
lump layout and entity-text format are stable, well-documented parts of the GoldSrc format, but
if the `.res`/zip come out empty or wrong, paste me what it logged (turn on **Verbose Scan
Log** first) and I'll fix it.

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
  The built-in Source-only presets (Fast, Full, Publish, Pack_BSP, etc.) were removed too, leaving
  a single **GoldSrc** preset.
- **Gold accent color** in place of CompilePal's red, across the whole theme
- **Checkbox parameter picker**: every option for a tool (HLCSG, HLBSP, etc.) shows as a
  checkbox you can toggle directly - checking/unchecking adds or removes it from the active
  preset immediately, no extra "add" step. A dropdown at the top of that window defaults to
  **Hide Debugging Options** (things like `-chart`, `-leakonly`, `-nt`/`-nd`/`-nu`/`-na`) and can
  be switched to **Show All Options**. Repeatable options (RESGEN's "Extra File", CUSTOM's
  "Command Line Argument") don't stay checked - each click adds one more instance, since a
  plain toggle can't represent "added three times with three different values"; edit each
  instance's value afterwards in the main parameter list.
- **GoldSrc resource packaging (RESGEN)**: generates a `.res` and a dependency zip for
  distribution instead of packing anything into the bsp - see the section above.

## Setting up a game configuration

GoldSrc has no file for CompileGold to auto-detect, so add one manually: Game Selector -> `+`,
then fill in:

| Field          | Value |
|----------------|-------|
| Name           | e.g. `Half-Life` |
| Bin Folder     | folder containing `hlcsg.exe`, `hlbsp.exe`, `hlvis.exe`, `hlrad.exe`, `hlfix.exe` |
| Game Folder    | your mod folder (e.g. `...\Half-Life\your_mod`) |
| Game EXE       | path to `hl.exe` |
| Map Folder     | the mod's `maps` folder (compiled `.bsp` gets copied here) |
| SDK Map Folder | folder where your `.rmf` source files live |

If your tools use different filenames (e.g. `sdHLCSG.exe`, or `_x64` variants), rename/symlink
them to the plain names above, or edit the `$hl*Exe$` tokens in
`CompilePalX/GameConfiguration/GameConfigurationManager.cs`.

Then pick the **GoldSrc** preset before compiling a `.rmf`.

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
