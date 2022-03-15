using static Automatics.Configuration;
using System;
using System.Collections.Generic;

namespace Automatics
{
    public static class ValheimObject
    {
        public static class Flora
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Flora()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.Dandelion, Flag.Dandelion },
                    { Name.Mushroom, Flag.Mushroom },
                    { Name.Raspberries, Flag.Raspberries },
                    { Name.Blueberries, Flag.Blueberries },
                    { Name.Carrot, Flag.Carrot },
                    { Name.CarrotSeeds, Flag.CarrotSeeds },
                    { Name.YellowMushroom, Flag.YellowMushroom },
                    { Name.Thistle, Flag.Thistle },
                    { Name.Turnip, Flag.Turnip },
                    { Name.TurnipSeeds, Flag.TurnipSeeds },
                    { Name.Onion, Flag.Onion },
                    { Name.OnionSeeds, Flag.OnionSeeds },
                    { Name.Barley, Flag.Barley },
                    { Name.Cloudberries, Flag.Cloudberries },
                    { Name.Flex, Flag.Flex },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.Dandelion, Name.Dandelion },
                    { Flag.Mushroom, Name.Mushroom },
                    { Flag.Raspberries, Name.Raspberries },
                    { Flag.Blueberries, Name.Blueberries },
                    { Flag.Carrot, Name.Carrot },
                    { Flag.CarrotSeeds, Name.CarrotSeeds },
                    { Flag.YellowMushroom, Name.YellowMushroom },
                    { Flag.Thistle, Name.Thistle },
                    { Flag.Turnip, Name.Turnip },
                    { Flag.TurnipSeeds, Name.TurnipSeeds },
                    { Flag.Onion, Name.Onion },
                    { Flag.OnionSeeds, Name.OnionSeeds },
                    { Flag.Barley, Name.Barley },
                    { Flag.Cloudberries, Name.Cloudberries },
                    { Flag.Flex, Name.Flex },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
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
            }

            [Flags]
            public enum Flag : long
            {
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

                [LocalizedDescription("@config_flags_all_label")]
                All = -1L,
            }
        }

        public static class Vein
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Vein()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.Copper, Flag.Copper },
                    { Name.Tin, Flag.Tin },
                    { Name.MudPile, Flag.MudPile },
                    { Name.Obsidian, Flag.Obsidian },
                    { Name.Silver, Flag.Silver },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.Copper, Name.Copper },
                    { Flag.Tin, Name.Tin },
                    { Flag.MudPile, Name.MudPile },
                    { Flag.Obsidian, Name.Obsidian },
                    { Flag.Silver, Name.Silver },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
                public const string Copper = "$piece_deposit_copper";
                public const string Tin = "$piece_deposit_tin";
                public const string MudPile = "$piece_mudpile";
                public const string Obsidian = "$piece_deposit_obsidian";
                public const string Silver = "$piece_deposit_silvervein";
            }

            [Flags]
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.Copper)]
                Copper = 1L << 0,

                [LocalizedDescription(Name.Tin)]
                Tin = 1L << 1,

                [LocalizedDescription(Name.MudPile)]
                MudPile = 1L << 2,

                [LocalizedDescription(Name.Obsidian)]
                Obsidian = 1L << 3,

                [LocalizedDescription(Name.Silver)]
                Silver = 1L << 4,

                [LocalizedDescription("@config_flags_all_label")]
                All = -1L,
            }
        }

        public static class Spawner
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Spawner()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.GreydwarfNest, Flag.GreydwarfNest },
                    { Name.EvilBonePile, Flag.EvilBonePile },
                    { Name.BodyPile, Flag.BodyPile },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.GreydwarfNest, Name.GreydwarfNest },
                    { Flag.EvilBonePile, Name.EvilBonePile },
                    { Flag.BodyPile, Name.BodyPile },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
                public const string GreydwarfNest = "$enemy_greydwarfspawner";
                public const string EvilBonePile = "$enemy_skeletonspawner";
                public const string BodyPile = "$enemy_draugrspawner";
            }

            [Flags]
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.GreydwarfNest)]
                GreydwarfNest = 1L << 0,

                [LocalizedDescription(Name.EvilBonePile)]
                EvilBonePile = 1L << 1,

                [LocalizedDescription(Name.BodyPile)]
                BodyPile = 1L << 2,

                [LocalizedDescription("@config_flags_all_label")]
                All = -1L,
            }
        }

        public static class Other
        {
            private static readonly Dictionary<string, Flag> NameByFlag;
            private static readonly Dictionary<Flag, string> FlagByName;

            static Other()
            {
                NameByFlag = new Dictionary<string, Flag>
                {
                    { Name.Vegvisir, Flag.Vegvisir },
                    { Name.Runestone, Flag.Runestone },
                    { Name.WildBeehive, Flag.WildBeehive },
                    { "Beehive", Flag.WildBeehive },
                    { Name.Portal, Flag.Portal },
                    { "Teleport", Flag.Portal },
                };

                FlagByName = new Dictionary<Flag, string>
                {
                    { Flag.Vegvisir, Name.Vegvisir },
                    { Flag.Runestone, Name.Runestone },
                    { Flag.WildBeehive, Name.WildBeehive },
                    { Flag.Portal, Name.Portal },
                };
            }

            public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

            public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

            public static class Name
            {
                public const string Vegvisir = "$piece_vegvisir";
                public const string Runestone = "$piece_lorestone";
                public const string WildBeehive = "@piece_wild_beehive";
                public const string Portal = "$piece_portal";
            }

            [Flags]
            public enum Flag : long
            {
                None = 0,

                [LocalizedDescription(Name.Vegvisir)]
                Vegvisir = 1L << 0,

                [LocalizedDescription(Name.Runestone)]
                Runestone = 1L << 1,

                [LocalizedDescription(Name.WildBeehive)]
                WildBeehive = 1L << 2,

                [LocalizedDescription(Name.Portal)]
                Portal = 1L << 3,

                [LocalizedDescription("@config_flags_all_label")]
                All = -1L,
            }
        }
    }
}