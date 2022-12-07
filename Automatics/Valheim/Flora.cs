using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;

namespace Automatics.Valheim
{
    internal static class Flora
    {
        [Flags]
        public enum Flags : long
        {
            None = 0,

            [LocalizedDescription(Names.Dandelion)]
            Dandelion = 1L << 0,

            [LocalizedDescription(Names.Mushroom)]
            Mushroom = 1L << 1,

            [LocalizedDescription(Names.Raspberries)]
            Raspberries = 1L << 2,

            [LocalizedDescription(Names.Blueberries)]
            Blueberries = 1L << 3,

            [LocalizedDescription(Names.Carrot)]
            Carrot = 1L << 4,

            [LocalizedDescription(Names.CarrotSeeds)]
            CarrotSeeds = 1L << 5,

            [LocalizedDescription(Names.YellowMushroom)]
            YellowMushroom = 1L << 6,

            [LocalizedDescription(Names.Thistle)]
            Thistle = 1L << 7,

            [LocalizedDescription(Names.Turnip)]
            Turnip = 1L << 8,

            [LocalizedDescription(Names.TurnipSeeds)]
            TurnipSeeds = 1L << 9,

            [LocalizedDescription(Names.Onion)]
            Onion = 1L << 10,

            [LocalizedDescription(Names.OnionSeeds)]
            OnionSeeds = 1L << 11,

            [LocalizedDescription(Names.Barley)]
            Barley = 1L << 12,

            [LocalizedDescription(Names.Cloudberries)]
            Cloudberries = 1L << 13,

            [LocalizedDescription(Names.Flex)]
            Flex = 1L << 14,

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 15) - 1
        }

        private static readonly Dictionary<string, Flags> NameByFlags;

        static Flora()
        {
            NameByFlags = new Dictionary<string, Flags>
            {
                { Names.Dandelion, Flags.Dandelion },
                { Names.Mushroom, Flags.Mushroom },
                { Names.Raspberries, Flags.Raspberries },
                { Names.Blueberries, Flags.Blueberries },
                { Names.Carrot, Flags.Carrot },
                { Names.CarrotSeeds, Flags.CarrotSeeds },
                { Names.YellowMushroom, Flags.YellowMushroom },
                { Names.Thistle, Flags.Thistle },
                { Names.Turnip, Flags.Turnip },
                { Names.TurnipSeeds, Flags.TurnipSeeds },
                { Names.Onion, Flags.Onion },
                { Names.OnionSeeds, Flags.OnionSeeds },
                { Names.Barley, Flags.Barley },
                { Names.Cloudberries, Flags.Cloudberries },
                { Names.Flex, Flags.Flex }
            };
        }

        public static Flags None => Flags.None;
        public static Flags Dandelion => Flags.Dandelion;
        public static Flags Mushroom => Flags.Mushroom;
        public static Flags Raspberries => Flags.Raspberries;
        public static Flags Blueberries => Flags.Blueberries;
        public static Flags Carrot => Flags.Carrot;
        public static Flags CarrotSeeds => Flags.CarrotSeeds;
        public static Flags YellowMushroom => Flags.YellowMushroom;
        public static Flags Thistle => Flags.Thistle;
        public static Flags Turnip => Flags.Turnip;
        public static Flags TurnipSeeds => Flags.TurnipSeeds;
        public static Flags Onion => Flags.Onion;
        public static Flags OnionSeeds => Flags.OnionSeeds;
        public static Flags Barley => Flags.Barley;
        public static Flags Cloudberries => Flags.Cloudberries;
        public static Flags Flex => Flags.Flex;
        public static Flags All => Flags.All;

        public static bool Get(string name, out Flags flora)
        {
            return NameByFlags.TryGetValue(name, out flora);
        }

        public static bool IsFlora(string name)
        {
            return NameByFlags.ContainsKey(name);
        }

        public static bool GetName(Flags flora, out string name)
        {
            var floraName = NameByFlags
                .Where(pair => pair.Value == flora)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            var empty = string.IsNullOrEmpty(floraName);
            name = empty ? string.Empty : floraName;
            return !empty;
        }

        [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
        private static class Names
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
    }
}