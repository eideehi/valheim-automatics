using HarmonyLib;

namespace Automatics.AutomaticProcessing
{
    internal static class Module
    {
        [AutomaticsInitializer(3)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.IsModuleDisabled) return;

            Harmony.CreateAndPatchAll(typeof(Patches),
                Automatics.GetHarmonyId("automatic-processing"));
        }
    }
}