using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Automatics.Valheim;

namespace Automatics.AutomaticMapping
{
    internal enum PinCategory
    {
        Animal,
        Monster,
        Flora,
        Mineral,
        Spawner,
        Dungeon,
        Spot,
        Portal,
        Vehicle,
        Other
    }

    internal static class PinCategories
    {
        internal static readonly PinCategory[] Categories =
            (PinCategory[])Enum.GetValues(typeof(PinCategory));

        private static readonly ValheimObject[] Registries =
        {
            ValheimObject.Animal, ValheimObject.Monster, ValheimObject.Flora,
            ValheimObject.Mineral, ValheimObject.Spawner, ValheimObject.Dungeon,
            ValheimObject.Spot, MappingObject.Vehicle, MappingObject.Other
        };

        private static readonly PinCategory[] RegistryCategories =
        {
            PinCategory.Animal, PinCategory.Monster, PinCategory.Flora,
            PinCategory.Mineral, PinCategory.Spawner, PinCategory.Dungeon,
            PinCategory.Spot, PinCategory.Vehicle, PinCategory.Other
        };

        private static readonly Dictionary<string, PinCategory?> Labels =
            new Dictionary<string, PinCategory?>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PinCategory?> SavedNames =
            new Dictionary<string, PinCategory?>(StringComparer.Ordinal);
        private static readonly List<FloraLabel> FloraLabels = new List<FloraLabel>();
        private static bool _labelsBuilt;

        internal static bool TryClassify(Minimap.PinData pin, out PinCategory category)
        {
            if (DynamicObjectMapping.TryGetPinCategory(pin, out category)) return true;
            if (StaticObjectMapping.TryGetPinKind(pin, out var kind))
            {
                switch (kind)
                {
                    case PinKind.Flora: category = PinCategory.Flora; break;
                    case PinKind.Mineral: category = PinCategory.Mineral; break;
                    case PinKind.Spawner: category = PinCategory.Spawner; break;
                    case PinKind.Dungeon: category = PinCategory.Dungeon; break;
                    case PinKind.Spot: category = PinCategory.Spot; break;
                    case PinKind.Portal: category = PinCategory.Portal; break;
                    default: category = PinCategory.Other; break;
                }
                return true;
            }

            // Saved pins lose their live association. A name that belongs to several
            // categories, or no registry, has no category and stays visible.
            category = default;
            if (!pin.m_save || string.IsNullOrEmpty(pin.m_name)) return false;
            if (!_labelsBuilt) BuildLabels();
            if (!SavedNames.TryGetValue(pin.m_name, out var match))
            {
                var found = Labels.TryGetValue(pin.m_name, out match);
                foreach (var flora in FloraLabels)
                {
                    var countMatch = flora.Pattern.Match(pin.m_name);
                    if (!countMatch.Success ||
                        !int.TryParse(countMatch.Groups[1].Value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out var count) || count <= 1 ||
                        Map.StoredPinName(StaticObjectMapping.ComputeFloraPinName(flora.Label, count)) !=
                        pin.m_name) continue;
                    if (!found) match = PinCategory.Flora;
                    else if (match != PinCategory.Flora) match = null;
                    found = true;
                }
                SavedNames[pin.m_name] = match;
            }
            if (!match.HasValue) return false;
            category = match.Value;
            return true;
        }

        internal static void OnRegistryChanged(ValheimObject registry)
        {
            Cleanup();
            if (Module.Running) Map.RefreshPins();
        }

        internal static void Cleanup()
        {
            Labels.Clear();
            SavedNames.Clear();
            FloraLabels.Clear();
            _labelsBuilt = false;
        }

        private static void BuildLabels()
        {
            for (var i = 0; i < Registries.Length; i++)
                foreach (var element in Registries[i].GetAllElements())
                {
                    var name = Map.StoredPinName(element.label);
                    var category = RegistryCategories[i];
                    if (Labels.TryGetValue(name, out var previous))
                    {
                        if (previous != category) Labels[name] = null;
                    }
                    else Labels.Add(name, category);

                    if (category != PinCategory.Flora) continue;
                    var countToken = int.MaxValue.ToString(CultureInfo.InvariantCulture);
                    var sample = Map.StoredPinName(
                        StaticObjectMapping.ComputeFloraPinName(element.label, int.MaxValue));
                    var offset = sample.IndexOf(countToken, StringComparison.Ordinal);
                    if (offset < 0) continue;
                    FloraLabels.Add(new FloraLabel
                    {
                        Label = element.label,
                        Pattern = new Regex("^" + Regex.Escape(sample.Substring(0, offset)) +
                            "([0-9]+)" + Regex.Escape(sample.Substring(offset + countToken.Length)) + "$")
                    });
                }
            _labelsBuilt = true;
        }

        private sealed class FloraLabel
        {
            internal string Label;
            internal Regex Pattern;
        }
    }
}
