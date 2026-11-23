using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class Ipt2NodeCompatibility
    {
        private static bool installed;
        internal static readonly Guid SupportedBuild = new Guid("81412a20-3cfa-4eeb-8b9b-81d7018108c5");

        internal static void Reset() { installed = false; }

        internal static void Apply(Harmony harmony)
        {
            if (installed) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("ImprovedPublicTransport2");
            if (assembly == null || assembly.ManifestModule.ModuleVersionId != SupportedBuild) return;

            Type cache = assembly.GetType("ImprovedPublicTransport2.Data.CachedNodeData", true);
            Type panel = assembly.GetType("ImprovedPublicTransport2.UI.PublicTransportStopWorldInfoPanel", true);
            MethodInfo[] targets = {
                AccessTools.DeclaredMethod(cache, "TryLoadData", null, null),
                AccessTools.DeclaredMethod(cache, "OnSaveData", null, null),
                AccessTools.DeclaredMethod(panel, "ProcessNodes", null, null)
            };
            MethodInfo patch = AccessTools.DeclaredMethod(typeof(Ipt2NodeCompatibility), "Transpiler", null, null);
            try
            {
                foreach (MethodInfo target in targets)
                    harmony.Patch(target, null, null, new HarmonyMethod(patch), null);
                installed = true;
                Logging.Info("IPT2 node cache, serialization and stop search patched.");
            }
            catch
            {
                foreach (MethodInfo target in targets)
                    if (target != null) harmony.Unpatch(target, patch);
                throw;
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            bool allocate = original.Name == "TryLoadData";
            bool save = original.Name == "OnSaveData";
            int matches = 0;
            for (int i = 0; i + 1 < code.Count; ++i)
            {
                CodeInstruction bound = code[i];
                if (!bound.LoadsConstant(32768)) continue;
                OpCode next = code[i + 1].opcode;
                if (allocate ? next != OpCodes.Newarr : next != OpCodes.Blt && next != OpCodes.Blt_S)
                    continue;

                if (save)
                {
                    bound.opcode = OpCodes.Ldsfld;
                    bound.operand = AccessTools.DeclaredField(original.DeclaringType, "m_cachedNodeData");
                    code.Insert(++i, new CodeInstruction(OpCodes.Ldlen, null));
                    code.Insert(++i, new CodeInstruction(OpCodes.Conv_I4, null));
                }
                else
                {
                    bound.opcode = OpCodes.Call;
                    bound.operand = AccessTools.DeclaredMethod(typeof(Capacity), "GetNodeBufferLength", null, null);
                }
                ++matches;
            }
            if (matches != (allocate ? 2 : 1))
                throw new InvalidOperationException("Unexpected IPT node bounds in " + original.Name);
            return code;
        }
    }
}
