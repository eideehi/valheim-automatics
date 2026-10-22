using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Automatics.Valheim
{
    /// <summary>
    ///     Reads the module hotkeys. A hotkey fires on its main key with every modifier it names
    ///     held, whatever other keys are held (moving, jumping), but not while a modifier key it
    ///     does not name is held, so that Left Shift + E does not also fire a binding of E alone.
    ///     BepInEx's <c>KeyboardShortcut.IsDown</c> and <c>IsPressed</c> refuse while any other key
    ///     is held. Each caller keeps its own input gates.
    /// </summary>
    internal static class HotkeyInput
    {
        // Every modifier key Unity has; the Command keys share their codes with Meta and Apple.
        private static readonly KeyCode[] ModifierKeys =
        {
            KeyCode.RightShift, KeyCode.LeftShift, KeyCode.RightControl, KeyCode.LeftControl,
            KeyCode.RightAlt, KeyCode.LeftAlt, KeyCode.RightCommand, KeyCode.LeftCommand,
            KeyCode.LeftWindows, KeyCode.RightWindows, KeyCode.AltGr
        };

        /// <summary>The hotkey's main key went down this frame, with its modifiers held.</summary>
        public static bool IsDown(KeyboardShortcut key)
        {
            var mainKey = key.MainKey;
            return mainKey != KeyCode.None && Input.GetKeyDown(mainKey) && ModifiersMatch(key, mainKey);
        }

        /// <summary>The hotkey's main key is held, with its modifiers held.</summary>
        public static bool IsHeld(KeyboardShortcut key)
        {
            var mainKey = key.MainKey;
            return mainKey != KeyCode.None && Input.GetKey(mainKey) && ModifiersMatch(key, mainKey);
        }

        // Reached only while the main key is down, so the frames without it allocate nothing. The
        // main key counts as named: a hotkey bound to Left Shift alone is not refused by Left Shift.
        private static bool ModifiersMatch(KeyboardShortcut key, KeyCode mainKey)
        {
            var modifiers = key.Modifiers.ToArray();
            foreach (var modifier in modifiers)
            {
                if (!Input.GetKey(modifier)) return false;
            }

            foreach (var modifier in ModifierKeys)
            {
                if (modifier != mainKey && Input.GetKey(modifier) && !modifiers.Contains(modifier)) return false;
            }

            return true;
        }
    }
}
