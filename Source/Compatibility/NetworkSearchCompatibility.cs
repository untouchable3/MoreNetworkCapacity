using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class NetworkSearchCompatibility
    {
        private static readonly string[] Assemblies = {
            "NodeControllerRenewal", "IntersectionMarkingTool", "MoveIt", "TransportLinesManager",
            "SingleTrackAI", "NetworkMultitool"
        };
        private static readonly HashSet<string> installed = new HashSet<string>();
        private const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        internal static void Reset() { installed.Clear(); }

        internal static void Apply(Harmony harmony, bool levelLoaded)
        {
            if (harmony == null) return;
            foreach (string name in Assemblies)
            {
                if (installed.Contains(name)) continue;
                Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly(name);
                if (assembly == null) continue;
                try
                {
                    if (name == "MoveIt" || name == "NetworkMultitool")
                    {
                        // Detouring these tools can initialize their scene-dependent types.
                        if (!levelLoaded) continue;
                        ToolController controller = ToolsModifierControl.toolController;
                        string tool = name == "MoveIt" ? "MoveIt.MoveItTool" : "NetworkMultitool.NetworkMultitoolTool";
                        if (controller == null || controller.GetComponent(assembly.GetType(tool, true)) == null)
                            continue;
                    }
                    List<MethodInfo> targets = Targets(assembly);
                    foreach (MethodInfo method in targets)
                        Rewrite(method, PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null));
                    MethodInfo patch = typeof(NetworkSearchCompatibility).GetMethod("Rewrite", Methods);
                    List<MethodInfo> attempted = new List<MethodInfo>();
                    try
                    {
                        foreach (MethodInfo method in targets)
                        {
                            attempted.Add(method);
                            harmony.Patch(method, null, null, new HarmonyMethod(patch), null);
                        }
                    }
                    catch
                    {
                        foreach (MethodInfo method in attempted) harmony.Unpatch(method, patch);
                        throw;
                    }
                    installed.Add(name);
                    Logging.Info(name + " network capacity patched (" + targets.Count + " methods).");
                }
                catch (Exception ex) { Logging.Error(name + " network capacity compatibility failed: " + ex); }
            }
        }

        private static void Add(List<MethodInfo> list, Type type, string name, int arity)
        {
            MethodInfo found = null;
            foreach (MethodInfo m in type.GetMethods(Methods))
            {
                if (m.Name != name || (arity >= 0 && m.GetParameters().Length != arity)) continue;
                if (found != null) throw new AmbiguousMatchException(type.FullName + "." + name);
                found = m;
            }
            if (found == null || found.ContainsGenericParameters) throw new MissingMethodException(type.FullName, name);
            list.Add(found);
        }

        internal static List<MethodInfo> Targets(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            List<MethodInfo> list = new List<MethodInfo>();
            if (name == "MoveIt")
            {
                Type tool = assembly.GetType("MoveIt.MoveItTool", true);
                Add(list, tool, "GetMarqueeList", -1);
                Add(list, tool, "RaycastHoverInstance", -1);
            }
            else if (name == "SingleTrackAI")
                Add(list, assembly.GetType("SingleTrackAI.SingleTrainTrackAI", true), "CheckNextLane", 10);
            else if (name == "NetworkMultitool")
            {
                Type mode = assembly.GetType("NetworkMultitool.BaseNetworkMultitoolMode", true);
                Add(list, mode, "RenderNearNodes", 4);
                Add(list, mode.BaseType, "RayCast", 2);
            }
            else if (name == "TransportLinesManager")
            {
                Add(list, assembly.GetType("Commons.Utils.RoadSearchUtils", true), "FindNearNamedRoad", 13);
                Add(list, assembly.GetType("Commons.Utils.StopSearchUtils", true), "FindNearStops", 9);
                Type lines = assembly.GetType("TransportLinesManager.Utils.TLMLineUtils", true);
                Add(list, lines, "GetNearLines", 3);
                Add(list, lines, "GetNearStopPoints", 6);
            }
            else
            {
                Type extension = assembly.GetType(name == "NodeControllerRenewal" ?
                    "NodeController.AssetDataExtension" : "IMT.BuildingAssetDataExtension", true);
                Type parent = extension.BaseType;
                if (!parent.IsGenericType || parent.GetGenericTypeDefinition().FullName != "ModsCommon.Utilities.BaseBuildingAssetDataExtension`3")
                    throw new InvalidOperationException("Asset extension base changed.");
                Add(list, parent, "GetMap", 0);
            }
            return list;
        }

        // The asset editor may have a different live pool from the most recently loaded city.
        // Keep the ushort induction variable below its wrap point even with an oversized third-party pool.
        public static int AssetSegmentCount(NetManager manager)
        {
            return Math.Min(manager.m_segments.m_buffer.Length, Capacity.ExpandedSegmentCapacity);
        }

        internal static List<CodeInstruction> Rewrite(MethodBase __originalMethod, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>();
            foreach (CodeInstruction c in instructions) code.Add(new CodeInstruction(c));
            string assembly = __originalMethod.DeclaringType.Assembly.GetName().Name;
            if (assembly == "SingleTrackAI" || assembly == "NetworkMultitool")
            {
                bool signal = assembly == "SingleTrackAI";
                bool node = __originalMethod.Name == "RenderNearNodes";
                int original = signal || node ? Capacity.VanillaNodeCapacity : Capacity.VanillaSegmentCapacity;
                int match = -1;
                for (int i = 5; i + 6 < code.Count; i++)
                {
                    if (code[i].opcode != OpCodes.Ldc_I4 || !Equals(code[i].operand, original)) continue;
                    if (signal)
                    {
                        if (!Equals(code[i-5].operand, typeof(SimulationManager).GetField("m_currentFrameIndex"))
                            || code[i-2].opcode != OpCodes.Ldc_I4_8 || code[i-1].opcode != OpCodes.Shl
                            || code[i+1].opcode != OpCodes.Div || code[i+5].opcode != OpCodes.Sub
                            || !Equals(code[i+6].operand, 255)) continue;
                    }
                    else if (!Equals(code[i-5].operand, node ? typeof(NetNode).GetField("m_nextGridNode")
                            : typeof(NetSegment).GetField("m_nextGridSegment"))
                        || code[i-4].opcode != OpCodes.Stloc_S || code[i-3].opcode != OpCodes.Ldloc_S
                        || !Equals(code[i-4].operand, code[i-3].operand) || code[i-2].opcode != OpCodes.Brfalse_S
                        || (code[i+1].opcode != OpCodes.Blt && code[i+1].opcode != OpCodes.Blt_S)) continue;
                    if (match >= 0) throw new InvalidOperationException("Ambiguous capacity operand in " + __originalMethod);
                    match = i;
                }
                if (match < 0) throw new InvalidOperationException("Capacity operand changed in " + __originalMethod);
                code[match].opcode = signal ? OpCodes.Call : OpCodes.Ldsfld;
                code[match].operand = signal ? (MemberInfo)typeof(Capacity).GetMethod("GetNodeSimulationPhaseDivisor")
                    : typeof(Capacity).GetField(node ? "NodeLoopLimit" : "SegmentLoopLimit");
                return code;
            }
            if (__originalMethod.Name == "GetMap")
            {
                int b = code.FindIndex(delegate(CodeInstruction c) { return c.opcode == OpCodes.Ldc_I4 && Equals(c.operand, 36864); });
                if (b < 8 || b + 1 >= code.Count || code[b-1].opcode != OpCodes.Ldloc_3
                    || code[b-2].opcode != OpCodes.Stloc_3 || code[b-3].opcode != OpCodes.Conv_U2
                    || code[b-4].opcode != OpCodes.Add || code[b-5].opcode != OpCodes.Ldc_I4_1
                    || code[b-6].opcode != OpCodes.Ldloc_3 || code[b+1].opcode != OpCodes.Blt_S
                    || !(code[b+1].operand is Label)) throw new InvalidOperationException("Asset segment loop changed.");
                int body = code.FindIndex(delegate(CodeInstruction c) { return c.labels.Contains((Label)code[b+1].operand); });
                if (body < 0 || body + 5 >= b || code[body].opcode != OpCodes.Ldloc_0
                    || !Equals(code[body+1].operand, typeof(NetManager).GetField("m_segments"))
                    || code[body+3].opcode != OpCodes.Ldloc_3 || !Equals(code[body+4].operand, typeof(NetSegment))
                    || !Equals(code[body+5].operand, typeof(NetSegment).GetField("m_flags")))
                    throw new InvalidOperationException("Asset map no longer enumerates segment flags.");
                code[b].opcode = OpCodes.Ldloc_0;
                code[b].operand = null;
                code.Insert(b+1, new CodeInstruction(OpCodes.Call, typeof(NetworkSearchCompatibility).GetMethod("AssetSegmentCount")));
                return code;
            }

            bool moveIt = __originalMethod.DeclaringType.FullName == "MoveIt.MoveItTool";
            bool needNodes = moveIt || __originalMethod.Name != "FindNearNamedRoad";
            bool needSegments = moveIt || !needNodes;
            int nodes = 0, segments = 0;
            for (int i = 7; i + 1 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || code[i-7].opcode != OpCodes.Ldfld) continue;
                bool node = Equals(code[i-7].operand, typeof(NetNode).GetField("m_nextGridNode"));
                bool segment = Equals(code[i-7].operand, typeof(NetSegment).GetField("m_nextGridSegment"));
                if (!node && !segment) continue;
                int original = node && __originalMethod.Name != "FindNearStops" ? 32768 : 36864;
                if (!Equals(code[i].operand, original) || code[i-6].opcode != OpCodes.Stloc_S
                    || code[i-5].opcode != OpCodes.Ldloc_S || code[i-4].opcode != OpCodes.Ldc_I4_1
                    || code[i-3].opcode != OpCodes.Add || code[i-2].opcode != OpCodes.Dup
                    || code[i-1].opcode != OpCodes.Stloc_S || !Equals(code[i-5].operand, code[i-1].operand)
                    || code[i+1].opcode != (moveIt ? OpCodes.Ble_S : OpCodes.Clt)) continue;
                code[i].opcode = OpCodes.Ldsfld;
                code[i].operand = typeof(Capacity).GetField(node ? "NodeLoopLimit" : "SegmentLoopLimit");
                if (node) nodes++; else segments++;
            }
            if (nodes != (needNodes ? 1 : 0) || segments != (needSegments ? 1 : 0))
                throw new InvalidOperationException("Unexpected grid traversal in " + __originalMethod.Name);
            return code;
        }
    }
}
