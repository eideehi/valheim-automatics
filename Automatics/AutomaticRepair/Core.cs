namespace Automatics.AutomaticRepair
{
    internal static class Core
    {
        [AutomaticsInitializer(5)]
        private static void Initialize()
        {
            Config.Initialize();
            Hooks.OnPlayerUpdate += RepairItems.Run;
            Hooks.OnPlayerUpdate += RepairPieces.Run;
        }
    }
}