using static Automatics.Valheim.Creature;
using static Automatics.Valheim.Object;
using static Automatics.Valheim.Location;
using static Automatics.AutomaticMapPinning.OtherObject;
using System;
using System.Linq;
using Automatics.ModUtils;
using BepInEx.Configuration;
using StringList = Automatics.ModUtils.Configuration.StringList;

namespace Automatics.AutomaticMapPinning
{
    internal static class Config
    {
        private const string Section = "automatic_map_pinning";

        private static ConfigEntry<bool> _automaticMapPinningEnabled;
        private static ConfigEntry<int> _dynamicObjectSearchRange;
        private static ConfigEntry<int> _staticObjectSearchRange;
        private static ConfigEntry<int> _locationSearchRange;
        private static ConfigEntry<Animal> _allowPinningAnimal;
        private static ConfigEntry<Monster> _allowPinningMonster;
        private static ConfigEntry<Flora> _allowPinningFlora;
        private static ConfigEntry<MineralDeposit> _allowPinningVein;
        private static ConfigEntry<Spawner> _allowPinningSpawner;
        private static ConfigEntry<Etcetera> _allowPinningOther;
        private static ConfigEntry<Dungeon> _allowPinningDungeon;
        private static ConfigEntry<Spot> _allowPinningSpot;
        private static ConfigEntry<bool> _allowPinningShip;
        private static ConfigEntry<StringList> _allowPinningAnimalCustom;
        private static ConfigEntry<StringList> _allowPinningMonsterCustom;
        private static ConfigEntry<StringList> _allowPinningFloraCustom;
        private static ConfigEntry<StringList> _allowPinningVeinCustom;
        private static ConfigEntry<StringList> _allowPinningSpawnerCustom;
        private static ConfigEntry<bool> _ignoreTamedAnimals;
        private static ConfigEntry<float> _staticObjectSearchInterval;
        private static ConfigEntry<int> _floraPinMergeRange;
        private static ConfigEntry<bool> _inGroundVeinsNeedWishbone;
        private static ConfigEntry<KeyboardShortcut> _staticObjectSearchKey;

        public static bool AutomaticMapPinningEnabled => _automaticMapPinningEnabled.Value;
        public static int DynamicObjectSearchRange => _dynamicObjectSearchRange.Value;
        public static int StaticObjectSearchRange => _staticObjectSearchRange.Value;
        public static int LocationSearchRange => _locationSearchRange.Value;
        public static bool IsAllowPinning(Animal flag) => (_allowPinningAnimal.Value & flag) != 0;
        public static bool IsAllowPinning(Monster flag) => (_allowPinningMonster.Value & flag) != 0;
        public static bool IsAllowPinning(Flora flag) => (_allowPinningFlora.Value & flag) != 0;
        public static bool IsAllowPinning(MineralDeposit flag) => (_allowPinningVein.Value & flag) != 0;
        public static bool IsAllowPinning(Spawner flag) => (_allowPinningSpawner.Value & flag) != 0;
        public static bool IsAllowPinning(Etcetera flag) => (_allowPinningOther.Value & flag) != 0;
        public static bool IsAllowPinning(Dungeon flag) => (_allowPinningDungeon.Value & flag) != 0;
        public static bool IsAllowPinning(Spot flag) => (_allowPinningSpot.Value & flag) != 0;
        public static bool IsAllowPinningShip => _allowPinningShip.Value;

        private static bool CheckCustom(string iName, StringList list)
        {
            if (!list.Any()) return false;

            var dName = L10N.TranslateInternalNameOnly(iName);
            return list.Any(x =>
                L10N.IsInternalName(x)
                    ? iName.Equals(x, StringComparison.Ordinal)
                    : dName.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsCustomAnimal(string iName) => CheckCustom(iName, _allowPinningAnimalCustom.Value);

        public static bool IsCustomMonster(string iName) => CheckCustom(iName, _allowPinningMonsterCustom.Value);

        public static bool IsCustomFlora(string iName) => CheckCustom(iName, _allowPinningFloraCustom.Value);

        public static bool IsCustomVein(string iName) => CheckCustom(iName, _allowPinningVeinCustom.Value);

        public static bool IsCustomSpawner(string iName) => CheckCustom(iName, _allowPinningSpawnerCustom.Value);

        public static bool IgnoreTamedAnimals => _ignoreTamedAnimals.Value;
        public static float StaticObjectSearchInterval => _staticObjectSearchInterval.Value;
        public static int FloraPinMergeRange => _floraPinMergeRange.Value;
        public static bool InGroundVeinsNeedWishbone => _inGroundVeinsNeedWishbone.Value;
        public static KeyboardShortcut StaticObjectSearchKey => _staticObjectSearchKey.Value;

        public static void Initialize()
        {
            Configuration.ChangeSection(Section);
            _automaticMapPinningEnabled = Configuration.Bind("automatic_map_pinning_enabled", true);
            _dynamicObjectSearchRange = Configuration.Bind("dynamic_object_search_range", 64, (0, 256));
            _staticObjectSearchRange = Configuration.Bind("static_object_search_range", 16, (0, 256));
            _locationSearchRange = Configuration.Bind("location_search_range", 96, (0, 256));
            _allowPinningAnimal = Configuration.Bind("allow_pinning_animal", Animal.All);
            _allowPinningMonster = Configuration.Bind("allow_pinning_monster", Monster.All);
            _allowPinningFlora = Configuration.Bind("allow_pinning_flora", Flora.Raspberries | Flora.Mushroom | Flora.Blueberries | Flora.CarrotSeeds | Flora.Thistle | Flora.TurnipSeeds | Flora.Cloudberries);
            _allowPinningVein = Configuration.Bind("allow_pinning_vein", MineralDeposit.All ^ MineralDeposit.ObsidianDeposit);
            _allowPinningSpawner = Configuration.Bind("allow_pinning_spawner", Spawner.None);
            _allowPinningOther = Configuration.Bind("allow_pinning_other", Etcetera.WildBeehive);
            _allowPinningDungeon = Configuration.Bind("allow_pinning_dungeon", Dungeon.All);
            _allowPinningSpot = Configuration.Bind("allow_pinning_spot", Spot.All);
            _allowPinningShip = Configuration.Bind("allow_pinning_ship", true);
            _allowPinningAnimalCustom = Configuration.Bind("allow_pinning_animal_custom", new StringList());
            _allowPinningMonsterCustom = Configuration.Bind("allow_pinning_monster_custom", new StringList());
            _allowPinningFloraCustom = Configuration.Bind("allow_pinning_flora_custom", new StringList());
            _allowPinningVeinCustom = Configuration.Bind("allow_pinning_vein_custom", new StringList());
            _allowPinningSpawnerCustom = Configuration.Bind("allow_pinning_spawner_custom", new StringList());
            _ignoreTamedAnimals = Configuration.Bind("ignore_tamed_animals", true);
            _staticObjectSearchInterval = Configuration.Bind("static_object_search_interval", 0.25f, (0f, 8f));
            _floraPinMergeRange = Configuration.Bind("flora_pins_merge_range", 8, (0, 16));
            _inGroundVeinsNeedWishbone = Configuration.Bind("in_ground_veins_need_wishbone", true);
            _staticObjectSearchKey = Configuration.Bind("static_object_search_key", new KeyboardShortcut());

            _allowPinningAnimal.SettingChanged += OnDynamicObjectSettingChanged;
            _allowPinningMonster.SettingChanged += OnDynamicObjectSettingChanged;
            _allowPinningAnimalCustom.SettingChanged += OnDynamicObjectSettingChanged;
            _allowPinningMonsterCustom.SettingChanged += OnDynamicObjectSettingChanged;

            _allowPinningFlora.SettingChanged += OnStaticObjectSettingChanged;
            _allowPinningVein.SettingChanged += OnStaticObjectSettingChanged;
            _allowPinningSpawner.SettingChanged += OnStaticObjectSettingChanged;
            _allowPinningOther.SettingChanged += OnStaticObjectSettingChanged;
            _allowPinningFloraCustom.SettingChanged += OnStaticObjectSettingChanged;
            _allowPinningVeinCustom.SettingChanged += OnStaticObjectSettingChanged;
            _allowPinningSpawnerCustom.SettingChanged += OnStaticObjectSettingChanged;

            _allowPinningFlora.SettingChanged += OnFloraSettingChanged;
            _allowPinningFloraCustom.SettingChanged += OnFloraSettingChanged;
        }

        private static void OnDynamicObjectSettingChanged(object sender, EventArgs e)
        {
            DynamicMapPinning.ClearObjectCache();
        }

        private static void OnStaticObjectSettingChanged(object sender, EventArgs e)
        {
            StaticMapPinning.ClearObjectCache();
        }

        private static void OnFloraSettingChanged(object sender, EventArgs e)
        {
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                var flora = pickable.GetComponent<FloraObject>();
                if (flora == null && StaticMapPinning.IsFlora(pickable))
                    pickable.gameObject.AddComponent<FloraObject>();
                else if (flora != null && !StaticMapPinning.IsFlora(pickable))
                    UnityEngine.Object.Destroy(flora);
            }
        }
    }
}