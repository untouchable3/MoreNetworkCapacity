namespace MoreNetworkCapacity
{
    internal static class PatchState
    {
        private static bool _expansionEnabled;
        private static bool _saveExpanded;

        public static bool ExpansionEnabled
        {
            get { return _expansionEnabled; }
        }

        public static bool SaveExpanded
        {
            get { return _saveExpanded; }
        }

        internal static void MarkEnabled()
        {
            _expansionEnabled = true;
        }

        internal static void MarkDisabled()
        {
            _expansionEnabled = false;
        }

        internal static void MarkSaveExpanded()
        {
            _saveExpanded = true;
        }

        internal static void MarkSaveVanilla()
        {
            _saveExpanded = false;
        }
    }

    public static class NetManagerDeserializeState
    {
        private static bool _loadingExpanded;

        public static bool LoadingExpanded
        {
            get { return _loadingExpanded; }
        }

        internal static void SetLoadingExpanded(bool value)
        {
            _loadingExpanded = value;
            Capacity.NodeStorageSize = value
                ? Capacity.ExpandedNodeCapacity
                : Capacity.VanillaNodeCapacity;
        }

        internal static void ResetLoadingExpanded()
        {
            _loadingExpanded = false;
            Capacity.NodeStorageSize = PatchState.SaveExpanded
                ? Capacity.ExpandedNodeCapacity
                : Capacity.VanillaNodeCapacity;
        }
    }
}
