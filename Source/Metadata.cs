using System.Collections.Generic;
using ColossalFramework;

namespace MoreNetworkCapacity
{
    internal static class Metadata
    {
        private const string ExpandedSaveKey =
            "User/MoreNetworkCapacity/64K-v1";

        public static bool IsExpandedSave()
        {
            SimulationManager simulationManager = Singleton<SimulationManager>.instance;
            if (simulationManager == null || simulationManager.m_metaData == null)
            {
                return false;
            }

            Dictionary<string, bool> overrides =
                simulationManager.m_metaData.m_modOverride;
            if (overrides == null)
            {
                return false;
            }

            bool value;
            return overrides.TryGetValue(ExpandedSaveKey, out value) && value;
        }

        public static void SetExpandedSave()
        {
            PatchState.MarkSaveExpanded();
            Capacity.NodeStorageSize = Capacity.ExpandedNodeCapacity;
            SimulationManager simulationManager = Singleton<SimulationManager>.instance;
            if (simulationManager == null || simulationManager.m_metaData == null)
            {
                return;
            }

            lock (simulationManager.m_metaData)
            {
                if (simulationManager.m_metaData.m_modOverride == null)
                {
                    simulationManager.m_metaData.m_modOverride =
                        new Dictionary<string, bool>();
                }

                simulationManager.m_metaData.m_modOverride[ExpandedSaveKey] = true;
            }
        }

        public static void ClearExpandedSave()
        {
            PatchState.MarkSaveVanilla();
            Capacity.NodeStorageSize = Capacity.VanillaNodeCapacity;
            SimulationManager simulationManager = Singleton<SimulationManager>.instance;
            if (simulationManager == null || simulationManager.m_metaData == null)
            {
                return;
            }

            lock (simulationManager.m_metaData)
            {
                if (simulationManager.m_metaData.m_modOverride == null)
                {
                    return;
                }

                simulationManager.m_metaData.m_modOverride.Remove(ExpandedSaveKey);
            }
        }
    }
}
