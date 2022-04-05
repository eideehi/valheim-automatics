using static Automatics.ModUtils.Configuration;
using System;
using System.Collections.Generic;

namespace Automatics.AutomaticDoor
{
    public static class ValheimDoor
    {
        private static readonly Dictionary<string, Flag> NameByFlag;
        private static readonly Dictionary<Flag, string> FlagByName;

        static ValheimDoor()
        {
            NameByFlag = new Dictionary<string, Flag>
            {
                { Name.WoodDoor, Flag.WoodDoor },
                { Name.WoodGate, Flag.WoodGate },
                { Name.IronGate, Flag.IronGate },
                { Name.DarkwoodGate, Flag.DarkwoodGate },
                { Name.WoodShutter, Flag.WoodShutter },
            };

            FlagByName = new Dictionary<Flag, string>
            {
                { Flag.WoodDoor, Name.WoodDoor },
                { Flag.WoodGate, Name.WoodGate },
                { Flag.IronGate, Name.IronGate },
                { Flag.DarkwoodGate, Name.DarkwoodGate },
                { Flag.WoodShutter, Name.WoodShutter },
            };
        }

        public static bool GetFlag(string name, out Flag result) => NameByFlag.TryGetValue(name, out result);

        public static bool GetName(Flag flag, out string result) => FlagByName.TryGetValue(flag, out result);

        public static class Name
        {
            public const string WoodDoor = "$piece_wooddoor";
            public const string WoodGate = "$piece_woodgate";
            public const string IronGate = "$piece_irongate";
            public const string DarkwoodGate = "$piece_darkwoodgate";
            public const string WoodShutter = "$piece_woodwindowshutter";
        }

        [Flags]
        public enum Flag : long
        {
            None = 0,

            [LocalizedDescription(Name.WoodDoor)]
            WoodDoor = 1L << 0,

            [LocalizedDescription(Name.WoodGate)]
            WoodGate = 1L << 1,

            [LocalizedDescription(Name.IronGate)]
            IronGate = 1L << 2,

            [LocalizedDescription(Name.DarkwoodGate)]
            DarkwoodGate = 1L << 3,

            [LocalizedDescription(Name.WoodShutter)]
            WoodShutter = 1L << 4,

            [LocalizedDescription("@config_flags_all_label")]
            All = -1L,
        }
    }
}