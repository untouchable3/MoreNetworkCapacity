using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    // These reviewed methods have one station cursor and the same counter,
    // invalid-list handler and nonzero-cursor back edge as the core stop loops.
    // ClearStops, CloneLine, GetLastStop and ReleaseNode have different predicates
    // or deferred cursor updates and deliberately remain outside this recognizer.
    internal static class TransportLineMaintenanceBounds
    {
        internal static bool Handles(MethodBase original)
        {
            MethodInfo method = original as MethodInfo;
            if (method == null || method.DeclaringType != typeof(TransportLine) || method.IsStatic)
                return false;
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == "AddVehicle")
                return method.ReturnType == typeof(void) && parameters.Length == 3
                    && parameters[0].ParameterType == typeof(ushort)
                    && parameters[1].ParameterType == typeof(Vehicle).MakeByRefType()
                    && parameters[2].ParameterType == typeof(bool);
            if (method.Name == "GetPullValue")
                return method.ReturnType == typeof(int) && parameters.Length == 1
                    && parameters[0].ParameterType == typeof(ushort);
            if (method.Name == "GetStopProgress")
                return method.ReturnType == typeof(void) && parameters.Length == 4
                    && parameters[0].ParameterType == typeof(ushort)
                    && parameters[1].ParameterType == typeof(float).MakeByRefType()
                    && parameters[2].ParameterType == typeof(float).MakeByRefType()
                    && parameters[3].ParameterType == typeof(float).MakeByRefType();
            return false;
        }

        internal static int[] Resolve(MethodBase original, List<CodeInstruction> code)
        {
            if (!Handles(original) || code == null) return null;
            bool nextStop = original.Name == "AddVehicle";
            List<int> guards = TransportLineLoopBounds.FindStopGuards(
                code, nextStop ? 0 : 1, nextStop ? 1 : 0, original);
            return guards.Count == 1 ? guards.ToArray() : null;
        }
    }
}
