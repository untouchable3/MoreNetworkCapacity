# More Network Capacity 1.1.1

提高 Cities: Skylines 1 的网络节点和路段容量，约为原版的两倍，并处理相关存档、渲染及第三方模组数据。需要 Harmony。

Raises the network node and segment limits in Cities: Skylines 1 to roughly twice their vanilla capacity, with support for related save data, rendering, and third-party mod data. Requires Harmony.

目前提供容量或数据适配的模组包括 TM:PE、Node Controller Renewal、AdaptiveRoads、Network Skins、81 Tiles 2、Zoning Adjuster、Move It，以及使用 KianCommons 网络工具的模组。相关模组均为可选依赖，安装后自动检测适配。

Capacity or data compatibility is provided for TM:PE, Node Controller Renewal, AdaptiveRoads, Network Skins, 81 Tiles 2, Zoning Adjuster, Move It, and mods using KianCommons network tools. These mods are optional and detected automatically.

**使用前请备份存档。使用扩展网络的存档，需要保持本模组启用。项目包含 AI 辅助生成的代码，仍可能存在未知问题。**

**Back up your saves before use. Saves using expanded networks require this mod to remain enabled. This project includes AI-assisted code and may contain unknown issues.**

[创意工坊页面 · Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3814019901)

## 开发 · Development

当前公开源码为 1.1.1。运行缓冲区容量为 65024；兼容性适配并不意味着所有模组组合都已验证。

The public source is version 1.1.1. Runtime buffer capacity is 65024; compatibility support does not mean every mod combination has been tested.

本次更新 · Changes in this release:

- 补齐 TM:PE 高 ID 工具标记、缓存、资产导出和遍历范围，并加强关键数组、缓存引用与道路/轨道车道连接的加载检查。Extends TM:PE high-ID tool markers, caches, asset exports and traversal ranges, and strengthens load-time checks for required arrays, cached references and road/track lane connections.
- 修正 81 Tiles 的高 ID 电力、水等模拟范围与加载适配；修正 Zoning Adjuster 读档时丢失高 ID 路段分区设置的问题。Corrects 81 Tiles high-ID utility simulation ranges and loading compatibility, and preserves high-ID segment zoning settings when Zoning Adjuster loads saved data.
- 减少 Node Controller 角点标志判断和缓冲区检查中的重复反射开销，保留数组替换及重置后的检查。Reduces repeated reflection in Node Controller corner flag checks and buffer validation, retaining checks after array replacement and reset.
- 诊断 ZIP 增加暂停、线程、帧号、加载状态及当前工具信息，仅按按钮采集、本地导出，不自动上传。Adds pause, thread, frame, loading and current-tool state to diagnostic ZIPs; captured on demand and exported locally, with no automatic upload.

本地构建及专项检查通过；1.1.1 改动的完整游戏验证仍待完成，尚未量化性能提升。恢复高 ID 模拟会增加原先遗漏的计算量。

Local builds and targeted checks passed; full in-game validation of the 1.1.1 changes remains pending, and performance gains have not been measured. Restoring high-ID simulation adds previously omitted work.

Windows 构建需要本地 Cities: Skylines 1 游戏程序集、CitiesHarmony.Harmony.dll、CitiesHarmony.API.dll，以及 .NET Framework 3.5 编译器。依赖 DLL 不包含在仓库中。构建脚本和部分工具目前使用本地 Steam 路径；请先按你的安装位置调整。

Building on Windows requires local Cities: Skylines 1 assemblies, CitiesHarmony.Harmony.dll, CitiesHarmony.API.dll, and the .NET Framework 3.5 compiler. Dependency DLLs are not included. The build script and some tools currently use local Steam paths; adjust them for your installation first.

```powershell
.\build.ps1
```

欢迎贡献 · Contributions welcome: [CONTRIBUTING.md](CONTRIBUTING.md)

许可证 · License: [MIT](LICENSE)
