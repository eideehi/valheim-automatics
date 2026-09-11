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

        // The minimap the custom icons were last registered in, by instance id, so that the
        // destroyed minimap of an earlier world is not kept alive here.
        private static int _attachedMinimapId;

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

        // Invoked from the Minimap.UpdatePins transpiler twice per pin while the map is
        // shown, so this is a per-pin, per-frame hot path. Bail before any work when the
        // module is stopped or no icon pack is loaded (the common case) and look the icon up
        // with a plain loop to avoid the capturing-lambda closure + enumerator that
        // FirstOrDefault allocates. Stopped, the game's own size is kept.
        [UsedImplicitly]
        public static float ResizeIcon(Minimap.PinData pinData, float originalSize)
        {
            if (!Module.Running) return originalSize;
            if (Icons.Count == 0) return originalSize;

            var icon = FindIcon(pinData.m_type);
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
                default:
                    // None, or any future MapMode: never throw inside the transpiled
                    // UpdatePins hot path; an exception there breaks map rendering.
                    return originalSize;
            }
        }

        [UsedImplicitly]
        public static bool IsNameTagHidden(Minimap.PinData pinData)
        {
            // Called from the transpiled AddPin: stopped, every pin keeps its name tag.
            if (!Module.Running) return false;
            if (Icons.Count == 0) return false;
            if (pinData.m_type < _vanillaPinTypeLength) return false;
            var icon = FindIcon(pinData.m_type);
            return icon?.Options != null && icon.Options.hideNameTag;
        }

        private static Icon FindIcon(Minimap.PinType pinType)
        {
            for (var i = 0; i < Icons.Count; i++)
            {
                var icon = Icons[i];
                if (icon.PinType == pinType) return icon;
            }

            return null;
        }

        public static Minimap.PinType GetPinType(Target target)
        {
            if (Icons.Count == 0) return Minimap.PinType.Icon3;

            var internalName = target.name;
            var displayName = Automatics.L10N.TranslateInternalName(internalName);
            var prefabName = target.prefabName;
            var meta = target.metadata;

            // Plain loop tracking the best match by the same precedence the old
            // orderby encoded (metadata-bearing before metadata-null; among
            // metadata-bearing, lowest level via MetaData.CompareTo; first-in-list
            // wins on ties), without the per-call closure/iterator/OrderBy buffer.
            Icon best = null;
            for (var i = 0; i < Icons.Count; i++)
            {
                var data = Icons[i].Target;

                var nameMatch = string.IsNullOrEmpty(data.name) ||
                                (L10N.IsInternalName(data.name)
                                    ? IsNameMatch(internalName, data.name, true)
                                    : IsNameMatch(displayName, data.name, false));
                if (!nameMatch) continue;

                if (!(string.IsNullOrEmpty(data.prefabName) ||
                      IsNameMatch(prefabName, data.prefabName, true)))
                    continue;

                if (!(data.metadata == null || IsMetaDataEquals(data.metadata, meta)))
                    continue;

                if (best == null)
                {
                    best = Icons[i];
                    continue;
                }

                var bestMeta = best.Target.metadata;
                var curMeta = data.metadata;
                if (curMeta != null && bestMeta == null)
                    best = Icons[i];
                else if (curMeta != null && bestMeta != null && curMeta.CompareTo(bestMeta) < 0)
                    best = Icons[i];
            }

            return best == null ? Minimap.PinType.Icon3 : GetPinType(best);
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
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(pattern))
                return false;

            if (pattern.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Regex.IsMatch(name, pattern.Substring(2));
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            return exactMatch
                ? name.Equals(pattern, StringComparison.Ordinal)
                : name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMetaDataEquals(MetaData a, MetaData b)
        {
            return a != null && a.CompareTo(b) == 0;
        }

        private static bool ValidateRegexTarget(Target target, out string message)
        {
            if (!ValidateRegexPattern(target.name, "target.name", out message))
                return false;

            return ValidateRegexPattern(target.prefabName, "target.prefabName", out message);
        }

        private static bool ValidateRegexPattern(string pattern, string fieldName, out string message)
        {
            message = "";
            if (string.IsNullOrEmpty(pattern) ||
                !pattern.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                _ = new Regex(pattern.Substring(2));
                return true;
            }
            catch (ArgumentException e)
            {
                message = $"Invalid custom icon regex in {fieldName} `{pattern}`: {e.Message}";
                return false;
            }
        }

        /// <summary>
        ///     Registers the custom icons in <paramref name="map" />, once per minimap (R7.4). The
        ///     Minimap.Start postfix calls it for a new minimap, and turning the module on calls it
        ///     for the one that exists. A minimap that already has the icons is left alone:
        ///     registering again would extend its icon tables a second time. A new minimap - the
        ///     next world - reloads the icon files and registers them, as every minimap start
        ///     always has.
        ///     Nothing undoes a registration when the module is turned off. The game's own pin
        ///     update looks up each pin's icon by its type without a bound check, so a saved pin
        ///     with a custom icon would break the whole map if the icon went away.
        ///     The minimap is marked before the icons are registered, not after: a registration
        ///     that throws part way has already extended the tables, and trying it again would
        ///     extend them a second time.
        /// </summary>
        public static void EnsureAttached(Minimap map)
        {
            if (!map || map.GetInstanceID() == _attachedMinimapId) return;

            _attachedMinimapId = map.GetInstanceID();
            RegisterIcons(map, LoadResources());
        }

        private static List<(Icon, Sprite)> LoadResources()
        {
            _vanillaPinTypeLength = (Minimap.PinType)Enum
                .GetValues(typeof(Minimap.PinType))
                .OfType<Minimap.PinType>().Count();

            Icons.Clear();
            var sprites = new List<(Icon, Sprite)>();
            foreach (var directory in Automatics.GetAllResourcePath("Textures"))
                LoadIcons(directory, sprites);

            return sprites;
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

                    if (!ValidateRegexTarget(entry.target, out var message))
                    {
                        Automatics.Logger.Warning(message);
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

        private static void RegisterIcons(Minimap map, List<(Icon, Sprite)> sprites)
        {
            if (!sprites.Any()) return;

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
