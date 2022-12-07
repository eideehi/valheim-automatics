using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;

namespace Automatics.Valheim
{
    public static class Spot
    {
        [Flags]
        public enum Flags : long
        {
            None = 0,

            [LocalizedDescription(Automatics.L10NPrefix, Names.InfestedTree)]
            InfestedTree = 1L << 0,

            [LocalizedDescription(Automatics.L10NPrefix, Names.FireHole)]
            FireHole = 1L << 1,

            [LocalizedDescription(Automatics.L10NPrefix, Names.DrakeNest)]
            DrakeNest = 1L << 2,

            [LocalizedDescription(Automatics.L10NPrefix, Names.GoblinCamp)]
            GoblinCamp = 1L << 3,

            [LocalizedDescription(Automatics.L10NPrefix, Names.TarPit)]
            TarPit = 1L << 4,

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 5) - 1
        }

        private static readonly Dictionary<string, Flags> NameByFlags;

        static Spot()
        {
            NameByFlags = new Dictionary<string, Flags>
            {
                { Names.InfestedTree, Flags.InfestedTree },
                { "InfestedTree01", Flags.InfestedTree },
                { Names.FireHole, Flags.FireHole },
                { "FireHole", Flags.FireHole },
                { Names.DrakeNest, Flags.DrakeNest },
                { "DrakeNest01", Flags.DrakeNest },
                { Names.GoblinCamp, Flags.GoblinCamp },
                { "GoblinCamp1", Flags.GoblinCamp },
                { "GoblinCamp2", Flags.GoblinCamp },
                { Names.TarPit, Flags.TarPit },
                { "TarPit1", Flags.TarPit },
                { "TarPit2", Flags.TarPit },
                { "TarPit3", Flags.TarPit }
            };
        }

        public static Flags None => Flags.None;
        public static Flags InfestedTree => Flags.InfestedTree;
        public static Flags FireHole => Flags.FireHole;
        public static Flags DrakeNest => Flags.DrakeNest;
        public static Flags GoblinCamp => Flags.GoblinCamp;
        public static Flags TarPit => Flags.TarPit;
        public static Flags All => Flags.All;

        public static bool Get(string name, out Flags spot)
        {
            return NameByFlags.TryGetValue(name, out spot);
        }

        public static bool IsSpot(string name)
        {
            return NameByFlags.ContainsKey(name);
        }

        public static bool GetName(Flags spot, out string name)
        {
            var spotName = NameByFlags
                .Where(pair => pair.Value == spot)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            var empty = string.IsNullOrEmpty(spotName);
            name = empty ? string.Empty : spotName;
            return !empty;
        }

        [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
        private static class Names
        {
            public const string InfestedTree = "@location_infestedtree";
            public const string FireHole = "@location_firehole";
            public const string DrakeNest = "@location_drakenest";
            public const string GoblinCamp = "@location_goblincamp";
            public const string TarPit = "@location_tarpit";
        }
    }
}