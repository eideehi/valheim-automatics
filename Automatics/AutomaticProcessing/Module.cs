using HarmonyLib;

namespace Automatics.AutomaticProcessing
{
    internal static class Module
    {
        [AutomaticsInitializer(3)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.ModuleDisabled) return;

            Hooks.OnPlayerAwake += (player, zNetView) =>
            {
                Logics.Cleanup();
                SmelterProcess.Cleanup();
                ConnectionEffects.Cleanup();
            };
            Hooks.OnPlayerUpdate += OnPlayerUpdate;

            Harmony.CreateAndPatchAll(typeof(Patches),
                Automatics.GetHarmonyId("automatic-processing"));
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (Player.m_localPlayer != player || !player.IsOwner()) return;

            ConnectionEffects.Update(player);
        }
    }
}
