using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch]
    internal static class NodeSimulationPhasePatch
    {
        private sealed class Target
        {
            internal Type Type; internal string Name; internal int Parameters, Node, Frame, Phase, Elapsed;
            internal bool Static, NodeLocal; internal string State;
            internal Target(Type type, string name, int parameters, bool isStatic, bool local,
                int node, int frame, int phase, int elapsed, string state)
            { Type = type; Name = name; Parameters = parameters; Static = isStatic; NodeLocal = local;
                Node = node; Frame = frame; Phase = phase; Elapsed = elapsed; State = state; }
        }
        private static readonly Target[] Targets = {
            new Target(typeof(HumanAI), "CheckTrafficLights", 2, false, false, 1, 1, 2, 3, "GetTrafficLightState"),
            new Target(typeof(PedestrianZoneRoadAI), "GetBollardNodeState", 6, true, false, 0, 0, 1, 2, "GetBollardState"),
            new Target(typeof(RoadBaseAI), "GetTrafficLightNodeState", 6, true, false, 0, 2, 3, 4, "GetTrafficLightState"),
            new Target(typeof(TrainTrackBaseAI), "GetLevelCrossingNodeState", 6, true, false, 0, 2, 3, 4, "GetTrafficLightState"),
            new Target(typeof(CarAI), "NeedStopAtNode", 10, false, false, 3, 0, 1, 2, "GetBollardState"),
            new Target(typeof(CarAI), "CalculateSegmentPosition", 13, false, true, 11, 23, 24, 25, "GetTrafficLightState"),
            new Target(typeof(TrainAI), "ForceTrafficLights", 1, true, true, 1, 3, 4, 5, "GetTrafficLightState"),
            new Target(typeof(TrainAI), "CheckNextLane", 10, false, true, 12, 17, 18, 19, "GetTrafficLightState"),
            new Target(typeof(TramBaseAI), "CalculateSegmentPosition", 13, false, true, 13, 24, 25, 26, "GetTrafficLightState")
        };

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Target target in Targets)
            {
                MethodInfo found = null;
                foreach (MethodInfo method in target.Type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (method.Name == target.Name && method.IsStatic == target.Static
                        && method.GetParameters().Length == target.Parameters)
                    {
                        if (found != null) throw new InvalidOperationException("Ambiguous signal phase target: " + target.Name);
                        found = method;
                    }
                if (found == null) throw new InvalidOperationException("Missing signal phase target: " + target.Type.Name + "." + target.Name);
                yield return found;
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            Target target = null;
            foreach (Target candidate in Targets)
                if (original != null && original.DeclaringType == candidate.Type && original.Name == candidate.Name
                    && original.IsStatic == candidate.Static && original.GetParameters().Length == candidate.Parameters) target = candidate;
            if (target == null || !LoopLocalTypes.Agree(original, code)) throw new InvalidOperationException("Unreviewed signal phase target or locals.");
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            foreach (int index in new[] { target.Frame, target.Phase, target.Elapsed })
                if (index >= locals.Count || locals[index].LocalType != typeof(uint)) throw new InvalidOperationException("Signal phase local type changed.");
            if (target.NodeLocal)
            {
                if (target.Node >= locals.Count || locals[target.Node].LocalType != typeof(ushort)) throw new InvalidOperationException("Signal node local changed.");
                int expectedWrites = 0, actualWrites = 0;
                foreach (CodeInstruction c in PatchProcessor.GetOriginalInstructions(original, (ILGenerator)null))
                    if (NetworkSimulationSlicePatch.Local(c, false) == target.Node) ++expectedWrites;
                foreach (CodeInstruction c in code)
                    if (NetworkSimulationSlicePatch.Local(c, false) == target.Node) ++actualWrites;
                if (actualWrites != expectedWrites) throw new InvalidOperationException("Signal node definitions changed.");
            }
            else
            {
                int parameter = target.Node - (original.IsStatic ? 0 : 1);
                if (parameter < 0 || original.GetParameters()[parameter].ParameterType != typeof(ushort)) throw new InvalidOperationException("Signal node argument changed.");
            }
            List<int> significant = new List<int>();
            for (int i = 0; i < code.Count; ++i) if (code[i].opcode != OpCodes.Nop) significant.Add(i);
            int match = -1, constants = 0, frameWrites = 0, phaseWrites = 0, elapsedWrites = 0;
            bool state = false;
            foreach (CodeInstruction c in code)
            {
                if (NetworkSimulationSlicePatch.Local(c, false) == target.Frame) ++frameWrites;
                if (NetworkSimulationSlicePatch.Local(c, false) == target.Phase) ++phaseWrites;
                if (NetworkSimulationSlicePatch.Local(c, false) == target.Elapsed) ++elapsedWrites;
                if ((c.opcode == OpCodes.Ldloca || c.opcode == OpCodes.Ldloca_S)
                    && (AddressIndex(c) == target.Phase || AddressIndex(c) == target.Frame
                        || AddressIndex(c) == target.Elapsed || target.NodeLocal && AddressIndex(c) == target.Node))
                    throw new InvalidOperationException("Signal phase input escapes by reference.");
                MethodInfo call = c.operand as MethodInfo;
                if (c.opcode == OpCodes.Call && call != null && call.DeclaringType == typeof(RoadBaseAI)
                    && call.Name == target.State) state = true;
            }
            for (int p = 0; p < significant.Count; ++p)
            {
                int i = significant[p];
                if (!NetworkSimulationSlicePatch.Constant(code[i], Capacity.VanillaNodeCapacity)) continue;
                ++constants;
                if (p < 4 || p + 8 >= significant.Count) continue;
                CodeInstruction node = code[significant[p - 3]];
                bool source = target.NodeLocal ? NetworkSimulationSlicePatch.Local(node, true) == target.Node : Argument(node) == target.Node;
                if (!source || !FrameSource(code, significant, p - 4, target.Static && target.Parameters == 6)
                    || NetworkSimulationSlicePatch.Local(code[significant[p - 4]], false) != target.Frame
                    || code[significant[p - 2]].opcode != OpCodes.Ldc_I4_8
                    || code[significant[p - 1]].opcode != OpCodes.Shl || code[significant[p + 1]].opcode != OpCodes.Div_Un
                    || NetworkSimulationSlicePatch.Local(code[significant[p + 2]], false) != target.Phase
                    || NetworkSimulationSlicePatch.Local(code[significant[p + 3]], true) != target.Frame
                    || NetworkSimulationSlicePatch.Local(code[significant[p + 4]], true) != target.Phase
                    || code[significant[p + 5]].opcode != OpCodes.Sub
                    || !NetworkSimulationSlicePatch.Constant(code[significant[p + 6]], 255)
                    || code[significant[p + 7]].opcode != OpCodes.And
                    || NetworkSimulationSlicePatch.Local(code[significant[p + 8]], false) != target.Elapsed) continue;
                // A branch into the middle would bypass the reviewed stack expression.
                bool entry = false;
                int expression = p - (target.Static && target.Parameters == 6 ? 8 : 6);
                for (int k = significant[expression] + 1; k <= significant[p + 8]; ++k)
                    if (code[k].labels.Count != 0 || code[k].blocks.Count != 0) entry = true;
                if (!entry) match = match == -1 ? i : -2;
            }
            if (match < 0 || constants != 1 || !state || frameWrites != 1 || phaseWrites != 1 || elapsedWrites != 1)
                throw new InvalidOperationException("Signal node phase layout changed: " + original.DeclaringType.Name + "." + original.Name);
            code[match].opcode = OpCodes.Call;
            code[match].operand = typeof(Capacity).GetMethod("GetNodeSimulationPhaseDivisor");
            Logging.Info("Signal node frame phase follows active node capacity in " + original.DeclaringType.Name + "." + original.Name + ".");
            return code;
        }
        private static bool FrameSource(List<CodeInstruction> code, List<int> indexes, int store, bool reference)
        {
            int field = store - 1;
            if (reference)
            {
                if (store < 4 || code[indexes[store - 1]].opcode != OpCodes.Sub
                    || !NetworkSimulationSlicePatch.Constant(code[indexes[store - 2]], 15)) return false;
                field = store - 3;
            }
            if (field < 1) return false;
            FieldInfo actual = code[indexes[field]].operand as FieldInfo;
            MethodInfo owner = code[indexes[field - 1]].operand as MethodInfo;
            return code[indexes[field]].opcode == OpCodes.Ldfld && actual != null
                && actual.DeclaringType == typeof(SimulationManager)
                && actual.Name == (reference ? "m_referenceFrameIndex" : "m_currentFrameIndex")
                && code[indexes[field - 1]].opcode == OpCodes.Call && owner != null
                && owner.DeclaringType == typeof(ColossalFramework.Singleton<SimulationManager>) && owner.Name == "get_instance";
        }
        private static int AddressIndex(CodeInstruction c)
        {
            LocalVariableInfo info = c.operand as LocalVariableInfo;
            if (info != null) return info.LocalIndex;
            return c.operand is int || c.operand is byte || c.operand is short || c.operand is ushort ? Convert.ToInt32(c.operand) : -1;
        }
        private static int Argument(CodeInstruction c)
        {
            if (c.opcode == OpCodes.Ldarg_0) return 0; if (c.opcode == OpCodes.Ldarg_1) return 1;
            if (c.opcode == OpCodes.Ldarg_2) return 2; if (c.opcode == OpCodes.Ldarg_3) return 3;
            if (c.opcode != OpCodes.Ldarg && c.opcode != OpCodes.Ldarg_S) return -1;
            ParameterInfo p = c.operand as ParameterInfo;
            return p != null ? p.Position + (((MethodBase)p.Member).IsStatic ? 0 : 1) : Convert.ToInt32(c.operand);
        }
    }
}
