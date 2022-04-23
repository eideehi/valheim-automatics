using System;
using System.Collections.Generic;
using Automatics.ModUtils;
using JetBrains.Annotations;

namespace Automatics.Valheim
{
    using Description = Configuration.LocalizedDescriptionAttribute;

    public static class Creature
    {
        private static readonly Dictionary<string, Animal> Animals;
        private static readonly Dictionary<Animal, string> AnimalNames;
        private static readonly Dictionary<string, Monster> Monsters;
        private static readonly Dictionary<Monster, string> MonsterNames;

        static Creature()
        {
            Animals = new Dictionary<string, Animal>
            {
                { Name.Boar, Animal.Boar },
                { Name.Deer, Animal.Deer },
                { Name.Wolf, Animal.Wolf },
                { Name.Lox, Animal.Lox },
                { Name.Bird, Animal.Bird },
                { "Seagal", Animal.Bird },
                { "Crow", Animal.Bird },
                { Name.Fish, Animal.Fish },
                { "$animal_fish1", Animal.Fish },
                { "$animal_fish2", Animal.Fish },
                { "$animal_fish3", Animal.Fish },
            };

            AnimalNames = new Dictionary<Animal, string>
            {
                { Animal.Boar, Name.Boar },
                { Animal.Deer, Name.Deer },
                { Animal.Wolf, Name.Wolf },
                { Animal.Lox, Name.Lox },
                { Animal.Bird, Name.Bird },
                { Animal.Fish, Name.Fish },
            };

            Monsters = new Dictionary<string, Monster>
            {
                { Name.Greyling, Monster.Greyling },
                { Name.Neck, Monster.Neck },
                { Name.Ghost, Monster.Ghost },
                { Name.Greydwarf, Monster.Greydwarf },
                { Name.GreydwarfBrute, Monster.GreydwarfBrute },
                { Name.GreydwarfShaman, Monster.GreydwarfShaman },
                { Name.RancidRemains, Monster.RancidRemains },
                { Name.Skeleton, Monster.Skeleton },
                { Name.Troll, Monster.Troll },
                { Name.Abomination, Monster.Abomination },
                { Name.Blob, Monster.Blob },
                { Name.Draugr, Monster.Draugr },
                { Name.DraugrElite, Monster.DraugrElite },
                { Name.Leech, Monster.Leech },
                { Name.Oozer, Monster.Oozer },
                { Name.Surtling, Monster.Surtling },
                { Name.Wraith, Monster.Wraith },
                { Name.Drake, Monster.Drake },
                { Name.Fenring, Monster.Fenring },
                { Name.StoneGolem, Monster.StoneGolem },
                { Name.Deathsquito, Monster.Deathsquito },
                { Name.Fuling, Monster.Fuling },
                { Name.FulingBerserker, Monster.FulingBerserker },
                { Name.FulingShaman, Monster.FulingShaman },
                { Name.Growth, Monster.Growth },
                { Name.Serpent, Monster.Serpent },
                { Name.Bat, Monster.Bat },
                { Name.FenringCultist, Monster.FenringCultist },
                { Name.Ulv, Monster.Ulv },
            };

            MonsterNames = new Dictionary<Monster, string>
            {
                { Monster.Greyling, Name.Greyling },
                { Monster.Neck, Name.Neck },
                { Monster.Ghost, Name.Ghost },
                { Monster.Greydwarf, Name.Greydwarf },
                { Monster.GreydwarfBrute, Name.GreydwarfBrute },
                { Monster.GreydwarfShaman, Name.GreydwarfShaman },
                { Monster.RancidRemains, Name.RancidRemains },
                { Monster.Skeleton, Name.Skeleton },
                { Monster.Troll, Name.Troll },
                { Monster.Abomination, Name.Abomination },
                { Monster.Blob, Name.Blob },
                { Monster.Draugr, Name.Draugr },
                { Monster.DraugrElite, Name.DraugrElite },
                { Monster.Leech, Name.Leech },
                { Monster.Oozer, Name.Oozer },
                { Monster.Surtling, Name.Surtling },
                { Monster.Wraith, Name.Wraith },
                { Monster.Drake, Name.Drake },
                { Monster.Fenring, Name.Fenring },
                { Monster.StoneGolem, Name.StoneGolem },
                { Monster.Deathsquito, Name.Deathsquito },
                { Monster.Fuling, Name.Fuling },
                { Monster.FulingBerserker, Name.FulingBerserker },
                { Monster.FulingShaman, Name.FulingShaman },
                { Monster.Growth, Name.Growth },
                { Monster.Serpent, Name.Serpent },
                { Monster.Bat, Name.Bat },
                { Monster.FenringCultist, Name.FenringCultist },
                { Monster.Ulv, Name.Ulv },
            };
        }

        public static bool GetAnimal(string name, out Animal animal) => Animals.TryGetValue(name, out animal);

        public static bool IsAnimal(string name) => Animals.ContainsKey(name);

        public static bool GetAnimalName(Animal animal, out string name) => AnimalNames.TryGetValue(animal, out name);

        public static bool GetMonster(string name, out Monster monster) => Monsters.TryGetValue(name, out monster);

        public static bool IsMonster(string name) => Monsters.ContainsKey(name);

        public static bool GetMonsterName(Monster monster, out string name) => MonsterNames.TryGetValue(monster, out name);

        public static class Name
        {
            /* Animals */
            public const string Boar = "$enemy_boar";
            public const string Deer = "$enemy_deer";
            public const string Wolf = "$enemy_wolf";
            public const string Lox = "$enemy_lox";
            public const string Bird = "@animal_bird";
            public const string Fish = "$animal_fish";

            /* Monsters */
            public const string Greyling = "$enemy_greyling";
            public const string Neck = "$enemy_neck";
            public const string Ghost = "$enemy_ghost";
            public const string Greydwarf = "$enemy_greydwarf";
            public const string GreydwarfBrute = "$enemy_greydwarfbrute";
            public const string GreydwarfShaman = "$enemy_greydwarfshaman";
            public const string RancidRemains = "$enemy_skeletonpoison";
            public const string Skeleton = "$enemy_skeleton";
            public const string Troll = "$enemy_troll";
            public const string Abomination = "$enemy_abomination";
            public const string Blob = "$enemy_blob";
            public const string Draugr = "$enemy_draugr";
            public const string DraugrElite = "$enemy_draugrelite";
            public const string Leech = "$enemy_leech";
            public const string Oozer = "$enemy_blobelite";
            public const string Surtling = "$enemy_surtling";
            public const string Wraith = "$enemy_wraith";
            public const string Drake = "$enemy_drake";
            public const string Fenring = "$enemy_fenring";
            public const string StoneGolem = "$enemy_stonegolem";
            public const string Deathsquito = "$enemy_deathsquito";
            public const string Fuling = "$enemy_goblin";
            public const string FulingBerserker = "$enemy_goblinbrute";
            public const string FulingShaman = "$enemy_goblinshaman";
            public const string Growth = "$enemy_blobtar";
            public const string Serpent = "$enemy_serpent";
            public const string Bat = "$enemy_bat";
            public const string FenringCultist = "$enemy_fenringcultist";
            public const string Ulv = "$enemy_ulv";
        }

        [Flags]
        public enum Animal : long
        {
            [UsedImplicitly]
            None = 0,

            [Description(Name.Boar)]
            Boar = 1L << 0,

            [Description(Name.Deer)]
            Deer = 1L << 1,

            [Description(Name.Wolf)]
            Wolf = 1L << 2,

            [Description(Name.Lox)]
            Lox = 1L << 3,

            [Description(Name.Bird)]
            Bird = 1L << 4,

            [Description(Name.Fish)]
            Fish = 1L << 5,

            [UsedImplicitly]
            [Description("@select_all")]
            All = -1L,
        }

        [Flags]
        public enum Monster : long
        {
            [UsedImplicitly]
            None = 0,

            [Description(Name.Greyling)]
            Greyling = 1L << 0,

            [Description(Name.Neck)]
            Neck = 1L << 1,

            [Description(Name.Ghost)]
            Ghost = 1L << 2,

            [Description(Name.Greydwarf)]
            Greydwarf = 1L << 3,

            [Description(Name.GreydwarfBrute)]
            GreydwarfBrute = 1L << 4,

            [Description(Name.GreydwarfShaman)]
            GreydwarfShaman = 1L << 5,

            [Description(Name.RancidRemains)]
            RancidRemains = 1L << 6,

            [Description(Name.Skeleton)]
            Skeleton = 1L << 7,

            [Description(Name.Troll)]
            Troll = 1L << 8,

            [Description(Name.Abomination)]
            Abomination = 1L << 9,

            [Description(Name.Blob)]
            Blob = 1L << 10,

            [Description(Name.Draugr)]
            Draugr = 1L << 11,

            [Description(Name.DraugrElite)]
            DraugrElite = 1L << 12,

            [Description(Name.Leech)]
            Leech = 1L << 13,

            [Description(Name.Oozer)]
            Oozer = 1L << 14,

            [Description(Name.Surtling)]
            Surtling = 1L << 15,

            [Description(Name.Wraith)]
            Wraith = 1L << 16,

            [Description(Name.Drake)]
            Drake = 1L << 17,

            [Description(Name.Fenring)]
            Fenring = 1L << 18,

            [Description(Name.StoneGolem)]
            StoneGolem = 1L << 19,

            [Description(Name.Deathsquito)]
            Deathsquito = 1L << 20,

            [Description(Name.Fuling)]
            Fuling = 1L << 21,

            [Description(Name.FulingBerserker)]
            FulingBerserker = 1L << 22,

            [Description(Name.FulingShaman)]
            FulingShaman = 1L << 23,

            [Description(Name.Growth)]
            Growth = 1L << 24,

            [Description(Name.Serpent)]
            Serpent = 1L << 25,

            [Description(Name.Bat)]
            Bat = 1L << 26,

            [Description(Name.FenringCultist)]
            FenringCultist = 1L << 27,

            [Description(Name.Ulv)]
            Ulv = 1L << 28,

            [UsedImplicitly]
            [Description("@select_all")]
            All = -1L,
        }
    }
}