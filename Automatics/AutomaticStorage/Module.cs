using System;
using BepInEx.Configuration;
using UnityEngine;

namespace Automatics.AutomaticStorage
{
    /// <summary>
    ///     Automatic Storage on the module lifecycle. A scope owns its one hook subscription, so
    ///     turning the module off removes it instead of leaving it installed. It applies no patch
    ///     and attaches no component to anything in the scene, so an activation has nothing to
    ///     reconcile: the store key is read again on the first player update after the
    ///     subscription is back (R4.2).
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
        ///     The one predicate the store path reads, never the config value (R1.5). Two methods
        ///     check it, each as its first statement: the player-update handler, which can still be
        ///     called after its subscription is gone, and <see cref="AutomaticStorage.Store" />, the
        ///     one method that moves items. Store checks it exactly once, before its first transfer,
        ///     and never again, and calls nothing that checks it. A key press that was accepted
        ///     therefore moves every item it selected even when a stop is requested part way
        ///     through, and the next press sees the stop and moves nothing. A second check would end
        ///     a pass half done, and one between the copy into a container and the removal from the
        ///     inventory would leave the item in both places.
        /// </summary>
        internal static bool Running => Active && !StopRequested;

        private Module()
        {
        }

        public string Name => "automatic-storage";
        public string ConfigSection => "automatic_storage";
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
        ///     Acquisition order is the caller's half of the framework contract (R4.3). This module
        ///     acquires one thing, its subscription: it applies no patch and creates no component,
        ///     so there is neither a patch removal to put last nor a scene to go through.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            // A named method, never a lambda: the delegate that is removed has to be the delegate
            // that was added.
            scope.Subscribe<Action<Player, bool>>(
                h => Hooks.OnPlayerUpdate += h,
                h => Hooks.OnPlayerUpdate -= h,
                OnPlayerUpdate);

            Active = true;
        }

        /// <summary>
        ///     Lowers the applied state first, so the store path is shut before anything is taken
        ///     apart, then drops the module's session state (R6.1). The subscription the scope owns
        ///     is removed by <see cref="ModuleScope.Dispose" />, not here.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            AutomaticStorage.ResetSession();
        }

        [AutomaticsInitializer(7)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            // Reachable after the module stopped: a removal that failed, or an invocation list a
            // caller fetched before the removal, can still call a handler that is no longer
            // subscribed.
            if (!Running) return;
            if (!Config.EnableAutomaticStorage) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (!takeInput) return;
            if (Game.IsPaused()) return;
            if (player.InAttack() || player.InDodge()) return;
            if (Config.StoreItemsKey.MainKey == KeyCode.None) return;
            if (!Config.StoreItemsKey.IsDown()) return;

            var result = AutomaticStorage.Store(player);
            AutomaticStorage.ShowResultMessage(player, result);
        }
    }
}
