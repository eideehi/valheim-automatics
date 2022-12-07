using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;

namespace Automatics.Valheim
{
    internal static class Monster
    {
        [Flags]
        public enum Flags : long
        {
            None = 0,

            [LocalizedDescription(Names.Greyling)]
            Greyling = 1L << 0,

            [LocalizedDescription(Names.Neck)]
            Neck = 1L << 1,

            [LocalizedDescription(Names.Ghost)]
            Ghost = 1L << 2,

            [LocalizedDescription(Names.Greydwarf)]
            Greydwarf = 1L << 3,

            [LocalizedDescription(Names.GreydwarfBrute)]
            GreydwarfBrute = 1L << 4,

            [LocalizedDescription(Names.GreydwarfShaman)]
            GreydwarfShaman = 1L << 5,

            [LocalizedDescription(Names.RancidRemains)]
            RancidRemains = 1L << 6,

            [LocalizedDescription(Names.Skeleton)]
            Skeleton = 1L << 7,

            [LocalizedDescription(Names.Troll)]
            Troll = 1L << 8,

            [LocalizedDescription(Names.Abomination)]
            Abomination = 1L << 9,

            [LocalizedDescription(Names.Blob)]
            Blob = 1L << 10,

            [LocalizedDescription(Names.Draugr)]
            Draugr = 1L << 11,

            [LocalizedDescription(Names.DraugrElite)]
            DraugrElite = 1L << 12,

            [LocalizedDescription(Names.Leech)]
            Leech = 1L << 13,

            [LocalizedDescription(Names.Oozer)]
            Oozer = 1L << 14,

            [LocalizedDescription(Names.Surtling)]
            Surtling = 1L << 15,

            [LocalizedDescription(Names.Wraith)]
            Wraith = 1L << 16,

            [LocalizedDescription(Names.Drake)]
            Drake = 1L << 17,

            [LocalizedDescription(Names.Fenring)]
            Fenring = 1L << 18,

            [LocalizedDescription(Names.StoneGolem)]
            StoneGolem = 1L << 19,

            [LocalizedDescription(Names.Deathsquito)]
            Deathsquito = 1L << 20,

            [LocalizedDescription(Names.Fuling)]
            Fuling = 1L << 21,

            [LocalizedDescription(Names.FulingBerserker)]
            FulingBerserker = 1L << 22,

            [LocalizedDescription(Names.FulingShaman)]
            FulingShaman = 1L << 23,

            [LocalizedDescription(Names.Growth)]
            Growth = 1L << 24,

            [LocalizedDescription(Names.Serpent)]
            Serpent = 1L << 25,

            [LocalizedDescription(Names.Bat)]
            Bat = 1L << 26,

            [LocalizedDescription(Names.FenringCultist)]
            FenringCultist = 1L << 27,

            [LocalizedDescription(Names.Ulv)]
            Ulv = 1L << 28,

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 29) - 1
        }

        private static readonly Dictionary<string, Flags> NameByFlags;

        static Monster()
        {
            NameByFlags = new Dictionary<string, Flags>
            {
                { Names.Greyling, Flags.Greyling },
                { Names.Neck, Flags.Neck },
                { Names.Ghost, Flags.Ghost },
                { Names.Greydwarf, Flags.Greydwarf },
                { Names.GreydwarfBrute, Flags.GreydwarfBrute },
                { Names.GreydwarfShaman, Flags.GreydwarfShaman },
                { Names.RancidRemains, Flags.RancidRemains },
                { Names.Skeleton, Flags.Skeleton },
                { Names.Troll, Flags.Troll },
                { Names.Abomination, Flags.Abomination },
                { Names.Blob, Flags.Blob },
                { Names.Draugr, Flags.Draugr },
                { Names.DraugrElite, Flags.DraugrElite },
                { Names.Leech, Flags.Leech },
                { Names.Oozer, Flags.Oozer },
                { Names.Surtling, Flags.Surtling },
                { Names.Wraith, Flags.Wraith },
                { Names.Drake, Flags.Drake },
                { Names.Fenring, Flags.Fenring },
                { Names.StoneGolem, Flags.StoneGolem },
                { Names.Deathsquito, Flags.Deathsquito },
                { Names.Fuling, Flags.Fuling },
                { Names.FulingBerserker, Flags.FulingBerserker },
                { Names.FulingShaman, Flags.FulingShaman },
                { Names.Growth, Flags.Growth },
                { Names.Serpent, Flags.Serpent },
                { Names.Bat, Flags.Bat },
                { Names.FenringCultist, Flags.FenringCultist },
                { Names.Ulv, Flags.Ulv }
            };
        }

        public static Flags None => Flags.None;
        public static Flags Greyling => Flags.Greyling;
        public static Flags Neck => Flags.Neck;
        public static Flags Ghost => Flags.Ghost;
        public static Flags Greydwarf => Flags.Greydwarf;
        public static Flags GreydwarfBrute => Flags.GreydwarfBrute;
        public static Flags GreydwarfShaman => Flags.GreydwarfShaman;
        public static Flags RancidRemains => Flags.RancidRemains;
        public static Flags Skeleton => Flags.Skeleton;
        public static Flags Troll => Flags.Troll;
        public static Flags Abomination => Flags.Abomination;
        public static Flags Blob => Flags.Blob;
        public static Flags Draugr => Flags.Draugr;
        public static Flags DraugrElite => Flags.DraugrElite;
        public static Flags Leech => Flags.Leech;
        public static Flags Oozer => Flags.Oozer;
        public static Flags Surtling => Flags.Surtling;
        public static Flags Wraith => Flags.Wraith;
        public static Flags Drake => Flags.Drake;
        public static Flags Fenring => Flags.Fenring;
        public static Flags StoneGolem => Flags.StoneGolem;
        public static Flags Deathsquito => Flags.Deathsquito;
        public static Flags Fuling => Flags.Fuling;
        public static Flags FulingBerserker => Flags.FulingBerserker;
        public static Flags FulingShaman => Flags.FulingShaman;
        public static Flags Growth => Flags.Growth;
        public static Flags Serpent => Flags.Serpent;
        public static Flags Bat => Flags.Bat;
        public static Flags FenringCultist => Flags.FenringCultist;
        public static Flags Ulv => Flags.Ulv;
        public static Flags All => Flags.All;

        public static bool Get(string name, out Flags monster)
        {
            return NameByFlags.TryGetValue(name, out monster);
        }

        public static bool IsMonster(string name)
        {
            return NameByFlags.ContainsKey(name);
        }

        public static bool GetName(Flags monster, out string name)
        {
            var monsterName = NameByFlags
                .Where(pair => pair.Value == monster)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            var empty = string.IsNullOrEmpty(monsterName);
            name = empty ? string.Empty : monsterName;
            return !empty;
        }

        [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
        private static class Names
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
    }
}