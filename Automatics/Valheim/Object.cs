using System;
using System.Collections.Generic;
using Automatics.ModUtils;
using JetBrains.Annotations;

namespace Automatics.Valheim
{
    public static class Object
    {
        private static readonly Dictionary<string, Flora> Floras;
        private static readonly Dictionary<Flora, string> FloraNames;
        private static readonly Dictionary<string, MineralDeposit> MineralDeposits;
        private static readonly Dictionary<MineralDeposit, string> MineralDepositNames;

        static Object()
        {
            Floras = new Dictionary<string, Flora>
            {
                { Name.Dandelion, Flora.Dandelion },
                { Name.Mushroom, Flora.Mushroom },
                { Name.Raspberries, Flora.Raspberries },
                { Name.Blueberries, Flora.Blueberries },
                { Name.Carrot, Flora.Carrot },
                { Name.CarrotSeeds, Flora.CarrotSeeds },
                { Name.YellowMushroom, Flora.YellowMushroom },
                { Name.Thistle, Flora.Thistle },
                { Name.Turnip, Flora.Turnip },
                { Name.TurnipSeeds, Flora.TurnipSeeds },
                { Name.Onion, Flora.Onion },
                { Name.OnionSeeds, Flora.OnionSeeds },
                { Name.Barley, Flora.Barley },
                { Name.Cloudberries, Flora.Cloudberries },
                { Name.Flex, Flora.Flex },
            };

            FloraNames = new Dictionary<Flora, string>
            {
                { Flora.Dandelion, Name.Dandelion },
                { Flora.Mushroom, Name.Mushroom },
                { Flora.Raspberries, Name.Raspberries },
                { Flora.Blueberries, Name.Blueberries },
                { Flora.Carrot, Name.Carrot },
                { Flora.CarrotSeeds, Name.CarrotSeeds },
                { Flora.YellowMushroom, Name.YellowMushroom },
                { Flora.Thistle, Name.Thistle },
                { Flora.Turnip, Name.Turnip },
                { Flora.TurnipSeeds, Name.TurnipSeeds },
                { Flora.Onion, Name.Onion },
                { Flora.OnionSeeds, Name.OnionSeeds },
                { Flora.Barley, Name.Barley },
                { Flora.Cloudberries, Name.Cloudberries },
                { Flora.Flex, Name.Flex },
            };

            MineralDeposits = new Dictionary<string, MineralDeposit>
            {
                { Name.CopperDeposit, MineralDeposit.CopperDeposit },
                { Name.TinDeposit, MineralDeposit.TinDeposit },
                { Name.MudPile, MineralDeposit.MudPile },
                { Name.ObsidianDeposit, MineralDeposit.ObsidianDeposit },
                { Name.SilverVein, MineralDeposit.SilverVein },
            };

            MineralDepositNames = new Dictionary<MineralDeposit, string>
            {
                { MineralDeposit.CopperDeposit, Name.CopperDeposit },
                { MineralDeposit.TinDeposit, Name.TinDeposit },
                { MineralDeposit.MudPile, Name.MudPile },
                { MineralDeposit.ObsidianDeposit, Name.ObsidianDeposit },
                { MineralDeposit.SilverVein, Name.SilverVein },
            };
        }

        public static bool GetFlora(string name, out Flora flora) => Floras.TryGetValue(name, out flora);

        public static bool IsFlora(string name) => Floras.ContainsKey(name);

        public static bool GetFloraName(Flora flora, out string name) => FloraNames.TryGetValue(flora, out name);

        public static bool GetMineralDeposit(string name, out MineralDeposit mineralDeposit) =>
            MineralDeposits.TryGetValue(name, out mineralDeposit);

        public static bool IsMineralDeposit(string name) => MineralDeposits.ContainsKey(name);

        public static bool GetMineralDepositName(MineralDeposit mineralDeposit, out string name) =>
            MineralDepositNames.TryGetValue(mineralDeposit, out name);

        public static class Name
        {
            /* Floras */
            public const string Dandelion = "$item_dandelion";
            public const string Mushroom = "$item_mushroomcommon";
            public const string Raspberries = "$item_raspberries";
            public const string Blueberries = "$item_blueberries";
            public const string Carrot = "$item_carrot";
            public const string CarrotSeeds = "$item_carrotseeds";
            public const string YellowMushroom = "$item_mushroomyellow";
            public const string Thistle = "$item_thistle";
            public const string Turnip = "$item_turnip";
            public const string TurnipSeeds = "$item_turnipseeds";
            public const string Onion = "$item_onion";
            public const string OnionSeeds = "$item_onionseeds";
            public const string Barley = "$item_barley";
            public const string Cloudberries = "$item_cloudberries";
            public const string Flex = "$item_flax";

            /* Mineral deposits */
            public const string CopperDeposit = "$piece_deposit_copper";
            public const string TinDeposit = "$piece_deposit_tin";
            public const string MudPile = "$piece_mudpile";
            public const string ObsidianDeposit = "$piece_deposit_obsidian";
            public const string SilverVein = "$piece_deposit_silvervein";
        }

        [Flags]
        public enum Flora : long
        {
            [UsedImplicitly]
            None = 0,

            [LocalizedDescription(Name.Dandelion)]
            Dandelion = 1L << 0,

            [LocalizedDescription(Name.Mushroom)]
            Mushroom = 1L << 1,

            [LocalizedDescription(Name.Raspberries)]
            Raspberries = 1L << 2,

            [LocalizedDescription(Name.Blueberries)]
            Blueberries = 1L << 3,

            [LocalizedDescription(Name.Carrot)]
            Carrot = 1L << 4,

            [LocalizedDescription(Name.CarrotSeeds)]
            CarrotSeeds = 1L << 5,

            [LocalizedDescription(Name.YellowMushroom)]
            YellowMushroom = 1L << 6,

            [LocalizedDescription(Name.Thistle)]
            Thistle = 1L << 7,

            [LocalizedDescription(Name.Turnip)]
            Turnip = 1L << 8,

            [LocalizedDescription(Name.TurnipSeeds)]
            TurnipSeeds = 1L << 9,

            [LocalizedDescription(Name.Onion)]
            Onion = 1L << 10,

            [LocalizedDescription(Name.OnionSeeds)]
            OnionSeeds = 1L << 11,

            [LocalizedDescription(Name.Barley)]
            Barley = 1L << 12,

            [LocalizedDescription(Name.Cloudberries)]
            Cloudberries = 1L << 13,

            [LocalizedDescription(Name.Flex)]
            Flex = 1L << 14,

            [UsedImplicitly]
            [LocalizedDescription("@select_all")]
            All = -1L,
        }

        [Flags]
        public enum MineralDeposit : long
        {
            [UsedImplicitly]
            None = 0,

            [LocalizedDescription(Name.CopperDeposit)]
            CopperDeposit = 1L << 0,

            [LocalizedDescription(Name.TinDeposit)]
            TinDeposit = 1L << 1,

            [LocalizedDescription(Name.MudPile)]
            MudPile = 1L << 2,

            [LocalizedDescription(Name.ObsidianDeposit)]
            ObsidianDeposit = 1L << 3,

            [LocalizedDescription(Name.SilverVein)]
            SilverVein = 1L << 4,

            [UsedImplicitly]
            [LocalizedDescription("@select_all")]
            All = -1L,
        }
    }
}