using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Automatics.Debug
{
    [HarmonyPatch]
    internal static class Patches
    {
        private static KeyboardShortcut _toggleGodMode =
            new KeyboardShortcut(KeyCode.T, KeyCode.LeftShift, KeyCode.LeftAlt);

        private static KeyboardShortcut _toggleGhostMode =
            new KeyboardShortcut(KeyCode.Y, KeyCode.LeftShift, KeyCode.LeftAlt);

        private static KeyboardShortcut _toggleFlyMode =
            new KeyboardShortcut(KeyCode.U, KeyCode.LeftShift, KeyCode.LeftAlt);

        private static KeyboardShortcut _killAll =
            new KeyboardShortcut(KeyCode.K, KeyCode.LeftShift, KeyCode.LeftAlt);

        private static KeyboardShortcut _removeDrops =
            new KeyboardShortcut(KeyCode.L, KeyCode.LeftShift, KeyCode.LeftAlt);

        [HarmonyPostfix, HarmonyPatch(typeof(Player), "Awake")]
        private static void PlayerAwakePostfix(Player __instance)
        {
            Console.instance.TryRunCommand("devcommands");
            __instance.SetGodMode(true);
            __instance.SetGhostMode(true);
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Player), "Update")]
        private static void PlayerUpdatePostfix(Player __instance)
        {
            if (_toggleGodMode.IsDown())
            {
                __instance.SetGodMode(!__instance.InGodMode());
            }

            if (_toggleGhostMode.IsDown())
            {
                __instance.SetGhostMode(!__instance.InGhostMode());
            }

            if (_toggleFlyMode.IsDown())
            {
                Console.instance.TryRunCommand("fly");
            }

            if (_killAll.IsDown())
            {
                Console.instance.TryRunCommand("killall");
            }

            if (_removeDrops.IsDown())
            {
                Console.instance.TryRunCommand("removedrops");
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), "UseStamina")]
        private static bool PlayerUseStaminaPrefix(Player __instance, float v)
        {
            return !__instance.InGodMode();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), "ConsumeResources")]
        private static bool PlayerConsumeResourcesPrefix(Player __instance, Piece.Requirement[] requirements,
            int qualityLevel)
        {
            return !__instance.InGodMode();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Terminal), "Awake")]
        private static void TerminalAwakePostfix(Terminal __instance)
        {
            Command.RegisterCommands();
        }
    }
}