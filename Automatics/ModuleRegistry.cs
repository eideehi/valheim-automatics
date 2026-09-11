using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;

namespace Automatics
{
    /// <summary>
    ///     Owns the registered modules, turns setting changes into transition requests, and runs
    ///     the pump that applies them.
    ///     The registry never decides ordering, re-entrancy or retry policy: those belong to
    ///     <see cref="ModuleTransitionMachine" />, which is executed offline by
    ///     <c>tests/ModuleLifecycleHarness</c>. What lives here is the three transition callbacks
    ///     the machine drives, and the single rule they all answer to - a teardown counts as done
    ///     only when <see cref="TeardownIsClean" /> says so.
    ///     Two of the entry points below are reached from a game call stack: the setting handler
    ///     runs from the config UI and from a config file reload, and <see cref="Pump" /> runs
    ///     from <c>Update</c>. Nothing may be thrown at either, so both are contained at their
    ///     boundary, and the containment itself is written so that an exception whose description
    ///     throws cannot escape through the report of it.
    /// </summary>
    internal static class ModuleRegistry
    {
        private static readonly List<Entry> Entries = new List<Entry>();

        private static readonly Dictionary<string, Entry> EntriesByName =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        private static long _tick;

        /// <summary>
        ///     False is the degraded mode of R3.2: a module can still be gated off, but its
        ///     patches stay applied for the rest of the session.
        /// </summary>
        public static bool RuntimeToggleSupported => HarmonyCapabilities.CanUnpatch;

        /// <summary>
        ///     Called from a module's own <c>[AutomaticsInitializer]</c>. Binds the module's
        ///     configuration and, when the module supports it, starts listening to its toggle.
        /// </summary>
        public static void Register(IAutomaticsModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));

            var name = module.Name;
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A module name is required.", nameof(module));

            if (EntriesByName.ContainsKey(name))
            {
                LogWarning($"Module {name} is already registered.");
                return;
            }

            module.BindConfig();

            // The module's own members are read once, here. Registration runs from a module
            // initializer, which Automatics.InitializeModules already contains, so a getter that
            // throws fails this one module and nothing else. Every later path - the setting
            // handler, the pump, the state report - uses these cached values, so no game call
            // stack can be handed an exception out of a module property.
            var entry = new Entry(module, name, module.ConfigSection, module.ModuleToggle);
            Entries.Add(entry);
            EntriesByName.Add(name, entry);

            if (!module.SupportsRuntimeToggle) return;

            if (entry.Toggle == null)
            {
                LogWarning(
                    $"Module {name} claims runtime toggling but bound no toggle; it will only " +
                    "follow its setting at startup.");
                return;
            }

            entry.Toggle.SettingChanged += (sender, args) =>
            {
                // R1.4: the body records intent and nothing else - no patch work, no teardown.
                // Contained because this is raised from the ConfigurationManager OnGUI pass and
                // from a config file reload, neither of which may be handed an exception of ours.
                try
                {
                    RequestTransition(entry, entry.Toggle.Value == AutomaticsModule.Enabled);
                }
                catch (Exception e)
                {
                    Contain($"the setting handler of {entry.Name} threw", e);
                }
            };
        }

        /// <summary>
        ///     Asks every registered module for the state its setting currently describes. Called
        ///     once, straight after the module initializers have run.
        /// </summary>
        public static void RequestInitialStates()
        {
            for (var i = 0; i < Entries.Count; i++)
            {
                var entry = Entries[i];
                if (entry.Toggle == null) continue;

                try
                {
                    RequestTransition(entry, entry.Toggle.Value == AutomaticsModule.Enabled);
                }
                catch (Exception e)
                {
                    // One module that cannot state its initial intent must not stop the others,
                    // and must not take plugin startup down with it.
                    Contain($"could not request the initial state of {entry.Name}", e);
                }
            }
        }

        /// <summary>
        ///     Runs from <c>Update</c>, once per frame. At most one transition happens per frame
        ///     across every module, which is what bounds the cost of the reconciliation scans
        ///     (R4.6). With nothing registered this is a count comparison and a return.
        ///     The whole body of the loop is contained: the transition callbacks are already
        ///     isolated by the machine, but the state report that follows them is not, and this
        ///     is the update loop.
        /// </summary>
        public static void Pump()
        {
            if (Entries.Count == 0) return;

            _tick++;

            for (var i = 0; i < Entries.Count; i++)
            {
                // The containment starts at the list read, not after it, so that the boundary
                // this method advertises is the boundary the code actually has.
                var name = "<unknown>";
                try
                {
                    var entry = Entries[i];
                    name = entry.Name;
                    if (entry.Machine.Desired == null) continue;
                    if (!entry.Machine.Pump(_tick)) continue;

                    PublishState(entry);
                }
                catch (Exception e)
                {
                    Contain($"the module pump threw for {name}", e);
                }

                // Either a transition ran or one failed outright. Both spend this frame's single
                // transition, so no other module is pumped until the next one.
                return;
            }
        }

        /// <summary>
        ///     The applied state of a module, which is not the same thing as the value of its
        ///     setting. A module whose teardown failed reads as
        ///     <see cref="ModuleState.Faulted" /> however its toggle is set (R1.3 / R1.3a).
        /// </summary>
        public static ModuleState GetState(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return ModuleState.Disabled;

            return EntriesByName.TryGetValue(moduleName, out var entry)
                ? entry.Machine.State
                : ModuleState.Disabled;
        }

        /// <summary>
        ///     R1.5a. Raising the stop request is a single bool write, which is why it is allowed
        ///     in a setting-changed handler that R1.4 otherwise keeps free of work: it fixes the
        ///     observable stop boundary at the frame the user asked for it, rather than the frame
        ///     the pump gets to. Lowering it is the same one line, so asking for a disable and
        ///     changing one's mind before the pump runs costs no interruption at all.
        ///     The two writes are contained separately and on purpose. They are independent
        ///     obligations: a module whose stop setter throws must still have its transition
        ///     recorded, or the change the user made is lost outright rather than merely late.
        /// </summary>
        private static void RequestTransition(Entry entry, bool enable)
        {
            entry.RequestVersion++;

            try
            {
                entry.Module.SetStopRequested(!enable);
            }
            catch (Exception e)
            {
                Contain($"could not set the stop request of {entry.Name}", e);
            }

            try
            {
                entry.Machine.Request(enable);
            }
            catch (Exception e)
            {
                Contain($"could not record a transition request for {entry.Name}", e);
            }
        }

        /// <summary>
        ///     The single test for "is this scope really gone". Everything that could make a
        ///     teardown incomplete - a registration that would not undo, an exception out of the
        ///     module's own callback, a patch that is still installed - has to arrive here, and
        ///     no other expression is allowed to decide the same question.
        ///     <c>HasAnyPatches</c> is only consulted when the probe confirmed the unpatch API
        ///     exists; in the degraded mode patches are expected to remain, so their presence is
        ///     not a failure (R3.3).
        /// </summary>
        private static bool TeardownIsClean(ModuleScope scope)
        {
            return scope.TeardownFailures.Count == 0 &&
                   (!RuntimeToggleSupported || !Harmony.HasAnyPatches(scope.HarmonyId));
        }

        /// <summary>
        ///     Precondition, guaranteed by M7: no residual scope is outstanding. A generation is
        ///     spent on every attempt, so a component left over from a previous one is always
        ///     recognisable as stale.
        /// </summary>
        private static TransitionOutcome EnableTransition(Entry entry)
        {
            var module = entry.Module;

            // The request this activation is serving. A newer one arriving while Enable runs is
            // newer intent, and this transition is not allowed to undo any part of it.
            var served = entry.RequestVersion;

            entry.Generation++;

            var scope = new ModuleScope(entry.Name, Automatics.GetHarmonyId(entry.Name),
                entry.Generation);

            try
            {
                module.Enable(scope);
                entry.Current = scope;

                // Clears a stop request left standing by an earlier faulted teardown - but only
                // when no newer request has arrived. R1.5a fixes the observable stop boundary at
                // the moment of the request, so an older transition finishing later must never
                // erase it and let a module that was just asked to stop keep running until the
                // next pump.
                if (entry.RequestVersion == served) ClearStopRequest(entry);
                return TransitionOutcome.Succeeded;
            }
            catch (ScopeAttachDeferredException e)
            {
                // Something from an older generation is still waiting to be destroyed. Retrying
                // next frame is only honest if this attempt left nothing behind, so the rollback
                // is checked before the deferral is claimed: reporting a half-applied activation
                // as deferred would leave the machine calling the module disabled while its
                // patches and subscriptions were still live.
                LogDebug($"Deferring the activation of {entry.Name}: {Describe(e)}");
                scope.Dispose();
                ForceInactive(entry, scope);
                if (TeardownIsClean(scope)) return TransitionOutcome.Deferred;

                entry.Residual = scope;
                ReportResidual(entry, scope, "rolling back a deferred activation");
                return TransitionOutcome.Failed;
            }
            catch (Exception e)
            {
                LogError($"Failed to enable {entry.Name}: {Describe(e)}");

                // The failure has to reach the same list the success test reads; a record that
                // does not affect the outcome is a record that does not exist.
                scope.RecordFailure($"Enable() threw: {Describe(e)}");
                scope.Dispose();
                ForceInactive(entry, scope);

                // Keep the scope even though it failed. It is the only handle on whatever is
                // still installed, and recovery has nothing to retry without it.
                entry.Residual = scope;
                ReportResidual(entry, scope, "rolling back a failed activation");
                return TransitionOutcome.Failed;
            }
        }

        /// <summary>
        ///     Precondition: the module is enabled, so a current scope exists.
        /// </summary>
        private static TransitionOutcome DisableTransition(Entry entry)
        {
            var scope = entry.Current;
            if (scope == null) return TransitionOutcome.Succeeded;

            try
            {
                // The module lowers its own effective-state flag as the first statement here, so
                // the hot paths are shut before anything is dismantled.
                entry.Module.Disable(scope);
            }
            catch (Exception e)
            {
                LogError($"Failed to disable {entry.Name}: {Describe(e)}");
                scope.RecordFailure($"Disable() threw: {Describe(e)}");
            }

            scope.Dispose();

            if (TeardownIsClean(scope))
            {
                entry.Current = null;
                entry.Residual = null;
                return TransitionOutcome.Succeeded;
            }

            entry.Current = null;
            entry.Residual = scope;
            ReportResidual(entry, scope, "a teardown that did not complete");
            return TransitionOutcome.Failed;
        }

        /// <summary>
        ///     The only way out of <see cref="ModuleState.Faulted" />.
        ///     A new attempt at the same teardown means actually repeating the work whose failure
        ///     is being forgiven, not just forgiving it. So the module's own session cleanup runs
        ///     again first - <c>Disable</c> is idempotent by contract: it lowers a flag and
        ///     resets module-private state - and only then is the previous attempt's report
        ///     dropped, immediately replaced by whatever this attempt produced. Obligations the
        ///     ledger still holds are re-walked below and speak for themselves.
        /// </summary>
        private static TransitionOutcome RecoverTransition(Entry entry)
        {
            var scope = entry.Residual;
            if (scope == null) return TransitionOutcome.Succeeded;

            string retryFailure = null;
            try
            {
                entry.Module.Disable(scope);
            }
            catch (Exception e)
            {
                retryFailure = $"Disable() threw during recovery: {Describe(e)}";
                LogError($"Failed to disable {entry.Name} during recovery: {Describe(e)}");
            }

            // Clearing happens only here, only after the work above was redone, and only to be
            // replaced by this attempt's own result. Nothing is forgiven that was not retried.
            scope.ClearRecordedFailures();
            if (retryFailure != null) scope.RecordFailure(retryFailure);

            try
            {
                scope.Dispose();
            }
            catch (Exception e)
            {
                scope.RecordFailure($"recover() threw: {Describe(e)}");
            }

            if (!TeardownIsClean(scope))
            {
                ReportResidual(entry, scope, "a teardown that could not be retried");
                return TransitionOutcome.Failed;
            }

            entry.Residual = null;
            return TransitionOutcome.Succeeded;
        }

        /// <summary>
        ///     Puts the module back below its own effective-state flag after an activation that
        ///     did not finish. <c>Disable</c> lowers that flag as its first statement, so this is
        ///     the interface's only way to say "you are not running". Its failure joins the same
        ///     list as every other teardown failure.
        /// </summary>
        private static void ForceInactive(Entry entry, ModuleScope scope)
        {
            try
            {
                entry.Module.Disable(scope);
            }
            catch (Exception e)
            {
                scope.RecordFailure($"Disable() threw while rolling back: {Describe(e)}");
            }
        }

        /// <summary>
        ///     Lowers the stop request after an activation that is still the newest intent. It
        ///     runs inside the machine's own containment, but a module getter that throws here
        ///     would turn a successful activation into a failed one, which it is not.
        /// </summary>
        private static void ClearStopRequest(Entry entry)
        {
            try
            {
                entry.Module.SetStopRequested(false);
            }
            catch (Exception e)
            {
                Contain($"could not clear the stop request of {entry.Name}", e);
            }
        }

        private static void ReportResidual(Entry entry, ModuleScope scope, string context)
        {
            var report = new StringBuilder();
            report.Append($"{entry.Name} left something behind while {context}. ");
            report.Append(
                "The module is faulted; changing its setting again asks for another attempt.");

            var failures = scope.TeardownFailures;
            for (var i = 0; i < failures.Count; i++)
                report.Append($"\n  - {failures[i]}");

            LogError(report.ToString());
        }

        /// <summary>
        ///     Reports the applied state through the log and through the toggle's own
        ///     description, which depends on ConfigurationManager being installed and on it
        ///     re-reading the description after a rebuild (unverified until the game runs).
        ///     A transition into <see cref="ModuleState.Faulted" /> is logged at Warning, every
        ///     other one at Info. A fault can be reached without any exception - an activation
        ///     that runs out of deferrals - and the default log filter drops Info, so logging it
        ///     there would leave no trace of it at all.
        /// </summary>
        private static void PublishState(Entry entry)
        {
            var state = entry.Machine.State;
            if (state == entry.PublishedState) return;

            entry.PublishedState = state;

            var fault = entry.Machine.LastFault;
            var message = state == ModuleState.Faulted && !string.IsNullOrEmpty(fault)
                ? $"Module {entry.Name} is now {state}: {fault}"
                : $"Module {entry.Name} is now {state}";

            if (state == ModuleState.Faulted)
                LogWarning(message);
            else
                LogInfo(message);

            UpdateToggleDescription(entry);
            ConfigurationManagerBridge.Refresh();
        }

        /// <summary>
        ///     Appends the applied state to the toggle's description. The setting itself is never
        ///     written (R1.3b): where the value and the applied state disagree, this is what says
        ///     so.
        /// </summary>
        private static void UpdateToggleDescription(Entry entry)
        {
            var toggle = entry.Toggle;
            if (toggle == null) return;

            try
            {
                var current = toggle.Description;
                if (entry.BaseDescription == null)
                    entry.BaseDescription = current == null ? "" : current.Description;

                // A healthy module has no state line. Appending an empty one would leave a
                // trailing newline, which BepInEx writes to the .cfg as an empty "## " line.
                var stateLine = DescribeState(entry);
                var text = string.IsNullOrEmpty(stateLine)
                    ? entry.BaseDescription
                    : $"{entry.BaseDescription}\n{stateLine}";
                var tags = current?.Tags ?? Array.Empty<object>();

                // ConfigurationManager reads the attribute copy when one is attached, so both
                // have to be updated or the tooltip keeps the stale text.
                foreach (var tag in tags)
                    if (tag is ConfigurationManagerAttributes attributes)
                        attributes.Description = text;

                var description =
                    new ConfigDescription(text, current?.AcceptableValues, tags);
                if (!TrySetDescription(toggle, description))
                    LogDebug(
                        $"Could not replace the description of {entry.ConfigSection}:module");
            }
            catch (Exception e)
            {
                Contain($"could not report the state of {entry.Name} in the settings UI", e);
            }
        }

        private static string DescribeState(Entry entry)
        {
            if (entry.Machine.State == ModuleState.Faulted)
                return Automatics.L10N.Translate("@message_module_state_faulted");

            return RuntimeToggleSupported
                ? ""
                : Automatics.L10N.Translate("@message_module_runtime_toggle_degraded");
        }

        /// <summary>
        ///     ModUtils replaces descriptions the same way, but its helper is private and its
        ///     refresh only runs when the language changes, so the ten lines are repeated here
        ///     rather than reaching into a library that is read-only by decision (ADR-0002).
        /// </summary>
        private static bool TrySetDescription(ConfigEntryBase entry, ConfigDescription description)
        {
            var setter =
                AccessTools.PropertySetter(entry.GetType(), nameof(ConfigEntryBase.Description)) ??
                AccessTools.PropertySetter(typeof(ConfigEntryBase),
                    nameof(ConfigEntryBase.Description));
            if (setter != null)
            {
                setter.Invoke(entry, new object[] { description });
                return true;
            }

            var field = AccessTools.Field(entry.GetType(), "<Description>k__BackingField") ??
                        AccessTools.Field(typeof(ConfigEntryBase), "<Description>k__BackingField");
            if (field == null || !typeof(ConfigDescription).IsAssignableFrom(field.FieldType))
                return false;

            field.SetValue(entry, description);
            return true;
        }

        /// <summary>
        ///     Records a contained failure. Every argument it takes is already a plain string, and
        ///     the description of the exception cannot throw, so reporting a contained failure
        ///     cannot itself become an uncontained one.
        /// </summary>
        private static void Contain(string what, Exception e)
        {
            LogDebug($"{what}: {Describe(e)}");
        }

        /// <summary>
        ///     Containment is worthless if describing the failure can itself throw, which is
        ///     exactly what a module handing back a hostile exception would do.
        /// </summary>
        private static string Describe(Exception e)
        {
            try
            {
                return e.ToString();
            }
            catch
            {
                return "<the failure could not describe itself>";
            }
        }

        private static void LogError(string message)
        {
            try
            {
                Automatics.Logger?.Error(message);
            }
            catch
            {
                // A logger that cannot log is not a reason to escape into the caller.
            }
        }

        private static void LogWarning(string message)
        {
            try
            {
                Automatics.Logger?.Warning(message);
            }
            catch
            {
                // As above.
            }
        }

        private static void LogInfo(string message)
        {
            try
            {
                Automatics.Logger?.Info(message);
            }
            catch
            {
                // As above.
            }
        }

        private static void LogDebug(string message)
        {
            try
            {
                Automatics.Logger?.Debug(message);
            }
            catch
            {
                // As above.
            }
        }

        /// <summary>
        ///     One module's registration. <see cref="Current" /> and <see cref="Residual" /> are
        ///     never both set: a scope that could not be torn down blocks any new activation,
        ///     because M7 always routes recovery ahead of an enable.
        ///     <see cref="Name" />, <see cref="ConfigSection" /> and <see cref="Toggle" /> are
        ///     copies taken at registration, so that no later path has to call back into the
        ///     module just to describe it.
        /// </summary>
        private sealed class Entry
        {
            public Entry(IAutomaticsModule module, string name, string configSection,
                ConfigEntry<AutomaticsModule> toggle)
            {
                Module = module;
                Name = name;
                ConfigSection = configSection;
                Toggle = toggle;
                Machine = new ModuleTransitionMachine(name,
                    () => EnableTransition(this),
                    () => DisableTransition(this),
                    () => RecoverTransition(this));
                PublishedState = ModuleState.Disabled;
            }

            public IAutomaticsModule Module { get; }

            public string Name { get; }

            public string ConfigSection { get; }

            public ConfigEntry<AutomaticsModule> Toggle { get; }

            public ModuleTransitionMachine Machine { get; }

            /// <summary>The scope of the activation that is running. Null unless enabled.</summary>
            public ModuleScope Current { get; set; }

            /// <summary>
            ///     A scope whose teardown did not complete. This is what being faulted actually
            ///     means, and it is what recovery retries, so it is never dropped.
            /// </summary>
            public ModuleScope Residual { get; set; }

            /// <summary>Increases with every activation attempt. Zero means none has run.</summary>
            public int Generation { get; set; }

            /// <summary>
            ///     Increases with every request. A transition captures it before it runs, so a
            ///     request that arrives while it runs is recognisable as newer intent instead of
            ///     being undone by the transition that did not serve it. The machine keeps the
            ///     same counter for the same reason; this one guards the stop request, which the
            ///     machine knows nothing about.
            /// </summary>
            public int RequestVersion { get; set; }

            public ModuleState PublishedState { get; set; }

            /// <summary>The description as bound, before any state line was appended.</summary>
            public string BaseDescription { get; set; }
        }
    }
}
