using System;
using Automatics.ModUtils;
using JetBrains.Annotations;

namespace Automatics.AutomaticFeeding
{
    [Flags]
    internal enum Animal : long
    {
        [UsedImplicitly]
        None = 0,

        [LocalizedDescription("@config_automatic_feeding_animal_type_wild")]
        Wild = 1L << 0,

        [LocalizedDescription("@config_automatic_feeding_animal_type_tamed")]
        Tamed = 1L << 1,

        [UsedImplicitly]
        [LocalizedDescription("@select_all")]
        All = (1L << 2) - 1,
    }
}