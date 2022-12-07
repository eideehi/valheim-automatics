using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;

namespace Automatics.Valheim
{
    internal static class Animal
    {
        [Flags]
        public enum Flags : long
        {
            None = 0,

            [LocalizedDescription(Names.Boar)]
            Boar = 1L << 0,

            [LocalizedDescription(Names.Deer)]
            Deer = 1L << 1,

            [LocalizedDescription(Names.Wolf)]
            Wolf = 1L << 2,

            [LocalizedDescription(Names.Lox)]
            Lox = 1L << 3,

            [LocalizedDescription(Automatics.L10NPrefix, Names.Bird)]
            Bird = 1L << 4,

            [LocalizedDescription(Names.Fish)]
            Fish = 1L << 5,

            [LocalizedDescription(Automatics.L10NPrefix, "@select_all")]
            All = (1L << 6) - 1
        }

        private static readonly Dictionary<string, Flags> NameByFlags;

        static Animal()
        {
            NameByFlags = new Dictionary<string, Flags>
            {
                { Names.Boar, Flags.Boar },
                { Names.Deer, Flags.Deer },
                { Names.Wolf, Flags.Wolf },
                { Names.Lox, Flags.Lox },
                { Names.Bird, Flags.Bird },
                { "Seagal", Flags.Bird },
                { "Crow", Flags.Bird },
                { Names.Fish, Flags.Fish },
                { "$animal_fish1", Flags.Fish },
                { "$animal_fish2", Flags.Fish },
                { "$animal_fish3", Flags.Fish }
            };
        }

        public static Flags None => Flags.None;
        public static Flags Boar => Flags.Boar;
        public static Flags Deer => Flags.Deer;
        public static Flags Wolf => Flags.Wolf;
        public static Flags Lox => Flags.Lox;
        public static Flags Bird => Flags.Bird;
        public static Flags Fish => Flags.Fish;
        public static Flags All => Flags.All;

        public static bool Get(string name, out Flags animal)
        {
            return NameByFlags.TryGetValue(name, out animal);
        }

        public static bool IsAnimal(string name)
        {
            return NameByFlags.ContainsKey(name);
        }

        public static bool GetName(Flags animal, out string name)
        {
            var animalName = NameByFlags
                .Where(pair => pair.Value == animal)
                .Select(pair => pair.Key)
                .FirstOrDefault();
            var empty = string.IsNullOrEmpty(animalName);
            name = empty ? string.Empty : animalName;
            return !empty;
        }

        [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
        private static class Names
        {
            public const string Boar = "$enemy_boar";
            public const string Deer = "$enemy_deer";
            public const string Wolf = "$enemy_wolf";
            public const string Lox = "$enemy_lox";
            public const string Bird = "@animal_bird";
            public const string Fish = "$animal_fish";
        }
    }
}