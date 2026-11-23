using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class CsurToolBoxCompatibility
    {
        private static bool installed;
        internal static void Reset() { installed = false; }

        internal static void Apply(Harmony harmony)
        {
            if (installed || harmony == null) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("CSURToolBox");
            if (assembly == null) return;
            Type type = assembly.GetType("CSURToolBox.Patch.NetManagerOverlapQuadPatch", true);
            MethodInfo prefix = type.GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static);
            if (prefix == null || prefix.ReturnType != typeof(bool))
                throw new InvalidOperationException("CSUR ToolBox OverlapQuad prefix changed.");
            MethodInfo patch = typeof(CsurToolBoxCompatibility).GetMethod("Rewrite", BindingFlags.NonPublic | BindingFlags.Static);
            Rewrite(PatchProcessor.GetOriginalInstructions(prefix, (ILGenerator)null));
            harmony.Patch(prefix, null, null, new HarmonyMethod(patch), null);
            installed = true;
            Logging.Info("CSUR ToolBox OverlapQuad grid traversal uses the active segment capacity.");
        }

        private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            int match = -1;
            FieldInfo next = typeof(NetSegment).GetField("m_nextGridSegment");
            for (int i = 7; i + 3 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldc_I4 || !object.Equals(code[i].operand, Capacity.VanillaSegmentCapacity)
                    || code[i-7].opcode != OpCodes.Ldfld || !object.Equals(code[i-7].operand, next) || code[i-6].opcode != OpCodes.Stloc_S
                    || code[i-5].opcode != OpCodes.Ldloc_S || code[i-4].opcode != OpCodes.Ldc_I4_1
                    || code[i-3].opcode != OpCodes.Add || code[i-2].opcode != OpCodes.Dup
                    || code[i-1].opcode != OpCodes.Stloc_S || !object.Equals(code[i-5].operand, code[i-1].operand)
                    || code[i+1].opcode != OpCodes.Blt_S || code[i+3].opcode != OpCodes.Ldstr
                    || !object.Equals(code[i+3].operand, "Invalid list detected!\n")) continue;
                if (match >= 0) throw new InvalidOperationException("Ambiguous CSUR grid traversal guard.");
                match = i;
            }
            if (match < 0) throw new InvalidOperationException("CSUR grid traversal guard changed.");
            code[match].opcode = OpCodes.Ldsfld;
            code[match].operand = typeof(Capacity).GetField("SegmentLoopLimit");
            return code;
        }
    }
}
