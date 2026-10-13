using Automatics.Valheim;

namespace Automatics.AutomaticWoodcutting
{
    internal static class Globals
    {
        // Matched on the prefab name of the standing tree. Logs and stumps take the species of the
        // tree whose prefabs lead to them; see TreeSpecies.
        public static ValheimObject Tree { get; } = new ValheimObject("tree");
    }
}
