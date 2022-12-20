using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticMining
{
    internal static class Module
    {
        private static readonly Dictionary<long, float> MiningTimers;

        static Module()
        {
            MiningTimers = new Dictionary<long, float>();
        }

        [AutomaticsInitializer(6)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.IsModuleDisabled) return;

            Hooks.OnPlayerUpdate += OnPlayerUpdate;
            Hooks.OnPlayerFixedUpdate += OnPlayerFixedUpdate;
            Harmony.CreateAndPatchAll(typeof(Patches), Automatics.GetHarmonyId("automatic-mining"));
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (!Config.EnableAutomaticMining) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (!takeInput) return;
            if (Config.MiningInterval < 0.1f) return;
            if (Config.MiningKey.MainKey == KeyCode.None) return;

            if (Config.MiningKey.IsDown())
                AutomaticMining.TryMining(player);
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            if (!Config.EnableAutomaticMining) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (Config.MiningInterval < 0.1f) return;
            if (Config.MiningKey.MainKey != KeyCode.None) return;

            var id = player.GetPlayerID();
            if (!MiningTimers.TryGetValue(id, out var timer))
                timer = 0f;

            timer += delta;
            if (timer >= Config.MiningInterval)
            {
                MiningTimers[id] = 0f;
                AutomaticMining.TryMining(player);
            }
            else
            {
                MiningTimers[id] = timer;
            }
        }
    }
}