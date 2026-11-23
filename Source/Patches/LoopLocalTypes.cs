using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Shared preflight for every reviewed method, including earlier batches.
    // A local index does not establish its type when another transpiler supplies
    // its own LocalBuilder. Newly declared unrelated locals need consistent types.
    internal static class LoopLocalTypes
    {
        internal static bool Agree(MethodBase original, List<CodeInstruction> code)
        {
            if (original == null || code == null) return false;
            MethodBody body = original.GetMethodBody();
            if (body == null) return false;
            IList<LocalVariableInfo> locals = body.LocalVariables;
            Dictionary<int, Type> added = new Dictionary<int, Type>();
            foreach (CodeInstruction c in code)
            {
                string name = c.opcode.Name ?? "";
                if (!name.StartsWith("ldloc") && !name.StartsWith("stloc")) continue;
                int index;
                if (c.opcode == OpCodes.Ldloc_0 || c.opcode == OpCodes.Stloc_0) index = 0;
                else if (c.opcode == OpCodes.Ldloc_1 || c.opcode == OpCodes.Stloc_1) index = 1;
                else if (c.opcode == OpCodes.Ldloc_2 || c.opcode == OpCodes.Stloc_2) index = 2;
                else if (c.opcode == OpCodes.Ldloc_3 || c.opcode == OpCodes.Stloc_3) index = 3;
                else
                {
                    LocalVariableInfo declared = c.operand as LocalVariableInfo;
                    index = declared != null ? declared.LocalIndex : c.operand is int ? (int)c.operand
                        : c.operand is byte ? (byte)c.operand : c.operand is short ? (short)c.operand
                        : c.operand is ushort ? (ushort)c.operand : -1;
                }
                if (index < 0) return false;
                LocalVariableInfo metadata = c.operand as LocalVariableInfo;
                Type actual = metadata != null ? metadata.LocalType : index < locals.Count ? locals[index].LocalType : null;
                if (index < locals.Count)
                {
                    if (actual != locals[index].LocalType) return false;
                }
                else
                {
                    Type previous;
                    if (actual == null || added.TryGetValue(index, out previous) && previous != actual) return false;
                    added[index] = actual;
                }
            }
            return true;
        }
    }
}
