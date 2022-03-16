using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticMapPinning
{
    internal static class Map
    {
        public static IEnumerable<Minimap.PinData> Pins =>
            Reflection.GetField<List<Minimap.PinData>>(Minimap.instance, "m_pins");

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
            var data = Minimap.instance.AddPin(pos, type, name, save, false);
            Log.Debug(() => $"Add pin: [name: {data.m_name}, pos: {data.m_pos}]");
            return data;
        }

        public static Minimap.PinData AddPin(Vector3 pos, string name, bool save)
        {
            return AddPin(pos, Minimap.PinType.Icon3, name, save);
        }

        public static void RemovePin(Minimap.PinData data)
        {
            Minimap.instance.RemovePin(data);
            Log.Debug(() => $"Remove pin: [name: {data.m_name}, pos: {data.m_pos}]");
        }

        public static void RemovePin(Vector3 pos, bool save = true)
        {
            var pin = Pins.FirstOrDefault(x => save ? x.m_save : !x.m_save && x.m_pos == pos);
            if (pin != null)
                RemovePin(pin);
        }
    }
}