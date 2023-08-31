using HarmonyLib;

namespace Automatics.AutomaticFeeding
{
    internal static class Module
    {
        [AutomaticsInitializer(4)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.ModuleDisabled) return;

            Harmony.CreateAndPatchAll(typeof(Patches),
                Automatics.GetHarmonyId("automatic-feeding"));
        }
    }
}