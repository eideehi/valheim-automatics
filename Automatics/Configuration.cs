using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace Automatics
{
    internal static class Configuration
    {
        private const int DefaultOrder = 1024;

        public static int Order { get; set; } = DefaultOrder;

        public static void ResetOrder() => Order = DefaultOrder;

        public static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue,
            AcceptableValueBase acceptableValue = null, Action<ConfigurationManagerAttributes> initializer = null)
        {
            var attributes = new ConfigurationManagerAttributes
            {
                Category = GetCategory(section),
                Order = --Order,
                DispName = GetName(key),
                CustomDrawer = GetCustomDrawer(typeof(T))
            };
            initializer?.Invoke(attributes);

            Automatics.ModLogger.LogDebug($"Bind config: [{AttributesToString(attributes)}]");
            return Automatics.ModConfig.Bind(section, key, defaultValue,
                new ConfigDescription(GetDescription(key), acceptableValue, attributes));
        }

        public static ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, (T, T) acceptableValue,
            Action<ConfigurationManagerAttributes> initializer = null) where T : IComparable
        {
            var (minValue, maxValue) = acceptableValue;
            return Bind(section, key, defaultValue, new AcceptableValueRange<T>(minValue, maxValue), initializer);
        }

        private static string GetCategory(string category) => L10N.Translate($"@config_{category}_category");
        private static string GetName(string name) => L10N.Translate($"@config_{name}_name");

        private static string GetDescription(string description) =>
            L10N.Translate($"@config_{description}_description");

        private static string AttributesToString(ConfigurationManagerAttributes attributes)
        {
            var result =
                new StringBuilder("category: ").Append(attributes.Category)
                    .Append(", name: ").Append(attributes.DispName)
                    .Append(", description: ").Append(attributes.Description)
                    .Append(", order: ").Append(attributes.Order);
            if (!attributes.Browsable ?? false) result.Append(", hidden");
            if (attributes.ReadOnly ?? false) result.Append(", readonly");
            if (attributes.IsAdvanced ?? false) result.Append(", advanced");
            return result.ToString();
        }

        private static Action<ConfigEntryBase> GetCustomDrawer(Type type)
        {
            if (type == typeof(bool)) return BoolCustomDrawer;
            if (type.IsEnum && type.GetCustomAttributes(typeof(FlagsAttribute), false).Any()) return FlagsCustomDrawer;
            return null;
        }

        private static void BoolCustomDrawer(ConfigEntryBase entry)
        {
            var @bool = (bool)entry.BoxedValue;
            var text = L10N.Translate($"@config_checkbox_label_{(@bool ? "true" : "false")}");
            var result = GUILayout.Toggle(@bool, text, GUILayout.ExpandWidth(true));
            if (result != @bool)
                entry.BoxedValue = result;
        }

        private static void FlagsCustomDrawer(ConfigEntryBase entry)
        {
            var guiWidth = Mathf.Min(Screen.width, 650);
            var maxWidth = guiWidth - Mathf.RoundToInt(guiWidth / 2.5f) - 115;

            var type = entry.SettingType;
            var currentValue = Convert.ToInt64(entry.BoxedValue);
            var validator = entry.Description.AcceptableValues;

            GUILayout.BeginVertical(GUILayout.MaxWidth(maxWidth));

            var lineWidth = 0;
            GUILayout.BeginHorizontal();
            foreach (var @enum in Enum.GetValues(type))
            {
                if (validator != null && !validator.IsValid(@enum)) continue;

                var value = Convert.ToInt64(@enum);
                if (value == 0) continue;

                var label = GetFlagsLabel(type, @enum);

                var width = Mathf.FloorToInt(GUI.skin.toggle.CalcSize(new GUIContent(label + "_")).x);
                lineWidth += width;
                if (lineWidth > maxWidth)
                {
                    GUILayout.EndHorizontal();
                    lineWidth = width;
                    GUILayout.BeginHorizontal();
                }

                GUI.changed = false;
                var @checked = GUILayout.Toggle((currentValue & value) == value, label, GUILayout.ExpandWidth(false));
                if (!GUI.changed) continue;

                var newValue = @checked ? currentValue | value : currentValue & ~value;
                entry.BoxedValue = Enum.ToObject(type, newValue);
            }

            GUILayout.EndHorizontal();
            GUI.changed = false;

            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
        }

        private static string GetFlagsLabel(Type type, object @object)
        {
            var member = type.GetMember(Enum.GetName(type, @object) ?? "").FirstOrDefault();
            var attribute = member?.GetCustomAttributes(typeof(DescriptionAttribute), false)
                .OfType<DescriptionAttribute>().FirstOrDefault();
            return attribute?.Description ?? @object.ToString();
        }

        public class AcceptableValueEnum<T> : AcceptableValueBase where T : Enum
        {
            private readonly IList<T> _values;

            public AcceptableValueEnum(params T[] values) : base(typeof(T))
            {
                _values = MakeValues(values);
            }

            private static IList<T> MakeValues(IReadOnlyCollection<T> values)
            {
                var enumerable = new List<T>(values.Count == 0 ? Enum.GetValues(typeof(T)).OfType<T>() : values);
                if (!typeof(T).GetCustomAttributes(typeof(FlagsAttribute), false).Any()) return enumerable;

                var set = new HashSet<long>();
                foreach (var value in enumerable.Select(@enum => Convert.ToInt64(@enum)))
                {
                    foreach (var other in set.ToArray())
                        set.Add(other | value);
                    set.Add(value);
                }

                return set.Select(x => Enum.ToObject(typeof(T), x)).Cast<T>().ToList();
            }

            public override object Clamp(object value) => IsValid(value) ? value : _values[0];

            public override bool IsValid(object value)
            {
                if (value is T @enum) return _values.Contains(@enum);
                if (!(value is IConvertible)) return false;

                var @long = Convert.ToInt64(value);
                return _values.Any(x => Convert.ToInt64(x) == @long);
            }

            public override string ToDescriptionString() =>
                "# Acceptable values: " + string.Join(", ",
                    (from x in _values where Enum.IsDefined(typeof(T), x) select Enum.GetName(typeof(T), x)).ToArray());
        }

        public class LocalizedDescriptionAttribute : DescriptionAttribute
        {
            private readonly string _key;

            public LocalizedDescriptionAttribute(string key) : base(key)
            {
                _key = key;
            }

            public override string Description => L10N.Translate(_key);
        }
    }
}