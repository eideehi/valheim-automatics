using System;
using System.Collections.Generic;
using Automatics.ModUtils;
using JetBrains.Annotations;

namespace Automatics.AutomaticMapPinning
{
    using Description = Configuration.LocalizedDescriptionAttribute;

    public static class OtherObject
    {
        private static readonly Dictionary<string, Spawner> Spawners;
        private static readonly Dictionary<Spawner, string> SpawnerNames;
        private static readonly Dictionary<string, Etcetera> Etceteras;
        private static readonly Dictionary<Etcetera, string> EtceteraNames;

        static OtherObject()
        {
            Spawners = new Dictionary<string, Spawner>
            {
                { Name.GreydwarfNest, Spawner.GreydwarfNest },
                { Name.EvilBonePile, Spawner.EvilBonePile },
                { Name.BodyPile, Spawner.BodyPile },
            };

            SpawnerNames = new Dictionary<Spawner, string>
            {
                { Spawner.GreydwarfNest, Name.GreydwarfNest },
                { Spawner.EvilBonePile, Name.EvilBonePile },
                { Spawner.BodyPile, Name.BodyPile },
            };

            Etceteras = new Dictionary<string, Etcetera>
            {
                { Name.Vegvisir, Etcetera.Vegvisir },
                { Name.Runestone, Etcetera.Runestone },
                { Name.WildBeehive, Etcetera.WildBeehive },
                { "Beehive", Etcetera.WildBeehive },
                { Name.Portal, Etcetera.Portal },
                { "Teleport", Etcetera.Portal },
            };

            EtceteraNames = new Dictionary<Etcetera, string>
            {
                { Etcetera.Vegvisir, Name.Vegvisir },
                { Etcetera.Runestone, Name.Runestone },
                { Etcetera.WildBeehive, Name.WildBeehive },
                { Etcetera.Portal, Name.Portal },
            };
        }

        public static bool GetSpawner(string name, out Spawner spawner) => Spawners.TryGetValue(name, out spawner);

        public static bool IsSpawner(string name) => Spawners.ContainsKey(name);

        public static bool GetSpawnerName(Spawner spawner, out string name) =>
            SpawnerNames.TryGetValue(spawner, out name);

        public static bool GetEtcetera(string name, out Etcetera etcetera) => Etceteras.TryGetValue(name, out etcetera);

        public static bool IsEtcetera(string name) => Etceteras.ContainsKey(name);

        public static bool GetEtceteraName(Etcetera etcetera, out string name) =>
            EtceteraNames.TryGetValue(etcetera, out name);

        public static class Name
        {
            /* Spawners */
            public const string GreydwarfNest = "$enemy_greydwarfspawner";
            public const string EvilBonePile = "$enemy_skeletonspawner";
            public const string BodyPile = "$enemy_draugrspawner";

            /* Others */
            public const string Vegvisir = "$piece_vegvisir";
            public const string Runestone = "$piece_lorestone";
            public const string WildBeehive = "@piece_wild_beehive";
            public const string Portal = "$piece_portal";
        }

        [Flags]
        public enum Spawner : long
        {
            [UsedImplicitly]
            None = 0,

            [Description(Name.GreydwarfNest)]
            GreydwarfNest = 1L << 0,

            [Description(Name.EvilBonePile)]
            EvilBonePile = 1L << 1,

            [Description(Name.BodyPile)]
            BodyPile = 1L << 2,

            [UsedImplicitly]
            [Description("@select_all")]
            All = -1L,
        }

        [Flags]
        public enum Etcetera : long
        {
            [UsedImplicitly]
            None = 0,

            [Description(Name.Vegvisir)]
            Vegvisir = 1L << 0,

            [Description(Name.Runestone)]
            Runestone = 1L << 1,

            [Description(Name.WildBeehive)]
            WildBeehive = 1L << 2,

            [Description(Name.Portal)]
            Portal = 1L << 3,

            [UsedImplicitly]
            [Description("@select_all")]
            All = -1L,
        }
    }
}