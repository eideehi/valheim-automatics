using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class Map
    {
        private static Dictionary<string, Sprite> _customIcons;
        private static Dictionary<string, Minimap.PinType> _nameByPinTypeMap;

        private static Minimap VMap => Minimap.instance;

        public static IEnumerable<Minimap.PinData> Pins => Reflection.GetField<List<Minimap.PinData>>(VMap, "m_pins");

        private static void LoadCustomIcons()
        {
            _customIcons = new Dictionary<string, Sprite>();

            var texturesDir = Path.Combine(Automatics.ModLocation, "Textures");
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

                _customIcons[L10N.TranslateInternalNameOnly(element.Key)] = sprite;
                Automatics.ModLogger.LogInfo($"* Loaded custom icon for {element.Key}");
            }
        }

        private static void RegisterCustomIcons()
        {
            _nameByPinTypeMap = new Dictionary<string, Minimap.PinType>();

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

                _nameByPinTypeMap[customIcon.Key] = pinType;
                VMap.m_icons.Add(new Minimap.SpriteData
                {
                    m_name = pinType,
                    m_icon = customIcon.Value
                });

                Automatics.ModLogger.LogInfo($"Register new sprite data: ({pinType}, {customIcon.Key})");

                j++;
            }
        }

        public static void Initialize()
        {
            LoadCustomIcons();
            RegisterCustomIcons();
        }

        public static Minimap.PinType GetCustomIcon(string name)
        {
            var localizedName = L10N.TranslateInternalNameOnly(name);
            return _nameByPinTypeMap.TryGetValue(localizedName, out var icon) ? icon : Minimap.PinType.Icon3;
        }

        public static bool HavePinInRange(Vector3 pos, float radius)
        {
            return Pins.Any(data => Utils.DistanceXZ(data.m_pos, pos) <= radius);
        }

        public static bool FindPin(Func<Minimap.PinData, bool> predicate, out Minimap.PinData data)
        {
            data = Pins.FirstOrDefault(predicate);
            return data != null;
        }

        public static bool FindPinInRange(Vector3 pos, float radius, out Minimap.PinData data)
        {
            return FindPin(x => Utils.DistanceXZ(x.m_pos, pos) <= radius, out data);
        }

        public static Minimap.PinData AddPin(Vector3 pos, Minimap.PinType type, string name, bool save)
        {
            var data = VMap.AddPin(pos, type, name, save, false);
            Log.Debug(() => $"Add pin: [name: {data.m_name}, pos: {data.m_pos}]");
            return data;
        }

        public static Minimap.PinData AddPin(Vector3 pos, string iconName, string pinName, bool save)
        {
            return AddPin(pos, GetCustomIcon(iconName), pinName, save);
        }

        public static void RemovePin(Minimap.PinData data)
        {
            VMap.RemovePin(data);
            Log.Debug(() => $"Remove pin: [name: {data.m_name}, pos: {data.m_pos}]");
        }

        public static void RemovePin(Vector3 pos, bool save = true)
        {
            var pin = Pins.FirstOrDefault(x => (save ? x.m_save : !x.m_save) && x.m_pos == pos);
            if (pin != null)
                RemovePin(pin);
        }
    }
}