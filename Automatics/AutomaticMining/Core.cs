namespace Automatics.AutomaticMining
{
    internal static class Core
    {
        [AutomaticsInitializer(6)]
        private static void Initialize()
        {
            Config.Initialize();

            Hooks.OnPlayerUpdate += AutomaticMining.Run;
        }
    }
}