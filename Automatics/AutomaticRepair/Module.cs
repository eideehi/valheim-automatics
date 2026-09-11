using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Automatics.AutomaticRepair
{
    /// <summary>
    ///     A scope owns the repair patch and two player subscriptions. Repair attaches no scene
    ///     component: existing stations and pieces are reached on the next interaction or scan.
    /// </summary>
    internal sealed class Module : IAutomaticsModule
    {
        public static readonly Module Instance = new Module();

        // Applied state and the synchronous stop request (R1.5a). Each repair entry checks once;
        // an accepted batch finishes its repairs and tool costs even if a callback requests a
        // stop partway through. The next entry sees the stop and does nothing.
        internal static bool Active;
        internal static bool StopRequested;
        internal static bool Running => Active && !StopRequested;
        internal static int Generation;
        internal static ModuleScope CurrentScope;
        internal static bool Discovering => Active && CurrentScope != null;

        private static readonly Dictionary<long, float> ItemRepairTimers;
        private static readonly Dictionary<long, float> PieceRepairTimers;

        static Module()
        {
            ItemRepairTimers = new Dictionary<long, float>();
            PieceRepairTimers = new Dictionary<long, float>();
        }

        private Module()
        {
        }

        public string Name => "automatic-repair";
        public string ConfigSection => "automatic_repair";
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

        public void Enable(ModuleScope scope)
        {
            ResetSession();
            CurrentScope = scope;
            Generation = scope.Generation;

            // Patches before subscriptions, so the scope removes them in reverse order (R4.3).
            scope.PatchAll(typeof(Patches));
            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnPlayerAwake += h,
                h => Hooks.OnPlayerAwake -= h,
                OnPlayerAwake);
            scope.Subscribe<Action<Player, float>>(
                h => Hooks.OnPlayerFixedUpdate += h,
                h => Hooks.OnPlayerFixedUpdate -= h,
                OnPlayerFixedUpdate);
            Active = true;
        }

        public void Disable(ModuleScope scope)
        {
            Active = false;
            Generation = 0;
            CurrentScope = null;
            ResetSession();
        }

        // Bookkeeping has no running gate: disable, activation retries and a new player all
        // release references to the previous session. The scope handles patch and hook removal.
        private static void ResetSession()
        {
            ItemRepairTimers.Clear();
            PieceRepairTimers.Clear();
            ItemRepair.Cleanup();
            PieceRepair.Cleanup();
        }

        [AutomaticsInitializer(5)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        private static void OnPlayerAwake(Player player, ZNetView zNetView)
        {
            ResetSession();
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            if (!Module.Running) return;
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
