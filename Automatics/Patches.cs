using System.Diagnostics.CodeAnalysis;
using HarmonyLib;

namespace Automatics
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(Container), "Awake")]
        private static void ContainerAwakePostfix(Container __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<ContainerCache>();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Pickable), "Awake")]
        private static void PickableAwakePostfix(Pickable __instance, ZNetView ___m_nview)
        {
            if (___m_nview.GetZDO() != null)
                __instance.gameObject.AddComponent<PickableCache>();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static void TerminalAwakePostfix(bool ___m_terminalInitialized)
        {
            if (!___m_terminalInitialized) return;

            Automatics.OnInitTerminal?.Invoke();
        }
    }
}