using Automatics.Valheim;
using HarmonyLib;
using ModUtils;

namespace Automatics.AutomaticMapping
{
    internal static class Module
    {
        [AutomaticsInitializer(2)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.ModuleDisabled) return;

            PickableCache.OnCacheAdded += pickable =>
            {
                if (ValheimObject.Flora.IsDefined(Objects.GetName(pickable)))
                    pickable.gameObject.AddComponent<FloraNode>();
            };

            ShipCache.OnCacheAdded += ship =>
            {
                var wearNTear = ship.GetComponent<WearNTear>();
                if (wearNTear)
                    wearNTear.m_onDestroyed += () => DynamicObjectMapping.OnObjectDestroy(ship);
            };

            Hooks.OnPlayerAwake += (player, zNetView) =>
            {
                if (Player.m_localPlayer == player)
                    AutomaticMapping.Cleanup();
            };
            Hooks.OnPlayerUpdate += (player, takeInput) => { Navigation.Update(player); };
            Hooks.OnPlayerFixedUpdate += AutomaticMapping.DynamicMapping;

            var harmonyId = Automatics.GetHarmonyId("automatic-mapping");
            Harmony.CreateAndPatchAll(typeof(Patches), harmonyId);
            Harmony.CreateAndPatchAll(typeof(Patches.RandomFlyingBird_Initialize_Patch), harmonyId);
        }
    }
}
