using System;
using System.Collections.Generic;
using Automatics.ModUtils;
using JetBrains.Annotations;

namespace Automatics.Valheim
{
    public static class Location
    {
        private static readonly Dictionary<string, Dungeon> Dungeons;
        private static readonly Dictionary<Dungeon, string> DungeonNames;
        private static readonly Dictionary<string, Spot> Spots;
        private static readonly Dictionary<Spot, string> SpotNames;

        static Location()
        {
            Dungeons = new Dictionary<string, Dungeon>
            {
                { Name.BurialChambers, Dungeon.BurialChambers },
                { "Crypt2", Dungeon.BurialChambers },
                { "Crypt3", Dungeon.BurialChambers },
                { "Crypt4", Dungeon.BurialChambers },
                { Name.TrollCave, Dungeon.TrollCave },
                { "TrollCave", Dungeon.TrollCave },
                { "TrollCave02", Dungeon.TrollCave },
                { Name.SunkenCrypts, Dungeon.SunkenCrypts },
                { "SunkenCrypt1", Dungeon.SunkenCrypts },
                { "SunkenCrypt2", Dungeon.SunkenCrypts },
                { "SunkenCrypt3", Dungeon.SunkenCrypts },
                { "SunkenCrypt4", Dungeon.SunkenCrypts },
                { Name.MountainCave, Dungeon.MountainCave },
                { "MountainCave01", Dungeon.MountainCave },
                { "MountainCave02", Dungeon.MountainCave },
            };

            DungeonNames = new Dictionary<Dungeon, string>
            {
                { Dungeon.BurialChambers, Name.BurialChambers },
                { Dungeon.TrollCave, Name.TrollCave },
                { Dungeon.SunkenCrypts, Name.SunkenCrypts },
                { Dungeon.MountainCave, Name.MountainCave },
            };

            Spots = new Dictionary<string, Spot>
            {
                { Name.InfestedTree, Spot.InfestedTree },
                { "InfestedTree01", Spot.InfestedTree },
                { Name.FireHole, Spot.FireHole },
                { "FireHole", Spot.FireHole },
                { Name.DrakeNest, Spot.DrakeNest },
                { "DrakeNest01", Spot.DrakeNest },
                { Name.GoblinCamp, Spot.GoblinCamp },
                { "GoblinCamp1", Spot.GoblinCamp },
                { "GoblinCamp2", Spot.GoblinCamp },
                { Name.TarPit, Spot.TarPit },
                { "TarPit1", Spot.TarPit },
                { "TarPit2", Spot.TarPit },
                { "TarPit3", Spot.TarPit },
            };

            SpotNames = new Dictionary<Spot, string>
            {
                { Spot.InfestedTree, Name.InfestedTree },
                { Spot.FireHole, Name.FireHole },
                { Spot.DrakeNest, Name.DrakeNest },
                { Spot.GoblinCamp, Name.GoblinCamp },
                { Spot.TarPit, Name.TarPit },
            };
        }

        public static bool GetDungeon(string name, out Dungeon dungeon) => Dungeons.TryGetValue(name, out dungeon);

        public static bool IsDungeon(string name) => Dungeons.ContainsKey(name);

        public static bool GetDungeonName(Dungeon dungeon, out string name) => DungeonNames.TryGetValue(dungeon, out name);

        public static bool GetSpot(string name, out Spot spot) => Spots.TryGetValue(name, out spot);

        public static bool IsSpot(string name) => Spots.ContainsKey(name);

        public static bool GetSpotName(Spot spot, out string name) => SpotNames.TryGetValue(spot, out name);

        public static class Name
        {
            /* Dungeons */
            public const string BurialChambers = "$location_forestcrypt";
            public const string TrollCave = "$location_forestcave";
            public const string SunkenCrypts = "$location_sunkencrypt";
            public const string MountainCave = "$location_mountaincave";

            /* Spots */
            public const string InfestedTree = "@location_infestedtree";
            public const string FireHole = "@location_firehole";
            public const string DrakeNest = "@location_drakenest";
            public const string GoblinCamp = "@location_goblincamp";
            public const string TarPit = "@location_tarpit";
        }

        [Flags]
        public enum Dungeon : long
        {
            [UsedImplicitly]
            None = 0,

            [LocalizedDescription(Name.BurialChambers)]
            BurialChambers = 1L << 0,

            [LocalizedDescription(Name.TrollCave)]
            TrollCave = 1L << 1,

            [LocalizedDescription(Name.SunkenCrypts)]
            SunkenCrypts = 1L << 2,

            [LocalizedDescription(Name.MountainCave)]
            MountainCave = 1L << 3,

            [UsedImplicitly]
            [LocalizedDescription("@select_all")]
            All = -1L,
        }

        [Flags]
        public enum Spot : long
        {
            [UsedImplicitly]
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

            [UsedImplicitly]
            [LocalizedDescription("@select_all")]
            All = -1L,
        }
    }
}