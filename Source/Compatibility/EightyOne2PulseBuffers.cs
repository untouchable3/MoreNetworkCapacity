using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class EightyOne2PulseBuffers
    {
        private const string Owner = "EightyOne2.Patches.ExpandedElectricityManager";
        private const BindingFlags Methods = BindingFlags.DeclaredOnly | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static IEnumerable<MethodInfo> Targets(Assembly assembly)
        {
            Type manager = assembly.GetType(Owner, true);
            VerifyGetter(manager, "get_PulseUnits", "s_pulseUnits");
            VerifyGetter(manager, "get_PulseGroups", "s_pulseGroups");
            // A caller may already have inlined either property before MNC expands the arrays.
            foreach (string name in new[] {"get_PulseGroups", "get_PulseUnits", "ConductToCell", "ConductToNode",
                "GetRootGroup", "MergeGroups", "SimulationStepImpl"})
                yield return Require(manager, name);
            Type storage = assembly.GetType("EightyOne2.Patches.ElectricityManagerDataPatches", true);
            yield return Require(storage, "CustomSerialize");
            yield return Require(storage, "CustomDeserialize");
            Type container = assembly.GetType("EightyOne2.Serialization.ElectricityDataContainer", true);
            yield return Require(container, "Serialize");
            yield return Require(container, "Deserialize");

            Type water = assembly.GetType("EightyOne2.Patches.ExpandedWaterManager", true);
            foreach (string channel in new[] { "Water", "Sewage", "Heating" })
            {
                string getter = "get_" + channel + "PulseUnits";
                VerifyGetter(water, getter, "s_" + channel.ToLowerInvariant() + "PulseUnits");
                yield return Require(water, getter);
                yield return Require(water, "Conduct" + channel + "ToCell");
                yield return Require(water, "Conduct" + channel + "ToNode");
            }
            yield return Require(water, "SimulationStepImpl");
            Type waterStorage = assembly.GetType("EightyOne2.Patches.WaterManagerDataPatches", true);
            yield return Require(waterStorage, "CustomSerialize");
            yield return Require(waterStorage, "CustomDeserialize");
            Type waterContainer = assembly.GetType("EightyOne2.Serialization.WaterDataContainer", true);
            yield return Require(waterContainer, "Serialize");
            yield return Require(waterContainer, "Deserialize");
        }

        private static MethodInfo Require(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, Methods);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void VerifyGetter(Type owner, string name, string fieldName)
        {
            // Bypassing a getter is valid only while it just returns the reviewed field.
            List<CodeInstruction> code = PatchProcessor.GetOriginalInstructions(Require(owner, name), (ILGenerator)null);
            code.RemoveAll(delegate(CodeInstruction c) { return c.opcode == OpCodes.Nop; });
            if (code.Count != 2 || code[0].opcode != OpCodes.Ldsfld ||
                !Equals(code[0].operand, owner.GetField(fieldName, Methods)) || code[1].opcode != OpCodes.Ret)
                throw new InvalidOperationException("81 Tiles buffer getter changed: " + name);
        }

        private static void RegisterBuffer(Dictionary<MemberInfo, FieldInfo> reads, Type owner, string property)
        {
            MethodInfo getter = Require(owner, "get_" + property);
            string name = "s_" + char.ToLowerInvariant(property[0]) + property.Substring(1);
            FieldInfo field = owner.GetField(name, Methods);
            if (field == null || !field.IsStatic || field.FieldType != getter.ReturnType)
                throw new InvalidOperationException("81 Tiles storage layout changed: " + name);
            reads.Add(getter, field);
            reads.Add(field, field);
        }

        // Taking the address and crossing a non-inlined call keeps Unity Mono from
        // embedding the old static readonly array in the calling method's machine code.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T[] ReadCurrent<T>(ref T[] storage) { return storage; }

        internal static IEnumerable<CodeInstruction> Rewrite(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            Assembly assembly = original.DeclaringType.Assembly;
            var reads = new Dictionary<MemberInfo, FieldInfo>();
            Type electricity = assembly.GetType(Owner, true);
            RegisterBuffer(reads, electricity, "PulseGroups");
            RegisterBuffer(reads, electricity, "PulseUnits");
            Type water = assembly.GetType("EightyOne2.Patches.ExpandedWaterManager", true);
            RegisterBuffer(reads, water, "WaterPulseUnits");
            RegisterBuffer(reads, water, "SewagePulseUnits");
            RegisterBuffer(reads, water, "HeatingPulseUnits");
            int matches = 0;
            foreach (CodeInstruction instruction in instructions)
            {
                FieldInfo field = null;
                MemberInfo member = instruction.operand as MemberInfo;
                if (member != null && (instruction.opcode == OpCodes.Ldsfld || instruction.opcode == OpCodes.Call))
                    reads.TryGetValue(member, out field);
                if (field == null) { yield return instruction; continue; }
                CodeInstruction address = new CodeInstruction(instruction);
                address.opcode = OpCodes.Ldsflda;
                address.operand = field;
                yield return address;
                yield return new CodeInstruction(OpCodes.Call, typeof(EightyOne2PulseBuffers)
                    .GetMethod("ReadCurrent").MakeGenericMethod(field.FieldType.GetElementType()));
                matches++;
            }
            if (matches == 0) throw new InvalidOperationException("Missing pulse buffer read in " + original);
        }
    }
}
