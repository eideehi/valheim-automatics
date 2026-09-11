using System;
using BepInEx.Configuration;

namespace Automatics.AutomaticProcessing
{
    /// <summary>
    ///     Automatic Processing on the module lifecycle. A scope owns its patches and its two hook
    ///     subscriptions, so turning the module off removes them instead of leaving them
    ///     installed. It attaches no component to anything in the scene, so an activation has
    ///     nothing to reconcile: a station that already exists is reached through the patches the
    ///     next time the game updates it (R4.2).
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
        ///     The one predicate the side-effect entry points read, never the config value (R1.5).
        ///     Each of them checks it exactly once, as its first statement, and that placement is
        ///     what keeps items from being lost (R8.11). An entry point takes an item out of a
        ///     container and hands it to a station, and the claim, the removal, the store and the
        ///     RPC in between all call into code that is not ours: a synchronous callback or
        ///     another mod's patch there can change the setting, and so raise the stop request,
        ///     before the outer call returns. A second check after the item left its container
        ///     would strand it, so there is none, and no entry point calls another one. A transfer
        ///     that was accepted is delivered exactly once; the next call sees the stop and does
        ///     nothing. The checks sit in the entry points rather than in the patch methods because
        ///     the transpilers inject calls that run outside any patch method.
        /// </summary>
        internal static bool Running => Active && !StopRequested;

        private Module()
        {
        }

        public string Name => "automatic-processing";
        public string ConfigSection => "automatic_processing";
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
        ///     Acquisition order is the caller's half of the framework contract (R4.3): patches,
        ///     then subscriptions. The scope registers its single patch removal at the first
        ///     <c>PatchAll</c> and tears down in reverse, so acquiring in this order is what puts
        ///     the patch removal last (R1.6). There is no third step, because this module creates
        ///     no components.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            // 1. Patches.
            scope.PatchAll(typeof(Patches));

            // 2. Subscriptions. Named methods, never lambdas: the delegate that is removed has to
            //    be the delegate that was added.
            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnPlayerAwake += h,
                h => Hooks.OnPlayerAwake -= h,
                OnPlayerAwake);
            scope.Subscribe<Action<Player, bool>>(
                h => Hooks.OnPlayerUpdate += h,
                h => Hooks.OnPlayerUpdate -= h,
                OnPlayerUpdate);

            Active = true;
        }

        /// <summary>
        ///     Lowers the applied state first, so every entry point is shut before anything is
        ///     taken apart, then drops the module's session state (R6.1). What the scope owns -
        ///     the patches and the subscriptions - is removed by <see cref="ModuleScope.Dispose" />,
        ///     not here.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            ResetSession();
        }

        [AutomaticsInitializer(3)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     A new player means a new world session, which is where this module has always
        ///     dropped its state (R6.2). Deliberately not gated on <see cref="Running" />: it only
        ///     empties bookkeeping and has no effect on the game, so reaching it after a stop is
        ///     harmless.
        /// </summary>
        private static void OnPlayerAwake(Player player, ZNetView zNetView)
        {
            ResetSession();
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            // Reachable after the module stopped: a removal that failed, or an invocation list a
            // caller fetched before the removal, can still call a handler that is no longer
            // subscribed.
            if (!Running) return;
            if (Player.m_localPlayer != player || !player.IsOwner()) return;

            ConnectionEffects.Update(player);
        }

        /// <summary>
        ///     Everything this module keeps between calls: the nearby-container cache, the smelter
        ///     throttles, the turret charge timers and the connection-effect objects. A disable and
        ///     a new world session both start again from here (R6.1 / R6.2).
        /// </summary>
        internal static void ResetSession()
        {
            Logics.Cleanup();
            SmelterProcess.Cleanup();
            TurretProcess.Cleanup();
            ConnectionEffects.Cleanup();
        }
    }
}
