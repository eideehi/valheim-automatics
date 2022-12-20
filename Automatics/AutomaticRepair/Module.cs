using System.Collections.Generic;
using HarmonyLib;

namespace Automatics.AutomaticRepair
{
    internal static class Module
    {
        private static readonly Dictionary<long, float> ItemRepairTimers;
        private static readonly Dictionary<long, float> PieceRepairTimers;

        static Module()
        {
            ItemRepairTimers = new Dictionary<long, float>();
            PieceRepairTimers = new Dictionary<long, float>();
        }

        [AutomaticsInitializer(5)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.IsModuleDisabled) return;

            Hooks.OnPlayerFixedUpdate += OnPlayerFixedUpdate;
            Harmony.CreateAndPatchAll(typeof(Patches), Automatics.GetHarmonyId("automatic-repair"));
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            if (!Config.EnableAutomaticRepair) return;
            if (Game.IsPaused()) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;

            var id = player.GetPlayerID();
            if (!ItemRepairTimers.TryGetValue(id, out var timer))
                timer = 0f;

            timer += delta;
            if (timer >= 1f)
            {
                ItemRepairTimers[id] = 0f;
                ItemRepair.Repair(player);
            }
            else
            {
                ItemRepairTimers[id] = timer;
            }

            if (!PieceRepairTimers.TryGetValue(id, out timer))
                timer = 0f;

            timer += delta;
            if (timer >= 1f)
            {
                PieceRepairTimers[id] = 0f;
                PieceRepair.Repair(player);
            }
            else
            {
                PieceRepairTimers[id] = timer;
            }
        }
    }
}