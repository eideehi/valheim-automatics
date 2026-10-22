using System;
using System.Collections.Generic;
using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     Automatic Woodcutting on the module lifecycle. A scope owns its patches, three hook
    ///     subscriptions, one tree data subscription and every
    ///     <see cref="AutomaticWoodcutting" /> component it attaches, so
    ///     turning the module off removes them without interrupting a woodcutting pass already
    ///     begun. Passes and area woodcutting are peer features: enable_automatic_woodcutting
    ///     turns only the passes off.
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

        private static readonly Dictionary<long, float> WoodcuttingTimers;

        internal static int WoodcuttingTimerCount => WoodcuttingTimers.Count;

        static Module()
        {
            WoodcuttingTimers = new Dictionary<long, float>();
        }

        private Module()
        {
        }

        public string Name => "automatic-woodcutting";
        public string ConfigSection => "automatic_woodcutting";
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
        ///     Acquires the patches before the subscriptions and scene components. The scope tears
        ///     them down in reverse, leaving the patches installed until its callbacks and owned
        ///     components are inactive.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            Generation = scope.Generation;
            CurrentScope = scope;

            scope.PatchAll(typeof(Patches));

            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnLocalPlayerAwake += h,
                h => Hooks.OnLocalPlayerAwake -= h,
                OnLocalPlayerAwake);
            scope.Subscribe<Action<Player, bool>>(
                h => Hooks.OnPlayerUpdate += h,
                h => Hooks.OnPlayerUpdate -= h,
                OnPlayerUpdate);
            scope.Subscribe<Action<Player, float>>(
                h => Hooks.OnPlayerFixedUpdate += h,
                h => Hooks.OnPlayerFixedUpdate -= h,
                OnPlayerFixedUpdate);
            // A custom tree changed while the module was off reached no handler, so what was
            // resolved before is dropped.
            TreeSpecies.ClearSpecies();
            scope.Subscribe<Action<ValheimObject>>(
                h => ValheimObject.RegistryChanged += h,
                h => ValheimObject.RegistryChanged -= h,
                TreeSpecies.OnRegistryChanged);

            ReconcileTrees(scope);
            Active = true;
        }

        /// <summary>
        ///     Stops new woodcutting before clearing session bookkeeping. A woodcutting pass that
        ///     already crossed its entry gate finishes its hit and axe bookkeeping.
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
            WoodcuttingTimers.Clear();
            AreaWoodcutting.Reset();
        }

        [AutomaticsInitializer(11)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        private static void OnLocalPlayerAwake(Player player, ZNetView nview)
        {
            Cleanup();
        }

        internal static void AttachTree(GameObject target)
        {
            if (!Discovering) return;

            CurrentScope.TryAttach<AutomaticWoodcutting>(target, out _);
        }

        /// <summary>
        ///     Whether a Destructible is a woodcutting target: the stump of a tree, that is the stump
        ///     prefab of a standing tree prefab, or a small tree, one whose own prefab name the tree
        ///     data names. Any other Destructible, a bush or a planted sapling among them, is not.
        /// </summary>
        internal static bool IsTargetDestructible(GameObject target, ZDO zdo)
        {
            return TreeSpecies.IsStub(zdo.GetPrefab()) ||
                   Globals.Tree.GetIdentify(Objects.GetPrefabName(target), out _);
        }

        internal static void AttachDestructible(GameObject target, ZDO zdo)
        {
            if (!Discovering) return;
            if (!IsTargetDestructible(target, zdo)) return;

            CurrentScope.TryAttach<AutomaticWoodcutting>(target, out _);
        }

        private static void ReconcileTrees(ModuleScope scope)
        {
            foreach (var tree in UnityEngine.Object.FindObjectsByType<TreeBase>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(tree, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticWoodcutting>(tree.gameObject);
            }

            foreach (var log in UnityEngine.Object.FindObjectsByType<TreeLog>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(log, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticWoodcutting>(log.gameObject);
            }

            foreach (var destructible in UnityEngine.Object.FindObjectsByType<Destructible>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(destructible, out var zNetView)) continue;
                var zdo = zNetView.GetZDO();
                if (zdo == null || !IsTargetDestructible(destructible.gameObject, zdo)) continue;

                scope.Attach<AutomaticWoodcutting>(destructible.gameObject);
            }
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (!Module.Running) return;
            if (!Config.EnableAutomaticWoodcutting) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (!takeInput) return;
            if (Config.WoodcuttingKey.MainKey == KeyCode.None) return;
            if (!HotkeyInput.IsDown(Config.WoodcuttingKey)) return;

            RunKeyPass(player);
        }

        /// <summary>
        ///     What the key does when it goes down: one pass whatever the interval is, and the
        ///     interval starts over.
        /// </summary>
        internal static void RunKeyPass(Player player)
        {
            WoodcuttingTimers[player.GetPlayerID()] = 0f;
            AutomaticWoodcutting.TryWoodcutting(player);
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            if (!Module.Running) return;
            if (!Config.EnableAutomaticWoodcutting) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            // An interval of 0 turns the periodic passes off; the key still works.
            if (Config.WoodcuttingInterval <= 0f) return;

            var id = player.GetPlayerID();
            if (!WoodcuttingTimers.TryGetValue(id, out var timer))
                timer = 0f;

            timer += delta;
            if (timer >= Config.WoodcuttingInterval)
            {
                WoodcuttingTimers[id] = 0f;
                AutomaticWoodcutting.TryWoodcutting(player);
            }
            else
            {
                WoodcuttingTimers[id] = timer;
            }
        }
    }
}
