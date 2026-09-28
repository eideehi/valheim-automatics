using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFarming
{
    /// <summary>
    ///     Automatic Farming on the module lifecycle. A scope owns its container hover and manual planting patches and its
    ///     three player hook subscriptions, so turning the module off removes them instead of
    ///     leaving them installed. It attaches no component to anything in the scene, so an
    ///     activation has nothing to reconcile: crops, cultivated ground and designated containers
    ///     are looked up again on the next farming pass (R4.2).
    /// </summary>
    internal sealed class Module : IAutomaticsModule
    {
        public static readonly Module Instance = new Module();

        /// <summary>
        ///     The applied state: true only between the last statement of <see cref="Enable" />
        ///     and the first statement of <see cref="Disable" />.
        /// </summary>
        internal static bool Active;

        /// <summary>
        ///     The stop request (R1.5a). The registry raises it in the frame the user asked for the
        ///     change, several frames before the pump dismantles anything, and lowers it again if
        ///     the request is withdrawn before the pump runs.
        /// </summary>
        internal static bool StopRequested;

        /// <summary>
        ///     The one predicate this module's entry points read, never the config value (R1.5).
        ///     Six methods check it, each as its first statement: the two player hook handlers,
        ///     which can still be called after their subscriptions are gone, the container hover
        ///     and manual placement postfixes, the placement ghost prefix that opens lattice
        ///     snapping, and <see cref="AutomaticFarming.ClaimsCrop(Player, Pickable)" />, which
        ///     Automatic Pickup asks before it collects a crop. A farming pass is accepted only at
        ///     the two hook gates and then finishes (R8.11): it harvests a crop into the inventory
        ///     or a container, spends seeds and places the sapling, and each of those calls into
        ///     code that is not ours, where a synchronous callback or another mod's patch can raise
        ///     the stop request before the pass returns. A second check there would leave a
        ///     harvested spot unplanted, so there is none, and nothing a pass reaches calls an
        ///     entry point. The next call sees the stop and does nothing. Pickup's claim reads the
        ///     same predicate, so it lets go of the crops in the frame farming stops taking them
        ///     (R7.5).
        /// </summary>
        internal static bool Running => Active && !StopRequested;

        private static readonly Dictionary<long, float> FarmingTimers;

        /// <summary>The number of interval timers held, for the lifecycle smoke check.</summary>
        internal static int FarmingTimerCount => FarmingTimers.Count;

        static Module()
        {
            FarmingTimers = new Dictionary<long, float>();
        }

        private Module()
        {
        }

        public string Name => "automatic-farming";
        public string ConfigSection => "automatic_farming";
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
        ///     Acquisition order is the caller's half of the framework contract (R4.3): the patch,
        ///     then the subscriptions. The scope registers its single patch removal at the first
        ///     <c>PatchAll</c> and tears down in reverse, so acquiring in this order is what puts the
        ///     patch removal last (R1.6). There is no third step, because this module creates no
        ///     components.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            // 1. Patches.
            scope.PatchAll(typeof(Patches));

            // 2. Subscriptions. Named methods, never lambdas: the delegate that is removed has to be
            //    the delegate that was added.
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

            Active = true;
        }

        /// <summary>
        ///     Lowers the applied state first, so every entry point is shut before anything is taken
        ///     apart, then drops the module's session state (R6.1): the interval timers and the
        ///     sapling lookup. No item is held between calls, so there is nothing to put back, and
        ///     the container roles stay on their containers for the next activation. What the scope
        ///     owns - the patch and the subscriptions - is removed by
        ///     <see cref="ModuleScope.Dispose" />, not here.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            ResetSession();
            CropPlanting.ClearCaches();
        }

        [AutomaticsInitializer(9)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     A new player means a new world session, which is where this module has always dropped
        ///     its timers (R6.2). Deliberately not gated on <see cref="Running" />: it only empties
        ///     bookkeeping and has no effect on the game, so reaching it after a stop is harmless.
        /// </summary>
        private static void OnPlayerAwake(Player player, ZNetView zNetView)
        {
            ResetSession();
        }

        private static void ResetSession()
        {
            FarmingTimers.Clear();
            GridPlanting.ClearPending();
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            // Reachable after the module stopped: a removal that failed, or an invocation list a
            // caller fetched before the removal, can still call a handler that is no longer
            // subscribed.
            if (!Running) return;
            GridPlanting.ConsumePending(player);
            if (!Config.EnableAutomaticFarming) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (!takeInput) return;

            // Container designation is independent of the farming-key/interval
            // path, so it is handled before those guards: a player can tag chests
            // whether farming runs on a key or on an interval.
            TryDesignateContainer(player);

            if (Config.FarmingKey.MainKey == KeyCode.None) return;
            if (!Config.FarmingKey.IsDown()) return;

            // The key runs one pass whatever the interval is, and the interval starts over.
            FarmingTimers[player.GetPlayerID()] = 0f;
            AutomaticFarming.TryFarming(player);
        }

        // Cycles the role of the container the player is looking at. Doing nothing
        // when no container is hovered keeps the key harmless elsewhere; a hovered
        // but inaccessible container reports a clear failure instead of silently
        // mutating or claiming a chest a peer owns.
        private static void TryDesignateContainer(Player player)
        {
            if (Config.DesignateContainerKey.MainKey == KeyCode.None) return;
            if (!Config.DesignateContainerKey.IsDown()) return;

            var hovering = Reflections.GetField<GameObject>(player, "m_hovering");
            if (!hovering) return;

            var container = hovering.GetComponentInParent<Container>();
            if (!container) return;

            if (ContainerRoles.TryCycleRole(player, container, out var role))
            {
                var roleLabel = Automatics.L10N.Localize(ContainerRoles.RoleLabelKey(role));
                player.Message(MessageHud.MessageType.Center,
                    Automatics.L10N.Localize("@message_automatic_farming_container_role_changed",
                        roleLabel));
            }
            else
            {
                player.Message(MessageHud.MessageType.Center,
                    Automatics.L10N.Localize("@message_automatic_farming_container_designate_failed"));
            }
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            // Reachable after the module stopped, for the same reason as OnPlayerUpdate.
            if (!Running) return;
            if (!Config.EnableAutomaticFarming) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            // An interval of 0 turns the periodic passes off; the key still works.
            if (Config.FarmingInterval <= 0f) return;

            var id = player.GetPlayerID();
            if (!FarmingTimers.TryGetValue(id, out var timer))
                timer = 0f;

            timer += delta;
            if (timer >= Config.FarmingInterval)
            {
                FarmingTimers[id] = 0f;
                AutomaticFarming.TryFarming(player);
            }
            else
            {
                FarmingTimers[id] = timer;
            }
        }
    }
}
