using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity.Patches
{
    internal static class RenderGroupMeshDataPatch
    {
        private static int _fixCount;

        internal static void Apply(Harmony harmony)
        {
            Type meshDataType = typeof(RenderGroup).GetNestedType(
                "MeshData",
                BindingFlags.Public
                    | BindingFlags.NonPublic);
            if (meshDataType == null)
            {
                throw new MissingMemberException(
                    "RenderGroup.MeshData type was not found.");
            }

            MethodInfo[] methods = meshDataType.GetMethods(
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
            int patched = 0;
            for (int i = 0; i < methods.Length; ++i)
            {
                MethodInfo method = methods[i];
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name != "PopulateMesh"
                    || parameters.Length != 1)
                {
                    continue;
                }

                try
                {
                    harmony.Patch(
                        method,
                        new HarmonyMethod(
                            typeof(RenderGroupMeshDataPatch).GetMethod(
                                "Prefix",
                                BindingFlags.Static
                                    | BindingFlags.Public
                                    | BindingFlags.NonPublic)),
                        null,
                        null,
                        null);
                    ++patched;
                    Logging.Info(
                        "RenderGroup.MeshData patched: "
                        + method
                        + ".");
                    if (parameters[0].ParameterType == typeof(RenderGroup.MeshWrapper))
                    {
                        harmony.Patch(method, null, null, new HarmonyMethod(typeof(RenderGroupMeshDataPatch)
                            .GetMethod("RewriteSplitUvs", BindingFlags.Static | BindingFlags.NonPublic)), null);
                        Logging.Info("RenderGroup split meshes use chunk-relative UV4 coordinates.");
                    }
                }
                catch (Exception exception)
                {
                    Logging.Warning(
                        "RenderGroup.MeshData patch skipped for "
                        + method
                        + ": "
                        + exception
                        + ".");
                }
            }

            Logging.Info(
                "RenderGroup.MeshData patch pass completed: patched="
                + patched
                + ", candidates="
                + methods.Length
                + ".");

            PatchMeshExtensionsSetUvs(harmony);
        }

        // The game builds a two-component slice, discards it and submits the
        // entire four-component channel. Keep the mesh and channel arguments
        // and replace that unused copy with a slice at the actual chunk start.
        private static IEnumerable<CodeInstruction> RewriteSplitUvs(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            FieldInfo uv = typeof(RenderGroup.MeshData).GetField("m_uvs4");
            int guard = -1;
            for (int i = 1; i + 1 < code.Count; i++)
            {
                if (code[i-1].opcode != OpCodes.Ldarg_0 || code[i].opcode != OpCodes.Ldfld
                    || !Equals(code[i].operand, uv) || code[i+1].opcode != OpCodes.Brfalse) continue;
                if (guard >= 0) throw new InvalidOperationException("Ambiguous split UV4 guard.");
                guard = i+1;
            }
            if (guard < 0 || !(code[guard].operand is Label)) throw new InvalidOperationException("Missing split UV4 guard.");
            int end = code.FindIndex(guard+1, delegate(CodeInstruction c) { return c.labels.Contains((Label)code[guard].operand); });
            int start = guard+1;
            int submit = end-1;
            MethodInfo setUvs = submit >= 0 ? code[submit].operand as MethodInfo : null;
            if (end-start != 34 || code[start].opcode != OpCodes.Ldloc_1 || code[start+1].opcode != OpCodes.Ldloc_0
                || code[start+2].opcode != OpCodes.Sub || code[start+3].opcode != OpCodes.Ldc_I4_1
                || code[start+4].opcode != OpCodes.Add || code[start+5].opcode != OpCodes.Newarr
                || !Equals(code[start+5].operand, typeof(Vector2)) || !Equals(code[start+16].operand, uv)
                || code[start+21].opcode != OpCodes.Stobj || !Equals(code[start+21].operand, typeof(Vector2))
                || code[submit-4].opcode != OpCodes.Ldloc_3 || code[submit-3].opcode != OpCodes.Ldc_I4_3
                || code[submit-2].opcode != OpCodes.Ldarg_0 || !Equals(code[submit-1].operand, uv)
                || code[submit].opcode != OpCodes.Call || setUvs == null || setUvs.DeclaringType.Name != "MeshExtensions"
                || setUvs.Name != "SetUVs")
                throw new InvalidOperationException("Unexpected split UV4 copy or submission.");

            // Preserve other transpilers' branch targets and exception regions.
            HashSet<Label> removed = new HashSet<Label>();
            for (int i = start; i < submit-4; i++)
            {
                if (code[i].blocks.Count != 0) throw new InvalidOperationException("Protected split UV4 copy.");
                foreach (Label label in code[i].labels) removed.Add(label);
            }
            for (int i = 0; i < code.Count; i++)
            {
                if (i >= start && i < submit-4) continue;
                object target = code[i].operand;
                if (target is Label && removed.Contains((Label)target)
                    || target is Label[] && Array.Exists((Label[])target, removed.Contains))
                    throw new InvalidOperationException("External branch into split UV4 copy.");
            }
            code.InsertRange(submit, new[] { new CodeInstruction(OpCodes.Ldloc_0, null), new CodeInstruction(OpCodes.Ldloc_1, null),
                new CodeInstruction(OpCodes.Call, typeof(RenderGroupMeshDataPatch).GetMethod("SliceUvs",
                    BindingFlags.Static | BindingFlags.NonPublic)) });
            code.RemoveRange(start, submit-4-start);
            return code;
        }

        private static Vector4[] SliceUvs(Vector4[] source, int first, int last)
        {
            Vector4[] slice = new Vector4[last-first+1];
            Array.Copy(source, first, slice, 0, slice.Length);
            return slice;
        }

        private static void Prefix(object __instance)
        {
            if (__instance == null)
            {
                return;
            }

            FieldInfo verticesField = AccessTools.Field(
                __instance.GetType(),
                "m_vertices");
            FieldInfo uv4Field = AccessTools.Field(
                __instance.GetType(),
                "m_uvs4");
            if (verticesField == null || uv4Field == null)
            {
                return;
            }

            Vector3[] vertices = verticesField.GetValue(
                __instance) as Vector3[];
            Vector4[] uv4 = uv4Field.GetValue(
                __instance) as Vector4[];
            if (vertices == null || uv4 == null
                || vertices.Length == uv4.Length)
            {
                return;
            }

            Vector4[] replacement = new Vector4[vertices.Length];
            Array.Copy(
                uv4,
                replacement,
                Math.Min(uv4.Length, replacement.Length));
            uv4Field.SetValue(__instance, replacement);

            if (Interlocked.Increment(ref _fixCount) <= 8)
            {
                Logging.Warning(
                    "Normalized RenderGroup.MeshData uv4 length from "
                    + uv4.Length
                    + " to vertex count "
                    + vertices.Length
                    + ".");
            }
        }

        private static void PatchMeshExtensionsSetUvs(Harmony harmony)
        {
            Type extensionsType = typeof(RenderGroup).Assembly.GetType(
                "MeshExtensions");
            if (extensionsType == null)
            {
                Logging.Warning("MeshExtensions type was not found.");
                return;
            }

            MethodInfo target = null;
            MethodInfo[] methods = extensionsType.GetMethods(
                BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; ++i)
            {
                MethodInfo method = methods[i];
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name != "SetUVs"
                    || parameters.Length != 3
                    || parameters[0].ParameterType != typeof(Mesh)
                    || parameters[1].ParameterType != typeof(int)
                    || parameters[2].ParameterType != typeof(Vector4[]))
                {
                    continue;
                }

                target = method;
                break;
            }

            if (target == null)
            {
                Logging.Warning("MeshExtensions.SetUVs target was not found.");
                return;
            }

            try
            {
                MethodInfo prefix = typeof(RenderGroupMeshDataPatch).GetMethod(
                    "SetUvsPrefix",
                    BindingFlags.Static
                        | BindingFlags.NonPublic);
                harmony.Patch(
                    target,
                    new HarmonyMethod(prefix),
                    null,
                    null,
                    null);
                Logging.Info("MeshExtensions.SetUVs length guard patched.");
            }
            catch (Exception exception)
            {
                Logging.Warning(
                    "MeshExtensions.SetUVs patch skipped: "
                    + exception.GetType().Name
                    + ".");
            }
        }

        private static void SetUvsPrefix(
            Mesh __0,
            ref Vector4[] __2)
        {
            if (__0 == null
                || __2 == null
                || __0.vertexCount == __2.Length)
            {
                return;
            }

            int oldLength = __2.Length;
            int vertexCount = __0.vertexCount;
            Vector4[] replacement = new Vector4[vertexCount];
            Array.Copy(
                __2,
                replacement,
                Math.Min(__2.Length, replacement.Length));
            __2 = replacement;

            if (Interlocked.Increment(ref _fixCount) <= 8)
            {
                Logging.Warning(
                    "Normalized MeshExtensions.SetUVs array from "
                    + oldLength
                    + " to mesh vertex count "
                    + vertexCount
                    + ".");
            }
        }
    }
}
