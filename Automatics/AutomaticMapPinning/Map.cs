using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Automatics.ModUtils;
using LitJson;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    using PinData = Minimap.PinData;
    using PinType = Minimap.PinType;

    internal static class Map
    {
        private static readonly List<CustomIcon> CustomIcons;

        static Map()
        {
            CustomIcons = new List<CustomIcon>();
        }

        private static Minimap ValheimMap => Minimap.instance;

        private static IEnumerable<PinData> GetAllPins() => Reflection.GetField<List<PinData>>(ValheimMap, "m_pins");

        public static void Initialize()
        {
            Deprecated.Map.Initialize();
            LoadCustomIcons();
            RegisterCustomIcons();
        }

        private static void LoadCustomIcons()
        {
            CustomIcons.Clear();

            var texturesDir = Path.Combine(Automatics.ModLocation, "Textures");
            var customIconsJson = Path.Combine(texturesDir, "custom-map-icon.json");
            if (!File.Exists(customIconsJson)) return;

            try
            {
                Automatics.ModLogger.LogInfo($"Load custom icon data from {customIconsJson}");

                var reader = new JsonReader(File.ReadAllText(customIconsJson))
                {
                    AllowComments = true,
                };

                foreach (var data in JsonMapper.ToObject<List<CustomIconData>>(reader))
                {
                    var sprite = Image.CreateSprite(texturesDir, data.sprite);
                    if (sprite == null) continue;

                    CustomIcons.Add(new CustomIcon
                    {
                        Target = data.target,
                        Sprite = sprite,
                    });

                    Automatics.ModLogger.LogInfo($"* Loaded custom icon data for {data.target.name}");
                }
            }
            catch (Exception e)
            {
                Automatics.ModLogger.LogError($"Failed to load custom icon data: {customIconsJson}\n{e}");
            }
        }

        private static void RegisterCustomIcons()
        {
            if (!CustomIcons.Any()) return;

            var visibleIconTypes = Reflection.GetField<bool[]>(ValheimMap, "m_visibleIconTypes");
            var originalArraySize = visibleIconTypes.Length;
            var newVisibleIconTypes = new bool[originalArraySize + CustomIcons.Count];
            for (var i = 0; i < newVisibleIconTypes.Length; i++)
                newVisibleIconTypes[i] = i < originalArraySize && visibleIconTypes[i];
            Reflection.SetField(ValheimMap, "m_visibleIconTypes", newVisibleIconTypes);

            Automatics.ModLogger.LogInfo(
                $"Minimap.m_visibleIconTypes Expanded: {originalArraySize} -> {newVisibleIconTypes.Length}");

            for (var j = 0; j < CustomIcons.Count; j++)
            {
                var icon = CustomIcons[j];
                var pinType = (PinType)(originalArraySize + j);

                icon.PinType = pinType;
                ValheimMap.m_icons.Add(new Minimap.SpriteData
                {
                    m_name = pinType,
                    m_icon = icon.Sprite
                });

                Automatics.ModLogger.LogInfo(
                    $"Register new sprite data: ({pinType}, {Image.GetTextureFileName(icon.Sprite)})");
            }
        }

        private static PinType GetCustomIcon(Target target)
        {
            var type = GetCustomIconInternal(target);
            return type != PinType.Icon3 ? type : Deprecated.Map.GetCustomIcon(target.name);
        }

        private static PinType GetCustomIconInternal(Target target)
        {
            if (!CustomIcons.Any()) return PinType.Icon3;

            var iName = target.name;
            var dName = L10N.TranslateInternalNameOnly(iName);
            var meta = target.metadata;
            return (from x in CustomIcons
                    where (L10N.IsInternalName(x.Target.name)
                              ? iName.Equals(x.Target.name, StringComparison.Ordinal)
                              : dName.IndexOf(x.Target.name, StringComparison.OrdinalIgnoreCase) >= 0) &&
                          (x.Target.metadata == null || IsMetaDataEquals(x.Target.metadata, meta))
                    orderby x.Target.metadata != null descending,
                        x.Target.metadata
                    select x.PinType)
                .DefaultIfEmpty(PinType.Icon3)
                .FirstOrDefault();
        }

        private static bool IsMetaDataEquals(MetaData a, MetaData b)
        {
            if (a == null || b == null) return false;
            return a.level == b.level;
        }

        public static bool HavePinInRange(Vector3 pos, float radius)
        {
            return GetAllPins().Any(data => Utils.DistanceXZ(data.m_pos, pos) <= radius);
        }

        public static bool FindPin(Func<PinData, bool> predicate, out PinData data)
        {
            data = GetAllPins().FirstOrDefault(predicate);
            return data != null;
        }

        public static bool FindPinInRange(Vector3 pos, float radius, out PinData data)
        {
            return FindPin(x => Utils.DistanceXZ(x.m_pos, pos) <= radius, out data);
        }

        public static PinData AddPin(Vector3 pos, PinType type, string name, bool save)
        {
            var data = ValheimMap.AddPin(pos, type, name, save, false);
            Log.Debug(() => $"Add pin: [name: {data.m_name}, pos: {data.m_pos}]");
            return data;
        }

        public static PinData AddPin(Vector3 pos, string pinName, bool save, Target target)
        {
            return AddPin(pos, GetCustomIcon(target), pinName, save);
        }

        public static void RemovePin(PinData data)
        {
            ValheimMap.RemovePin(data);
            Log.Debug(() => $"Remove pin: [name: {data.m_name}, pos: {data.m_pos}]");
        }

        public static void RemovePin(Vector3 pos, bool save = true)
        {
            var pin = GetAllPins().FirstOrDefault(x => (save ? x.m_save : !x.m_save) && x.m_pos == pos);
            if (pin != null)
                RemovePin(pin);
        }

        private class CustomIcon
        {
            public Target Target;
            public PinType PinType;
            public Sprite Sprite;
        }
    }

    [Serializable]
    public struct CustomIconData
    {
        public Target target;
        public SpriteInfo sprite;
    }

    [Serializable]
    public struct Target
    {
        public string name;
        public MetaData metadata;
    }

    [Serializable]
    public class MetaData : IComparer<MetaData>
    {
        public int level;

        public int Compare(MetaData x, MetaData y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return x.level.CompareTo(y.level);
        }
    }
}