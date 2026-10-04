using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Automatics.Valheim;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    /// <summary>
    ///     Automatic Mapping on the module lifecycle. A scope owns its patches, its hook, cache,
    ///     registry and allowlist subscriptions, and the components it attaches to fish, birds,
    ///     ships, mine rocks and flora, so turning the module off removes them instead of leaving
    ///     them installed.
    ///     <para>
    ///         Two things deliberately stay across a disable. The custom map icons stay registered
    ///         in the live minimap (see <see cref="IconPack.EnsureAttached" />). And the destroy
    ///         callbacks this module adds to game objects stay on those objects: they sit in a
    ///         game object's <c>Action</c> field, which the scope has no way to take back. Each
    ///         object carries at most one, and it does nothing while the module is off (see
    ///         <see cref="HookDestroyOnce" />).
    ///     </para>
    ///     <para>
    ///         The pins follow one rule. A disable removes exactly the unsaved pins this module
    ///         holds; it never changes a pin's saved flag or owner and never removes a saved pin.
    ///         While the module is off it touches no pin. Saved automatic pins are recognised
    ///         again when it is turned back on, and unsaved ones are recreated as the scans reach
    ///         their objects.
    ///     </para>
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
        ///     change, before the pump dismantles anything, and lowers it again if the request is
        ///     withdrawn before the pump runs.
        /// </summary>
        internal static bool StopRequested;

        /// <summary>
        ///     The one predicate the entry points with an effect on the map read, never the config
        ///     value (R1.5). Each of them checks it as its first statement, so from the frame a stop
        ///     is requested no pin is added, moved or removed, no click is taken and no icon is
        ///     resized. They are reached from the game's own methods through the patches and the
        ///     transpilers, and on a Harmony build that cannot remove patches they stay reachable
        ///     for the rest of the session, so the first statement is where they stop.
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
        ///     runs.
        /// </summary>
        internal static ModuleScope CurrentScope;

        /// <summary>
        ///     Whether newly seen objects are taken into this activation's ownership. Deliberately
        ///     not <see cref="Running" />: between a disable request and the pump that applies it,
        ///     an object dropped here is never recovered if the request is withdrawn, because no
        ///     transition then runs and no reconciliation comes back for it. Attaching under a stop
        ///     request has no effect on the map - every component here only feeds the scans, which
        ///     are stopped - and a disable that goes ahead destroys it with everything else.
        /// </summary>
        internal static bool Discovering => Active && CurrentScope != null;

        /// <summary>
        ///     The game objects that already carry this module's destroy callback, one mark each.
        ///     The keys are held weakly, so an object and its mark go away together.
        /// </summary>
        private static readonly ConditionalWeakTable<Component, object> DestroyHooked =
            new ConditionalWeakTable<Component, object>();

        private static readonly object DestroyHookMark = new object();

        private static readonly List<Pickable> PickableBuffer = new List<Pickable>();

        private Module()
        {
        }

        public string Name => "automatic-mapping";
        public string ConfigSection => "automatic_mapping";
        public bool SupportsRuntimeToggle => true;
        public ConfigEntry<AutomaticsModule> ModuleToggle => Config.ModuleEntry;

        public void SetStopRequested(bool value)
        {
            StopRequested = value;
            if (value)
            {
                MapFilter.Cleanup();
                PinTooltip.Cleanup();
            }
        }

        public void BindConfig()
        {
            Config.Initialize();
        }

        /// <summary>
        ///     Acquisition order is the caller's half of the framework contract (R4.3): patches,
        ///     then subscriptions, then the session, then components. The scope registers its
        ///     single patch removal at the first <c>PatchAll</c> and tears down in reverse, so
        ///     acquiring in this order is what puts the patch removal last (R1.6).
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            Generation = scope.Generation;
            CurrentScope = scope;

            // 1. Patches. The postfixes that attach components and destroy callbacks have to be in
            //    place before the scans below, or an object that appears in between is reached by
            //    neither. Harmony applies one patch class per call, and the two nested classes
            //    resolve their targets themselves.
            scope.PatchAll(typeof(Patches));
            scope.PatchAll(typeof(Patches.RandomFlyingBird_Initialize_Patch));
            scope.PatchAll(typeof(Patches.Minimap_OnDestroy_Patch));

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
            scope.Subscribe<Action<Player, float>>(
                h => Hooks.OnPlayerFixedUpdate += h,
                h => Hooks.OnPlayerFixedUpdate -= h,
                AutomaticMapping.DynamicMapping);
            scope.Subscribe<Action<Pickable>>(
                h => PickableCache.OnCacheAdded += h,
                h => PickableCache.OnCacheAdded -= h,
                AttachFlora);
            scope.Subscribe<Action<Piece>>(
                h => ShipCache.OnCacheAdded += h,
                h => ShipCache.OnCacheAdded -= h,
                OnShipCached);
            scope.Subscribe<Action<ValheimObject>>(
                h => ValheimObject.RegistryChanged += h,
                h => ValheimObject.RegistryChanged -= h,
                DynamicObjectMapping.OnValheimObjectRegistryChanged);
            scope.Subscribe<Action<ValheimObject>>(
                h => ValheimObject.RegistryChanged += h,
                h => ValheimObject.RegistryChanged -= h,
                StaticObjectMapping.OnValheimObjectRegistryChanged);
            scope.Subscribe<Action<ValheimObject>>(
                h => ValheimObject.RegistryChanged += h,
                h => ValheimObject.RegistryChanged -= h,
                PinCategories.OnRegistryChanged);
            scope.Subscribe<Action<ValheimObject>>(
                h => Config.StaticAllowlistChanged += h,
                h => Config.StaticAllowlistChanged -= h,
                StaticObjectMapping.OnStaticAllowlistChanged);

            // 3. The session. Registry and allowlist changes made while the module was off were
            //    not observed, so both classifications start again from the live registries
            //    (R7.3). A minimap that already exists gets the custom icons and the pin index
            //    here; one created later gets them from its own Start postfix (R7.4).
            DynamicObjectMapping.InvalidateClassification();
            StaticObjectMapping.InvalidateClassification();
            var map = Minimap.instance;
            if (map)
            {
                IconPack.EnsureAttached(map);
                Map.Attach(map);
            }

            // 4. Components and destroy callbacks, reconciled against what is already in the
            //    scene (R4.2).
            ReconcileFish(scope);
            ReconcileBirds(scope);
            ReconcileShips(scope);
            ReconcileMineRock5(scope);
            ReconcileFlora(scope);
            ReconcileDestroyHooks();

            Active = true;
        }

        /// <summary>
        ///     Lowers the applied state first, so every entry point is shut before anything is
        ///     taken apart. Then the pins: vehicle pins are saved pins that trail their vehicle, so
        ///     they are first moved to where their vehicle was last seen, and then the unsaved pins
        ///     this module holds are removed from the map. Saved pins stay, manual and shared pins
        ///     are never touched, and no pin's saved flag or owner is changed. Last, the module's
        ///     session state and pin index are dropped (R6.1). What the scope owns - the patches,
        ///     the subscriptions, the components - is removed by
        ///     <see cref="ModuleScope.Dispose" />, not here. A second call finds nothing held and
        ///     changes nothing.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            Generation = 0;
            CurrentScope = null;

            // Without a minimap there is no pin to move or to remove.
            if (Minimap.instance)
            {
                DynamicObjectMapping.MoveVehiclePinsToTargets();
                DynamicObjectMapping.RemoveCachedPins();
                StaticObjectMapping.RemoveCachedPins();
            }

            ResetSession();
            Map.Detach();
            MineRock5Cache.Clear();
        }

        [AutomaticsInitializer(2)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     Everything the scans keep between calls: the dynamic and static pin caches, the
        ///     static object cache and its tiles, the navigation target with its overlay and guide
        ///     swarm, the scan timers and the profiler. A disable and a new world session both start again from
        ///     here (R6.1 / R6.2).
        /// </summary>
        internal static void ResetSession()
        {
            AutomaticMapping.Cleanup();
        }

        /// <summary>
        ///     A new local player means a new world session, which is where this module has always
        ///     dropped its state (R6.2). Deliberately not gated on <see cref="Running" />: it only
        ///     empties bookkeeping, so reaching it after a stop is harmless.
        /// </summary>
        private static void OnPlayerAwake(Player player, ZNetView zNetView)
        {
            if (Player.m_localPlayer == player)
                ResetSession();
        }

        /// <summary>
        ///     The navigation overlay and guide swarm, and the manual pin shortcut. Both check the
        ///     module gate; <see cref="Navigation.Update" /> opens with the gate itself, and hides
        ///     the overlay and the swarm in the frame a stop is requested.
        /// </summary>
        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            Navigation.Update(player);
            ObjectPinning.OnPlayerUpdate(player, takeInput);
            Navigation.OnPlayerUpdate(player, takeInput);
        }

        /// <summary>
        ///     The only entry point for the caching components the Fish, RandomFlyingBird and Ship
        ///     postfixes attach. Through the scope, never a bare engine attach: a component created
        ///     while the module runs has to be owned by the activation that created it, or the next
        ///     disable leaves it in the scene. <c>TryAttach</c> and not <c>Attach</c>, because the
        ///     caller is a Harmony postfix and an exception from here damages the game rather than
        ///     the mod.
        /// </summary>
        internal static void AttachCache<TCache>(GameObject target)
            where TCache : Component, IScopedComponent
        {
            if (!Discovering) return;

            CurrentScope.TryAttach<TCache>(target, out _);
        }

        /// <summary>
        ///     The same for the MineRock5 snapshot binding, which is then filled from the rock.
        /// </summary>
        internal static void AttachMineRock5(MineRock5 rock)
        {
            if (!Discovering) return;

            if (CurrentScope.TryAttach<MineRock5CacheBinding>(rock.gameObject, out var binding))
                binding.Initialize(rock);
        }

        /// <summary>
        ///     Raised by the always-on root cache for every Pickable that appears. A configured
        ///     flora gets the node the flora pins are merged through.
        /// </summary>
        private static void AttachFlora(Pickable pickable)
        {
            if (!Discovering) return;
            if (!ValheimObject.Flora.IsDefined(Objects.GetName(pickable))) return;

            CurrentScope.TryAttach<FloraNode>(pickable.gameObject, out _);
        }

        /// <summary>
        ///     Raised when a ship's cache is attached, by the Ship.Awake postfix or by the scan in
        ///     <see cref="Enable" />. The scan runs before <see cref="Active" /> is raised, so this
        ///     is deliberately not gated: all it does is give the ship its one destroy callback,
        ///     and that callback checks the module itself.
        /// </summary>
        private static void OnShipCached(Piece ship)
        {
            HookVehicleDestroy(ship);
        }

        /// <summary>
        ///     A flora, mineral, spawner or other object loses its pin when it is destroyed.
        /// </summary>
        internal static void HookStaticObjectDestroy(Destructible destructible, ZNetView zNetView)
        {
            HookDestroyOnce(destructible,
                () => StaticObjectMapping.OnObjectDestroy(destructible, zNetView));
        }

        /// <summary>
        ///     A portal loses its pin when its piece is destroyed.
        /// </summary>
        internal static void HookPortalDestroy(TeleportWorld portal, ZNetView zNetView)
        {
            var wearNTear = portal.GetComponent<WearNTear>();
            if (wearNTear)
                HookDestroyOnce(wearNTear,
                    () => StaticObjectMapping.OnObjectDestroy(portal, zNetView));
        }

        /// <summary>
        ///     A ship or a cart loses its pin when its piece is destroyed.
        /// </summary>
        internal static void HookVehicleDestroy(Component vehicle)
        {
            var wearNTear = vehicle.GetComponent<WearNTear>();
            if (wearNTear)
                HookDestroyOnce(wearNTear, () => DynamicObjectMapping.OnObjectDestroy(vehicle));
        }

        /// <summary>
        ///     Adds <paramref name="onDestroyed" /> to the destroy callback of
        ///     <paramref name="owner" />, once per object lifetime.
        ///     <para>
        ///         The callback is a plain <c>Action</c> field on a game object, and taking one
        ///         delegate back out of it would need the very delegate that was added, kept for
        ///         every object in the world. So it stays on the object across a disable, and it
        ///         does nothing there: every target it routes to opens with <see cref="Running" />.
        ///         The mark is what keeps a re-enable from adding a second one, and what lets the
        ///         scan in <see cref="Enable" /> reach the objects that appeared while the module
        ///         was off.
        ///     </para>
        ///     <para>
        ///         Each callback captures game components only - never a component of this module,
        ///         which the next disable destroys - and routes through a static entry point, so a
        ///         callback added by an earlier activation reaches the current one.
        ///     </para>
        /// </summary>
        private static void HookDestroyOnce(Component owner, Action onDestroyed)
        {
            if (!owner || DestroyHooked.TryGetValue(owner, out _)) return;

            switch (owner)
            {
                case Destructible destructible:
                    DestroyHooked.Add(owner, DestroyHookMark);
                    destructible.m_onDestroyed += onDestroyed;
                    break;
                case WearNTear wearNTear:
                    DestroyHooked.Add(owner, DestroyHookMark);
                    wearNTear.m_onDestroyed += onDestroyed;
                    break;
            }
        }

        // The caches below are filled by this module's own patches, so everything that spawned
        // while the module was off is missing from them. The scans therefore enumerate the scene
        // themselves, inactive objects included, and apply the test the postfixes apply - a ZDO -
        // so an object is taken by the same rule whichever path reaches it (R4.2). An inactive
        // object without one is a template, and a component on it would be copied, unowned, onto
        // every object spawned from it. The sort is declined
        // because each scan runs once per activation and the order means nothing here.

        private static void ReconcileFish(ModuleScope scope)
        {
            foreach (var fish in UnityEngine.Object.FindObjectsByType<Fish>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (HasZdo(fish))
                    scope.Attach<FishCache>(fish.gameObject);
        }

        private static void ReconcileBirds(ModuleScope scope)
        {
            foreach (var bird in UnityEngine.Object.FindObjectsByType<RandomFlyingBird>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (HasZdo(bird))
                    scope.Attach<BirdCache>(bird.gameObject);
        }

        /// <summary>
        ///     Attaching the cache raises <see cref="OnShipCached" /> for a ship that is active;
        ///     one that is not gets its destroy callback here directly.
        /// </summary>
        private static void ReconcileShips(ModuleScope scope)
        {
            foreach (var ship in UnityEngine.Object.FindObjectsByType<Ship>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!HasZdo(ship)) continue;

                scope.Attach<ShipCache>(ship.gameObject);
                HookVehicleDestroy(ship);
            }
        }

        private static void ReconcileMineRock5(ModuleScope scope)
        {
            foreach (var rock in UnityEngine.Object.FindObjectsByType<MineRock5>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (HasZdo(rock))
                    scope.Attach<MineRock5CacheBinding>(rock.gameObject).Initialize(rock);
        }

        /// <summary>
        ///     Unlike the caches above, the pickable cache is filled by the always-on root patch,
        ///     so it holds every pickable that appeared while this module was off as well.
        /// </summary>
        private static void ReconcileFlora(ModuleScope scope)
        {
            try
            {
                PickableCache.Fill(PickableBuffer);
                for (var i = 0; i < PickableBuffer.Count; i++)
                {
                    var pickable = PickableBuffer[i];
                    if (pickable && ValheimObject.Flora.IsDefined(Objects.GetName(pickable)))
                        scope.Attach<FloraNode>(pickable.gameObject);
                }
            }
            finally
            {
                PickableBuffer.Clear();
            }
        }

        /// <summary>
        ///     The objects that appeared while the module was off have no destroy callback yet;
        ///     the ones that already carry one are skipped by the mark.
        /// </summary>
        private static void ReconcileDestroyHooks()
        {
            foreach (var destructible in UnityEngine.Object.FindObjectsByType<Destructible>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (Objects.GetZNetView(destructible, out var zNetView) && zNetView.GetZDO() != null)
                    HookStaticObjectDestroy(destructible, zNetView);

            foreach (var portal in UnityEngine.Object.FindObjectsByType<TeleportWorld>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (Objects.GetZNetView(portal, out var zNetView) && zNetView.GetZDO() != null)
                    HookPortalDestroy(portal, zNetView);

            foreach (var wagon in UnityEngine.Object.FindObjectsByType<Vagon>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (HasZdo(wagon))
                    HookVehicleDestroy(wagon);
        }

        private static bool HasZdo(Component component)
        {
            return Objects.GetZNetView(component, out var zNetView) && zNetView.GetZDO() != null;
        }
    }
}
