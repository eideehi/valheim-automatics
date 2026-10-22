using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityEngine
{
    // The key codes these checks use, with Unity's values. The modifier keys are all of Unity's,
    // with every alias Unity gives them.
    public enum KeyCode
    {
        None = 0,
        Tab = 9,
        Space = 32,
        A = 97,
        D = 100,
        E = 101,
        S = 115,
        W = 119,
        F8 = 289,
        RightShift = 303,
        LeftShift = 304,
        RightControl = 305,
        LeftControl = 306,
        RightAlt = 307,
        LeftAlt = 308,
        LeftMeta = 310,
        LeftCommand = 310,
        LeftApple = 310,
        LeftWindows = 311,
        RightMeta = 309,
        RightCommand = 309,
        RightApple = 309,
        RightWindows = 312,
        AltGr = 313,
        Mouse0 = 323,
        Mouse1 = 324,
        JoystickButton0 = 330
    }

    // Unity's keyboard state for one frame, as each case sets it: the keys that went down this
    // frame and the keys held, which include the keys that went down.
    public static class Input
    {
        internal static readonly HashSet<KeyCode> Down = new HashSet<KeyCode>();
        internal static readonly HashSet<KeyCode> Held = new HashSet<KeyCode>();

        public static bool GetKey(KeyCode key)
        {
            return Held.Contains(key);
        }

        public static bool GetKeyDown(KeyCode key)
        {
            return Down.Contains(key);
        }
    }
}

namespace BepInEx.Configuration
{
    // Mirrors the key list of BepInEx 5.4.23.5 KeyboardShortcut: the main key first, then the
    // modifiers without repeats or the main key, in key code order; no main key leaves an empty
    // shortcut. Its own IsDown and IsPressed are left out; the helper replaces them.
    public struct KeyboardShortcut
    {
        public static readonly KeyboardShortcut Empty = default(KeyboardShortcut);

        private readonly KeyCode[] _allKeys;

        public KeyboardShortcut(KeyCode mainKey, params KeyCode[] modifiers)
        {
            if (mainKey == KeyCode.None && modifiers.Any())
                throw new ArgumentException("Can't set mainKey to KeyCode.None if there are any modifiers");

            _allKeys = mainKey == KeyCode.None
                ? new KeyCode[1]
                : new[] { mainKey }.Concat(modifiers.Distinct().Where(x => x != mainKey).OrderBy(x => (int)x))
                    .ToArray();
        }

        public KeyCode MainKey => _allKeys != null && _allKeys.Length != 0 ? _allKeys[0] : KeyCode.None;

        public IEnumerable<KeyCode> Modifiers => _allKeys?.Skip(1) ?? Enumerable.Empty<KeyCode>();

        public override string ToString()
        {
            return MainKey == KeyCode.None ? "Not set" : string.Join(" + ", _allKeys);
        }
    }
}
