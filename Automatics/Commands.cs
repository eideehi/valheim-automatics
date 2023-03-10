using Automatics.ConsoleCommands;

namespace Automatics
{
    internal static class Commands
    {
        public static void Register()
        {
            new ShowCommands().Register();
            new PrintNames().Register();
            new PrintObjects().Register();
        }
    }
}