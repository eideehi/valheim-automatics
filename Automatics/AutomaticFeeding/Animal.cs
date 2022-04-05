using System;
using static Automatics.ModUtils.Configuration;

namespace Automatics.AutomaticFeeding
{
    [Flags]
    internal enum Animal : long
    {
        None = 0,

        [LocalizedDescription("@config_automatic_feed_animal_type_wild")]
        Wild = 1L << 0,

        [LocalizedDescription("@config_automatic_feed_animal_type_tamed")]
        Tamed = 1L << 1,

        [LocalizedDescription("@select_all")]
        All = -1L
    }
}