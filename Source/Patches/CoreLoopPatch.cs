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

            int[] indexes = ResolveOccurrenceIndexes(
                original,
                list,
                occurrences);
            if (indexes == null)
            {
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

                if (HasNarrowingConversionBefore(list, index))
                {
                    Logging.Warning(
                        "Skipping 16-bit bound in "
                        + original.DeclaringType.FullName
                        + "."
                        + original.Name
                        + " occurrence "
                        + index
                        + " (constant "
                        + constant
                        + ").");
                    continue;
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
            int[] exactIndexes = new int[occurrences.Count];
            bool exactMatch = true;
            for (int i = 0; i < occurrences.Count; ++i)
            {
                int index;
                int constant;
                if (!int.TryParse(occurrences[i][1], out index)
                    || !int.TryParse(occurrences[i][3], out constant)
                    || index < 0
                    || index >= instructions.Count
                    || !IsInt32Constant(instructions[index], constant)
                    || index + 1 >= instructions.Count
                    || instructions[index + 1].opcode.FlowControl
                        != FlowControl.Cond_Branch)
                {
                    exactMatch = false;
                    break;
                }

                exactIndexes[i] = index;
            }

            if (exactMatch)
            {
                return exactIndexes;
            }

            int[] fallbackIndexes = FindFallbackIndexes(
                instructions,
                occurrences);
            if (fallbackIndexes != null)
            {
                Logging.Warning(
                    "Using branch-bound fallback for "
                    + original.DeclaringType.FullName
                    + "."
                    + original.Name
                    + ".");
            }
            else
            {
                Logging.Error(
                    "Could not resolve shifted loop occurrences for "
                    + original.DeclaringType.FullName
                    + "."
                    + original.Name
                    + ". Leaving this method unchanged.");
            }

            return fallbackIndexes;
        }

        private static int[] FindFallbackIndexes(
            List<CodeInstruction> instructions,
            List<string[]> occurrences)
        {
            Dictionary<string, List<string[]>> occurrenceGroups =
                new Dictionary<string, List<string[]>>();
            for (int i = 0; i < occurrences.Count; ++i)
            {
                string key = GetOccurrenceKey(occurrences[i]);
                if (key == null)
                {
                    return null;
                }

                List<string[]> group;
                if (!occurrenceGroups.TryGetValue(key, out group))
                {
                    group = new List<string[]>();
                    occurrenceGroups.Add(key, group);
                }
                group.Add(occurrences[i]);
            }

            Dictionary<string[], int> resolved =
                new Dictionary<string[], int>();
            foreach (KeyValuePair<string, List<string[]>> pair
                in occurrenceGroups)
            {
                List<string[]> group = pair.Value;
                group.Sort(CompareOccurrences);

                int constant = int.Parse(group[0][3]);
                List<int> candidates = FindBranchBoundCandidates(
                    instructions,
                    constant);
                if (candidates.Count != group.Count)
                {
                    return null;
                }

                for (int i = 0; i < group.Count; ++i)
                {
                    resolved.Add(group[i], candidates[i]);
                }
            }

            int[] result = new int[occurrences.Count];
            for (int i = 0; i < occurrences.Count; ++i)
            {
                if (!resolved.TryGetValue(occurrences[i], out result[i]))
                {
                    return null;
                }
            }

            return result;
        }

        private static string GetOccurrenceKey(string[] occurrence)
        {
            int constant;
            if (!int.TryParse(occurrence[3], out constant)
                || (occurrence[4] != "node"
                    && occurrence[4] != "segment"))
            {
                return null;
            }

            return occurrence[4] + ":" + constant;
        }

        private static int CompareOccurrences(
            string[] left,
            string[] right)
        {
            int leftIndex;
            int rightIndex;
            if (!int.TryParse(left[1], out leftIndex)
                || !int.TryParse(right[1], out rightIndex))
            {
                return string.CompareOrdinal(left[1], right[1]);
            }

            return leftIndex.CompareTo(rightIndex);
        }

        private static List<int> FindBranchBoundCandidates(
            List<CodeInstruction> instructions,
            int constant)
        {
            List<int> result = new List<int>();
            for (int i = 0; i + 1 < instructions.Count; ++i)
            {
                if (IsInt32Constant(instructions[i], constant)
                    && instructions[i + 1].opcode.FlowControl
                        == FlowControl.Cond_Branch)
                {
                    result.Add(i);
                }
            }

            return result;
        }

        private static bool HasNarrowingConversionBefore(
            List<CodeInstruction> instructions,
            int index)
        {
            int start = Math.Max(0, index - 6);
            for (int i = start; i < index; ++i)
            {
                if (instructions[i].opcode == OpCodes.Conv_U2
                    || instructions[i].opcode == OpCodes.Conv_I2)
                {
                    return true;
                }
            }

            return false;
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

                string parameterName =
                    parameters[i].ParameterType.FullName;
                if (parameterName != null)
                {
                    // Mono.Cecil writes nested types with '/', while
                    // runtime reflection writes them with '+'.
                    parameterName = parameterName.Replace('+', '/');
                }

                result += parameterName;
            }

            return result;
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
