using System.Diagnostics.CodeAnalysis;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.Debug
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [HarmonyPatch]
    internal static class Patches
    {
        private static KeyboardShortcut _toggleGodMode =
            new KeyboardShortcut(KeyCode.I, KeyCode.LeftShift, KeyCode.LeftAlt);

        private static KeyboardShortcut _toggleGhostMode =
            new KeyboardShortcut(KeyCode.O, KeyCode.LeftShift, KeyCode.LeftAlt);

        private static KeyboardShortcut _debug =
            new KeyboardShortcut(KeyCode.P, KeyCode.LeftShift, KeyCode.LeftAlt);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "Awake")]
        private static void Player_Awake_Postfix(Player __instance)
        {
            Player.m_debugMode = true;
            __instance.SetGodMode(true);
            __instance.SetGhostMode(true);
            if (!__instance.NoCostCheat())
                __instance.ToggleNoPlacementCost();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "Update")]
        private static void Player_Update_Postfix(Player __instance)
        {
            if (_toggleGodMode.IsDown()) __instance.SetGodMode(!__instance.InGodMode());

            if (_toggleGhostMode.IsDown()) __instance.SetGhostMode(!__instance.InGhostMode());

            if (_debug.IsDown())
            {
                // Add the process want to run during development
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player), "UseStamina")]
        private static bool Player_UseStamina_Prefix(Player __instance, float v)
        {
            return !__instance.InGodMode();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Terminal), "Awake")]
        private static void Terminal_Awake_Postfix(Terminal __instance, bool ___m_cheat)
        {
            if (!___m_cheat) Reflections.SetField(__instance, "m_cheat", true);
        }
    }
}