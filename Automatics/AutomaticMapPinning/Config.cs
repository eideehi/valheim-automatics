using static Automatics.ValheimCharacter;
using static Automatics.ValheimLocation;
using static Automatics.ValheimObject;
using BepInEx.Configuration;

namespace Automatics.AutomaticMapPinning
{
    internal static class Config
    {
        private const string Section = "automatic_map_pinning";

        private static ConfigEntry<bool> _automaticMapPinningEnabled;
        private static ConfigEntry<int> _dynamicObjectSearchRange;
        private static ConfigEntry<int> _staticObjectSearchRange;
        private static ConfigEntry<int> _locationSearchRange;
        private static ConfigEntry<Animal.Flag> _allowPinningAnimal;
        private static ConfigEntry<Monster.Flag> _allowPinningMonster;
        private static ConfigEntry<Flora.Flag> _allowPinningFlora;
        private static ConfigEntry<Vein.Flag> _allowPinningVein;
        private static ConfigEntry<Spawner.Flag> _allowPinningSpawner;
        private static ConfigEntry<Other.Flag> _allowPinningOther;
        private static ConfigEntry<Dungeon.Flag> _allowPinningDungeon;
        private static ConfigEntry<Spot.Flag> _allowPinningSpot;
        private static ConfigEntry<bool> _ignoreTamedAnimals;
        private static ConfigEntry<float> _staticObjectSearchInterval;
        private static ConfigEntry<int> _floraPinMergeRange;
        private static ConfigEntry<bool> _inGroundVeinsNeedWishbone;

        public static bool AutomaticMapPinningEnabled => _automaticMapPinningEnabled.Value;
        public static int DynamicObjectSearchRange => _dynamicObjectSearchRange.Value;
        public static int StaticObjectSearchRange => _staticObjectSearchRange.Value;
        public static int LocationSearchRange => _locationSearchRange.Value;
        public static bool IsAllowPinning(Animal.Flag flag) => (_allowPinningAnimal.Value & flag) != 0;
        public static bool IsAllowPinning(Monster.Flag flag) => (_allowPinningMonster.Value & flag) != 0;
        public static bool IsAllowPinning(Flora.Flag flag) => (_allowPinningFlora.Value & flag) != 0;
        public static bool IsAllowPinning(Vein.Flag flag) => (_allowPinningVein.Value & flag) != 0;
        public static bool IsAllowPinning(Spawner.Flag flag) => (_allowPinningSpawner.Value & flag) != 0;
        public static bool IsAllowPinning(Other.Flag flag) => (_allowPinningOther.Value & flag) != 0;
        public static bool IsAllowPinning(Dungeon.Flag flag) => (_allowPinningDungeon.Value & flag) != 0;
        public static bool IsAllowPinning(Spot.Flag flag) => (_allowPinningSpot.Value & flag) != 0;
        public static bool IgnoreTamedAnimals => _ignoreTamedAnimals.Value;
        public static float StaticObjectSearchInterval => _staticObjectSearchInterval.Value;
        public static int FloraPinMergeRange => _floraPinMergeRange.Value;
        public static bool InGroundVeinsNeedWishbone => _inGroundVeinsNeedWishbone.Value;

        public static void Initialize()
        {
            Configuration.ResetOrder();
            _automaticMapPinningEnabled = Configuration.Bind(Section, "automatic_map_pinning_enabled", true);
            _dynamicObjectSearchRange = Configuration.Bind(Section, "dynamic_object_search_range", 64, (0, 256));
            _staticObjectSearchRange = Configuration.Bind(Section, "static_object_search_range", 16, (0, 256));
            _locationSearchRange = Configuration.Bind(Section, "location_search_range", 96, (0, 256));
            _allowPinningAnimal = Configuration.Bind(Section, "allow_pinning_animal", Animal.Flag.All);
            _allowPinningMonster = Configuration.Bind(Section, "allow_pinning_monster", Monster.Flag.All);
            _allowPinningFlora = Configuration.Bind(Section, "allow_pinning_flora", Flora.Flag.All ^ Flora.Flag.Dandelion);
            _allowPinningVein = Configuration.Bind(Section, "allow_pinning_vein", Vein.Flag.All ^ Vein.Flag.Obsidian);
            _allowPinningSpawner = Configuration.Bind(Section, "allow_pinning_spawner", Spawner.Flag.None);
            _allowPinningOther = Configuration.Bind(Section, "allow_pinning_other", Other.Flag.WildBeehive);
            _allowPinningDungeon = Configuration.Bind(Section, "allow_pinning_dungeon", Dungeon.Flag.All);
            _allowPinningSpot = Configuration.Bind(Section, "allow_pinning_spot", Spot.Flag.All);
            _ignoreTamedAnimals = Configuration.Bind(Section, "ignore_tamed_animals", true);
            _staticObjectSearchInterval = Configuration.Bind(Section, "static_object_search_interval", 0.25f, (0f, 8f));
            _floraPinMergeRange = Configuration.Bind(Section, "flora_pins_merge_range", 8, (0, 16));
            _inGroundVeinsNeedWishbone = Configuration.Bind(Section, "in_ground_veins_need_wishbone", true);
        }
    }
}