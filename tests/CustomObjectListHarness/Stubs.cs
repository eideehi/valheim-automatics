using System;
using System.Collections.Generic;
using ModUtils;

namespace BepInEx.Configuration
{
    public abstract class AcceptableValueBase
    {
    }

    public abstract class ConfigEntryBase
    {
        public object BoxedValue { get; set; }
    }

    // Mirrors BepInEx 5.4.23.5 TypeConverter.
    public class TypeConverter
    {
        public Func<object, Type, string> ConvertToString { get; set; }
        public Func<string, Type, object> ConvertToObject { get; set; }
    }

    // Mirrors BepInEx 5.4.23.5 TomlTypeConverter for the converters added at run time: a value is
    // written and read through the converter added for its type, and a second converter for the
    // same type is refused. The built-in converters are left out; no case here uses them.
    public static class TomlTypeConverter
    {
        private static readonly Dictionary<Type, TypeConverter> TypeConverters =
            new Dictionary<Type, TypeConverter>();

        public static string ConvertToString(object value, Type valueType)
        {
            return GetConverter(valueType).ConvertToString(value, valueType);
        }

        public static object ConvertToValue(string value, Type valueType)
        {
            return GetConverter(valueType).ConvertToObject(value, valueType);
        }

        public static bool AddConverter(Type type, TypeConverter converter)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (converter == null) throw new ArgumentNullException(nameof(converter));
            if (TypeConverters.ContainsKey(type)) return false;

            TypeConverters.Add(type, converter);
            return true;
        }

        private static TypeConverter GetConverter(Type valueType)
        {
            if (TypeConverters.TryGetValue(valueType, out var converter)) return converter;
            throw new InvalidOperationException("Cannot convert to type " + valueType.Name);
        }
    }
}

namespace ModUtils
{
    public abstract class ConfigurationGuiCustomEditor
    {
    }

    public static class ConfigurationCustomDrawer
    {
        public delegate bool IsMatchConfig(Type type, BepInEx.Configuration.AcceptableValueBase acceptableValue);

        public delegate Action<BepInEx.Configuration.ConfigEntryBase> CustomDrawerSupplier();

        public static void Register(IsMatchConfig matcher, CustomDrawerSupplier supplier)
        {
        }
    }

    public static class ConfigurationGui
    {
        public static void RegisterEditor(ConfigurationCustomDrawer.IsMatchConfig matcher,
            Func<ConfigurationGuiCustomEditor> factory)
        {
        }
    }
}

namespace UnityEngine
{
    // Only what the custom object list drawer names, so that its source file compiles here. The
    // drawer is never drawn by these checks.
    public struct Vector2
    {
        public float x;
        public float y;
    }

    public static class Mathf
    {
        public static int Min(int a, int b)
        {
            return Math.Min(a, b);
        }

        public static float Max(params float[] values)
        {
            var max = values.Length == 0 ? 0f : values[0];
            foreach (var value in values) max = Math.Max(max, value);
            return max;
        }

        public static int RoundToInt(float value)
        {
            return (int)Math.Round(value);
        }

        public static int FloorToInt(float value)
        {
            return (int)Math.Floor(value);
        }
    }

    public static class Screen
    {
        public static int width => 0;
    }

    public sealed class GUIContent
    {
        public GUIContent(string text)
        {
        }

        public GUIContent(string text, string tooltip)
        {
        }
    }

    public sealed class GUIStyle
    {
        public Vector2 CalcSize(GUIContent content)
        {
            return new Vector2();
        }
    }

    public sealed class GUISkin
    {
        public GUIStyle label { get; } = new GUIStyle();
        public GUIStyle button { get; } = new GUIStyle();
    }

    public static class GUI
    {
        public static GUISkin skin { get; } = new GUISkin();
    }

    public sealed class GUILayoutOption
    {
    }

    public static class GUILayout
    {
        public static GUILayoutOption Width(float width)
        {
            return new GUILayoutOption();
        }

        public static GUILayoutOption MaxWidth(float maxWidth)
        {
            return new GUILayoutOption();
        }

        public static GUILayoutOption ExpandWidth(bool expand)
        {
            return new GUILayoutOption();
        }

        public static void BeginVertical(params GUILayoutOption[] options)
        {
        }

        public static void EndVertical()
        {
        }

        public static void BeginHorizontal(params GUILayoutOption[] options)
        {
        }

        public static void EndHorizontal()
        {
        }

        public static void FlexibleSpace()
        {
        }

        public static void Label(string text, params GUILayoutOption[] options)
        {
        }

        public static void Label(GUIContent content, params GUILayoutOption[] options)
        {
        }

        public static string TextField(string text, params GUILayoutOption[] options)
        {
            return text;
        }

        public static bool Button(string text, params GUILayoutOption[] options)
        {
            return false;
        }
    }
}

namespace Automatics
{
    internal static class Automatics
    {
        public static TestLogger Logger { get; } = new TestLogger();
        public static TestL10N L10N { get; } = new TestL10N();
    }

    internal sealed class TestLogger
    {
        public List<string> Warnings { get; } = new List<string>();

        public void Debug(string message)
        {
        }

        public void Warning(string message)
        {
            Warnings.Add(message);
        }

        public void Error(string message)
        {
        }
    }

    internal sealed class TestL10N
    {
        public string Translate(string word)
        {
            return word;
        }

        public string TranslateInternalName(string internalName)
        {
            return internalName;
        }

        public string LocalizeTextOnly(string text, params object[] args)
        {
            return text;
        }
    }
}

namespace Automatics.Valheim
{
    internal sealed class ObjectElementListEditor : ConfigurationGuiCustomEditor
    {
    }
}
