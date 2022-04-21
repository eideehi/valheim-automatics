namespace Automatics.AutomaticFeeding
{
    internal static class Core
    {
        [AutomaticsInitializer(4)]
        private static void Initialize()
        {
            Config.Initialize();
        }
    }
}