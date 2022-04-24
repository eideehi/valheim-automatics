using Automatics.ModUtils;
using BepInEx.Configuration;

namespace Automatics.AutomaticMapPinning
{
    using static Valheim.Creature;
    using static Valheim.Object;
    using static Valheim.Location;
    using static ValheimObject;
    using StringList = Configuration.StringList;

    internal static class Config
    {
        private const string Section = "automatic_map_pinning";

        private static ConfigEntry<bool> _automaticMapPinningEnabled;
        private static ConfigEntry<int> _dynamicObjectSearchRange;
        private static ConfigEntry<int> _staticObjectSearchRange;
        private static ConfigEntry<int> _locationSearchRange;
        private static ConfigEntry<Animal> _allowPinningAnimal;
        private static ConfigEntry<StringList> _allowPinningAnimalCustom;
        private static ConfigEntry<Monster> _allowPinningMonster;
        private static ConfigEntry<StringList> _allowPinningMonsterCustom;
        private static ConfigEntry<Flora> _allowPinningFlora;
        private static ConfigEntry<StringList> _allowPinningFloraCustom;
        private static ConfigEntry<MineralDeposit> _allowPinningVein;
        private static ConfigEntry<StringList> _allowPinningVeinCustom;
        private static ConfigEntry<Spawner> _allowPinningSpawner;
        private static ConfigEntry<StringList> _allowPinningSpawnerCustom;
        private static ConfigEntry<Other> _allowPinningOther;
        private static ConfigEntry<StringList> _allowPinningOtherCustom;
        private static ConfigEntry<Dungeon> _allowPinningDungeon;
        private static ConfigEntry<StringList> _allowPinningDungeonCustom;
        private static ConfigEntry<Spot> _allowPinningSpot;
        private static ConfigEntry<StringList> _allowPinningSpotCustom;
        private static ConfigEntry<bool> _allowPinningShip;
        private static ConfigEntry<bool> _ignoreTamedAnimals;
        private static ConfigEntry<float> _staticObjectSearchInterval;
        private static ConfigEntry<int> _floraPinMergeRange;
        private static ConfigEntry<bool> _needToEquipWishboneForUndergroundDeposits;
        private static ConfigEntry<KeyboardShortcut> _staticObjectSearchKey;

        public static bool AutomaticMapPinningEnabled => _automaticMapPinningEnabled.Value;
        public static int DynamicObjectSearchRange => _dynamicObjectSearchRange.Value;
        public static int StaticObjectSearchRange => _staticObjectSearchRange.Value;
        public static int LocationSearchRange => _locationSearchRange.Value;
        public static Animal AllowPinningAnimal => _allowPinningAnimal.Value;
        public static StringList AllowPinningAnimalCustom => _allowPinningAnimalCustom.Value;
        public static Monster AllowPinningMonster => _allowPinningMonster.Value;
        public static StringList AllowPinningMonsterCustom => _allowPinningMonsterCustom.Value;
        public static Flora AllowPinningFlora => _allowPinningFlora.Value;
        public static StringList AllowPinningFloraCustom => _allowPinningFloraCustom.Value;
        public static MineralDeposit AllowPinningVein => _allowPinningVein.Value;
        public static StringList AllowPinningVeinCustom => _allowPinningVeinCustom.Value;
        public static Spawner AllowPinningSpawner => _allowPinningSpawner.Value;
        public static StringList AllowPinningSpawnerCustom => _allowPinningSpawnerCustom.Value;
        public static Other AllowPinningOther => _allowPinningOther.Value;
        public static StringList AllowPinningOtherCustom => _allowPinningOtherCustom.Value;
        public static Dungeon AllowPinningDungeon => _allowPinningDungeon.Value;
        public static StringList AllowPinningDungeonCustom => _allowPinningDungeonCustom.Value;
        public static Spot AllowPinningSpot => _allowPinningSpot.Value;
        public static StringList AllowPinningSpotCustom => _allowPinningSpotCustom.Value;
        public static bool IsAllowPinningShip => _allowPinningShip.Value;
        public static bool IgnoreTamedAnimals => _ignoreTamedAnimals.Value;
        public static float StaticObjectSearchInterval => _staticObjectSearchInterval.Value;
        public static int FloraPinMergeRange => _floraPinMergeRange.Value;
        public static bool NeedToEquipWishboneForUndergroundDeposits => _needToEquipWishboneForUndergroundDeposits.Value;
        public static KeyboardShortcut StaticObjectSearchKey => _staticObjectSearchKey.Value;

        public static void Initialize()
        {
            Configuration.ChangeSection(Section);
            _automaticMapPinningEnabled = Configuration.Bind("automatic_map_pinning_enabled", true);
            _dynamicObjectSearchRange = Configuration.Bind("dynamic_object_search_range", 64, (0, 256));
            _staticObjectSearchRange = Configuration.Bind("static_object_search_range", 16, (0, 256));
            _locationSearchRange = Configuration.Bind("location_search_range", 96, (0, 256));
            _allowPinningAnimal = Configuration.Bind("allow_pinning_animal", Animal.All);
            _allowPinningAnimalCustom = Configuration.Bind("allow_pinning_animal_custom", new StringList());
            _allowPinningMonster = Configuration.Bind("allow_pinning_monster", Monster.All);
            _allowPinningMonsterCustom = Configuration.Bind("allow_pinning_monster_custom", new StringList());
            _allowPinningFlora = Configuration.Bind("allow_pinning_flora", Flora.Raspberries | Flora.Mushroom | Flora.Blueberries | Flora.CarrotSeeds | Flora.Thistle | Flora.TurnipSeeds | Flora.Cloudberries);
            _allowPinningFloraCustom = Configuration.Bind("allow_pinning_flora_custom", new StringList());
            _allowPinningVein = Configuration.Bind("allow_pinning_deposit", MineralDeposit.All ^ MineralDeposit.ObsidianDeposit);
            _allowPinningVeinCustom = Configuration.Bind("allow_pinning_deposit_custom", new StringList());
            _allowPinningSpawner = Configuration.Bind("allow_pinning_spawner", Spawner.None);
            _allowPinningSpawnerCustom = Configuration.Bind("allow_pinning_spawner_custom", new StringList());
            _allowPinningOther = Configuration.Bind("allow_pinning_other", Other.WildBeehive);
            _allowPinningOtherCustom = Configuration.Bind("allow_pinning_other_custom", new StringList());
            _allowPinningDungeon = Configuration.Bind("allow_pinning_dungeon", Dungeon.All);
            _allowPinningDungeonCustom = Configuration.Bind("allow_pinning_dungeon_custom", new StringList());
            _allowPinningSpot = Configuration.Bind("allow_pinning_spot", Spot.All);
            _allowPinningSpotCustom = Configuration.Bind("allow_pinning_spot_custom", new StringList());
            _allowPinningShip = Configuration.Bind("allow_pinning_ship", true);
            _ignoreTamedAnimals = Configuration.Bind("ignore_tamed_animals", true);
            _staticObjectSearchInterval = Configuration.Bind("static_object_search_interval", 0.25f, (0f, 8f));
            _floraPinMergeRange = Configuration.Bind("flora_pins_merge_range", 8, (0, 16));
            _needToEquipWishboneForUndergroundDeposits = Configuration.Bind("need_to_equip_wishbone_for_underground_deposits", true);
            _staticObjectSearchKey = Configuration.Bind("static_object_search_key", new KeyboardShortcut());

            _allowPinningAnimal.SettingChanged += DynamicPinning.OnSettingChanged;
            _allowPinningMonster.SettingChanged += DynamicPinning.OnSettingChanged;
            _allowPinningAnimalCustom.SettingChanged += DynamicPinning.OnSettingChanged;
            _allowPinningMonsterCustom.SettingChanged += DynamicPinning.OnSettingChanged;
            _ignoreTamedAnimals.SettingChanged += DynamicPinning.OnSettingChanged;

            _allowPinningFlora.SettingChanged += StaticPinning.OnSettingChanged;
            _allowPinningVein.SettingChanged += StaticPinning.OnSettingChanged;
            _allowPinningSpawner.SettingChanged += StaticPinning.OnSettingChanged;
            _allowPinningOther.SettingChanged += StaticPinning.OnSettingChanged;
            _allowPinningFloraCustom.SettingChanged += StaticPinning.OnSettingChanged;
            _allowPinningVeinCustom.SettingChanged += StaticPinning.OnSettingChanged;
            _allowPinningSpawnerCustom.SettingChanged += StaticPinning.OnSettingChanged;

            _allowPinningFlora.SettingChanged += StaticPinning.OnFloraSettingChanged;
            _allowPinningFloraCustom.SettingChanged += StaticPinning.OnFloraSettingChanged;
            _floraPinMergeRange.SettingChanged += StaticPinning.OnFloraSettingChanged;
        }
    }
}