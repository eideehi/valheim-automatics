﻿using System;
using System.Collections.Generic;
using ModUtils;

namespace Automatics.AutomaticDoor
{
    public static class ValheimDoor
    {
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

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 5) - 1
        }

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
                { Name.WoodShutter, Flag.WoodShutter }
            };

            FlagByName = new Dictionary<Flag, string>
            {
                { Flag.WoodDoor, Name.WoodDoor },
                { Flag.WoodGate, Name.WoodGate },
                { Flag.IronGate, Name.IronGate },
                { Flag.DarkwoodGate, Name.DarkwoodGate },
                { Flag.WoodShutter, Name.WoodShutter }
            };
        }

        public static bool GetFlag(string name, out Flag result)
        {
            return NameByFlag.TryGetValue(name, out result);
        }

        public static bool GetName(Flag flag, out string result)
        {
            return FlagByName.TryGetValue(flag, out result);
        }

        public static class Name
        {
            public const string WoodDoor = "$piece_wooddoor";
            public const string WoodGate = "$piece_woodgate";
            public const string IronGate = "$piece_irongate";
            public const string DarkwoodGate = "$piece_darkwoodgate";
            public const string WoodShutter = "$piece_woodwindowshutter";
        }
    }
}