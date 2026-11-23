using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class EightyOne2WaterRecovery
    {
        private const ushort NoGroup = ushort.MaxValue;
        private static readonly string[] Services = { "Water", "Sewage", "Heating" };
        private static WaterManager.Cell[] _waitingForCycle;

        internal static void Reset() { _waitingForCycle = null; }

        internal static MethodInfo[] Resolve(MethodInfo simulation)
        {
            Type intRef = typeof(int).MakeByRefType();
            Type groupArray = typeof(WaterManager.PulseGroup[]);
            Type[] simulationArgs = { typeof(WaterManager), typeof(int), typeof(WaterManager.Cell[]),
                intRef, intRef, intRef, intRef, intRef, intRef, intRef, intRef, intRef, intRef, intRef,
                typeof(bool).MakeByRefType(), groupArray, groupArray, groupArray };
            ParameterInfo[] args = simulation.GetParameters();
            for (int i = 0; i < simulationArgs.Length; ++i)
                if (args[i].ParameterType != simulationArgs[i])
                    throw new InvalidOperationException("81 Tiles water simulation arguments changed.");

            Type[] conductArgs = { typeof(ushort), typeof(NetNode).MakeByRefType(), typeof(ushort),
                typeof(WaterManager.Node[]), groupArray, typeof(int), intRef, typeof(bool).MakeByRefType() };
            MethodInfo[] targets = new MethodInfo[4];
            targets[0] = simulation;
            for (int i = 0; i < Services.Length; ++i)
            {
                targets[i + 1] = AccessTools.Method(simulation.DeclaringType, "Conduct" + Services[i] + "ToNode", conductArgs, null);
                if (targets[i + 1] == null || !targets[i + 1].IsStatic || targets[i + 1].ReturnType != typeof(void))
                    throw new InvalidOperationException("81 Tiles " + Services[i] + " node propagation signature changed.");
            }
            return targets;
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            bool simulation = original.Name == "SimulationStepImpl";
            List<CodeInstruction> code = new List<CodeInstruction>();
            int[] replaced = new int[Services.Length];
            MethodInfo findRoot = AccessTools.Method(typeof(EightyOne2WaterRecovery), "FindRoot", null, null);
            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (instruction.opcode != OpCodes.Call || method == null || method.DeclaringType != original.DeclaringType)
                {
                    code.Add(instruction);
                    continue;
                }

                int service = Array.FindIndex(Services, delegate(string name) { return method.Name == "GetRoot" + name + "Group"; });
                if (service < 0)
                {
                    code.Add(instruction);
                    continue;
                }

                ParameterInfo[] args = method.GetParameters();
                if (!method.IsStatic || method.ReturnType != typeof(ushort) || args.Length != 2
                    || args[0].ParameterType != typeof(ushort) || args[1].ParameterType != typeof(WaterManager.PulseGroup[])
                    || (!simulation && original.Name != "Conduct" + Services[service] + "ToNode")
                    || instruction.blocks.Count != 0)
                    throw new InvalidOperationException("81 Tiles water group lookup changed: " + original.Name);

                // Preserve both original operands and pass the matching live group count.
                CodeInstruction count = new CodeInstruction(OpCodes.Ldarg_S, (byte)(simulation ? 3 + service * 3 : 5));
                count.labels.AddRange(instruction.labels);
                code.Add(count);
                if (simulation) code.Add(new CodeInstruction(OpCodes.Ldind_I4, null));
                code.Add(new CodeInstruction(OpCodes.Call, findRoot));
                ++replaced[service];
            }

            for (int i = 0; i < Services.Length; ++i)
                if (replaced[i] != (simulation || original.Name == "Conduct" + Services[i] + "ToNode" ? 1 : 0))
                    throw new InvalidOperationException("81 Tiles water group call sites changed: " + original.Name);
            return code;
        }

        internal static ushort FindRoot(ushort group, WaterManager.PulseGroup[] groups, int count)
        {
            ushort first = group;
            int limit = Math.Min(count, groups.Length);
            for (int hops = 0; hops < limit; ++hops)
            {
                if (group >= limit) break;
                ushort parent = groups[group].m_mergeIndex;
                if (parent == NoGroup) return group;
                group = parent;
            }
            throw new InvalidPulseGroupException(first, group, count, groups);
        }

        internal static bool Prefix(int __1, WaterManager.Cell[] __2)
        {
            if (_waitingForCycle == null) return true;
            if (!ReferenceEquals(_waitingForCycle, __2)) Reset();
            else
            {
                if (__1 == 0 || __1 == 1000) return true;
                if (!CanRestart(Singleton<SimulationManager>.instance.m_currentFrameIndex)) return false;
                Reset();
            }
            return true;
        }

        internal static bool CanRestart(uint frame) { return (frame & 255u) == 0; }

        internal static Exception Finalizer(Exception __exception, WaterManager __0, WaterManager.Cell[] __2,
            ref int __3, ref int __4, ref int __5, ref int __6, ref int __7, ref int __8,
            ref int __9, ref int __10, ref int __11, ref int __12, ref int __13, ref bool __14,
            WaterManager.PulseGroup[] __15, WaterManager.PulseGroup[] __16, WaterManager.PulseGroup[] __17)
        {
            InvalidPulseGroupException failure = __exception as InvalidPulseGroupException;
            if (failure == null) return __exception;

            string service;
            if (ReferenceEquals(failure.Groups, __15)) service = Services[0];
            else if (ReferenceEquals(failure.Groups, __16)) service = Services[1];
            else if (ReferenceEquals(failure.Groups, __17)) service = Services[2];
            else return __exception;

            __3 = __4 = __5 = __6 = __7 = __8 = __9 = __10 = __11 = __12 = __13 = 0;
            __14 = false;
            ClearTransientGroups(__0.m_nodeData, __2);
            _waitingForCycle = __2;
            Logging.Warning("81 Tiles " + service + " pulse chain invalid (start " + failure.First
                + ", encountered " + failure.Group + ", active groups " + failure.Count + "/" + failure.Groups.Length
                + "). Discarded the partial utility cycle; simulation will rebuild it at the next phase zero.");
            return null;
        }

        internal static void ClearTransientGroups(WaterManager.Node[] nodes, WaterManager.Cell[] cells)
        {
            for (int i = 0; i < nodes.Length; ++i)
                nodes[i].m_waterPulseGroup = nodes[i].m_sewagePulseGroup = nodes[i].m_heatingPulseGroup = NoGroup;
            for (int i = 0; i < cells.Length; ++i)
            {
                cells[i].m_waterPulseGroup = cells[i].m_sewagePulseGroup = cells[i].m_heatingPulseGroup = NoGroup;
                // The next cycle publishes the temporary flags before rebuilding them.
                cells[i].m_tmpHasWater = cells[i].m_hasWater;
                cells[i].m_tmpHasSewage = cells[i].m_hasSewage;
                cells[i].m_tmpHasHeating = cells[i].m_hasHeating;
            }
        }

        private sealed class InvalidPulseGroupException : Exception
        {
            internal readonly ushort First, Group;
            internal readonly int Count;
            internal readonly WaterManager.PulseGroup[] Groups;

            internal InvalidPulseGroupException(ushort first, ushort group, int count, WaterManager.PulseGroup[] groups)
                : base("Invalid 81 Tiles utility pulse group chain.")
            {
                First = first;
                Group = group;
                Count = count;
                Groups = groups;
            }
        }
    }
}
