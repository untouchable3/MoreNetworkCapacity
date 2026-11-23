using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class IntersectionMarkingCompatibility
    {
        internal sealed class Target
        {
            internal readonly string Type, Method, Anchor;
            internal readonly bool BothPools;
            internal Target(string type, string method, string anchor, bool bothPools)
            { Type = type; Method = method; Anchor = anchor; BothPools = bothPools; }
        }
        internal static readonly Target[] Targets = {
            new Target("IMT.Settings", "<AddOther>g__InvertChevrons|143_0", "get_Invert", true),
            new Target("IMT.Settings", "<AddOther>g__RecalculateMarkings|143_1", "RecalculateAllStyleData", true),
            new Target("IMT.Mod", "ThemeMixer_TerrainTexture_Postfix", "Update", true),
            new Target("IMT.Tools.IntersectionMarkingTool", "ApplyPresetToAsset", "ApplyLinkedMarking", false)
        };
        private static bool _installed;
        internal static void Reset() { _installed = false; }

        internal static void Apply(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("IntersectionMarkingTool");
            if (assembly == null) return;
            MethodInfo[] methods = new MethodInfo[Targets.Length];
            MethodInfo patch = typeof(IntersectionMarkingCompatibility).GetMethod("Transpiler", BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < Targets.Length; ++i)
            {
                methods[i] = Resolve(assembly, Targets[i]);
                ApplyTarget(methods[i], Targets[i], PatchProcessor.GetOriginalInstructions(methods[i], (ILGenerator)null));
            }
            List<MethodInfo> attempted = new List<MethodInfo>();
            try
            {
                foreach (MethodInfo method in methods)
                {
                    attempted.Add(method);
                    harmony.Patch(method, null, null, new HarmonyMethod(patch), null);
                }
                _installed = true;
                Logging.Info("Intersection Marking Tool bulk style, theme and prefab operations cover actual network capacity (4 methods/7 bounds).");
            }
            catch
            {
                foreach (MethodInfo method in attempted) harmony.Unpatch(method, patch);
                throw;
            }
        }

        internal static MethodInfo Resolve(Assembly assembly, Target target)
        {
            Type type = assembly.GetType(target.Type, false);
            if (type == null) throw new InvalidOperationException("IMT traversal target type missing: " + target.Type);
            MethodInfo found = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name != target.Method) continue;
                if (found != null) throw new InvalidOperationException("Ambiguous IMT traversal target: " + target.Method);
                found = method;
            }
            if (found == null || found.ReturnType != typeof(void) || found.IsGenericMethod
                || found.IsStatic != target.BothPools)
                throw new InvalidOperationException("IMT traversal signature changed: " + target.Method);
            ParameterInfo[] parameters = found.GetParameters();
            if (target.BothPools ? parameters.Length != 0 : parameters.Length != 4
                || parameters[0].ParameterType != typeof(NetInfo)
                || parameters[1].ParameterType.FullName != "IMT.Manager.IntersectionTemplate"
                || parameters[2].ParameterType != typeof(bool) || parameters[3].ParameterType != typeof(bool))
                throw new InvalidOperationException("IMT traversal parameters changed: " + target.Method);
            return found;
        }

        private static IEnumerable<CodeInstruction> Transpiler(MethodBase __originalMethod, IEnumerable<CodeInstruction> instructions)
        {
            foreach (Target target in Targets)
                if (__originalMethod.DeclaringType.FullName == target.Type && __originalMethod.Name == target.Method)
                    return ApplyTarget(__originalMethod, target, instructions);
            throw new InvalidOperationException("Unexpected IMT traversal target.");
        }

        internal static List<CodeInstruction> ApplyTarget(MethodBase method, Target target, IEnumerable<CodeInstruction> instructions)
        {
            if (!Resolve(method.DeclaringType.Assembly, target).Equals(method))
                throw new InvalidOperationException("Unexpected IMT traversal method.");
            return ApplyRanges(method, target, instructions);
        }

        internal static List<CodeInstruction> ApplyRanges(MethodBase method, Target target, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            MethodBody body = method.GetMethodBody();
            foreach (CodeInstruction instruction in code)
            {
                LocalVariableInfo local = instruction.operand as LocalVariableInfo;
                if (local != null && (local.LocalIndex < 0 || local.LocalIndex >= body.LocalVariables.Count
                    || body.LocalVariables[local.LocalIndex].LocalType != local.LocalType))
                    throw new InvalidOperationException("IMT local variable metadata changed.");
            }
            List<int> matches = new List<int>();
            if (target.BothPools) matches.Add(FindBound(code, body, target, 32768, true, false));
            matches.Add(FindBound(code, body, target, 36864, false, !target.BothPools));
            // Validate every root and loop before touching any instruction.
            foreach (int index in matches)
            {
                bool node = (int)code[index].operand == 32768;
                code[index].opcode = OpCodes.Call;
                code[index].operand = typeof(IntersectionMarkingCompatibility).GetMethod(node ? "NodeCount" : "SegmentCount",
                    BindingFlags.NonPublic | BindingFlags.Static);
            }
            return code;
        }

        private static int FindBound(List<CodeInstruction> code, MethodBody methodBody, Target target, int limit, bool node, bool preset)
        {
            int match = -1, constants = 0;
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || !object.Equals(code[i].operand, limit)) continue;
                ++constants;
                if (i < (preset ? 6 : 5) || i + 1 >= code.Count
                    || (code[i + 1].opcode != OpCodes.Blt && code[i + 1].opcode != OpCodes.Blt_S)
                    || !(code[i + 1].operand is Label)) continue;
                int local = Local(code[i - 1], false);
                int start = i - (preset ? 6 : 5);
                if (local < 0 || local >= methodBody.LocalVariables.Count
                    || methodBody.LocalVariables[local].LocalType != (preset ? typeof(ushort) : typeof(int))
                    || Local(code[start], false) != local || code[start + 1].opcode != OpCodes.Ldc_I4_1
                    || code[start + 2].opcode != OpCodes.Add || Local(code[i - 2], true) != local
                    || preset && code[i - 3].opcode != OpCodes.Conv_U2) continue;
                Label label = (Label)code[i + 1].operand;
                int body = -1;
                for (int j = 0; j < start; ++j) if (code[j].labels.Contains(label)) { body = j; break; }
                if (body < 0) continue;
                int stores = 0, seed = -1; bool address = false;
                for (int j = 0; j < code.Count; ++j)
                {
                    if (Local(code[j], true) == local)
                    { ++stores; if (j > 0 && code[j - 1].opcode == OpCodes.Ldc_I4_0) seed = j; }
                    if ((code[j].opcode == OpCodes.Ldloca || code[j].opcode == OpCodes.Ldloca_S)
                        && OperandLocal(code[j].operand) == local) address = true;
                }
                if (stores != 2 || address || seed < 1 || seed >= body || seed + 1 >= code.Count
                    || (code[seed + 1].opcode != OpCodes.Br && code[seed + 1].opcode != OpCodes.Br_S)
                    || !(code[seed + 1].operand is Label) || !code[i - 1].labels.Contains((Label)code[seed + 1].operand)
                    || !CounterFlow(code, seed, body, start, i)
                    || !OperationReachable(code, body, start, target)) continue;
                if (preset)
                {
                    if (body + 1 >= start || Local(code[body], false) != local
                        || !Calls(code[body + 1], "ModsCommon.Utilities.NetExtension", "GetSegment")) continue;
                }
                else
                {
                    string manager = node ? "IMT.Manager.NodeMarkingManager" : "IMT.Manager.SegmentMarkingManager";
                    string marking = node ? "IMT.Manager.NodeMarking" : "IMT.Manager.SegmentMarking";
                    if (body + 4 >= start || !GenericCall(code[body], "ModsCommon.SingletonManager`1", manager, "get_Instance")
                        || Local(code[body + 1], false) != local || code[body + 2].opcode != OpCodes.Conv_U2
                        || !code[body + 3].opcode.Name.StartsWith("ldloca", StringComparison.Ordinal)
                        || !GenericCall(code[body + 4], "IMT.Manager.MarkingManager`1", marking, "TryGetMarking")) continue;
                }
                match = i;
            }
            if (constants != 1 || match < 0)
                throw new InvalidOperationException("IMT traversal counter/root changed for capacity " + limit);
            return match;
        }
        private static bool OperationReachable(List<CodeInstruction> code, int body, int increment, Target target)
        {
            string expected = target.Anchor == "get_Invert" ? "IMT.Manager.ChevronFillerStyle"
                : target.Anchor == "ApplyLinkedMarking" ? target.Type : "IMT.Manager.Marking";
            bool[] visited = new bool[code.Count];
            Stack<int> pending = new Stack<int>(); pending.Push(body);
            while (pending.Count != 0)
            {
                int index = pending.Pop();
                if (index < body || index >= increment || visited[index]) continue;
                visited[index] = true;
                CodeInstruction instruction = code[index];
                if (Calls(instruction, expected, target.Anchor)) return true;
                FlowControl flow = instruction.opcode.FlowControl;
                if (flow == FlowControl.Return || flow == FlowControl.Throw) continue;
                if (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch)
                {
                    Label[] targets = instruction.operand is Label ? new[] { (Label)instruction.operand } : instruction.operand as Label[];
                    if (targets != null)
                        foreach (Label label in targets)
                            for (int next = body; next < increment; ++next)
                                if (code[next].labels.Contains(label)) pending.Push(next);
                    if (flow == FlowControl.Branch) continue;
                }
                pending.Push(index + 1);
            }
            return false;
        }
        private static bool CounterFlow(List<CodeInstruction> code, int seed, int body, int increment, int bound)
        {
            for (int i = 0; i < code.Count; ++i)
            {
                Label[] targets = code[i].operand is Label ? new[] { (Label)code[i].operand } : code[i].operand as Label[];
                if (targets == null) continue;
                foreach (Label target in targets)
                    for (int destination = body; destination <= bound; ++destination)
                    {
                        if (!code[destination].labels.Contains(target)) continue;
                        // No external branch can enter a live counter loop, and
                        // no branch may skip part of its increment or guard load.
                        if ((i < seed - 1 || i > bound + 1)
                            || destination > increment && destination < bound - 1
                            || destination == bound || destination == bound - 1 && i != seed + 1) return false;
                    }
            }
            return true;
        }
        private static bool Calls(CodeInstruction instruction, string type, string name)
        {
            MethodInfo method = instruction.operand as MethodInfo;
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && method != null && method.Name == name && method.DeclaringType.FullName == type;
        }
        private static bool GenericCall(CodeInstruction instruction, string definition, string argument, string name)
        {
            MethodInfo method = instruction.operand as MethodInfo;
            if ((instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt) || method == null
                || method.Name != name || !method.DeclaringType.IsGenericType) return false;
            Type type = method.DeclaringType;
            Type[] arguments = type.GetGenericArguments();
            return type.GetGenericTypeDefinition().FullName == definition && arguments.Length == 1
                && arguments[0].FullName == argument;
        }
        private static int Local(CodeInstruction instruction, bool store)
        {
            OpCode code = instruction.opcode;
            if (code == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (code == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (code == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (code == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (code != (store ? OpCodes.Stloc : OpCodes.Ldloc) && code != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            LocalVariableInfo variable = instruction.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            return OperandLocal(instruction.operand);
        }
        private static int OperandLocal(object operand)
        {
            LocalVariableInfo variable = operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            return operand is byte ? (byte)operand : operand is short ? (short)operand : operand is int ? (int)operand : -1;
        }
        internal static int NodeCount()
        { return Supported(Singleton<NetManager>.exists ? Capacity.GetNodeBufferLength() : Capacity.VanillaNodeCapacity, Capacity.VanillaNodeCapacity); }
        internal static int SegmentCount()
        { return Supported(Singleton<NetManager>.exists ? Capacity.GetSegmentBufferLength() : Capacity.VanillaSegmentCapacity, Capacity.VanillaSegmentCapacity); }
        private static int Supported(int count, int vanilla)
        {
            if (count != vanilla && count != Capacity.ExpandedNodeCapacity)
                throw new InvalidOperationException("Unsupported network capacity for IMT traversal: " + count);
            return count;
        }
    }
}
