namespace Automatics.AutomaticFeeding
{
    internal static class Logics
    {
        public static bool IsAllowToFeedFromContainer(AnimalType type)
        {
            return (Config.AllowToFeedFromContainer & type) != 0;
        }

        public static bool IsAllowToFeedFromPlayer(AnimalType type)
        {
            return (Config.AllowToFeedFromPlayer & type) != 0;
        }
    }
}