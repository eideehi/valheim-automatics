using static Automatics.ModUtils.Configuration;
using System;
using System.Collections.Generic;

namespace Automatics.ModUtils
{
    public static class ValheimCharacter
    {
        public static class Animal
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Animal()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.Boar, Flag.Boar },
                    { Name.Deer, Flag.Deer },
                    { Name.Wolf, Flag.Wolf },
                    { Name.Lox, Flag.Lox },
                    { Name.Bird, Flag.Bird },
                    { "Seagal", Flag.Bird },
                    { "Crow", Flag.Bird },
                    { Name.Fish, Flag.Fish },
                    { "$animal_fish1", Flag.Fish },
                    { "$animal_fish2", Flag.Fish },
                    { "$animal_fish3", Flag.Fish },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.Boar, Name.Boar },
                    { Flag.Deer, Name.Deer },
                    { Flag.Wolf, Name.Wolf },
                    { Flag.Lox, Name.Lox },
                    { Flag.Bird, Name.Bird },
                    { Flag.Fish, Name.Fish },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
                public const string Boar = "$enemy_boar";
                public const string Deer = "$enemy_deer";
                public const string Wolf = "$enemy_wolf";
                public const string Lox = "$enemy_lox";
                public const string Bird = "@animal_bird";
                public const string Fish = "$animal_fish";
            }

            [Flags]
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.Boar)]
                Boar = 1L << 0,

                [LocalizedDescription(Name.Deer)]
                Deer = 1L << 1,

                [LocalizedDescription(Name.Wolf)]
                Wolf = 1L << 2,

                [LocalizedDescription(Name.Lox)]
                Lox = 1L << 3,

                [LocalizedDescription(Name.Bird)]
                Bird = 1L << 4,

                [LocalizedDescription(Name.Fish)]
                Fish = 1L << 5,

                [LocalizedDescription("@select_all")]
                All = -1L,
            }
        }

        public static class Monster
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Monster()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.Greyling, Flag.Greyling },
                    { Name.Neck, Flag.Neck },
                    { Name.Ghost, Flag.Ghost },
                    { Name.Greydwarf, Flag.Greydwarf },
                    { Name.GreydwarfBrute, Flag.GreydwarfBrute },
                    { Name.GreydwarfShaman, Flag.GreydwarfShaman },
                    { Name.RancidRemains, Flag.RancidRemains },
                    { Name.Skeleton, Flag.Skeleton },
                    { Name.Troll, Flag.Troll },
                    { Name.Abomination, Flag.Abomination },
                    { Name.Blob, Flag.Blob },
                    { Name.Draugr, Flag.Draugr },
                    { Name.DraugrElite, Flag.DraugrElite },
                    { Name.Leech, Flag.Leech },
                    { Name.Oozer, Flag.Oozer },
                    { Name.Surtling, Flag.Surtling },
                    { Name.Wraith, Flag.Wraith },
                    { Name.Drake, Flag.Drake },
                    { Name.Fenring, Flag.Fenring },
                    { Name.StoneGolem, Flag.StoneGolem },
                    { Name.Deathsquito, Flag.Deathsquito },
                    { Name.Fuling, Flag.Fuling },
                    { Name.FulingBerserker, Flag.FulingBerserker },
                    { Name.FulingShaman, Flag.FulingShaman },
                    { Name.Growth, Flag.Growth },
                    { Name.Serpent, Flag.Serpent },
                    { Name.Bat, Flag.Bat },
                    { Name.FenringCultist, Flag.FenringCultist },
                    { Name.Ulv, Flag.Ulv },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.Greyling, Name.Greyling },
                    { Flag.Neck, Name.Neck },
                    { Flag.Ghost, Name.Ghost },
                    { Flag.Greydwarf, Name.Greydwarf },
                    { Flag.GreydwarfBrute, Name.GreydwarfBrute },
                    { Flag.GreydwarfShaman, Name.GreydwarfShaman },
                    { Flag.RancidRemains, Name.RancidRemains },
                    { Flag.Skeleton, Name.Skeleton },
                    { Flag.Troll, Name.Troll },
                    { Flag.Abomination, Name.Abomination },
                    { Flag.Blob, Name.Blob },
                    { Flag.Draugr, Name.Draugr },
                    { Flag.DraugrElite, Name.DraugrElite },
                    { Flag.Leech, Name.Leech },
                    { Flag.Oozer, Name.Oozer },
                    { Flag.Surtling, Name.Surtling },
                    { Flag.Wraith, Name.Wraith },
                    { Flag.Drake, Name.Drake },
                    { Flag.Fenring, Name.Fenring },
                    { Flag.StoneGolem, Name.StoneGolem },
                    { Flag.Deathsquito, Name.Deathsquito },
                    { Flag.Fuling, Name.Fuling },
                    { Flag.FulingBerserker, Name.FulingBerserker },
                    { Flag.FulingShaman, Name.FulingShaman },
                    { Flag.Growth, Name.Growth },
                    { Flag.Serpent, Name.Serpent },
                    { Flag.Bat, Name.Bat },
                    { Flag.FenringCultist, Name.FenringCultist },
                    { Flag.Ulv, Name.Ulv },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
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
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.Greyling)]
                Greyling = 1L << 0,

                [LocalizedDescription(Name.Neck)]
                Neck = 1L << 1,

                [LocalizedDescription(Name.Ghost)]
                Ghost = 1L << 2,

                [LocalizedDescription(Name.Greydwarf)]
                Greydwarf = 1L << 3,

                [LocalizedDescription(Name.GreydwarfBrute)]
                GreydwarfBrute = 1L << 4,

                [LocalizedDescription(Name.GreydwarfShaman)]
                GreydwarfShaman = 1L << 5,

                [LocalizedDescription(Name.RancidRemains)]
                RancidRemains = 1L << 6,

                [LocalizedDescription(Name.Skeleton)]
                Skeleton = 1L << 7,

                [LocalizedDescription(Name.Troll)]
                Troll = 1L << 8,

                [LocalizedDescription(Name.Abomination)]
                Abomination = 1L << 9,

                [LocalizedDescription(Name.Blob)]
                Blob = 1L << 10,

                [LocalizedDescription(Name.Draugr)]
                Draugr = 1L << 11,

                [LocalizedDescription(Name.DraugrElite)]
                DraugrElite = 1L << 12,

                [LocalizedDescription(Name.Leech)]
                Leech = 1L << 13,

                [LocalizedDescription(Name.Oozer)]
                Oozer = 1L << 14,

                [LocalizedDescription(Name.Surtling)]
                Surtling = 1L << 15,

                [LocalizedDescription(Name.Wraith)]
                Wraith = 1L << 16,

                [LocalizedDescription(Name.Drake)]
                Drake = 1L << 17,

                [LocalizedDescription(Name.Fenring)]
                Fenring = 1L << 18,

                [LocalizedDescription(Name.StoneGolem)]
                StoneGolem = 1L << 19,

                [LocalizedDescription(Name.Deathsquito)]
                Deathsquito = 1L << 20,

                [LocalizedDescription(Name.Fuling)]
                Fuling = 1L << 21,

                [LocalizedDescription(Name.FulingBerserker)]
                FulingBerserker = 1L << 22,

                [LocalizedDescription(Name.FulingShaman)]
                FulingShaman = 1L << 23,

                [LocalizedDescription(Name.Growth)]
                Growth = 1L << 24,

                [LocalizedDescription(Name.Serpent)]
                Serpent = 1L << 25,

                [LocalizedDescription(Name.Bat)]
                Bat = 1L << 26,

                [LocalizedDescription(Name.FenringCultist)]
                FenringCultist = 1L << 27,

                [LocalizedDescription(Name.Ulv)]
                Ulv = 1L << 28,

                [LocalizedDescription("@select_all")]
                All = -1L,
            }
        }
    }
}