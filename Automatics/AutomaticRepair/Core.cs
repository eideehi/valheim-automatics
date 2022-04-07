using static Automatics.ModUtils.Configuration;

namespace Automatics.AutomaticRepair
{
    internal static class Core
    {
        public static void Initialize()
        {
            Config.Initialize();
            Automatics.OnPlayerUpdate += RepairItems.Run;
            Automatics.OnPlayerUpdate += RepairPieces.Run;
        }
    }

    internal enum RepairMessage
    {
        [LocalizedDescription("@config_automatic_repair_repair_message_none")]
        None,

        [LocalizedDescription("@config_automatic_repair_repair_message_center")]
        Center,

        [LocalizedDescription("@config_automatic_repair_repair_message_top_left")]
        TopLeft,
    }
}