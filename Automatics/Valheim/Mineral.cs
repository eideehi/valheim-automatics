using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;

namespace Automatics.Valheim
{
    internal static class Mineral
    {
        [Flags]
        public enum Flags : long
        {
            None = 0,

            [LocalizedDescription(Names.CopperDeposit)]
            CopperDeposit = 1L << 0,

            [LocalizedDescription(Names.TinDeposit)]
            TinDeposit = 1L << 1,

            [LocalizedDescription(Names.MudPile)]
            MudPile = 1L << 2,

            [LocalizedDescription(Names.ObsidianDeposit)]
            ObsidianDeposit = 1L << 3,

            [LocalizedDescription(Names.SilverVein)]
            SilverVein = 1L << 4,

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 5) - 1
        }

        private static readonly Dictionary<string, Flags> NameByFlags;

        static Mineral()
        {
            NameByFlags = new Dictionary<string, Flags>
            {
                { Names.CopperDeposit, Flags.CopperDeposit },
                { Names.TinDeposit, Flags.TinDeposit },
                { Names.MudPile, Flags.MudPile },
                { Names.ObsidianDeposit, Flags.ObsidianDeposit },
                { Names.SilverVein, Flags.SilverVein }
            };
        }

        public static Flags None => Flags.None;
        public static Flags CopperDeposit => Flags.CopperDeposit;
        public static Flags TinDeposit => Flags.TinDeposit;
        public static Flags MudPile => Flags.MudPile;
        public static Flags ObsidianDeposit => Flags.ObsidianDeposit;
        public static Flags SilverVein => Flags.SilverVein;
        public static Flags All => Flags.All;

        public static bool Get(string name, out Flags mineral)
        {
            return NameByFlags.TryGetValue(name, out mineral);
        }

        public static bool IsMineral(string name)
        {
            return NameByFlags.ContainsKey(name);
        }

        public static bool GetName(Flags mineral, out string name)
        {
            var mineralName = NameByFlags
                .Where(pair => pair.Value == mineral)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            var empty = string.IsNullOrEmpty(mineralName);
            name = empty ? string.Empty : mineralName;
            return !empty;
        }

        [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
        private static class Names
        {
            public const string CopperDeposit = "$piece_deposit_copper";
            public const string TinDeposit = "$piece_deposit_tin";
            public const string MudPile = "$piece_mudpile";
            public const string ObsidianDeposit = "$piece_deposit_obsidian";
            public const string SilverVein = "$piece_deposit_silvervein";
        }
    }
}