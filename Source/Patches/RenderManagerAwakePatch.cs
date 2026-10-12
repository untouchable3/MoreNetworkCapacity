using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace MoreNetworkCapacity.Patches
{
    [HarmonyPatch(typeof(RenderManager), "Awake")]
    internal static class RenderManagerAwakePatch
    {
        private const int MethodToken = 100676925;

        private static readonly int[,] Replacements =
        {
            { 7, 0x0017 },
            { 64, 0x00BB },
        };

        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            ConfigureColorMapFields(null);
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            EnsureCapacity();

            RenderManager manager = Singleton<RenderManager>.instance;
            if (manager == null || manager.m_objectColorMap == null)
            {
                return;
            }

            InitializeColorMap(manager.m_objectColorMap);
            QueueBuildingColorRefresh();
        }

        internal static void EnsureCapacity()
        {
            RenderManager manager = Singleton<RenderManager>.instance;
            if (manager == null)
            {
                return;
            }

            int width;
            int height;
            ConfigureColorMapFields(manager, out width, out height);
            EnsureIndexArray(manager);
            EnsureColorMap(
                manager,
                Capacity.GetColorIndexCapacity(),
                width,
                height);
        }

        private static void ConfigureColorMapFields(
            RenderManager manager)
        {
            int width;
            int height;
            ConfigureColorMapFields(manager, out width, out height);
        }

        private static void ConfigureColorMapFields(
            RenderManager manager,
            out int width,
            out int height)
        {
            int required = Capacity.GetColorIndexCapacity();
            width = RenderManager.OBJECT_COLORMAP_WIDTH;
            height = RenderManager.OBJECT_COLORMAP_HEIGHT;

            if ((width <= 0 || height <= 0)
                && manager != null
                && manager.m_objectColorMap != null)
            {
                width = manager.m_objectColorMap.width;
                height = manager.m_objectColorMap.height;
            }

            if (width <= 0 || height <= 0)
            {
                width = NextPowerOfTwo(CeilingSquareRoot(required));
                height = NextPowerOfTwo(
                    (required + width - 1) / width);
            }
            else
            {
                width = NextPowerOfTwo(width);
                height = NextPowerOfTwo(height);
                if ((long)width * height < required)
                {
                    height = NextPowerOfTwo(
                        (required + width - 1) / width);
                }
            }

            if ((long)width * height < required)
            {
                throw new InvalidOperationException(
                    "Object color map capacity is smaller than required.");
            }

            RenderManager.OBJECT_COLORMAP_WIDTH = width;
            RenderManager.OBJECT_COLORMAP_HEIGHT = height;
            RenderManager.DefaultColorLocation = new Vector4(
                0.5f / width,
                0.5f / height,
                0.0f,
                0.0f);

            Logging.Info(
                "Object color map configured as "
                + width
                + "x"
                + height
                + " (required entries="
                + required
                + ").");
        }

        private static void EnsureIndexArray(RenderManager manager)
        {
            int required = Capacity.GetColorIndexCapacity();
            if (manager.m_indices != null
                && manager.m_indices.Length >= required)
            {
                return;
            }

            ushort[] previous = manager.m_indices;
            ushort[] replacement = new ushort[required];
            for (int i = 0; i < replacement.Length; ++i)
            {
                replacement[i] = ushort.MaxValue;
            }

            if (previous != null)
            {
                Array.Copy(
                    previous,
                    replacement,
                    Math.Min(previous.Length, replacement.Length));
            }

            manager.m_indices = replacement;
            Logging.Info(
                "Render instance index array expanded to "
                + replacement.Length
                + " entries.");
        }

        private static void EnsureColorMap(
            RenderManager manager,
            int required,
            int width,
            int height)
        {
            Texture2D previous = manager.m_objectColorMap;
            if (previous != null
                && previous.width >= width
                && previous.height >= height
                && (long)previous.width * previous.height >= required)
            {
                return;
            }

            Texture2D replacement = new Texture2D(
                width,
                height,
                TextureFormat.ARGB32,
                false,
                false);
            replacement.filterMode = FilterMode.Point;

            Color32[] pixels = new Color32[width * height];
            Color32 white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < pixels.Length; ++i)
            {
                pixels[i] = white;
            }

            if (previous != null)
            {
                int copyWidth = Math.Min(previous.width, width);
                int copyHeight = Math.Min(previous.height, height);
                Color32[] previousPixels = previous.GetPixels32();
                if (copyWidth > 0 && copyHeight > 0)
                {
                    for (int y = 0; y < copyHeight; ++y)
                    {
                        int destination = y * width;
                        int source = y * previous.width;
                        for (int x = 0; x < copyWidth; ++x)
                        {
                            pixels[destination + x] =
                                previousPixels[source + x];
                        }
                    }
                }
            }

            replacement.SetPixels32(pixels);
            replacement.Apply();
            manager.m_objectColorMap = replacement;
            Shader.SetGlobalTexture("_ObjectColorMap", replacement);
            if (previous != null)
            {
                UnityEngine.Object.Destroy(previous);
            }

            Logging.Info(
                "Object color-map texture resized to "
                + width
                + "x"
                + height
                + ".");
        }

        private static void InitializeColorMap(Texture2D texture)
        {
            Color32[] pixels = new Color32[texture.width * texture.height];
            Color32 white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < pixels.Length; ++i)
            {
                pixels[i] = white;
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            Logging.Info(
                "Object color-map texture initialized to white: "
                + texture.width
                + "x"
                + texture.height
                + ".");
        }

        private static void QueueBuildingColorRefresh()
        {
            BuildingManager buildingManager =
                Singleton<BuildingManager>.instance;
            if (buildingManager == null)
            {
                Logging.Warning(
                    "Could not queue a building color refresh: "
                    + "BuildingManager is not available.");
                return;
            }

            buildingManager.UpdateBuildingColors();
            Logging.Info(
                "Queued a full building color refresh after "
                + "object color-map initialization.");
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            MethodBase original,
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list =
                new List<CodeInstruction>(instructions);
            if (original.MetadataToken != MethodToken)
            {
                Logging.Warning(
                    "Runtime RenderManager.Awake token differs "
                    + "(expected "
                    + MethodToken
                    + ", found "
                    + original.MetadataToken
                    + "). Matching by method identity.");
            }

            MethodInfo capacityMethod = AccessTools.Method(
                typeof(Capacity),
                "GetColorIndexCapacity",
                null,
                null);
            List<int> indexes = FindColorCapacityIndexes(list);
            if (indexes.Count != Replacements.GetLength(0))
            {
                Logging.Error(
                    "RenderManager color-index pattern mismatch: expected "
                    + Replacements.GetLength(0)
                    + ", found "
                    + indexes.Count
                    + " in "
                    + list.Count
                    + " instructions. Leaving values unchanged.");
                return list;
            }

            for (int i = 0; i < indexes.Count; ++i)
            {
                ReplaceExactConstant(
                    list,
                    indexes[i],
                    capacityMethod);
            }

            Logging.Info(
                "Render color-index capacity replaced in "
                + indexes.Count
                + " locations.");
            return list;
        }

        private static List<int> FindColorCapacityIndexes(
            List<CodeInstruction> instructions)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < instructions.Count; ++i)
            {
                if (!IsInt32Constant(
                        instructions[i],
                        Capacity.VanillaColorIndexCapacity))
                {
                    continue;
                }

                bool allocation =
                    i + 2 < instructions.Count
                    && instructions[i + 1].opcode == OpCodes.Newarr
                    && instructions[i + 2].opcode == OpCodes.Stfld;
                bool iteration =
                    i + 1 < instructions.Count
                    && instructions[i + 1].opcode.FlowControl
                        == FlowControl.Cond_Branch;
                if (allocation || iteration)
                {
                    result.Add(i);
                }
            }

            return result;
        }

        private static void ReplaceExactConstant(
            List<CodeInstruction> instructions,
            int index,
            MethodInfo capacityMethod)
        {
            if (index < 0 || index >= instructions.Count)
            {
                throw new InvalidOperationException(
                    "Instruction index "
                    + index
                    + " is outside RenderManager.Awake.");
            }

            CodeInstruction instruction = instructions[index];
            if (!IsInt32Constant(
                    instruction,
                    Capacity.VanillaColorIndexCapacity))
            {
                throw new InvalidOperationException(
                    "Unexpected RenderManager color-map IL at index "
                    + index
                    + ".");
            }

            instruction.opcode = OpCodes.Call;
            instruction.operand = capacityMethod;
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

        private static int CeilingSquareRoot(int value)
        {
            int result = 0;
            while ((long)result * result < value)
            {
                ++result;
            }

            return result;
        }

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value)
            {
                result <<= 1;
            }

            return result;
        }
    }
}
