using System;
using BepInEx.Configuration;

namespace Automatics.AutomaticSupply
{
    /// <summary>
    ///     Automatic Supply on the module lifecycle. A scope owns its requirement, consumption and
    ///     station intake patches and one player hook subscription, so turning the module off
    ///     removes them. It attaches no component to anything in the scene: nearby containers are
    ///     looked up again when a requirement is counted or an item is taken.
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
        ///     The stop request. The registry raises it in the frame the user asked for the
        ///     change, several frames before the pump dismantles anything.
        /// </summary>
        internal static bool StopRequested;

        /// <summary>
        ///     The predicate every entry point opens with, never the config value. An action that
        ///     already took items from a container finishes with its own refund whatever this
        ///     reads by then: the closing half of every prefix and finalizer pair is not gated.
        /// </summary>
        internal static bool Running => Active && !StopRequested;

        private Module()
        {
        }

        public string Name => "automatic-supply";
        public string ConfigSection => "automatic_supply";
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
        ///     The patches, then the subscription, so the scope takes the patches off last.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            ResetSession();

            // 1. Patches.
            scope.PatchAll(typeof(Patches));

            // 2. Subscription. A named method, never a lambda: the delegate that is removed has to
            //    be the delegate that was added.
            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnLocalPlayerAwake += h,
                h => Hooks.OnLocalPlayerAwake -= h,
                OnLocalPlayerAwake);

            Active = true;
        }

        /// <summary>
        ///     Lowers the applied state first, then drops the session state: the stock cache, the
        ///     counting scope and any action left open. Items an open action took stay in the
        ///     player inventory; nothing is dropped or created.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            ResetSession();
        }

        [AutomaticsInitializer(10)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     The local player's spawn means a new world session. Not gated on <see cref="Running" />: it
        ///     only empties bookkeeping.
        /// </summary>
        private static void OnLocalPlayerAwake(Player player, ZNetView zNetView)
        {
            ResetSession();
        }

        private static void ResetSession()
        {
            SupplyContainers.Clear();
            SupplyScope.Reset();
            SupplyTransfer.Abandon();
            Patches.ResetSession();
        }
    }
}
