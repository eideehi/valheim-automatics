using System.Diagnostics;
using HarmonyLib;

namespace Automatics.Debug
{
    internal static class Debug
    {
        [Conditional("DEBUG")]
        [AutomaticsInitializer(9999)]
        private static void Initialize()
        {
            Hooks.OnInitTerminal += Commands.Register;
            Harmony.CreateAndPatchAll(typeof(Patches), Automatics.GetHarmonyId("debug"));
        }
    }
}