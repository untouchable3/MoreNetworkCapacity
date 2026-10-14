using System;
using System.Reflection;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class ZoningAdjusterCompatibility
    {
        private const string AssemblyName = "ZoningAdjuster";
        private const string ModName = "Zoning Adjuster";
        private static FieldInfo _flags;
        private static bool _installed;
        internal static void Reset() { _flags = null; _installed = false; }

        internal static void ApplyEarly(Harmony harmony)
        {
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly(AssemblyName);
            if (assembly == null) return;
            Type type = ThirdPartyCapacityCompatibility.FindType(assembly, "ZoningAdjuster.SegmentData", ModName);
            if (type == null) throw new InvalidOperationException("Zoning Adjuster SegmentData type missing.");
            FieldInfo flags = ThirdPartyCapacityCompatibility.FindField(type, "_zoneBlockFlags");
            MethodBase[] targets = ValidateTargets(type, flags);
            if (!_installed && harmony != null)
            {
                MethodInfo ctorPatch = AccessTools.Method(typeof(ZoningAdjusterCompatibility), "ConstructedPostfix", null, null);
                MethodInfo readPatch = AccessTools.Method(typeof(ZoningAdjusterCompatibility), "ReadDataPrefix", null, null);
                _flags = flags;
                try
                {
                    harmony.Patch(targets[0], null, new HarmonyMethod(ctorPatch), null, null);
                    harmony.Patch(targets[1], new HarmonyMethod(readPatch), null, null, null);
                    _installed = true;
                }
                catch
                {
                    harmony.Unpatch(targets[0], ctorPatch);
                    harmony.Unpatch(targets[1], readPatch);
                    _flags = null;
                    throw;
                }
            }
            _flags = flags;
            object instance = ArrayCompatibility.GetStaticInstance(type, "Instance", "s_instance", "Zoning Adjuster SegmentData");
            Expand(instance);
        }

        internal static MethodBase[] ValidateTargets(Type type, FieldInfo flags)
        {
            ConstructorInfo ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            MethodInfo read = type.GetMethod("ReadData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, new Type[] { typeof(byte[]) }, null);
            if (flags == null || flags.IsStatic || flags.FieldType != typeof(byte[]) || flags.DeclaringType != type
                || ctor == null || read == null || read.ReturnType != typeof(void))
                throw new InvalidOperationException("Zoning Adjuster data layout changed.");
            List<CodeInstruction> init = PatchProcessor.GetOriginalInstructions(ctor, (ILGenerator)null);
            List<CodeInstruction> copy = PatchProcessor.GetOriginalInstructions(read, (ILGenerator)null);
            bool allocated = false, copies = false, min = false;
            for (int i = 1; i < init.Count; ++i)
                if (init[i].opcode == OpCodes.Stfld && flags.Equals(init[i].operand)
                    && init[i - 1].opcode == OpCodes.Newarr && init[i - 1].operand as Type == typeof(byte)) allocated = true;
            foreach (CodeInstruction instruction in copy)
            {
                copies |= instruction.opcode == OpCodes.Ldfld && flags.Equals(instruction.operand);
                MethodInfo method = instruction.operand as MethodInfo;
                min |= instruction.opcode == OpCodes.Call && method != null && method.DeclaringType == typeof(Math)
                    && method.Name == "Min" && method.ReturnType == typeof(int);
            }
            if (!allocated || !copies || !min) throw new InvalidOperationException("Zoning Adjuster initialization/read pattern changed.");
            return new MethodBase[] { ctor, read };
        }

        public static void ConstructedPostfix(object __instance) { Expand(__instance); }
        public static void ReadDataPrefix(object __instance) { Expand(__instance); }
        private static void Expand(object instance)
        {
            FieldInfo field = _flags;
            if (instance == null || field == null || !field.DeclaringType.IsInstanceOfType(instance)) return;
            byte[] current = field.GetValue(instance) as byte[];
            // The installed version initializes segment flags from the zone
            // block pool (49152). Recognize only that layout and our own size.
            if (current == null || current.Length == Capacity.ExpandedSegmentCapacity) return;
            if (current.Length != 49152)
            {
                Logging.Error("Zoning Adjuster flags have an unsupported length " + current.Length + "; leaving the array unchanged.");
                return;
            }
            ArrayCompatibility.ExpandDirect(field, instance, 49152, Capacity.ExpandedSegmentCapacity,
                null, false, "Zoning Adjuster SegmentData._zoneBlockFlags");
        }

        internal static void Apply()
        {
            ApplyEarly(HarmonyBootstrap.HarmonyInstance);
            Assembly assembly =
                ThirdPartyCapacityCompatibility.FindLoadedAssembly(
                    AssemblyName);
            if (assembly == null)
            {
                Logging.Info(
                    "Zoning Adjuster is not loaded; compatibility is inactive.");
                return;
            }

            Type type = ThirdPartyCapacityCompatibility.FindType(
                assembly,
                "ZoningAdjuster.SegmentData",
                ModName);
            if (type == null)
            {
                return;
            }

            object instance = ArrayCompatibility.GetStaticInstance(
                type,
                "Instance",
                "s_instance",
                "Zoning Adjuster SegmentData");
            FieldInfo field = ThirdPartyCapacityCompatibility.FindField(
                type,
                "_zoneBlockFlags");
            _flags = field;
            Expand(instance);

            Logging.Info("Zoning Adjuster compatibility pass completed.");
        }
    }
}
