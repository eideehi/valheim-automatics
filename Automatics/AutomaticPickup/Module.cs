using HarmonyLib;

namespace Automatics.AutomaticPickup
{
    internal static class Module
    {
        [AutomaticsInitializer(8)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.ModuleDisabled) return;

            Hooks.OnPlayerAwake += OnPlayerAwake;
            Harmony.CreateAndPatchAll(typeof(Patches), Automatics.GetHarmonyId("automatic-pickup"));
        }

        private static void OnPlayerAwake(Player player, ZNetView zNetView)
        {
            if (zNetView.GetZDO() != null)
                player.gameObject.AddComponent<AutomaticPickup>();
        }
    }
}