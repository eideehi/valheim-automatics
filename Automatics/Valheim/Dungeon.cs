using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;

namespace Automatics.Valheim
{
    internal static class Dungeon
    {
        [Flags]
        public enum Flags : long
        {
            None = 0,

            [LocalizedDescription(Names.BurialChambers)]
            BurialChambers = 1L << 0,

            [LocalizedDescription(Names.TrollCave)]
            TrollCave = 1L << 1,

            [LocalizedDescription(Names.SunkenCrypts)]
            SunkenCrypts = 1L << 2,

            [LocalizedDescription(Names.MountainCave)]
            MountainCave = 1L << 3,

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 4) - 1
        }

        private static readonly Dictionary<string, Flags> NameByFlags;

        static Dungeon()
        {
            NameByFlags = new Dictionary<string, Flags>
            {
                { Names.BurialChambers, Flags.BurialChambers },
                { "Crypt2", Flags.BurialChambers },
                { "Crypt3", Flags.BurialChambers },
                { "Crypt4", Flags.BurialChambers },
                { Names.TrollCave, Flags.TrollCave },
                { "TrollCave", Flags.TrollCave },
                { "TrollCave02", Flags.TrollCave },
                { Names.SunkenCrypts, Flags.SunkenCrypts },
                { "SunkenCrypt1", Flags.SunkenCrypts },
                { "SunkenCrypt2", Flags.SunkenCrypts },
                { "SunkenCrypt3", Flags.SunkenCrypts },
                { "SunkenCrypt4", Flags.SunkenCrypts },
                { Names.MountainCave, Flags.MountainCave },
                { "MountainCave01", Flags.MountainCave },
                { "MountainCave02", Flags.MountainCave }
            };
        }

        public static Flags None => Flags.None;
        public static Flags BurialChambers => Flags.BurialChambers;
        public static Flags TrollCave => Flags.TrollCave;
        public static Flags SunkenCrypts => Flags.SunkenCrypts;
        public static Flags MountainCave => Flags.MountainCave;
        public static Flags All => Flags.All;

        public static bool Get(string name, out Flags dungeon)
        {
            return NameByFlags.TryGetValue(name, out dungeon);
        }

        public static bool IsDungeon(string name)
        {
            return NameByFlags.ContainsKey(name);
        }

        public static bool GetName(Flags dungeon, out string name)
        {
            var dungeonName = NameByFlags
                .Where(pair => pair.Value == dungeon)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            var empty = string.IsNullOrEmpty(dungeonName);
            name = empty ? string.Empty : dungeonName;
            return !empty;
        }

        [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
        private static class Names
        {
            public const string BurialChambers = "$location_forestcrypt";
            public const string TrollCave = "$location_forestcave";
            public const string SunkenCrypts = "$location_sunkencrypt";
            public const string MountainCave = "$location_mountaincave";
        }
    }
}