using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Deprecated
    {
        public static class Map
        {
            private static List<IconInfo> _customIcons;

            private static Minimap VMap => Minimap.instance;

            public static void Initialize()
            {
                _customIcons = new List<IconInfo>();
                LoadCustomIcons(Automatics.GetDefaultResourcePath("Textures"));
                LoadCustomIcons(Automatics.GetInjectedResourcePath("Textures"));
                RegisterCustomIcons();
            }

            private static void LoadCustomIcons(string texturesDir)
            {
                if (string.IsNullOrEmpty(texturesDir)) return;

                var customIconsCsv = Path.Combine(texturesDir, "CustomMapIcon.csv");
                if (!File.Exists(customIconsCsv)) return;

                var spriteData = new Dictionary<string, SpriteInfo>();
                try
                {
                    Automatics.ModLogger.LogInfo($"Load custom icon data from {customIconsCsv}");
                    foreach (var line in File.ReadLines(customIconsCsv).Skip(1))
                    {
                        var values = Csv.ParseLine(line);
                        if (values.Count < 4) continue;

                        spriteData[values[0]] = new SpriteInfo
                        {
                            file = values[1],
                            width = int.TryParse(values[2], out var value) ? value : 0,
                            height = int.TryParse(values[3], out value) ? value : 0
                        };
                    }
                }
                catch (Exception e)
                {
                    Automatics.ModLogger.LogError($"Failed to load custom icon data: {customIconsCsv}\n{e}");
                    return;
                }

                Automatics.ModLogger.LogInfo($"Load custom icons from {texturesDir}");
                foreach (var element in spriteData)
                {
                    var sprite = Image.CreateSprite(texturesDir, element.Value);
                    if (sprite == null) continue;

                    _customIcons.Add(new IconInfo
                    {
                        Name = element.Key,
                        IsInternalName = L10N.IsInternalName(element.Key),
                        Sprite = sprite,
                    });
                    Automatics.ModLogger.LogInfo($"* Loaded custom icon for {element.Key}");
                }
            }

            private static void RegisterCustomIcons()
            {
                if (!_customIcons.Any()) return;

                var iconTypes = Reflection.GetField<bool[]>(VMap, "m_visibleIconTypes");
                var iconTypeCount = iconTypes.Length;
                var newIconTypes = new bool[iconTypeCount + _customIcons.Count];
                for (var i = 0; i < newIconTypes.Length; i++)
                    newIconTypes[i] = i < iconTypeCount && iconTypes[i];
                Reflection.SetField(VMap, "m_visibleIconTypes", newIconTypes);
                Automatics.ModLogger.LogInfo(
                    $"Minimap.m_visibleIconTypes Expanded: {iconTypeCount} -> {newIconTypes.Length}");

                var j = 0;
                foreach (var customIcon in _customIcons)
                {
                    var pinType = (Minimap.PinType)(iconTypeCount + j);

                    customIcon.PinType = pinType;
                    VMap.m_icons.Add(new Minimap.SpriteData
                    {
                        m_name = pinType,
                        m_icon = customIcon.Sprite
                    });

                    Automatics.ModLogger.LogInfo($"Register new sprite data: ({pinType}, {customIcon.Name})");

                    j++;
                }
            }

            public static Minimap.PinType GetCustomIcon(string iName)
            {
                if (!_customIcons.Any()) return Minimap.PinType.Icon3;

                var dName = L10N.TranslateInternalNameOnly(iName);
                return (from x in _customIcons
                        where x.IsInternalName
                            ? iName.Equals(x.Name, StringComparison.Ordinal)
                            : dName.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0
                        select x.PinType)
                    .DefaultIfEmpty(Minimap.PinType.Icon3)
                    .FirstOrDefault();
            }

            private class IconInfo
            {
                public string Name;
                public bool IsInternalName;
                public Minimap.PinType PinType;
                public Sprite Sprite;
            }
        }
    }
}