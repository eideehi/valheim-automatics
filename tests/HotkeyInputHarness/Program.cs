using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using BepInEx.Configuration;
using UnityEngine;

namespace Automatics
{
    /// <summary>
    ///     Offline checks for how module hotkeys are read from the keyboard. The project compiles
    ///     <c>Automatics/Valheim/HotkeyInput.cs</c> directly; Stubs.cs stands in for Unity's key
    ///     codes and keyboard state, set by each case, and for the key list of BepInEx's
    ///     <c>KeyboardShortcut</c>. Under the game's default bindings Left Shift runs, Left Control
    ///     crouches and W moves forward.
    /// </summary>
    internal static class Program
    {
        // Every modifier key Unity has, written out here rather than taken from the helper.
        private static readonly KeyCode[] ModifierKeys =
        {
            KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.LeftCommand, KeyCode.RightCommand,
            KeyCode.LeftWindows, KeyCode.RightWindows, KeyCode.AltGr
        };

        private static readonly KeyboardShortcut KeyAlone = new KeyboardShortcut(KeyCode.F8);
        private static readonly KeyboardShortcut ShiftE = new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift);
        private static readonly KeyboardShortcut PlainE = new KeyboardShortcut(KeyCode.E);

        private static readonly KeyboardShortcut ControlAltF8 =
            new KeyboardShortcut(KeyCode.F8, KeyCode.LeftControl, KeyCode.LeftAlt);

        private static readonly KeyboardShortcut ShiftAlone = new KeyboardShortcut(KeyCode.LeftShift);

        private static readonly KeyboardShortcut ShiftWithControl =
            new KeyboardShortcut(KeyCode.LeftShift, KeyCode.LeftControl);

        private static int _failures;

        private static int Main()
        {
            Run("a hotkey fires when its main key goes down with every modifier it names held",
                FiresWithItsModifiers);
            Run("a hotkey does not fire while a modifier it names is up", NeedsEveryNamedModifier);
            Run("a pressed hotkey fires only in the frame its main key goes down", PressNeedsTheKeyToGoDown);
            Run("a hotkey fires while movement keys and other keys that are not modifiers are held",
                IgnoresKeysThatAreNotModifiers);
            Run("a hotkey does not fire while a modifier key it does not name is held",
                RefusesAnUnnamedModifier);
            Run("a chord does not also fire the binding of its main key alone", ChordDoesNotFireThePlainKey);
            Run("with the default bindings a single-key hotkey does not fire while Left Shift or Left Ctrl is held",
                SingleKeyRefusesRunAndCrouch);
            Run("the default pickup-all hotkey, Left Shift + E, fires while running forward",
                ShiftEFiresWhileRunning);
            Run("a hotkey whose main key is a modifier fires on that key, also while moving",
                ModifierMainKeyFires);
            Run("a hotkey whose main key is a modifier does not fire while another modifier key is held",
                ModifierMainKeyRefusesAnotherModifier);
            Run("a hotkey whose main key is a modifier fires with the modifiers it names held",
                ModifierMainKeyWithNamedModifier);
            Run("an unbound hotkey never fires", UnboundNeverFires);
            Run("a held hotkey counts while its keys stay down, whatever other keys that are not modifiers are held",
                HeldCountsWhileKeysStayDown);
            Run("a held hotkey does not count while its main key or a named modifier is up, or another modifier key is held",
                HeldNeedsTheSameKeys);
            Run("every keyboard state: a hotkey fires exactly when its main key is down, every modifier it names is held and no other modifier key is",
                EveryKeyboardState);

            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"ok: {name}");
            }
            catch (Exception e)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(e.Message);
            }
        }

        // --- Pressed reads -----------------------------------------------------------------

        private static void FiresWithItsModifiers()
        {
            AssertDown(true, KeyAlone, new[] { KeyCode.F8 });
            AssertDown(true, ShiftE, new[] { KeyCode.E }, KeyCode.LeftShift);
            AssertDown(true, ControlAltF8, new[] { KeyCode.F8 }, KeyCode.LeftControl, KeyCode.LeftAlt);
            // The modifiers may go down in the same frame as the main key.
            AssertDown(true, ShiftE, new[] { KeyCode.E, KeyCode.LeftShift });
        }

        private static void NeedsEveryNamedModifier()
        {
            AssertDown(false, ShiftE, new[] { KeyCode.E });
            AssertDown(false, ControlAltF8, new[] { KeyCode.F8 }, KeyCode.LeftControl);
            AssertDown(false, ControlAltF8, new[] { KeyCode.F8 }, KeyCode.LeftAlt);
            // The key on the other side is another key.
            AssertDown(false, ShiftE, new[] { KeyCode.E }, KeyCode.RightShift);
        }

        private static void PressNeedsTheKeyToGoDown()
        {
            AssertDown(false, KeyAlone, new KeyCode[0], KeyCode.F8);
            AssertDown(false, ShiftE, new KeyCode[0], KeyCode.E, KeyCode.LeftShift);
            // A modifier going down while the main key is held does not press the hotkey.
            AssertDown(false, ShiftE, new[] { KeyCode.LeftShift }, KeyCode.E);
        }

        private static void IgnoresKeysThatAreNotModifiers()
        {
            AssertDown(true, KeyAlone, new[] { KeyCode.F8 }, KeyCode.W);
            AssertDown(true, KeyAlone, new[] { KeyCode.F8 }, KeyCode.W, KeyCode.D, KeyCode.Space);
            AssertDown(true, KeyAlone, new[] { KeyCode.F8 }, KeyCode.Mouse0, KeyCode.Mouse1);
            AssertDown(true, KeyAlone, new[] { KeyCode.F8 }, KeyCode.JoystickButton0, KeyCode.Tab);
            AssertDown(true, ShiftE, new[] { KeyCode.E }, KeyCode.LeftShift, KeyCode.W, KeyCode.A);
            AssertDown(true, ControlAltF8, new[] { KeyCode.F8 }, KeyCode.LeftControl, KeyCode.LeftAlt,
                KeyCode.S, KeyCode.Space);
            // Another key going down in the same frame does not stop it either.
            AssertDown(true, KeyAlone, new[] { KeyCode.F8, KeyCode.Space });
        }

        private static void RefusesAnUnnamedModifier()
        {
            foreach (var modifier in ModifierKeys)
            {
                AssertDown(false, KeyAlone, new[] { KeyCode.F8 }, modifier);
                if (modifier != KeyCode.LeftShift)
                    AssertDown(false, ShiftE, new[] { KeyCode.E }, KeyCode.LeftShift, modifier);
                if (modifier != KeyCode.LeftControl && modifier != KeyCode.LeftAlt)
                    AssertDown(false, ControlAltF8, new[] { KeyCode.F8 }, KeyCode.LeftControl,
                        KeyCode.LeftAlt, modifier);
            }

            // Also when the extra modifier goes down in the same frame.
            AssertDown(false, KeyAlone, new[] { KeyCode.F8, KeyCode.LeftControl });
        }

        private static void ChordDoesNotFireThePlainKey()
        {
            AssertDown(false, PlainE, new[] { KeyCode.E }, KeyCode.LeftShift);
            AssertDown(true, ShiftE, new[] { KeyCode.E }, KeyCode.LeftShift);
            AssertDown(true, PlainE, new[] { KeyCode.E });
        }

        private static void SingleKeyRefusesRunAndCrouch()
        {
            AssertDown(false, KeyAlone, new[] { KeyCode.F8 }, KeyCode.LeftShift, KeyCode.W);
            AssertDown(false, KeyAlone, new[] { KeyCode.F8 }, KeyCode.LeftControl);
            AssertDown(false, KeyAlone, new[] { KeyCode.F8 }, KeyCode.LeftControl, KeyCode.W);
        }

        private static void ShiftEFiresWhileRunning()
        {
            AssertDown(true, ShiftE, new[] { KeyCode.E }, KeyCode.LeftShift, KeyCode.W);
            AssertHeld(true, ShiftE, KeyCode.E, KeyCode.LeftShift, KeyCode.W);
        }

        private static void ModifierMainKeyFires()
        {
            AssertDown(true, ShiftAlone, new[] { KeyCode.LeftShift });
            AssertDown(true, ShiftAlone, new[] { KeyCode.LeftShift }, KeyCode.W, KeyCode.Mouse0);
            AssertHeld(true, ShiftAlone, KeyCode.LeftShift);
            AssertHeld(true, ShiftAlone, KeyCode.LeftShift, KeyCode.W, KeyCode.Mouse0);
        }

        private static void ModifierMainKeyRefusesAnotherModifier()
        {
            foreach (var modifier in ModifierKeys.Where(x => x != KeyCode.LeftShift))
            {
                AssertDown(false, ShiftAlone, new[] { KeyCode.LeftShift }, modifier);
                AssertHeld(false, ShiftAlone, KeyCode.LeftShift, modifier);
            }
        }

        private static void ModifierMainKeyWithNamedModifier()
        {
            AssertDown(true, ShiftWithControl, new[] { KeyCode.LeftShift }, KeyCode.LeftControl);
            AssertDown(true, ShiftWithControl, new[] { KeyCode.LeftShift }, KeyCode.LeftControl, KeyCode.W);
            AssertDown(false, ShiftWithControl, new[] { KeyCode.LeftShift });
            AssertDown(false, ShiftWithControl, new[] { KeyCode.LeftShift }, KeyCode.LeftControl, KeyCode.LeftAlt);
            // Holding the modifier and pressing nothing else is not the hotkey.
            AssertDown(false, ShiftWithControl, new[] { KeyCode.LeftControl });
        }

        private static void UnboundNeverFires()
        {
            var everything = Enum.GetValues(typeof(KeyCode)).Cast<KeyCode>().ToArray();
            foreach (var key in new[] { new KeyboardShortcut(), KeyboardShortcut.Empty, new KeyboardShortcut(KeyCode.None) })
            {
                AssertDown(false, key, everything);
                AssertHeld(false, key, everything);
                AssertDown(false, key, new KeyCode[0]);
                AssertHeld(false, key);
            }
        }

        // --- Held reads --------------------------------------------------------------------

        private static void HeldCountsWhileKeysStayDown()
        {
            AssertHeld(true, KeyAlone, KeyCode.F8);
            AssertHeld(true, KeyAlone, KeyCode.F8, KeyCode.W, KeyCode.Space);
            AssertHeld(true, ShiftE, KeyCode.E, KeyCode.LeftShift);
            AssertHeld(true, ShiftE, KeyCode.E, KeyCode.LeftShift, KeyCode.W, KeyCode.Mouse0);
            // The frame the main key goes down counts as held too.
            Set(new[] { KeyCode.E }, KeyCode.LeftShift, KeyCode.W);
            AssertTrue(HotkeyInput.IsHeld(ShiftE), "A held read should count the frame its main key goes down.");
        }

        private static void HeldNeedsTheSameKeys()
        {
            AssertHeld(false, ShiftE, KeyCode.LeftShift);
            AssertHeld(false, ShiftE, KeyCode.E);
            AssertHeld(false, ShiftE, KeyCode.E, KeyCode.RightShift);
            AssertHeld(false, ShiftE, KeyCode.E, KeyCode.LeftShift, KeyCode.LeftControl);
            AssertHeld(false, KeyAlone, KeyCode.F8, KeyCode.LeftShift, KeyCode.W);
            AssertHeld(false, PlainE, KeyCode.E, KeyCode.LeftShift);
        }

        // --- Every state -------------------------------------------------------------------

        // Every set of held keys from the pool below, with nothing, the main key, or every held key
        // going down in that frame, for bindings of a key alone, chords and a modifier as the main
        // key, against the rule written out here.
        private static void EveryKeyboardState()
        {
            var pool = new[]
            {
                KeyCode.W, KeyCode.Space, KeyCode.Mouse0, KeyCode.E, KeyCode.F8, KeyCode.LeftShift,
                KeyCode.RightShift, KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.AltGr, KeyCode.LeftWindows
            };
            var bindings = new[] { KeyAlone, PlainE, ShiftE, ControlAltF8, ShiftAlone, ShiftWithControl };

            var checkedStates = 0;
            for (var mask = 0; mask < 1 << pool.Length; mask++)
            {
                var held = pool.Where((x, i) => (mask & (1 << i)) != 0).ToArray();
                foreach (var binding in bindings)
                {
                    var downs = new[]
                    {
                        new KeyCode[0],
                        held.Where(x => x == binding.MainKey).ToArray(),
                        held
                    };
                    foreach (var down in downs)
                    {
                        Set(down, held);
                        var expectedDown = Expected(binding, down, held, true);
                        var expectedHeld = Expected(binding, down, held, false);
                        if (HotkeyInput.IsDown(binding) != expectedDown)
                            throw new Exception(
                                $"Pressed read of {binding} should be {expectedDown} with {Describe(down, held)}.");
                        if (HotkeyInput.IsHeld(binding) != expectedHeld)
                            throw new Exception(
                                $"Held read of {binding} should be {expectedHeld} with {Describe(down, held)}.");
                        checkedStates++;
                    }
                }
            }

            AssertTrue(checkedStates == (1 << pool.Length) * bindings.Length * 3,
                $"Expected every state to be checked, checked {checkedStates}.");
        }

        private static bool Expected(KeyboardShortcut binding, KeyCode[] down, KeyCode[] held, bool pressed)
        {
            var mainKey = binding.MainKey;
            if (mainKey == KeyCode.None) return false;
            if (!(pressed ? down : held).Contains(mainKey)) return false;
            var named = binding.Modifiers.Concat(new[] { mainKey }).ToArray();
            return binding.Modifiers.All(held.Contains) &&
                   held.Where(ModifierKeys.Contains).All(named.Contains);
        }

        // --- Keyboard and assertions -------------------------------------------------------

        // The keys in down go down this frame; they and the keys in held are held.
        private static void Set(KeyCode[] down, params KeyCode[] held)
        {
            Input.Down.Clear();
            Input.Held.Clear();
            foreach (var key in down)
            {
                Input.Down.Add(key);
                Input.Held.Add(key);
            }

            foreach (var key in held) Input.Held.Add(key);
        }

        private static string Describe(KeyCode[] down, KeyCode[] held)
        {
            return $"down [{string.Join(", ", down)}], held [{string.Join(", ", held.Union(down))}]";
        }

        private static void AssertDown(bool expected, KeyboardShortcut key, KeyCode[] down, params KeyCode[] held)
        {
            Set(down, held);
            var actual = HotkeyInput.IsDown(key);
            if (actual != expected)
                throw new Exception(
                    $"Pressed read of {key} should be {expected} with {Describe(down, held)}, got {actual}.");
        }

        private static void AssertHeld(bool expected, KeyboardShortcut key, params KeyCode[] held)
        {
            Set(new KeyCode[0], held);
            var actual = HotkeyInput.IsHeld(key);
            if (actual != expected)
                throw new Exception(
                    $"Held read of {key} should be {expected} with {Describe(new KeyCode[0], held)}, got {actual}.");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
