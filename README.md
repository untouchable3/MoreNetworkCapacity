# More Network Capacity (MNC) 1.3.0

[简体中文](README.zh-CN.md) · [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3814019901) · [Report an issue](https://github.com/untouchable3/MoreNetworkCapacity/issues)

More Network Capacity raises the network node and segment limits in Cities: Skylines. It also adapts save handling, rendering, simulation, and supported third-party mods to the expanded network. [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2040656402) is required.

**Back up your city before enabling MNC.** Keep MNC enabled when loading a city that uses the expanded network. On some saves, the simulation can take more than ten minutes to start after loading.

## What's new in 1.3.0

Added compatibility for Improved Public Transport 2, 3, and 4 and extended support for several network tools. Fixed out-of-bounds errors in 81 Tiles 2 utility queues and incorrect colors on distant roads and race routes. Added the Network ID Inspector and Road Color Analyzer and reorganized the settings.

## Compatibility

MNC includes targeted compatibility for TM:PE, Node Controller Renewal, Intersection Marking Tool, Adaptive Roads, Network Skins, 81 Tiles 2, Zoning Adjuster, Move It, Transport Lines Manager, Transfer Manager CE, Direct Connect Roads, CSUR ToolBox, Network Multitool, SingleTrackAI, and Improved Public Transport 2, 3, and 4. It also adapts some network tools that use KianCommons.

These mods are optional and detected at runtime. Each adaptation covers specific code paths. Check the **Compatibility** tab in the MNC options to see the detected mods and methods MNC has patched for your setup.

## Diagnostics

- **Network ID Inspector** shows IDs for nodes, segments, and transit stops. To inspect an underground stop, choose the matching transit line tool and hover over the stop.
- **Road Color Analyzer** records material, texture, and rendering data. For a color change that follows the camera, use a single segment and mark the abnormal and recovered views. To compare two areas, keep the camera fixed and mark each segment's appearance.
- **Diagnostic ZIP** is exported locally from **MNC options → Diagnostics**. It can include a generated color report and the most recently modified local save. Review the ZIP and uncheck the save option if a save is unnecessary for the report.

When opening an issue, include the game and MNC versions, the steps that lead to the problem, and the related mods. Attach a diagnostic ZIP when available.

## Development

- **Capacity and persistence.** Vanilla buffer sizes are 32,768 nodes and 36,864 segments. MNC expands both buffers to 65,024 entries, including reserved slots. The capacity stays below 65,536 so tools that iterate with a 16-bit unsigned index can terminate without wrapping. See [`Capacity.cs`](Source/Capacity.cs).
- **Patches and adapters.** `Source/Patches` covers the game's network traversal, serialization, and rendering. `Source/Compatibility` contains adapters for individual mods. `Tools` contains IL inspection utilities and offline regression tests.
- **Windows builds.** The repository's [`build.ps1`](build.ps1) requires a local Cities: Skylines installation, Harmony assemblies, and the .NET Framework 3.5 compiler. If your Steam library is elsewhere, set `MNC_STEAM_ROOT` to the folder containing `steamapps`. Run `powershell -NoProfile -File .\build.ps1` to generate and verify the patch targets, compile the mod, and assemble `dist/MoreNetworkCapacity`.

This project uses AI-assisted development. Contributions are welcome under the [MIT License](LICENSE). See the [contribution guide](CONTRIBUTING.md).
