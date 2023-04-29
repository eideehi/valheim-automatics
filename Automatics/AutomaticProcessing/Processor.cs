using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;

namespace Automatics.AutomaticProcessing
{
    internal class Processor
    {
        public static readonly Processor Beehive =
            new Processor("$piece_beehive", Process.Store, Process.None, Process.Store);

        public static readonly Processor Bonfire =
            new Processor("$piece_bonfire", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor BlastFurnace =
            new Processor("$piece_blastfurnace", Process.All ^ Process.Charge, Process.None,
                Process.Craft, Process.Refuel, Process.Store);

        public static readonly Processor Campfire =
            new Processor("$piece_firepit", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor CharcoalKiln =
            new Processor("$piece_charcoalkiln", Process.Craft | Process.Store, Process.None,
                Process.Craft, Process.Store);

        public static readonly Processor CookingStation =
            new Processor("$piece_cookingstation", Process.Store, Process.None, Process.Craft,
                Process.Store);

        public static readonly Processor Fermenter =
            new Processor("$piece_fermenter", Process.Craft | Process.Store, Process.None,
                Process.Craft, Process.Store);

        public static readonly Processor HangingBrazier =
            new Processor("$piece_brazierceiling01", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor Hearth =
            new Processor("$piece_hearth", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor IronCookingStation =
            new Processor("$piece_cookingstation_iron", Process.Store, Process.None, Process.Craft,
                Process.Store);

        public static readonly Processor JackOTurnip =
            new Processor("$piece_jackoturnip", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor Sconce =
            new Processor("$piece_sconce", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor Smelter =
            new Processor("$piece_smelter", Process.All ^ Process.Charge, Process.None,
                Process.Craft, Process.Refuel, Process.Store);

        public static readonly Processor SpinningWheel =
            new Processor("$piece_spinningwheel", Process.Store, Process.None, Process.Craft,
                Process.Store);

        public static readonly Processor StandingBlueBurningIronTorch =
            new Processor("$piece_groundtorchblue", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor StandingBrazier =
            new Processor("$piece_brazierfloor01", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor StandingGreenBurningIronTorch =
            new Processor("$piece_groundtorchgreen", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor StandingIronTorch =
            new Processor("$piece_groundtorch", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor StandingWoodTorch =
            new Processor("$piece_groundtorchwood", Process.Refuel, Process.None, Process.Refuel);

        public static readonly Processor StoneOven =
            new Processor("$piece_oven", Process.All ^ Process.Charge, Process.None, Process.Craft,
                Process.Refuel, Process.Store);

        public static readonly Processor Windmill =
            new Processor("$piece_windmill", Process.Store, Process.None, Process.Craft,
                Process.Store);

        public static readonly Processor WispFountain =
            new Processor("$piece_wisplure", Process.Store, Process.None, Process.Store);

        public static readonly Processor SapExtractor =
            new Processor("$piece_sapcollector", Process.Store, Process.None, Process.Store);

        public static readonly Processor EitrRefinery =
            new Processor("$piece_eitrrefinery", Process.Store, Process.None, Process.Craft,
                Process.Refuel, Process.Store);

        public static readonly Processor Ballista =
            new Processor("$piece_turret", Process.Charge, Process.None, Process.Charge);

        private static readonly List<Processor> AllInstance;

        static Processor()
        {
            AllInstance = new List<Processor>
            {
                Beehive,
                Bonfire,
                BlastFurnace,
                Campfire,
                CharcoalKiln,
                CookingStation,
                Fermenter,
                HangingBrazier,
                Hearth,
                IronCookingStation,
                JackOTurnip,
                Sconce,
                Smelter,
                SpinningWheel,
                StandingBlueBurningIronTorch,
                StandingBrazier,
                StandingGreenBurningIronTorch,
                StandingIronTorch,
                StandingWoodTorch,
                StoneOven,
                Windmill,
                WispFountain,
                SapExtractor,
                EitrRefinery,
                Ballista,
            };
        }

        public static IEnumerable<Processor> GetAllInstance()
        {
            return AllInstance.ToList();
        }

        public readonly string name;
        public readonly Process defaultAllowedProcesses;
        public readonly IEnumerable<Process> processes;

        private Processor(string name, Process defaultAllowedProcesses, params Process[] processes)
        {
            this.name = name;
            this.processes = new List<Process>(processes);
            this.defaultAllowedProcesses = defaultAllowedProcesses;
        }
    }

    [Flags]
    internal enum Process : long
    {
        None = 0,

        [LocalizedDescription(Automatics.L10NPrefix,
            "@config_automatic_processing_processing_type_craft")]
        Craft = 1L << 0,

        [LocalizedDescription(Automatics.L10NPrefix,
            "@config_automatic_processing_processing_type_refuel")]
        Refuel = 1L << 1,

        [LocalizedDescription(Automatics.L10NPrefix,
            "@config_automatic_processing_processing_type_store")]
        Store = 1L << 2,

        [LocalizedDescription(Automatics.L10NPrefix,
            "@config_automatic_processing_processing_type_charge")]
        Charge = 1L << 3,

        [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
        All = (1L << 4) - 1
    }
}