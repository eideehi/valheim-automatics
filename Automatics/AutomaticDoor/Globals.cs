using Automatics.Valheim;
using ModUtils;

namespace Automatics.AutomaticDoor
{
    internal static class Globals
    {
        public static ValheimObject Door { get; } = new ValheimObject("door");
    }

    internal static class Logics
    {
        public static bool IsAllowAutomaticDoor(Door door)
        {
            return Globals.Door.GetIdentify(Objects.GetName(door), out var identifier) &&
                   Config.AllowAutomaticDoor.Contains(identifier);
        }
    }
}