using static Automatics.ModUtils.Configuration;
using System;
using System.Collections.Generic;

namespace Automatics.ModUtils
{
    public static class ValheimLocation
    {
        public static class Dungeon
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Dungeon()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.BurialChambers, Flag.BurialChambers },
                    { "Crypt2", Flag.BurialChambers },
                    { "Crypt3", Flag.BurialChambers },
                    { "Crypt4", Flag.BurialChambers },
                    { Name.TrollCave, Flag.TrollCave },
                    { "TrollCave", Flag.TrollCave },
                    { "TrollCave02", Flag.TrollCave },
                    { Name.SunkenCrypts, Flag.SunkenCrypts },
                    { "SunkenCrypt1", Flag.SunkenCrypts },
                    { "SunkenCrypt2", Flag.SunkenCrypts },
                    { "SunkenCrypt3", Flag.SunkenCrypts },
                    { "SunkenCrypt4", Flag.SunkenCrypts },
                    { Name.MountainCave, Flag.MountainCave },
                    { "MountainCave01", Flag.MountainCave },
                    { "MountainCave02", Flag.MountainCave },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.BurialChambers, Name.BurialChambers },
                    { Flag.TrollCave, Name.TrollCave },
                    { Flag.SunkenCrypts, Name.SunkenCrypts },
                    { Flag.MountainCave, Name.MountainCave },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
                public const string BurialChambers = "$location_forestcrypt";
                public const string TrollCave = "$location_forestcave";
                public const string SunkenCrypts = "$location_sunkencrypt";
                public const string MountainCave = "$location_mountaincave";
            }

            [Flags]
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.BurialChambers)]
                BurialChambers = 1L << 0,

                [LocalizedDescription(Name.TrollCave)]
                TrollCave = 1L << 1,

                [LocalizedDescription(Name.SunkenCrypts)]
                SunkenCrypts = 1L << 2,

                [LocalizedDescription(Name.MountainCave)]
                MountainCave = 1L << 3,

                [LocalizedDescription("@config_flags_all_label")]
                All = -1L,
            }
        }

        public static class Spot
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Spot()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.InfestedTree, Flag.InfestedTree },
                    { "InfestedTree01", Flag.InfestedTree },
                    { Name.FireHole, Flag.FireHole },
                    { "FireHole", Flag.FireHole },
                    { Name.DrakeNest, Flag.DrakeNest },
                    { "DrakeNest01", Flag.DrakeNest },
                    { Name.GoblinCamp, Flag.GoblinCamp },
                    { "GoblinCamp2", Flag.GoblinCamp },
                    { Name.TarPit, Flag.TarPit },
                    { "TarPit1", Flag.TarPit },
                    { "TarPit2", Flag.TarPit },
                    { "TarPit3", Flag.TarPit },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.InfestedTree, Name.InfestedTree },
                    { Flag.FireHole, Name.FireHole },
                    { Flag.DrakeNest, Name.DrakeNest },
                    { Flag.GoblinCamp, Name.GoblinCamp },
                    { Flag.TarPit, Name.TarPit },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
                public const string InfestedTree = "@spot_infested_tree";
                public const string FireHole = "@spot_fire_hole";
                public const string DrakeNest = "@spot_drake_nest";
                public const string GoblinCamp = "@spot_goblin_camp";
                public const string TarPit = "@spot_tar_pit";
            }

            [Flags]
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.InfestedTree)]
                InfestedTree = 1L << 0,

                [LocalizedDescription(Name.FireHole)]
                FireHole = 1L << 1,

                [LocalizedDescription(Name.DrakeNest)]
                DrakeNest = 1L << 2,

                [LocalizedDescription(Name.GoblinCamp)]
                GoblinCamp = 1L << 3,

                [LocalizedDescription(Name.TarPit)]
                TarPit = 1L << 4,

                [LocalizedDescription("@config_flags_all_label")]
                All = -1L,
            }
        }
    }
}