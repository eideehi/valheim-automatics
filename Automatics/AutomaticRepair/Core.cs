namespace Automatics.AutomaticRepair
{
    internal static class Core
    {
        [AutomaticsInitializer(5)]
        private static void Initialize()
        {
            Config.Initialize();
            Automatics.OnPlayerUpdate += RepairItems.Run;
            Automatics.OnPlayerUpdate += RepairPieces.Run;
        }
    }
}