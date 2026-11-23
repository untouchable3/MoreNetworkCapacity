# More Network Capacity（MNC）1.3.0

[English](README.md) · [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3814019901) · [报告问题](https://github.com/untouchable3/MoreNetworkCapacity/issues)

More Network Capacity 提高《城市：天际线》一代的路网节点和路段上限，并适配扩容后的存档读写、渲染、模拟和第三方模组。运行需要 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2040656402)。

**启用前请备份城市存档。** 已使用扩展路网的城市需要保持 MNC 启用再加载。部分存档读档后可能需要十分钟以上才开始模拟。

## 1.3.0 更新

适配 Improved Public Transport 2、3 和 4，改善多款路网工具的兼容性。修复 81 Tiles 2 水电队列数组越界，以及远景道路和赛事路线取色错误。新增路网 ID 查看器和道路颜色分析器，优化设置界面。

## 兼容性

MNC 已对 TM:PE、Node Controller Renewal、Intersection Marking Tool、Adaptive Roads、Network Skins、81 Tiles 2、Zoning Adjuster、Move It、Transport Lines Manager、Transfer Manager CE、Direct Connect Roads、CSUR ToolBox、Network Multitool、SingleTrackAI 和 Improved Public Transport 2、3、4 提供专项适配，也适配了部分使用 KianCommons 的路网工具。

这些模组均为可选，MNC 会在运行时自动检测。每项适配有具体的代码覆盖范围。可在 MNC 设置的「兼容状态」页查看当前检测到的模组和 MNC 实际挂接的方法。

## 故障排查

- **路网 ID 查看器**可查看节点、路段和站点编号。查看地下站点时，打开对应的线路工具，把鼠标移到站点上。
- **道路颜色分析器**可以采集材质、纹理和渲染数据。同一路段随视角变色时，用单项分析记录异常和恢复。对比不同区域时，保持视角不动，逐段标记正常或异常。
- **诊断包**在「MNC 设置 → 故障排查」中导出到本机，可包含已生成的颜色报告和最近修改的本地存档。分享前请检查 ZIP 内的内容。如果排查无需存档，可在导出前取消勾选存档选项。

提交 Issue 时，请写明游戏与 MNC 版本、触发步骤和相关模组，有诊断包时可一并附上。

## 开发

- **路网容量与存档。** 原版节点和路段缓冲区分别为 32,768 和 36,864 项。MNC 将两者扩展到 65,024 项，其中包含保留槽位。容量低于 65,536，因此使用 16 位无符号索引的工具可以正常终止遍历，避免索引回绕。参见 [`Capacity.cs`](Source/Capacity.cs)。
- **补丁与适配。** `Source/Patches` 处理游戏的路网遍历、存档和渲染，`Source/Compatibility` 包含各模组的适配，`Tools` 提供 IL 检查工具和离线回归测试。
- **Windows 构建。** 仓库中的 [`build.ps1`](build.ps1) 需要本机游戏程序集、Harmony 程序集和 .NET Framework 3.5 编译器。Steam 库安装在其他位置时，将环境变量 `MNC_STEAM_ROOT` 设为包含 `steamapps` 的目录。运行 `powershell -NoProfile -File .\build.ps1`，脚本会生成并验证补丁目标、编译模组，并输出到 `dist/MoreNetworkCapacity`。

本项目使用 AI 辅助开发，采用 [MIT 许可证](LICENSE)。欢迎通过 [贡献指南](CONTRIBUTING.md) 参与。
