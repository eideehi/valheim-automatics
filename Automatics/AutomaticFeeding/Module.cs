using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFeeding
{
    /// <summary>
    ///     Automatic Feeding on the module lifecycle. A scope owns its patch and every
    ///     <see cref="AutomaticFeeding" /> component it attaches to a tameable animal, so turning the
    ///     module off removes them without interrupting a feeding call already begun. The module
    ///     subscribes to no hook: the component acts only when one of the patches calls into it.
    /// </summary>
    internal sealed class Module : IAutomaticsModule
    {
        public static readonly Module Instance = new Module();

        internal static bool Active;
        internal static bool StopRequested;
        internal static bool Running => Active && !StopRequested;
        internal static int Generation;
        internal static ModuleScope CurrentScope;

        /// <summary>
        ///     Discovery follows the applied activation rather than the stop request. If a disable
        ///     request is withdrawn before the pump runs, no later reconciliation occurs, so an
        ///     animal seen in that window still has to join the current scope.
        /// </summary>
        internal static bool Discovering => Active && CurrentScope != null;

        private Module()
        {
        }

        public string Name => "automatic-feeding";
        public string ConfigSection => "automatic_feeding";
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
        ///     Acquires the patch before the scene components. The scope tears them down in
        ///     reverse, leaving the patch installed until every component it owns is gone.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            Generation = scope.Generation;
            CurrentScope = scope;

            // The Tameable.Awake postfix must be installed before the scan, or an animal that
            // appears between them is reached by neither path.
            scope.PatchAll(typeof(Patches));

            ReconcileTameables(scope);
            Active = true;
        }

        /// <summary>
        ///     Stops new feeding. The module keeps no session state of its own: each component
        ///     drops its feed targets when the scope detaches it, and no item is ever held between
        ///     calls, so there is nothing to put back.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            Generation = 0;
            CurrentScope = null;
        }

        [AutomaticsInitializer(4)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     The runtime attach path, called from the <c>Tameable.Awake</c> postfix.
        ///     <c>TryAttach</c> and not <c>Attach</c>, because the caller is a Harmony postfix and an
        ///     exception from here would damage the game rather than the mod.
        /// </summary>
        internal static void AttachFeeding(GameObject target)
        {
            if (!Discovering) return;

            CurrentScope.TryAttach<AutomaticFeeding>(target, out _);
        }

        /// <summary>
        ///     Animals already in the scene never run <c>Tameable.Awake</c> again, so the activation
        ///     attaches to them itself, inactive ones included, by the ZDO rule the postfix applies.
        ///     The list of living characters is not used: it is not the same set as the objects that
        ///     ran <c>Tameable.Awake</c>.
        /// </summary>
        private static void ReconcileTameables(ModuleScope scope)
        {
            foreach (var tameable in UnityEngine.Object.FindObjectsByType<Tameable>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(tameable, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticFeeding>(tameable.gameObject);
            }
        }
    }
}
