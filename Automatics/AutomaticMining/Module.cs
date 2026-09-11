using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMining
{
    /// <summary>
    ///     Automatic Mining on the module lifecycle. A scope owns its patch, three hook
    ///     subscriptions and every <see cref="AutomaticMining" /> component it attaches, so
    ///     turning the module off removes them without interrupting a mining pass already begun.
    /// </summary>
    internal sealed class Module : IAutomaticsModule
    {
        public static readonly Module Instance = new Module();

        internal static bool Active;
        internal static bool StopRequested;
        internal static bool Running => Active && !StopRequested;
        internal static int Generation;
        internal static ModuleScope CurrentScope;
        internal static bool Discovering => Active && CurrentScope != null;

        private static readonly Dictionary<long, float> MiningTimers;

        internal static int MiningTimerCount => MiningTimers.Count;

        static Module()
        {
            MiningTimers = new Dictionary<long, float>();
        }

        private Module()
        {
        }

        public string Name => "automatic-mining";
        public string ConfigSection => "automatic_mining";
        public bool SupportsRuntimeToggle => true;
        public ConfigEntry<AutomaticsModule> ModuleToggle => Config.ModuleEntry;

        public void SetStopRequested(bool value)
        {
            StopRequested = value;
        }

        public void BindConfig()
        {
            Config.Initialize();
        }

        /// <summary>
        ///     Acquires the patch before the subscriptions and scene components. The scope tears
        ///     them down in reverse, leaving the patch installed until its callbacks and owned
        ///     components are inactive.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            Generation = scope.Generation;
            CurrentScope = scope;

            scope.PatchAll(typeof(Patches));

            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnPlayerAwake += h,
                h => Hooks.OnPlayerAwake -= h,
                OnPlayerAwake);
            scope.Subscribe<Action<Player, bool>>(
                h => Hooks.OnPlayerUpdate += h,
                h => Hooks.OnPlayerUpdate -= h,
                OnPlayerUpdate);
            scope.Subscribe<Action<Player, float>>(
                h => Hooks.OnPlayerFixedUpdate += h,
                h => Hooks.OnPlayerFixedUpdate -= h,
                OnPlayerFixedUpdate);

            ReconcileMinerals(scope);
            Active = true;
        }

        /// <summary>
        ///     Stops new mining before clearing session bookkeeping. A mining pass that already
        ///     crossed its entry gate finishes its damage and pickaxe bookkeeping.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            Generation = 0;
            CurrentScope = null;
            Cleanup();
        }

        private static void Cleanup()
        {
            MiningTimers.Clear();
        }

        [AutomaticsInitializer(6)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        private static void OnPlayerAwake(Player player, ZNetView nview)
        {
            Cleanup();
        }

        internal static void AttachMineral(GameObject target)
        {
            if (!Discovering) return;

            CurrentScope.TryAttach<AutomaticMining>(target, out _);
        }

        private static void ReconcileMinerals(ModuleScope scope)
        {
            foreach (var destructible in UnityEngine.Object.FindObjectsByType<Destructible>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(destructible, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticMining>(destructible.gameObject);
            }

            foreach (var mineRock in UnityEngine.Object.FindObjectsByType<MineRock>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(mineRock, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticMining>(mineRock.gameObject);
            }

            foreach (var mineRock5 in UnityEngine.Object.FindObjectsByType<MineRock5>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(mineRock5, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticMining>(mineRock5.gameObject);
            }
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (!Module.Running) return;
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
            if (!Module.Running) return;
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
