using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class IconPack
    {
        private static readonly List<Icon> Icons;
        private static readonly List<Minimap.PinType> OverridablePins;

        private static Minimap.PinType _vanillaPinTypeLength;

        static IconPack()
        {
            Icons = new List<Icon>();
            OverridablePins = new List<Minimap.PinType>
            {
                Minimap.PinType.Icon0,
                Minimap.PinType.Icon1,
                Minimap.PinType.Icon2,
                Minimap.PinType.Icon3,
                Minimap.PinType.Icon4,
            };
        }

        [UsedImplicitly]
        public static float ResizeIcon(Minimap.PinData pinData, float originalSize)
        {
            var icon = Icons.FirstOrDefault(x => x.PinType == pinData.m_type);
            if (icon == null) return originalSize;

            var options = icon.Options;
            if (options == null) return originalSize;

            switch (Minimap.instance.m_mode)
            {
                case Minimap.MapMode.Large:
                    return options.iconScaleLargeMap > 0
                        ? originalSize * options.iconScaleLargeMap
                        : originalSize;
                case Minimap.MapMode.Small:
                    return options.iconScaleSmallMap > 0
                        ? originalSize * options.iconScaleSmallMap
                        : originalSize;
                case Minimap.MapMode.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return originalSize;
        }

        [UsedImplicitly]
        public static bool IsNameTagHidden(Minimap.PinData pinData)
        {
            if (pinData.m_type <= _vanillaPinTypeLength) return false;
            var icon = Icons.FirstOrDefault(x => x.PinType == pinData.m_type);
            return icon?.Options != null && icon.Options.hideNameTag;
        }

        public static Minimap.PinType GetPinType(Target target)
        {
            if (!Icons.Any()) return Minimap.PinType.Icon3;

            var internalName = target.name;
            var displayName = Automatics.L10N.TranslateInternalName(internalName);
            var prefabName = target.prefabName;
            var meta = target.metadata;
            return (from x in Icons
                    let data = x.Target
                    where (string.IsNullOrEmpty(data.name) ||
                           (L10N.IsInternalName(data.name)
                               ? IsNameMatch(internalName, data.name, true)
                               : IsNameMatch(displayName, data.name, false))) &&
                          (string.IsNullOrEmpty(data.prefabName) ||
                           IsNameMatch(prefabName, data.prefabName, true)) &&
                          (data.metadata == null || IsMetaDataEquals(data.metadata, meta))
                    orderby data.metadata != null descending,
                        data.metadata
                    select GetPinType(x))
                .DefaultIfEmpty(Minimap.PinType.Icon3)
                .FirstOrDefault();
        }

        private static Minimap.PinType GetPinType(Icon icon)
        {
            if (icon.PinType > Minimap.PinType.EventArea || icon.Options == null)
                return icon.PinType;
            var overrideIcon = (Minimap.PinType)icon.Options.defaultIconOverride;
            return OverridablePins.Contains(overrideIcon) ? overrideIcon : icon.PinType;
        }

        private static bool IsNameMatch(string name, string pattern, bool exactMatch)
        {
            if (pattern.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
            {
                return Regex.IsMatch(name, pattern.Substring(2));
            }

            return exactMatch
                ? name.Equals(pattern, StringComparison.Ordinal)
                : name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMetaDataEquals(MetaData a, MetaData b)
        {
            return a != null && a.CompareTo(b) == 0;
        }

        public static void Initialize()
        {
            _vanillaPinTypeLength = (Minimap.PinType)Enum
                .GetValues(typeof(Minimap.PinType))
                .OfType<Minimap.PinType>().Count();

            Icons.Clear();
            var sprites = new List<(Icon, Sprite)>();
            foreach (var directory in Automatics.GetAllResourcePath("Textures"))
                LoadIcons(directory, sprites);

            LoadIcons(Automatics.GetInjectedResourcePath("Textures"), sprites);
            RegisterIcons(sprites);
        }

        private static void LoadIcons(string directory, List<(Icon, Sprite)> sprites)
        {
            if (string.IsNullOrEmpty(directory)) return;

            var file = Path.Combine(directory, "custom-map-icon.json");
            if (!File.Exists(file)) return;

            try
            {
                Automatics.Logger.Info($"Load icon data from {file}");

                var spriteLoader = new SpriteLoader();
                spriteLoader.SetDebugLogger(Automatics.Logger);

                var iconPack = Json.Parse<List<IconPackEntry>>(File.ReadAllText(file));
                foreach (var entry in iconPack)
                {
                    var icon = new Icon
                    {
                        Target = entry.target,
                        Options = entry.options,
                    };

                    if (string.IsNullOrEmpty(entry.target.name) &&
                        string.IsNullOrEmpty(entry.target.prefabName))
                    {
                        Automatics.Logger.Warning(
                            "Both target.name and target.prefabName cannot be omitted.");
                        continue;
                    }

                    if (entry.sprite != null)
                    {
                        var data = entry.sprite;
                        var path = Path.Combine(directory, data.file);
                        var sprite = spriteLoader.Load(path, data.width, data.height);
                        if (sprite == null) continue;
                        sprites.Add((icon, sprite));
                    }
                    else
                    {
                        var options = entry.options;
                        if (options == null)
                        {
                            Automatics.Logger.Warning("Both sprite and options cannot be omitted.");
                            continue;
                        }

                        if (options.defaultIconOverride == -1)
                        {
                            Automatics.Logger.Warning(
                                "If sprite is omitted, options.defaultIconOverride must be set.");
                            continue;
                        }

                        if (!OverridablePins.Contains((Minimap.PinType)options.defaultIconOverride))
                        {
                            Automatics.Logger.Warning(
                                $"{options.defaultIconOverride} is an icon that does not support override.");
                            continue;
                        }

                        if (options.hideNameTag)
                        {
                            Automatics.Logger.Warning(
                                "If sprite is omitted, options.hideNameTag cannot be set.");
                            continue;
                        }
                    }

                    Icons.Add(icon);

                    Automatics.Logger.Info(() =>
                    {
                        var name = entry.target.name;
                        if (string.IsNullOrEmpty(name))
                            name = entry.target.prefabName;
                        return $"* Loaded icon data for {name}";
                    });
                }
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Failed to load icon data: {file}\n{e}");
            }
        }

        private static void RegisterIcons(List<(Icon, Sprite)> sprites)
        {
            if (!sprites.Any()) return;
            var map = Minimap.instance;

            var visibleIconTypes = Reflections.GetField<bool[]>(map, "m_visibleIconTypes");
            var originalArraySize = visibleIconTypes.Length;
            var newVisibleIconTypes = new bool[originalArraySize + sprites.Count];
            for (var i = 0; i < newVisibleIconTypes.Length; i++)
                newVisibleIconTypes[i] = i < originalArraySize && visibleIconTypes[i];
            Reflections.SetField(map, "m_visibleIconTypes", newVisibleIconTypes);

            Automatics.Logger.Info(
                $"Minimap.m_visibleIconTypes Expanded: {originalArraySize} -> {newVisibleIconTypes.Length}");

            for (var j = 0; j < sprites.Count; j++)
            {
                var (icon, sprite) = sprites[j];
                var pinType = (Minimap.PinType)(originalArraySize + j);

                icon.PinType = pinType;
                map.m_icons.Add(new Minimap.SpriteData
                {
                    m_name = pinType,
                    m_icon = sprite
                });

                Automatics.Logger.Info(
                    $"Register new sprite data: ({pinType}, {SpriteLoader.GetTextureFileName(sprite)})");
            }
        }

        private class Icon
        {
            public Target Target;
            public Minimap.PinType PinType = Minimap.PinType.Icon3;
            public Options Options;
        }
    }

    [Serializable]
    public struct IconPackEntry
    {
        public Target target;
        public SpriteInfo sprite;
        public Options options;
    }

    [Serializable]
    public struct Target
    {
        public string name;
        public string prefabName;
        public MetaData metadata;
    }

    [Serializable]
    public class MetaData : IComparable<MetaData>
    {
        public int level = -1;

        public int CompareTo(MetaData other)
        {
            if (ReferenceEquals(this, other)) return 0;
            if (other == null) return 1;
            return level.CompareTo(other.level);
        }
    }

    [Serializable]
    public class SpriteInfo
    {
        public string file;
        public int width;
        public int height;
    }

    [Serializable]
    public class Options
    {
        public int defaultIconOverride = -1;
        public bool hideNameTag;
        public float iconScaleLargeMap;
        public float iconScaleSmallMap;
    }
}