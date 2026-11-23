using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch]
    internal static class CoreLoopPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            Assembly assembly = typeof(NetManager).Assembly;
            foreach (string[] target in GeneratedPatchTargets.BranchBoundMethods)
            {
                Type type = assembly.GetType(target[0], false);
                if (type == null)
                {
                    Logging.Warning(
                        "Could not find patch target type "
                        + target[0]
                        + ".");
                    continue;
                }

                MethodBase method = FindTargetMethod(
                    type,
                    target[1],
                    target[2],
                    target[3]);
                if (method != null)
                    yield return method;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            List<string[]> occurrences = GetOccurrences(original);
            if (occurrences.Count == 0)
            {
                Logging.Error(
                    "No exact loop occurrences are defined for "
                    + original.DeclaringType.FullName
                    + "."
                    + original.Name
                    + ".");
                return list;
            }

            if (!LoopLocalTypes.Agree(original, list))
            {
                Logging.Error("Local variable declarations do not match the reviewed method " + original.DeclaringType.FullName
                    + "." + original.Name + ". Leaving this method unchanged.");
                return list;
            }
            int[] indexes = ResolveOccurrenceIndexes(
                original,
                list,
                occurrences);
            if (indexes == null)
            {
                return list;
            }

            if (CinematicLoopBounds.Handles(original))
            {
                Logging.Info("Verified original UInt16 camera bounds in " + original.DeclaringType.FullName + "."
                    + original.Name + "; preserving " + indexes.Length + " reviewed bounds.");
                return list;
            }
            FieldInfo nodeLimitField = AccessTools.Field(
                typeof(Capacity),
                "NodeLoopLimit");
            FieldInfo segmentLimitField = AccessTools.Field(
                typeof(Capacity),
                "SegmentLoopLimit");
            int nodeReplacements = 0;
            int segmentReplacements = 0;

            for (int occurrenceIndex = 0;
                occurrenceIndex < occurrences.Count;
                ++occurrenceIndex)
            {
                string[] occurrence = occurrences[occurrenceIndex];
                int index = indexes[occurrenceIndex];
                int constant;
                if (!int.TryParse(occurrence[3], out constant)
                    || (occurrence[4] != "node"
                        && occurrence[4] != "segment")
                    || index < 0
                    || index >= list.Count)
                {
                    throw new InvalidOperationException(
                        "Invalid exact loop occurrence for "
                        + original.DeclaringType.FullName
                        + "."
                        + original.Name
                        + ".");
                }

                CodeInstruction instruction = list[index];
                if (!IsInt32Constant(instruction, constant))
                {
                    throw new InvalidOperationException(
                        "IL mismatch for "
                        + original.DeclaringType.FullName
                        + "."
                        + original.Name
                        + " occurrence "
                        + index
                        + " (expected constant "
                        + constant
                        + ").");
                }

                if (occurrence[4] == "node")
                {
                    instruction.opcode = OpCodes.Ldsfld;
                    instruction.operand = nodeLimitField;
                    ++nodeReplacements;
                }
                else
                {
                    instruction.opcode = OpCodes.Ldsfld;
                    instruction.operand = segmentLimitField;
                    ++segmentReplacements;
                }
            }

            Logging.Info(
                "Exact loop bounds patched in "
                + original.DeclaringType.FullName
                + "."
                + original.Name
                + ": nodes="
                + nodeReplacements
                + ", segments="
                + segmentReplacements
                + ".");
            return list;
        }

        private static int[] ResolveOccurrenceIndexes(
            MethodBase original,
            List<CodeInstruction> instructions,
            List<string[]> occurrences)
        {
            if (CinematicLoopBounds.Handles(original) || TransportLifecycleLoopBounds.Handles(original)
                || StationLaneLoopBounds.Handles(original) || EventRaceLoopBounds.Handles(original)
                || NetManagerLoopBounds.Handles(original) || RoadAccessLoopBounds.Handles(original)
                || ServicePathLoopBounds.Handles(original) || LaneChainLoopBounds.Handles(original)
                || BuildingChainLoopBounds.Handles(original) || GridChainLoopBounds.Handles(original))
            {
                int[] resolved;
                if (CinematicLoopBounds.Handles(original)) resolved = CinematicLoopBounds.Resolve(original, instructions);
                else if (TransportLifecycleLoopBounds.Handles(original)) resolved = TransportLifecycleLoopBounds.Resolve(original, instructions);
                else if (StationLaneLoopBounds.Handles(original)) resolved = StationLaneLoopBounds.Resolve(original, instructions);
                else if (EventRaceLoopBounds.Handles(original)) resolved = EventRaceLoopBounds.Resolve(original, instructions);
                else if (NetManagerLoopBounds.Handles(original)) resolved = NetManagerLoopBounds.Resolve(original, instructions);
                else if (RoadAccessLoopBounds.Handles(original)) resolved = RoadAccessLoopBounds.Resolve(original, instructions);
                else if (ServicePathLoopBounds.Handles(original)) resolved = ServicePathLoopBounds.Resolve(original, instructions);
                else if (LaneChainLoopBounds.Handles(original)) resolved = LaneChainLoopBounds.Resolve(original, instructions);
                else if (BuildingChainLoopBounds.Handles(original)) resolved = BuildingChainLoopBounds.Resolve(original, instructions);
                else resolved = GridChainLoopBounds.Resolve(original, instructions);
                bool valid = resolved != null && resolved.Length == occurrences.Count;
                if (valid)
                    for (int i = 0; i < resolved.Length; ++i)
                    {
                        int constant;
                        if (!int.TryParse(occurrences[i][3], out constant) || resolved[i] < 0
                            || resolved[i] >= instructions.Count || (i > 0 && resolved[i] <= resolved[i - 1])
                            || !IsInt32Constant(instructions[resolved[i]], constant)
                            || (occurrences[i][4] != "node" && occurrences[i][4] != "segment")) valid = false;
                    }
                if (valid) return resolved;
                Logging.Error("Could not resolve reviewed chain or pool guard for " + original.DeclaringType.FullName
                    + "." + original.Name + ". Leaving this method unchanged.");
                return null;
            }
            if (NetworkToolLoopBounds.Handles(original))
            {
                bool segment = original.DeclaringType == typeof(NetAdjust);
                int[] tool = NetworkToolLoopBounds.Resolve(original, instructions);
                if (tool != null && occurrences.Count == 1
                    && occurrences[0][3] == (segment ? "36864" : "32768")
                    && occurrences[0][4] == (segment ? "segment" : "node")) return tool;
                Logging.Error("Could not resolve network tool traversal guard for " + original.DeclaringType.FullName
                    + "." + original.Name + ". Leaving this method unchanged.");
                return null;
            }

            if (TransportLineMaintenanceBounds.Handles(original))
            {
                int[] maintenance = TransportLineMaintenanceBounds.Resolve(original, instructions);
                if (maintenance != null && occurrences.Count == 1 && occurrences[0][3] == "32768"
                    && occurrences[0][4] == "node") return maintenance;
                Logging.Error("Could not resolve station maintenance guard for TransportLine." + original.Name + ". Leaving this method unchanged.");
                return null;
            }

            if (original.DeclaringType == typeof(TransportLine) && original.Name == "CopyMissingPaths")
            {
                int[] paired = TransportLineLoopBounds.ResolvePairedStops(instructions, original);
                if (paired != null && occurrences.Count == 1 && occurrences[0][3] == "32768"
                    && occurrences[0][4] == "node") return paired;
                Logging.Error("Could not resolve paired station cursors for TransportLine.CopyMissingPaths. Leaving this method unchanged.");
                return null;
            }

            int expectedSegments, expectedNextStops;
            if (original.DeclaringType == typeof(TransportLine)
                && TransportLineLoopBounds.TryGetReviewedShape(original.Name, out expectedSegments, out expectedNextStops))
            {
                int expectedCount = expectedSegments + expectedNextStops;
                List<int> guards = TransportLineLoopBounds.FindStopGuards(instructions, expectedSegments, expectedNextStops, original);
                bool expected = occurrences.Count == expectedCount;
                foreach (string[] occurrence in occurrences)
                    if (occurrence[3] != "32768" || occurrence[4] != "node") expected = false;
                if (expected && guards.Count == expectedCount) return guards.ToArray();
                Logging.Error(
                    "Could not resolve shifted loop occurrences for TransportLine." + original.Name + ". "
                    + "Leaving this method unchanged. Verified stop guards=" + guards.Count
                    + ", expected node guards=" + expectedCount + ".");
                return null;
            }

            Logging.Error("No reviewed semantic loop rule handles " + original.DeclaringType.FullName
                + "." + original.Name + ". Leaving this method unchanged.");
            return null;
        }

        private static List<string[]> GetOccurrences(
            MethodBase original)
        {
            int expectedToken = FindExpectedToken(original);
            if (expectedToken < 0)
            {
                return new List<string[]>();
            }

            return GetOccurrences(expectedToken);
        }

        private static List<string[]> GetOccurrences(int token)
        {
            List<string[]> result = new List<string[]>();
            foreach (string[] occurrence
                in GeneratedPatchTargets.BranchBoundOccurrences)
            {
                int occurrenceToken;
                if (int.TryParse(occurrence[0], out occurrenceToken)
                    && occurrenceToken == token)
                {
                    result.Add(occurrence);
                }
            }

            return result;
        }

        private static int FindExpectedToken(MethodBase original)
        {
            string signature = GetSignature(original);
            foreach (string[] target
                in GeneratedPatchTargets.BranchBoundMethods)
            {
                if (target[0] == original.DeclaringType.FullName
                    && target[1] == original.Name
                    && target[3] == signature)
                {
                    int token;
                    return int.TryParse(target[2], out token)
                        ? token
                        : -1;
                }
            }

            return -1;
        }

        private static MethodBase FindTargetMethod(
            Type type,
            string name,
            string tokenText,
            string signature)
        {
            int expectedToken;
            if (!int.TryParse(tokenText, out expectedToken))
            {
                Logging.Warning(
                    "Invalid metadata token for "
                    + type.FullName
                    + "."
                    + name
                    + ": "
                    + tokenText);
                return null;
            }

            MethodBase match = null;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.Name == name
                    && GetSignature(method) == signature)
                {
                    if (match != null)
                    {
                        Logging.Error(
                            "Multiple exact loop targets match "
                            + type.FullName
                            + "."
                            + name
                            + "("
                            + signature
                            + ").");
                        return null;
                    }

                    match = method;
                }
            }

            if (match == null)
            {
                Logging.Warning(
                    "Could not find exact loop target "
                    + type.FullName
                    + "."
                    + name
                    + "("
                    + signature
                    + ") [token "
                    + tokenText
                    + "].");
                return null;
            }

            if (match.MetadataToken != expectedToken)
            {
                Logging.Warning(
                    "Runtime method token differs for "
                    + type.FullName
                    + "."
                    + name
                    + " (expected "
                    + expectedToken
                    + ", found "
                    + match.MetadataToken
                    + "). Matching by method identity.");
            }

            return match;
        }

        private static string GetSignature(MethodBase method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            string result = "";
            for (int i = 0; i < parameters.Length; ++i)
            {
                if (i != 0)
                    result += ",";

                result += GetCecilTypeName(parameters[i].ParameterType);
            }

            return result;
        }

        private static string GetCecilTypeName(Type type)
        {
            if (type.IsByRef) return GetCecilTypeName(type.GetElementType()) + "&";
            if (type.IsPointer) return GetCecilTypeName(type.GetElementType()) + "*";
            if (type.IsArray) return GetCecilTypeName(type.GetElementType())
                + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericParameter) return type.Name;
            if (!type.IsGenericType) return (type.FullName ?? type.Name).Replace('+', '/');

            // Reflection FullName uses assembly-qualified [[T, Assembly]] for
            // closed generics; the reviewed Cecil signature uses Type<T>.
            Type[] arguments = type.GetGenericArguments();
            string name = type.GetGenericTypeDefinition().FullName.Replace('+', '/') + "<";
            for (int i = 0; i < arguments.Length; ++i)
            {
                if (i != 0) name += ",";
                name += GetCecilTypeName(arguments[i]);
            }
            return name + ">";
        }

        private static bool IsInt32Constant(
            CodeInstruction instruction,
            int value)
        {
            return (instruction.opcode == OpCodes.Ldc_I4
                    || instruction.opcode == OpCodes.Ldc_I4_S)
                && instruction.operand is int
                && (int)instruction.operand == value;
        }
    }
}
