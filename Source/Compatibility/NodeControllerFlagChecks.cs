using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class NodeControllerFlagChecks
    {
        private static bool _installed;
        internal static void Reset() { _installed = false; }

        internal static MethodInfo Resolve(Assembly assembly)
        {
            Type type = assembly.GetType("NodeController.Patches.NetSegmentPatches", false);
            MethodInfo target = type == null ? null : type.GetMethod("CalculateCornerPrefix",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null,
                new Type[] { typeof(NetInfo), typeof(NetInfo), typeof(ushort), typeof(ushort),
                    typeof(bool), typeof(bool), typeof(UnityEngine.Vector3).MakeByRefType(),
                    typeof(UnityEngine.Vector3).MakeByRefType(), typeof(bool).MakeByRefType() }, null);
            if (target == null || target.ReturnType != typeof(bool))
                throw new InvalidOperationException("Node Controller corner flag-check target changed.");
            return target;
        }

        internal static void Apply(Harmony harmony, Assembly assembly)
        {
            if (_installed || harmony == null) return;
            MethodInfo target = Resolve(assembly);
            MethodInfo patch = typeof(NodeControllerFlagChecks).GetMethod("Transpiler",
                BindingFlags.Static | BindingFlags.NonPublic);
            new List<CodeInstruction>(Transpiler(target,
                PatchProcessor.GetOriginalInstructions(target, (ILGenerator)null)));
            try
            {
                harmony.Patch(target, null, null, new HarmonyMethod(patch), null);
                _installed = true;
                Logging.Info("Node Controller corner Middle flag check uses equivalent typed bit test.");
            }
            catch
            {
                harmony.Unpatch(target, patch);
                throw;
            }
        }

        internal static IEnumerable<CodeInstruction> Transpiler(MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            if (original == null || !Resolve(original.DeclaringType.Assembly).Equals(original))
                throw new InvalidOperationException("Unexpected Node Controller flag-check target.");
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int match = -1;
            int calls = 0;
            FieldInfo flags = typeof(NetNode).GetField("m_flags");
            for (int i = 0; i < list.Count; ++i)
            {
                MethodInfo method = list[i].operand as MethodInfo;
                if (method == null || method.DeclaringType != typeof(ColossalFramework.EnumExtensions)
                    || method.Name != "IsFlagSet" || !method.IsGenericMethod) continue;
                ++calls;
                Type[] arguments = method.GetGenericArguments();
                ParameterInfo[] parameters = method.GetParameters();
                if (list[i].opcode != OpCodes.Call || arguments.Length != 1
                    || arguments[0] != typeof(NetNode.Flags) || method.ReturnType != typeof(bool)
                    || parameters.Length != 2 || parameters[0].ParameterType != typeof(NetNode.Flags)
                    || parameters[1].ParameterType != typeof(NetNode.Flags) || i < 2 || i + 1 >= list.Count
                    || list[i - 2].opcode != OpCodes.Ldfld || !flags.Equals(list[i - 2].operand)
                    || (list[i - 1].opcode != OpCodes.Ldc_I4_S && list[i - 1].opcode != OpCodes.Ldc_I4)
                    || Convert.ToInt32(list[i - 1].operand) != (int)NetNode.Flags.Middle
                    || list[i + 1].opcode != OpCodes.Stind_I1) continue;
                match = i;
            }
            if (calls != 1 || match < 0)
                throw new InvalidOperationException("Node Controller corner flag-check IL pattern changed.");
            // Mutate the call only: incoming labels and exception boundaries remain intact.
            list[match].operand = typeof(NodeControllerFlagChecks).GetMethod("AnyNodeFlag",
                BindingFlags.Static | BindingFlags.NonPublic);
            return list;
        }

        internal static bool AnyNodeFlag(NetNode.Flags value, NetNode.Flags flag)
        {
            // The game extension tests any matching bit, including compound masks; zero is false.
            return (value & flag) != 0;
        }
    }
}
