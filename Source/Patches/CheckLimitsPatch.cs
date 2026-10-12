using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(NetManager), "CheckLimits")]
    internal static class CheckLimitsPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_I4
                    && instruction.operand is int)
                {
                    int value = (int)instruction.operand;
                    if (value == 32256)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(
                            typeof(Capacity),
                            "GetNodeStopLimit",
                            null,
                            null);
                    }
                    else if (value == 36352)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(
                            typeof(Capacity),
                            "GetSegmentStopLimit",
                            null,
                            null);
                    }
                }

                yield return instruction;
            }
        }
    }
}
