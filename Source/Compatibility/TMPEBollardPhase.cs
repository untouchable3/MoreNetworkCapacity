using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MoreNetworkCapacity.Patches;

namespace MoreNetworkCapacity.Compatibility
{
    // The original divisor encodes the node's 256-frame simulation phase.
    // It must change with node slices, including when TM:PE replaces CarAI.
    internal static class TMPEBollardPhase
    {
        private const string TargetType = "TrafficManager.Patch._VehicleAI._CarAI.CalculateSegmentPositionPatch2";
        private static bool installed;
        internal static void Reset() { installed = false; }
        internal static MethodInfo Resolve(Assembly assembly)
        {
            Type type = assembly.GetType(TargetType, false);
            if (type == null) throw new InvalidOperationException("Required TM:PE bollard phase type missing.");
            MethodInfo result = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "Prefix" || method.ReturnType != typeof(bool)) continue;
                if (!Signature(method)) continue;
                if (result != null) throw new InvalidOperationException("Ambiguous TM:PE bollard phase prefix.");
                result = method;
            }
            if (result == null) throw new InvalidOperationException("Required TM:PE bollard phase prefix missing.");
            return result;
        }
        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (installed || harmony == null) return;
            MethodInfo method = Resolve(assembly);
            Transform(method, PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null));
            MethodInfo transpiler = AccessTools.Method(typeof(TMPEBollardPhase), "Transpiler", null, null);
            try
            {
                harmony.Patch(method, null, null, new HarmonyMethod(transpiler), null);
                installed = true;
                Logging.Info("TM:PE bollard frame offset now follows the active node simulation phase.");
            }
            catch { harmony.Unpatch(method, transpiler); throw; }
        }
        private static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        { return Transform(original, instructions); }
        internal static List<CodeInstruction> Transform(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            if (original == null || original.DeclaringType == null || original.DeclaringType.FullName != TargetType
                || original.DeclaringType.Assembly.GetName().Name != "TrafficManager" || original.Name != "Prefix"
                || !Signature(original) || !LoopLocalTypes.Agree(original, code))
                throw new InvalidOperationException("Unreviewed TM:PE bollard phase target or locals.");
            bool reads = false, writes = false;
            foreach (CodeInstruction instruction in code)
            {
                MethodInfo method = instruction.operand as MethodInfo;
                if (method == null || method.DeclaringType != typeof(RoadBaseAI)) continue;
                if (instruction.opcode != OpCodes.Call) continue;
                if (method.Name == "GetBollardState" && method.GetParameters().Length == 7) reads = true;
                if (method.Name == "SetBollardState" && method.GetParameters().Length == 7) writes = true;
            }
            List<int> significant = new List<int>();
            int position = -1, count = 0;
            for (int i = 0; i < code.Count; ++i)
            {
                if (code[i].opcode == OpCodes.Nop) continue;
                if (NetworkSimulationSlicePatch.Constant(code[i], 32768)) { position = significant.Count; ++count; }
                significant.Add(i);
            }
            bool valid = reads && writes && count == 1 && position >= 5 && position + 8 < significant.Count;
            // These four locals belong to the reviewed current DLL's prefix.
            const int node = 4, frame = 15, phase = 16, elapsed = 17;
            IList<LocalVariableInfo> locals = original.GetMethodBody().LocalVariables;
            valid = valid && locals.Count > elapsed && locals[node].LocalType == typeof(ushort)
                && locals[frame].LocalType == typeof(uint) && locals[phase].LocalType == typeof(uint) && locals[elapsed].LocalType == typeof(uint);
            if (valid)
            {
                FieldInfo field = code[significant[position - 5]].operand as FieldInfo;
                valid = code[significant[position - 5]].opcode == OpCodes.Ldfld && field != null
                    && field.DeclaringType == typeof(SimulationManager) && field.Name == "m_currentFrameIndex" && field.FieldType == typeof(uint)
                    && NetworkSimulationSlicePatch.Local(code[significant[position - 4]], false) == frame
                    && NetworkSimulationSlicePatch.Local(code[significant[position - 3]], true) == node
                    && code[significant[position - 2]].opcode == OpCodes.Ldc_I4_8 && code[significant[position - 1]].opcode == OpCodes.Shl
                    && code[significant[position + 1]].opcode == OpCodes.Div_Un
                    && NetworkSimulationSlicePatch.Local(code[significant[position + 2]], false) == phase
                    && NetworkSimulationSlicePatch.Local(code[significant[position + 3]], true) == frame
                    && NetworkSimulationSlicePatch.Local(code[significant[position + 4]], true) == phase
                    && code[significant[position + 5]].opcode == OpCodes.Sub
                    && NetworkSimulationSlicePatch.Constant(code[significant[position + 6]], 255)
                    && code[significant[position + 7]].opcode == OpCodes.And
                    && NetworkSimulationSlicePatch.Local(code[significant[position + 8]], false) == elapsed;
            }
            int frameWrites = 0, phaseWrites = 0, elapsedWrites = 0;
            for (int i = 0; i < code.Count && valid; ++i)
            {
                int store = NetworkSimulationSlicePatch.Local(code[i], false);
                if (store == frame) ++frameWrites;
                if (store == phase) ++phaseWrites;
                if (store == elapsed) ++elapsedWrites;
                if (store == node && i > significant[position - 3]) valid = false;
                if (code[i].opcode == OpCodes.Ldloca || code[i].opcode == OpCodes.Ldloca_S)
                {
                    int address = AddressIndex(code[i]);
                    if (address == node || address == frame || address == phase || address == elapsed) valid = false;
                }
                // Existing entry labels before the whole field-load expression
                // are valid. Interior branches/EH boundaries bypass its stack.
                if (i > significant[position - 5] && i <= significant[position + 8]
                    && (code[i].labels.Count != 0 || code[i].blocks.Count != 0)) valid = false;
            }
            valid = valid && frameWrites == 1 && phaseWrites == 1 && elapsedWrites == 1;
            if (!valid) throw new InvalidOperationException("TM:PE bollard phase pattern changed.");
            MethodInfo getter = AccessTools.Method(typeof(Capacity), "GetNodeSimulationPhaseDivisor", Type.EmptyTypes, null);
            if (getter == null || !getter.IsStatic || getter.ReturnType != typeof(int))
                throw new InvalidOperationException("Node simulation phase divisor helper unavailable.");
            int constant = significant[position];
            code[constant].opcode = OpCodes.Call; code[constant].operand = getter;
            return code;
        }
        private static bool Signature(MethodBase method)
        {
            MethodInfo info = method as MethodInfo;
            if (info == null || !info.IsStatic || info.ReturnType != typeof(bool) || info.ContainsGenericParameters) return false;
            Type[] expected = { typeof(CarAI), typeof(ushort), typeof(Vehicle).MakeByRefType(), typeof(PathUnit.Position), typeof(PathUnit.Position),
                typeof(uint), typeof(byte), typeof(PathUnit.Position), typeof(uint), typeof(byte), typeof(int),
                typeof(UnityEngine.Vector3).MakeByRefType(), typeof(UnityEngine.Vector3).MakeByRefType(), typeof(float).MakeByRefType() };
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != expected.Length) return false;
            for (int i = 0; i < expected.Length; ++i) if (parameters[i].ParameterType != expected[i]) return false;
            return true;
        }
        private static int AddressIndex(CodeInstruction instruction)
        {
            LocalVariableInfo local = instruction.operand as LocalVariableInfo;
            if (local != null) return local.LocalIndex;
            object value = instruction.operand;
            if (value is int || value is byte || value is short || value is ushort) return Convert.ToInt32(value);
            return -1;
        }
    }
}
