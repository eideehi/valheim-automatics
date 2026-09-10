using System;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    /// <summary>
    ///     The pilot for the module lifecycle framework. This module has no
    ///     <c>Config.Enable&lt;Feature&gt;</c> flag of its own, so turning it off cannot be faked
    ///     with a soft gate: the patches, the subscription and the components have to come off
    ///     for real, which is exactly why it was chosen to go first.
    /// </summary>
    internal sealed class Module : IAutomaticsModule
    {
        public static readonly Module Instance = new Module();

        /// <summary>
        ///     The applied state: true only between the last statement of <see cref="Enable" />
        ///     and the first statement of <see cref="Disable" />. Hot paths read this, never the
        ///     config value (R1.5).
        /// </summary>
        internal static bool Active;

        /// <summary>
        ///     The stop request (R1.5a). The registry raises it in the frame the user asked for
        ///     the change, which is several frames before the pump dismantles anything.
        ///     Reversible on purpose: asking for a disable and changing one's mind before the
        ///     pump runs lowers it again with no transition and no interruption.
        /// </summary>
        internal static bool StopRequested;

        /// <summary>
        ///     The one predicate every hot path uses. It carries both the applied state (R1.5)
        ///     and the stop request (R1.5a), so the observable stop boundary is the request, not
        ///     the teardown.
        /// </summary>
        internal static bool Running => Active && !StopRequested;

        /// <summary>
        ///     The generation of the activation that owns the components in the scene. Zero means
        ///     nothing is owned, which is what makes a component left over from an older
        ///     activation recognisable while the engine still has it queued for destruction.
        /// </summary>
        internal static int Generation;

        /// <summary>
        ///     The running activation's scope, and the owner of every component created while it
        ///     runs. The two attach paths that run on a game call stack go through this, so
        ///     nothing this module creates ends up without an owner to destroy it (MAJOR-4).
        /// </summary>
        internal static ModuleScope CurrentScope;

        /// <summary>
        ///     Whether newly seen objects should be taken into this activation's ownership.
        ///     Deliberately <em>not</em> <see cref="Running" />: the stop request must not
        ///     suppress discovery. Between a disable request and the pump that applies it, a
        ///     Player.Awake or PickableItem.Awake that is dropped here is never recovered - if
        ///     the user withdraws the request before the pump runs, no transition happens, so no
        ///     reconciliation ever runs and those objects stay unattached for the rest of the
        ///     session. Attaching during a stop request costs nothing: the component's own Live
        ///     gate keeps it from acting, and if the disable does go through, the scope destroys
        ///     it during teardown like everything else it owns.
        ///     Do not "make this consistent" with Running. Discovery and side effects are
        ///     different questions and sharing one predicate answers one of them wrongly.
        /// </summary>
        internal static bool Discovering => Active && CurrentScope != null;

        private Module()
        {
        }

        public string Name => "automatic-pickup";
        public string ConfigSection => "automatic_pickup";
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
        ///     then subscriptions, then components. The scope registers its single patch removal
        ///     at the first <c>PatchAll</c>, and the teardown walks the registrations in reverse,
        ///     so acquiring in this order is what puts the patch removal last (R1.6). Subscribing
        ///     or attaching first would leave the patches coming off before them, and nothing in
        ///     the framework detects that.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            Generation = scope.Generation;
            CurrentScope = scope;

            // 1. Patches. The PickableItem.Awake postfix has to be installed before the scan
            //    below, or an item that spawns between the two is cached by neither.
            scope.PatchAll(typeof(Patches));

            // 2. Subscriptions. A named method, never a lambda: the delegate that is removed has
            //    to be the delegate that was added.
            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnPlayerAwake += h,
                h => Hooks.OnPlayerAwake -= h,
                OnPlayerAwake);

            // 3. Components, reconciled against what is already in the scene (R4.2).
            ReconcilePickableItems(scope);
            ReconcilePlayers(scope);

            Active = true;
        }

        /// <summary>
        ///     Lowers the applied state first, so every hot path is shut before anything is taken
        ///     apart, then resets the module's own session state. What the scope owns - the
        ///     patches, the subscription, the components - is removed by
        ///     <see cref="ModuleScope.Dispose" />, not here.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            Generation = 0;
            CurrentScope = null;
            AutomaticPickup.ClearSharedBuffers();
        }

        [AutomaticsInitializer(8)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     Runs on the call stack of <c>Player.Awake</c>. It re-checks the applied state
        ///     because a removal that failed, or an invocation list a caller had already fetched,
        ///     can still reach a handler that is no longer subscribed (MAJOR-5). The predicate is
        ///     <see cref="Discovering" /> and not <see cref="Running" /> - see there for why.
        /// </summary>
        private static void OnPlayerAwake(Player player, ZNetView zNetView)
        {
            if (!Discovering) return;
            if (zNetView.GetZDO() == null) return;

            CurrentScope.TryAttach<AutomaticPickup>(player.gameObject, out _);
        }

        /// <summary>
        ///     The only entry point for the caching component, called from the
        ///     <c>PickableItem.Awake</c> postfix. Everything this module creates while it runs is
        ///     owned by the current scope, or the disable that follows would leave it in the
        ///     scene (MAJOR-4). <c>TryAttach</c> and not <c>Attach</c>, because the caller is a
        ///     Harmony postfix and an exception from here damages the game rather than the mod.
        /// </summary>
        internal static void AttachCache(GameObject target)
        {
            if (!Discovering) return;

            CurrentScope.TryAttach<PickableItemCache>(target, out _);
        }

        /// <summary>
        ///     The cache is filled by this module's own patch, so everything that spawned while
        ///     the module was off is missing from it and <c>PickableItemCache.Fill</c> would
        ///     report an empty world. The scan therefore enumerates the objects itself,
        ///     inactive ones included (R4.2). The ZDO test is the one the postfix applies, so a
        ///     pickable is cached by the same rule whichever path reaches it.
        /// </summary>
        private static void ReconcilePickableItems(ModuleScope scope)
        {
            // Inactive objects are included on purpose: without them a re-enable misses
            // everything parked under a deactivated root. The sort is declined because the scan
            // runs once per activation and the order it produces means nothing here.
            foreach (var pickableItem in UnityEngine.Object.FindObjectsByType<PickableItem>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(pickableItem, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<PickableItemCache>(pickableItem.gameObject);
            }
        }

        /// <summary>
        ///     Players already in the scene never see another <c>Player.Awake</c>, so the
        ///     subscription above cannot reach them and the activation has to. <c>GetAllPlayers</c>
        ///     is deliberately not used: it lists the players the game considers alive, which is
        ///     not the same set as the objects that ran <c>Player.Awake</c>.
        /// </summary>
        private static void ReconcilePlayers(ModuleScope scope)
        {
            foreach (var player in UnityEngine.Object.FindObjectsByType<Player>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(player, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticPickup>(player.gameObject);
            }
        }
    }
}