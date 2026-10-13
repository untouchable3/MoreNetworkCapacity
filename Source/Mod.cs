using ICities;

namespace MoreNetworkCapacity
{
    public sealed class Mod : IUserMod
    {
        public string Name
        {
            get
            {
                return "More Network Capacity " + BuildInfo.Version;
            }
        }

        public string Description
        {
            get
            {
                return "Expands network node and segment capacity at runtime. "
                    + "Expanded saves must not be loaded without this mod.";
            }
        }

        public void OnEnabled()
        {
            LoadStageJournal.Record("Mod.OnEnabled entered; before bootstrap");
            HarmonyBootstrap.Enable();
        }

        public void OnDisabled()
        {
            HarmonyBootstrap.Disable();
        }

        public void OnSettingsUI(UIHelperBase helper)
        {
            DiagnosticSettings.Add(helper);
        }
    }
}
