using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class Map
    {
        private static readonly CustomIcon DefaultIcon;
        private static readonly List<CustomIcon> CustomIcons;

        private static Minimap.PinType _vanillaPinTypeLength;

        private static Minimap ValheimMap => Minimap.instance;

        static Map()
        {
            DefaultIcon = new CustomIcon
            {
                PinType = Minimap.PinType.Icon3,
                Options = new Options()
            };
            CustomIcons = new List<CustomIcon>();
        }

        private static List<Minimap.PinData> GetAllPins()
        {
            return Reflections.GetField<List<Minimap.PinData>>(ValheimMap, "m_pins");
        }

        private static void DestroyPinMarker(Minimap.PinData pinData)
        {
            Reflections.InvokeMethod(ValheimMap, "DestroyPinMarker", pinData);
        }

        public static Minimap.PinData GetPin(Predicate<Minimap.PinData> predicate)
        {
            return GetAllPins()
                .Where(x => x.m_uiElement && x.m_uiElement.gameObject.activeInHierarchy)
                .FirstOrDefault(predicate.Invoke);
        }

        public static Minimap.PinData GetClosestPin(Vector3 pos, float radius = 1f,
            Predicate<Minimap.PinData> predicate = null)
        {
            if (predicate == null)
                predicate = x => true;

            Minimap.PinData result = null;
            var minDistance = float.MaxValue;
            foreach (var pinData in GetAllPins().Where(x =>
                         x.m_uiElement && x.m_uiElement.gameObject.activeInHierarchy))
            {
                var distance = Utils.DistanceXZ(pos, pinData.m_pos);
                if (distance > radius || distance >= minDistance) continue;
                if (!predicate.Invoke(pinData)) continue;

                result = pinData;
                minDistance = distance;
            }

            return result;
        }

        public static bool HavePinInRange(Vector3 pos, float radius,
            Predicate<Minimap.PinData> predicate = null)
        {
            bool IsInRange(Minimap.PinData x) => Utils.DistanceXZ(x.m_pos, pos) <= radius;

            Predicate<Minimap.PinData> isValidPin;
            if (predicate == null)
                isValidPin = IsInRange;
            else
                isValidPin = x => IsInRange(x) && predicate(x);

            return GetPin(isValidPin) != null;
        }

        public static Minimap.PinData AddPin(Vector3 pos, string name, bool save, Target target)
        {
            var icon = GetCustomIcon(target);
            return AddPin(pos, icon.PinType, icon.Options.hideNameTag ? "" : name, save);
        }

        private static CustomIcon GetCustomIcon(Target target)
        {
            if (!CustomIcons.Any()) return DefaultIcon;

            var internalName = target.name;
            var displayName = Automatics.L10N.TranslateInternalName(internalName);
            var meta = target.metadata;
            return (from x in CustomIcons
                    where (L10N.IsInternalName(x.Target.name)
                              ? internalName.Equals(x.Target.name, StringComparison.Ordinal)
                              : displayName.IndexOf(x.Target.name,
                                  StringComparison.OrdinalIgnoreCase) >= 0) &&
                          (x.Target.metadata == null || IsMetaDataEquals(x.Target.metadata, meta))
                    orderby x.Target.metadata != null descending,
                        x.Target.metadata
                    select x)
                .DefaultIfEmpty(DefaultIcon)
                .FirstOrDefault();
        }

        private static bool IsMetaDataEquals(MetaData a, MetaData b)
        {
            return a != null && a.CompareTo(b) == 0;
        }

        private static string EscapePinName(string name)
        {
            return name.Replace("\n", "");
        }

        private static Minimap.PinData AddPin(Vector3 pos, Minimap.PinType type, string name,
            bool save)
        {
            if (name.StartsWith("@")) name = "$automatics_" + name.Substring(1);
            var pinData = ValheimMap.AddPin(pos, type, name, save, false);
            Automatics.Logger.Debug(() =>
                $"Add pin: [name: {EscapePinName(name)}, pos: {pinData.m_pos}, icon: {(int)type}]");
            return pinData;
        }

        public static Minimap.PinData RemovePin(Minimap.PinData pinData)
        {
            if (pinData.m_uiElement)
                DestroyPinMarker(pinData);

            if (!GetAllPins().Remove(pinData)) return null;

            Automatics.Logger.Debug(() =>
                $"Remove pin: [name: {EscapePinName(pinData.m_name)}, pos: {pinData.m_pos}, icon: {(int)pinData.m_type}]");
            return pinData;
        }

        public static Minimap.PinData RemovePin(Vector3 pos, float radius = 1f,
            Predicate<Minimap.PinData> predicate = null)
        {
            if (predicate == null)
                predicate = x => x.m_save;

            var pinData = GetClosestPin(pos, radius, predicate);
            return pinData != null ? RemovePin(pinData) : null;
        }

        [UsedImplicitly]
        public static float ResizeIcon(Minimap.PinData pinData, float originalSize)
        {
            var icon = CustomIcons.FirstOrDefault(x => x.PinType == pinData.m_type);
            if (icon == null) return originalSize;

            var options = icon.Options;
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
            var icon = CustomIcons.FirstOrDefault(x => x.PinType == pinData.m_type);
            return icon != null && icon.Options.hideNameTag;
        }

        public static void Initialize()
        {
            _vanillaPinTypeLength = (Minimap.PinType)Enum
                .GetValues(typeof(Minimap.PinType))
                .OfType<Minimap.PinType>().Count();

            CustomIcons.Clear();
            foreach (var directory in Automatics.GetAllResourcePath("Textures"))
                LoadCustomIcons(directory);

            LoadCustomIcons(Automatics.GetInjectedResourcePath("Textures"));
            RegisterCustomIcons();
        }

        private static void LoadCustomIcons(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;

            var file = Path.Combine(directory, "custom-map-icon.json");
            if (!File.Exists(file)) return;

            try
            {
                Automatics.Logger.Info($"Load custom icon data from {file}");

                var spriteLoader = new SpriteLoader();
                spriteLoader.SetDebugLogger(Automatics.Logger);

                var customIconPack = Json.Parse<List<CustomIconData>>(File.ReadAllText(file));
                foreach (var data in customIconPack)
                {
                    var info = data.sprite;
                    var path = Path.Combine(directory, info.file);
                    var sprite = spriteLoader.Load(path, info.width, info.height);
                    if (sprite == null) continue;

                    CustomIcons.Add(new CustomIcon
                    {
                        Target = data.target,
                        Sprite = sprite,
                        Options = data.options
                    });

                    Automatics.Logger.Info($"* Loaded custom icon data for {data.target.name}");
                }
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Failed to load custom icon data: {file}\n{e}");
            }
        }

        private static void RegisterCustomIcons()
        {
            if (!CustomIcons.Any()) return;

            var visibleIconTypes = Reflections.GetField<bool[]>(ValheimMap, "m_visibleIconTypes");
            var originalArraySize = visibleIconTypes.Length;
            var newVisibleIconTypes = new bool[originalArraySize + CustomIcons.Count];
            for (var i = 0; i < newVisibleIconTypes.Length; i++)
                newVisibleIconTypes[i] = i < originalArraySize && visibleIconTypes[i];
            Reflections.SetField(ValheimMap, "m_visibleIconTypes", newVisibleIconTypes);

            Automatics.Logger.Info(
                $"Minimap.m_visibleIconTypes Expanded: {originalArraySize} -> {newVisibleIconTypes.Length}");

            for (var j = 0; j < CustomIcons.Count; j++)
            {
                var icon = CustomIcons[j];
                var pinType = (Minimap.PinType)(originalArraySize + j);

                icon.PinType = pinType;
                ValheimMap.m_icons.Add(new Minimap.SpriteData
                {
                    m_name = pinType,
                    m_icon = icon.Sprite
                });

                Automatics.Logger.Info(
                    $"Register new sprite data: ({pinType}, {SpriteLoader.GetTextureFileName(icon.Sprite)})");
            }
        }

        private class CustomIcon
        {
            public Options Options;
            public Minimap.PinType PinType;
            public Sprite Sprite;
            public Target Target;
        }
    }

    [Serializable]
    public struct CustomIconData
    {
        public Target target;
        public SpriteInfo sprite;
        public Options options;
    }

    [Serializable]
    public struct Target
    {
        public string name;
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
    public struct SpriteInfo
    {
        public string file;
        public int width;
        public int height;
    }

    [Serializable]
    public struct Options
    {
        public bool hideNameTag;
        public float iconScaleLargeMap;
        public float iconScaleSmallMap;
    }
}