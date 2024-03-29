﻿using Automatics.Valheim;
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

            FejdStartup.startGameEvent += (startup, args) => { AutomaticMapping.Cleanup(); };

            Harmony.CreateAndPatchAll(typeof(Patches),
                Automatics.GetHarmonyId("automatic-mapping"));
        }
    }
}