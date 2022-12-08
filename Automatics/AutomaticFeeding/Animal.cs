using System;
using JetBrains.Annotations;
using ModUtils;

namespace Automatics.AutomaticFeeding
{
    [Flags]
    internal enum Animal : long
    {
        [UsedImplicitly]
        None = 0,

        [LocalizedDescription(Automatics.L10NPrefix, "@config_automatic_feeding_animal_type_wild")]
        Wild = 1L << 0,

        [LocalizedDescription(Automatics.L10NPrefix, "@config_automatic_feeding_animal_type_tamed")]
        Tamed = 1L << 1,

        [UsedImplicitly]
        [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
        All = (1L << 2) - 1
    }
}