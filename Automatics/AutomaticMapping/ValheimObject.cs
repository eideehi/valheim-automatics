using JetBrains.Annotations;
using ModUtils;
using System;
using System.Collections.Generic;

namespace Automatics.AutomaticMapping
{
    public static class ValheimObject
    {
        private static readonly Dictionary<string, Spawner> Spawners;
        private static readonly Dictionary<Spawner, string> SpawnerNames;
        private static readonly Dictionary<string, Other> Others;
        private static readonly Dictionary<Other, string> OtherNames;

        static ValheimObject()
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

            Others = new Dictionary<string, Other>
            {
                { Name.Vegvisir, Other.Vegvisir },
                { Name.Runestone, Other.Runestone },
                { Name.WildBeehive, Other.WildBeehive },
                { "Beehive", Other.WildBeehive },
                { Name.Portal, Other.Portal },
                { "Teleport", Other.Portal },
            };

            OtherNames = new Dictionary<Other, string>
            {
                { Other.Vegvisir, Name.Vegvisir },
                { Other.Runestone, Name.Runestone },
                { Other.WildBeehive, Name.WildBeehive },
                { Other.Portal, Name.Portal },
            };
        }

        public static bool GetSpawner(string name, out Spawner spawner) => Spawners.TryGetValue(name, out spawner);

        public static bool IsSpawner(string name) => Spawners.ContainsKey(name);

        public static bool GetSpawnerName(Spawner spawner, out string name) =>
            SpawnerNames.TryGetValue(spawner, out name);

        public static bool GetOther(string name, out Other other) => Others.TryGetValue(name, out other);

        public static bool IsOther(string name) => Others.ContainsKey(name);

        public static bool GetOtherName(Other other, out string name) =>
            OtherNames.TryGetValue(other, out name);

        public static class Name
        {
            /* Spawners */
            public const string GreydwarfNest = "$enemy_greydwarfspawner";
            public const string EvilBonePile = "$enemy_skeletonspawner";
            public const string BodyPile = "$enemy_draugrspawner";

            /* Others */
            public const string Vegvisir = "$piece_vegvisir";
            public const string Runestone = "$piece_lorestone";
            public const string WildBeehive = "@piece_wildbeehive";
            public const string Portal = "$piece_portal";
        }

        [Flags]
        public enum Spawner : long
        {
            [UsedImplicitly]
            None = 0,

            [LocalizedDescription(Name.GreydwarfNest)]
            GreydwarfNest = 1L << 0,

            [LocalizedDescription(Name.EvilBonePile)]
            EvilBonePile = 1L << 1,

            [LocalizedDescription(Name.BodyPile)]
            BodyPile = 1L << 2,

            [UsedImplicitly]
            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 3) - 1,
        }

        [Flags]
        public enum Other : long
        {
            [UsedImplicitly]
            None = 0,

            [LocalizedDescription(Name.Vegvisir)]
            Vegvisir = 1L << 0,

            [LocalizedDescription(Name.Runestone)]
            Runestone = 1L << 1,

            [LocalizedDescription(Automatics.L10NPrefix, Name.WildBeehive)]
            WildBeehive = 1L << 2,

            [LocalizedDescription(Name.Portal)]
            Portal = 1L << 3,

            [UsedImplicitly]
            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 4) - 1,
        }
    }
}