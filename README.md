# More Network Capacity 1.1.0

提高 Cities: Skylines 1 的网络节点和路段容量，约为原版的两倍，并处理相关存档、渲染及第三方模组数据。需要 Harmony。

Raises the network node and segment limits in Cities: Skylines 1 to roughly twice their vanilla capacity, with support for related save data, rendering, and third-party mod data. Requires Harmony.

目前提供容量或数据适配的模组包括 TM:PE、Node Controller Renewal、AdaptiveRoads、Network Skins、81 Tiles 2、Zoning Adjuster、Move It，以及使用 KianCommons 网络工具的模组。相关模组均为可选依赖，安装后自动检测适配。

Capacity or data compatibility is provided for TM:PE, Node Controller Renewal, AdaptiveRoads, Network Skins, 81 Tiles 2, Zoning Adjuster, Move It, and mods using KianCommons network tools. These mods are optional and detected automatically.

**使用前请备份存档。使用扩展网络的存档，需要保持本模组启用。项目包含 AI 辅助生成的代码，仍可能存在未知问题。**

**Back up your saves before use. Saves using expanded networks require this mod to remain enabled. This project includes AI-assisted code and may contain unknown issues.**

[创意工坊页面 · Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3814019901)

## 开发 · Development

当前公开源码为 1.1.0。运行缓冲区容量为 65024；兼容性适配并不意味着所有模组组合都已验证。

The public source is version 1.1.0. Runtime buffer capacity is 65024; compatibility support does not mean every mod combination has been tested.

本次更新 · Changes in this release:

- 修正高 ID 网络 AI 模拟范围。Corrects high-ID network AI simulation ranges.
- 完善 TM:PE 高 ID 定时灯模拟、保存和清理，以及交通规则保存、缓存重建和全网络更新范围。Improves TM:PE high-ID timed light simulation, saving and cleanup, traffic rule persistence, cache recreation and full-network updates.
- 修正 AdaptiveRoads 轨道渲染中的节点颜色索引。Corrects node-color indexes in AdaptiveRoads track rendering.
- 新增一键导出诊断 ZIP，包含版本、模组列表、网络和渲染状态、游戏日志及近期阶段/错误报告；仅本地导出，不自动上传。Adds on-demand diagnostic ZIP export with versions, mod lists, network/render state, game logs and recent stage/error reports; local export only, with no automatic upload.

本地构建及专项检查通过，并有成功读档记录。恢复高 ID 模拟会增加原先遗漏的计算量，本次更新不代表模拟速度提升。

Local builds and targeted checks passed, and a game load completed successfully. Restoring high-ID simulation adds previously omitted work; this release does not claim faster simulation.

Windows 构建需要本地 Cities: Skylines 1 游戏程序集、CitiesHarmony.Harmony.dll、CitiesHarmony.API.dll，以及 .NET Framework 3.5 编译器。依赖 DLL 不包含在仓库中。构建脚本和部分工具目前使用本地 Steam 路径；请先按你的安装位置调整。

Building on Windows requires local Cities: Skylines 1 assemblies, CitiesHarmony.Harmony.dll, CitiesHarmony.API.dll, and the .NET Framework 3.5 compiler. Dependency DLLs are not included. The build script and some tools currently use local Steam paths; adjust them for your installation first.

```powershell
.\build.ps1
```

欢迎贡献 · Contributions welcome: [CONTRIBUTING.md](CONTRIBUTING.md)

许可证 · License: [MIT](LICENSE)
