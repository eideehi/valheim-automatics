using System.Diagnostics.CodeAnalysis;
using Automatics.ModUtils;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Automatics.Debug
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
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

        private static KeyboardShortcut _debug =
            new KeyboardShortcut(KeyCode.P, KeyCode.LeftShift, KeyCode.LeftAlt);

        [HarmonyPostfix, HarmonyPatch(typeof(Player), "Awake")]
        private static void PlayerAwakePostfix(Player __instance)
        {
            __instance.SetGodMode(true);
            __instance.SetGhostMode(true);
            if (!__instance.NoCostCheat())
                __instance.ToggleNoPlacementCost();
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

            if (_debug.IsDown())
            {
                // Add the process want to run during development
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(Player), "UseStamina")]
        private static bool PlayerUseStaminaPrefix(Player __instance, float v)
        {
            return !__instance.InGodMode();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Terminal), "Awake")]
        private static void TerminalAwakePostfix(Terminal __instance, bool ___m_cheat)
        {
            if (!___m_cheat)
            {
                Reflection.SetField(__instance, "m_cheat", true);
            }
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static void TerminalAwakePostfix(bool ___m_terminalInitialized)
        {
            if (!___m_terminalInitialized) return;

            Command.RegisterCommands();
        }
    }
}