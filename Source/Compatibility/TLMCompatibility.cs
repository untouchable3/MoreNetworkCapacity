using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Compatibility
{
    internal static class TLMCompatibility
    {
        private delegate void Cycle(uint slot, ref long[][] array, int count);
        private delegate uint CurrentIndex(object owner);
        private static Cycle _finishCycle;
        private static CurrentIndex _currentIndex;
        private static FieldInfo _stopLong;
        private static MethodInfo _cycleMethod;
        private static bool _installed;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        internal static void Reset()
        {
            _installed = false; _finishCycle = null; _currentIndex = null;
            _stopLong = null; _cycleMethod = null; TLMStopStatistics.Clear();
        }

        internal static void Apply(Harmony harmony)
        {
            if (_installed || harmony == null) return;
            Assembly assembly = ThirdPartyCapacityCompatibility.FindLoadedAssembly("TransportLinesManager");
            if (assembly == null) return;
            Type type = assembly.GetType("TransportLinesManager.Data.Managers.TLMTransportLineStatusesManager", true);
            MethodInfo[] methods = Prepare(type);
            string[] patches = { "AddPrefix", "TotalPrefix", "CurrentPrefix", "LastPrefix", "UpdatePrefix", "CycleTranspiler" };
            List<MethodInfo> attempted = new List<MethodInfo>();
            try
            {
                for (int i = 0; i < methods.Length; ++i)
                {
                    attempted.Add(methods[i]);
                    HarmonyMethod patch = new HarmonyMethod(typeof(TLMCompatibility).GetMethod(patches[i], All));
                    if (i == 5) { patch.priority = Priority.Last; harmony.Patch(methods[i], null, null, patch, null); }
                    else harmony.Patch(methods[i], patch, null, null, null);
                }
                _installed = true;
                Logging.Info("TLM high-ID stop statistics enabled: sparse 17-slot history, legacy save layout preserved.");
            }
            catch
            {
                for (int i = 0; i < attempted.Count; ++i)
                    harmony.Unpatch(attempted[i], typeof(TLMCompatibility).GetMethod(patches[i], All));
                throw;
            }
        }

        internal static MethodInfo[] Prepare(Type type)
        {
            _stopLong = type.GetField("m_stopDataLong", All);
            FieldInfo stopInt = type.GetField("m_stopDataInt", All);
            if (_stopLong == null || _stopLong.IsStatic || _stopLong.FieldType != typeof(long[][])
                || stopInt == null || stopInt.IsStatic || stopInt.FieldType != typeof(int[][]))
                throw new InvalidOperationException("TLM stop statistics table layout changed.");
            ValidateEnum(type, "StopDataLong", new string[] { "INCOME" });
            ValidateEnum(type, "StopDataSmallInt", new string[] { "TOTAL_PASSENGERS", "TOURIST_PASSENGERS", "STUDENT_PASSENGERS" });
            MethodInfo add = Resolve(type, "AddToStop", false, typeof(ushort), typeof(long), typeof(Citizen).MakeByRefType());
            MethodInfo total = Resolve(type, "GetStopIncome", false, typeof(ushort), typeof(long).MakeByRefType());
            MethodInfo current = Resolve(type, "GetCurrentStopIncome", false, typeof(ushort), typeof(long).MakeByRefType());
            MethodInfo last = Resolve(type, "GetLastWeekStopIncome", false, typeof(ushort), typeof(long).MakeByRefType());
            MethodInfo simulation = Resolve(type, "SimulationStepImpl", true, typeof(int));
            MethodInfo update = Resolve(type, "UpdateData", true, typeof(SimulationManager.UpdateMode));
            MethodInfo generic = type.GetMethod("FinishCycle", All);
            if (generic == null || !generic.IsStatic || !generic.IsGenericMethodDefinition
                || generic.GetGenericArguments().Length != 1 || generic.ReturnType != typeof(void))
                throw new InvalidOperationException("TLM cycle method changed.");
            _cycleMethod = generic.MakeGenericMethod(typeof(long));
            _finishCycle = (Cycle)Delegate.CreateDelegate(typeof(Cycle), _cycleMethod);
            MethodInfo index = type.GetMethod("get_CurrentArrayEntryIdx", All);
            if (index == null || index.IsStatic || index.ReturnType != typeof(uint) || index.GetParameters().Length != 0)
                throw new InvalidOperationException("TLM cycle index changed.");
            DynamicMethod getter = new DynamicMethod("MNC_TLMCurrentIndex", typeof(uint), new Type[] { typeof(object) }, typeof(TLMCompatibility), true);
            ILGenerator il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, type); il.Emit(OpCodes.Call, index); il.Emit(OpCodes.Ret);
            _currentIndex = (CurrentIndex)getter.CreateDelegate(typeof(CurrentIndex));
            // Validate before installing any hook; never silently leave the add
            // prefix active without the corresponding period rollover.
            RewriteCycle(PatchProcessor.GetOriginalInstructions(simulation, (ILGenerator)null));
            return new MethodInfo[] { add, total, current, last, update, simulation };
        }

        private static void ValidateEnum(Type type, string name, string[] entries)
        {
            Type value = type.GetNestedType(name, All);
            if (value == null || !value.IsEnum || Enum.GetValues(value).Length != entries.Length)
                throw new InvalidOperationException("TLM enum layout changed: " + name);
            for (int i = 0; i < entries.Length; ++i)
                if (!Enum.IsDefined(value, entries[i]) || Convert.ToInt32(Enum.Parse(value, entries[i])) != i)
                    throw new InvalidOperationException("TLM enum column changed: " + name);
        }

        private static MethodInfo Resolve(Type type, string name, bool isStatic, params Type[] parameters)
        {
            MethodInfo method = type.GetMethod(name, All, null, parameters, null);
            if (method == null || method.IsStatic != isStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("TLM statistics signature changed: " + name);
            return method;
        }

        private static bool AddPrefix(object __instance, ushort __0, long __1, ref Citizen __2)
        {
            if (!TLMStopStatistics.IsHigh(__0)) return true;
            TLMStopStatistics.Add(__instance, __0, __1,
                (__2.m_flags & Citizen.Flags.Tourist) != 0, (__2.m_flags & Citizen.Flags.Student) != 0);
            return false;
        }
        private static bool TotalPrefix(object __instance, ushort __0, out long __1)
        { __1 = 0; if (!TLMStopStatistics.IsHigh(__0)) return true; __1 = TLMStopStatistics.Get(__instance, __0, -1); return false; }
        private static bool CurrentPrefix(object __instance, ushort __0, out long __1)
        { __1 = 0; if (!TLMStopStatistics.IsHigh(__0)) return true; __1 = TLMStopStatistics.Get(__instance, __0, 16); return false; }
        private static bool LastPrefix(object __instance, ushort __0, out long __1)
        { __1 = 0; if (!TLMStopStatistics.IsHigh(__0)) return true; __1 = TLMStopStatistics.Get(__instance, __0, (int)((_currentIndex(__instance) + 15) & 15)); return false; }

        private static void UpdatePrefix(SimulationManager.UpdateMode __0)
        {
            if (__0 == SimulationManager.UpdateMode.NewMap || __0 == SimulationManager.UpdateMode.NewGameFromMap
                || __0 == SimulationManager.UpdateMode.NewScenarioFromMap || __0 == SimulationManager.UpdateMode.UpdateScenarioFromMap
                || __0 == SimulationManager.UpdateMode.NewAsset) TLMStopStatistics.Clear();
        }

        internal static List<CodeInstruction> RewriteCycle(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> result = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions) result.Add(new CodeInstruction(instruction));
            int matches = 0;
            for (int i = 2; i < result.Count; ++i)
            {
                if (result[i].opcode != OpCodes.Call || !object.Equals(result[i].operand, _cycleMethod)
                    || result[i - 2].opcode != OpCodes.Ldflda || !object.Equals(result[i - 2].operand, _stopLong)
                    || !result[i - 1].LoadsConstant(Capacity.VanillaNodeCapacity)) continue;
                result[i].operand = typeof(TLMCompatibility).GetMethod("FinishStopCycle", All); ++matches;
            }
            if (matches != 1) throw new InvalidOperationException("TLM stop rollover anchor changed: " + matches + " matches.");
            return result;
        }
        private static IEnumerable<CodeInstruction> CycleTranspiler(IEnumerable<CodeInstruction> instructions)
        { return RewriteCycle(instructions); }

        private static void FinishStopCycle(uint slot, ref long[][] array, int count)
        {
            _finishCycle(slot, ref array, count);
            TLMStopStatistics.Rotate(slot);
        }
    }
}
