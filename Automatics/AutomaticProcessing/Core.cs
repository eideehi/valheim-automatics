using static Automatics.ModUtils.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticProcessing
{
    internal static class Core
    {
        [AutomaticsInitializer(3)]
        private static void Initialize()
        {
            Config.Initialize();
        }

        public static IEnumerable<Container> GetNearbyContainers(string target, Vector3 origin)
        {
            var range = Config.GetContainerSearchRange(target);
            return range <= 0
                ? Enumerable.Empty<Container>()
                : from x in ContainerCache.GetAllInstance()
                let distance = Vector3.Distance(origin, x.transform.position)
                where distance <= range
                orderby distance descending
                select x;
        }
    }

    internal static class Target
    {
        //public const string ArtisanTable = "$piece_artisanstation";
        public const string Beehive = "$piece_beehive";
        public const string Bonfire = "$piece_bonfire";
        public const string BlastFurnace = "$piece_blastfurnace";
        public const string Campfire = "$piece_firepit";
        //public const string Cauldron = "$piece_cauldron";
        public const string CharcoalKiln = "$piece_charcoalkiln";
        public const string CookingStation = "$piece_cookingstation";
        public const string Fermenter = "$piece_fermenter";
        //public const string Forge = "$piece_forge";
        public const string HangingBrazier = "$piece_brazierceiling01";
        public const string Hearth = "$piece_hearth";
        public const string IronCookingStation = "$piece_cookingstation_iron";
        public const string JackOTurnip = "$piece_jackoturnip";
        public const string Sconce = "$piece_sconce";
        public const string Smelter = "$piece_smelter";
        public const string SpinningWheel = "$piece_spinningwheel";
        public const string StandingBlueBurningIronTorch = "$piece_groundtorchblue";
        public const string StandingBrazier = "$piece_brazierfloor01";
        public const string StandingGreenBurningIronTorch = "$piece_groundtorchgreen";
        public const string StandingIronTorch = "$piece_groundtorch";
        public const string StandingWoodTorch = "$piece_groundtorchwood";
        public const string StoneOven = "$piece_oven";
        //public const string Stonecutter = "$piece_stonecutter";
        public const string Windmill = "$piece_windmill";
        //public const string Workbench = "$piece_workbench";

        public static readonly IList<string> All;

        static Target()
        {
            All = Array.AsReadOnly(new[]
            {
                //ArtisanTable,
                Beehive,
                Bonfire,
                BlastFurnace,
                Campfire,
                //Cauldron,
                CharcoalKiln,
                CookingStation,
                Fermenter,
                //Forge,
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
                //Stonecutter,
                Windmill,
                //Workbench,
            });
        }
    }

    [Flags]
    internal enum Type : long
    {
        None = 0,

        [LocalizedDescription("@config_automatic_processing_type_craft")]
        Craft = 1L << 0,

        [LocalizedDescription("@config_automatic_processing_type_refuel")]
        Refuel = 1L << 1,

        [LocalizedDescription("@config_automatic_processing_type_store")]
        Store = 1L << 2,

        [LocalizedDescription("@select_all")]
        All = -1L,
    }
}