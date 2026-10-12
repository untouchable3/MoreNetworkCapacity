using System;
using System.Reflection;
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
                }
                catch (Exception exception)
                {
                    Logging.Warning(
                        "RenderGroup.MeshData patch skipped for "
                        + method
                        + ": "
                        + exception.GetType().Name
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
