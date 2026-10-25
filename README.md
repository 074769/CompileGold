<p align="center">CompileGold is an easy to use wrapper for the GoldSRC SDHLT map compiling tools.</p>

<p align="center">
  <img src="https://github.com/074769/CompileGold/blob/df7fee15685aad41bbf8d86c32c6a3844e8ef4e0/%7BA918BF73-FD43-41A8-91A0-1FCECCE2543C%7D.png" alt="Sublime's custom image"/>
</p>


## Downloads

#### Latest Release

[Compile Gold](https://github.com/074769/CompileGold/releases)

Each release has two builds. Download the one that matches your Windows:
* `x64` - 64-bit Windows (most users)
* `x86` - 32-bit Windows

## Requirements

CompileGold does not bundle the .NET runtime, so you need to install the **.NET 10 Desktop Runtime** first. Install the one that matches the build you downloaded:
* x64 build: the **x64** Desktop Runtime
* x86 build: the **x86** Desktop Runtime

Download it from: https://dotnet.microsoft.com/download/dotnet/10.0 (under ".NET Desktop Runtime", pick x64 or x86).

CompileGold also needs a GoldSrc HLT-family compiler (such as [SDHLT](https://github.com/seedee/SDHLT) or ZHLT) that provides `hlcsg.exe`, `hlbsp.exe`, `hlvis.exe`, and `hlrad.exe`. These are not included, so point CompileGold at the folder that contains them.


## Features
* Light/Night mode.
* Made to work with SDHLT (https://github.com/seedee/SDHLT) - Have not tested other tools, since all parameters are from SDHLT.
* Packing
* Statistics/status
* Error Checking (Use google)
* Not freezing your computer while compiling (skill issue)
* Plugins and Custom Compile Steps (Shrugs)
* Batch Compiling
* Much More! (?)
