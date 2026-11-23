using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch]
    internal static class SaveFailureDiagnostics
    {
        private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var game = typeof(LoadingManager).Assembly;
            yield return AccessTools.Method(game.GetType("SavePanel+<SaveGame>c__Iterator1", true), "MoveNext", null, null);
            yield return AccessTools.Method(game.GetType("LoadingManager+<SaveSimulationData>c__IteratorC", true), "MoveNext", null, null);
            yield return AccessTools.Method(game.GetType("SerializableDataWrapper", true), "OnSaveData", null, null);
            foreach (string name in new[]{"NetManager","WaterManager","ElectricityManager"})
                yield return AccessTools.Method(game.GetType(name + "+Data", true), "Serialize", null, null);
        }

        private static IEnumerable<CodeInstruction> Transpiler(MethodBase original, IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo callback = typeof(ICities.ISerializableDataExtension).GetMethod("OnSaveData");
            foreach (CodeInstruction instruction in instructions)
            {
                if (original.DeclaringType.Name == "SerializableDataWrapper" && Equals(instruction.operand, callback))
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(SaveFailureDiagnostics), "CallSaveExtension", null, null);
                }
                yield return instruction;
            }
        }

        private static void CallSaveExtension(ICities.ISerializableDataExtension extension)
        {
            var step = ErrorReport.EnterSaveStep(extension == null ? "Missing save extension" : extension.GetType().FullName + ".OnSaveData");
            Exception failure = null;
            try { extension.OnSaveData(); }
            catch (Exception error) { failure = error; throw; }
            finally { ErrorReport.LeaveSaveStep(step, failure); }
        }

        private static void Prefix(MethodBase __originalMethod, out ErrorReport.SaveStep __state)
        {
            __state = ErrorReport.EnterSaveStep(__originalMethod.DeclaringType.FullName + "." + __originalMethod.Name);
        }

        private static void Finalizer(ErrorReport.SaveStep __state, Exception __exception)
        {
            ErrorReport.LeaveSaveStep(__state, __exception);
        }

        internal static void TilesFailure(MethodBase __originalMethod, object[] __args, Exception __exception)
        {
            if (__exception == null) return;
            var step = ErrorReport.EnterSaveStep(__originalMethod.DeclaringType.FullName + ".CustomSerialize");
            try
            {
                bool water = __originalMethod.DeclaringType.Name == "WaterManagerDataPatches";
                string[] channels = water ? new[]{"WaterPulseUnits","SewagePulseUnits","HeatingPulseUnits"}
                    : new[]{"PulseGroups","PulseUnits"};
                var text = new StringBuilder(__exception.Message);
                text.Append("\nGrid length=").Append(Length(__args[0]));
                var owner = __originalMethod.DeclaringType.Assembly.GetType("EightyOne2.Patches.Expanded"
                    + (water ? "WaterManager" : "ElectricityManager"), true);
                for (int i = 0; i < channels.Length; ++i)
                {
                    string channel = channels[i];
                    text.Append("\n").Append(channel).Append(" argument=").Append(Length(__args[i+1]));
                    string field = "s_" + char.ToLowerInvariant(channel[0]) + channel.Substring(1);
                    text.Append(" field=").Append(Length(owner.GetField(field, StaticMembers).GetValue(null)));
                    text.Append(" getter=").Append(Length(owner.GetMethod("get_" + channel, StaticMembers).Invoke(null,null)));
                }
                ErrorReport.CaptureSaveError(text.ToString(), __exception.ToString());
            }
            catch { /* The original failure is still captured if a field cannot be inspected. */ }
            finally { ErrorReport.LeaveSaveStep(step, __exception); }
        }

        private static int Length(object value)
        {
            Array array = value as Array;
            return array == null ? -1 : array.Length;
        }
    }
}
