using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // Two shared segment bitmaps serve roads and buildings with embedded paths.
    // Buildings retain their original, independent bitmaps and collision rules.
    internal static class ToolCollisionBuffers
    {
        internal const int VanillaWords = 36864 / 64;
        internal const int ExpandedWords = Capacity.ExpandedSegmentCapacity / 64;
        private static readonly FieldInfo First = AccessTools.Field(typeof(ToolController), "m_collidingSegments1");
        private static readonly FieldInfo Second = AccessTools.Field(typeof(ToolController), "m_collidingSegments2");
        private static readonly FieldInfo Depth = AccessTools.Field(typeof(ToolController), "m_collidingDepth");
        private static readonly FieldInfo Properties = AccessTools.Field(typeof(ToolManager), "m_properties");

        private static void ValidateFields()
        {
            foreach (FieldInfo field in new[] { First, Second })
                if (field == null || field.IsStatic || field.IsInitOnly || field.FieldType != typeof(ulong[]))
                    throw new InvalidOperationException("ToolController segment collision bitmap layout changed.");
            if (Depth == null || Depth.FieldType != typeof(int) || Depth.IsStatic)
                throw new InvalidOperationException("ToolController collision nesting layout changed.");
        }

        internal static List<CodeInstruction> RewriteAwake(IEnumerable<CodeInstruction> instructions)
        {
            ValidateFields();
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            List<int> matches = new List<int>();
            foreach (FieldInfo field in new[] { First, Second })
            {
                int found = -1;
                for (int i = 0; i + 2 < code.Count; ++i)
                {
                    if (code[i + 1].opcode != OpCodes.Newarr || !typeof(ulong).Equals(code[i + 1].operand)
                        || code[i + 2].opcode != OpCodes.Stfld || !field.Equals(code[i + 2].operand)) continue;
                    if (found >= 0 || code[i].opcode != OpCodes.Ldc_I4 || !VanillaWords.Equals(code[i].operand))
                        throw new InvalidOperationException("ToolController collision allocation pattern changed: " + field.Name);
                    found = i;
                }
                if (found < 0) throw new InvalidOperationException("ToolController collision allocation missing: " + field.Name);
                matches.Add(found);
            }
            // Validate both allocations before changing either instruction.
            foreach (int i in matches) code[i].operand = ExpandedWords;
            return code;
        }

        internal static void EnsureLoadedController()
        {
            if (!Singleton<ToolManager>.exists) return;
            if (Properties == null || Properties.FieldType != typeof(ToolController))
                throw new InvalidOperationException("ToolManager controller layout changed.");
            EnsureController(Properties.GetValue(Singleton<ToolManager>.instance) as ToolController);
        }

        internal static void EnsureController(ToolController controller)
        {
            if (Object.ReferenceEquals(controller, null)) return;
            ValidateFields();
            ulong[] first = First.GetValue(controller) as ulong[];
            ulong[] second = Second.GetValue(controller) as ulong[];
            // Before Awake there is no collision state; the Awake patch allocates it.
            if (first == null && second == null) return;
            if (first != null && second != null && first.Length == ExpandedWords && second.Length == ExpandedWords) return;
            if (first == null || second == null || first.Length != VanillaWords || second.Length != VanillaWords)
                throw new InvalidOperationException("Unexpected ToolController segment collision bitmap sizes.");
            if ((int)Depth.GetValue(controller) != 0)
                throw new InvalidOperationException("Cannot resize collision bitmaps while a tool holds their references.");
            ulong[] replacement1 = new ulong[ExpandedWords], replacement2 = new ulong[ExpandedWords];
            Array.Copy(first, replacement1, first.Length);
            Array.Copy(second, replacement2, second.Length);
            First.SetValue(controller, replacement1);
            Second.SetValue(controller, replacement2);
            Logging.Info("Expanded ToolController segment collision bitmaps: " + VanillaWords + " -> " + ExpandedWords + " words.");
        }
    }

    [HarmonyPatch(typeof(ToolController), "Awake")]
    internal static class ToolControllerCollisionBuffersPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        { return ToolCollisionBuffers.RewriteAwake(instructions); }
    }
}
