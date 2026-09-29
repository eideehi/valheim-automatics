using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace Automatics
{
    /// <summary>
    ///     Everything one activation of one module owns: its patches, its subscriptions and the
    ///     components it attached. Teardown is the reverse of acquisition (R1.6).
    ///     The ordering, the re-entrancy rules and the per-entry isolation of that teardown are
    ///     not implemented here. They live in <see cref="TeardownLedger" />, which depends on
    ///     nothing outside the base class library and is therefore executed for real by
    ///     <c>tests/ModuleLifecycleHarness</c>. What this class adds is only the part that has to
    ///     touch the engine or the patch library, and that part cannot be proven offline.
    ///     Because <c>Enable</c> acquires in the order patch, subscribe, attach, the reverse walk
    ///     gives components first, then subscriptions, then patches, with no ordering code of its
    ///     own. Components attached after the activation finished are appended, so the reverse
    ///     walk still reaches them before the subscriptions that created them.
    ///     One rule holds over every acquisition below: once it has been attempted, either the
    ///     resource is registered for teardown, or it has provably been released, or this scope
    ///     reports itself unclean. There is no fourth outcome, and in particular no outcome where
    ///     something is installed and nobody is holding the obligation to remove it.
    /// </summary>
    internal sealed class ModuleScope : IDisposable
    {
        /// <summary>
        ///     Patch applications that cannot be undone, keyed by Harmony id and patch type.
        ///     See <see cref="PatchAll" /> for why the degraded mode tracks this itself instead
        ///     of asking Harmony, and why an entry only appears once the application returned.
        /// </summary>
        private static readonly HashSet<string> PermanentPatches = new HashSet<string>();

        /// <summary>
        ///     Tracked components by identity, each with the ledger handles of its two entries.
        ///     Keyed by reference rather than by the engine's equality, under which every
        ///     destroyed component compares equal to every other.
        /// </summary>
        private readonly Dictionary<Component, TrackedEntries> _components =
            new Dictionary<Component, TrackedEntries>(IdentityComparer.Instance);

        private readonly TeardownLedger _ledger = new TeardownLedger();

        /// <summary>
        ///     The tracked count at which <see cref="Track" /> next drops components the engine
        ///     has already destroyed. See <see cref="PruneDestroyed" />.
        /// </summary>
        private int _pruneAt = MinimumPruneAt;

        private const int MinimumPruneAt = 64;

        /// <summary>
        ///     True while a teardown walk is running, so that an undo which calls
        ///     <see cref="Dispose" /> again does not start a second walk inside the first.
        /// </summary>
        private bool _disposing;

        /// <summary>
        ///     This scope's single Harmony instance, created by the first <see cref="PatchAll" />.
        ///     Null until then.
        /// </summary>
        private Harmony _harmony;

        /// <summary>
        ///     Undo obligations the ledger refused. Normally empty. See
        ///     <see cref="RegisterOrRelease" />.
        /// </summary>
        private readonly List<Orphan> _orphans = new List<Orphan>();

        public ModuleScope(string moduleName, string harmonyId, int generation)
        {
            if (string.IsNullOrEmpty(moduleName))
                throw new ArgumentException("A module name is required.", nameof(moduleName));
            if (string.IsNullOrEmpty(harmonyId))
                throw new ArgumentException("A Harmony id is required.", nameof(harmonyId));
            if (generation <= 0)
                throw new ArgumentOutOfRangeException(nameof(generation),
                    "Generation 0 is reserved for components that are not attached.");

            ModuleName = moduleName;
            HarmonyId = harmonyId;
            Generation = generation;
        }

        public string ModuleName { get; }

        /// <summary>
        ///     This module's own Harmony id, never the plugin's root id. The always-on root
        ///     patches are owned by a different id and no scope can reach them (R3.1).
        /// </summary>
        public string HarmonyId { get; }

        /// <summary>One or more. Zero is the reserved "not attached" stamp.</summary>
        public int Generation { get; }

        /// <summary>True once every registration has been undone.</summary>
        public bool IsDisposed => _orphans.Count == 0 && _ledger.IsDisposed;

        /// <summary>
        ///     What is still outstanding after a teardown, plus whatever was reported through
        ///     <see cref="RecordFailure" />. An empty list is the registry's single success test;
        ///     a failure that does not reach this list does not exist.
        /// </summary>
        public IReadOnlyList<string> TeardownFailures
        {
            get
            {
                var failures = new List<string>();

                // Newest first, matching the order they are undone in.
                for (var i = _orphans.Count - 1; i >= 0; i--)
                    if (_orphans[i].Failure != null)
                        failures.Add(_orphans[i].Failure);

                failures.AddRange(_ledger.Failures);
                return failures;
            }
        }

        /// <summary>
        ///     Undoes everything this scope owns, in reverse order of acquisition. Calling it
        ///     again walks only what is still outstanding, so a teardown that failed part way can
        ///     be retried without repeating what already succeeded (R2.2).
        ///     The ordering this guarantees, stated rather than implied:
        ///     <list type="number">
        ///         <item>
        ///             obligations the ledger refused, newest first - they are by construction
        ///             the most recent acquisitions;
        ///         </item>
        ///         <item>everything the ledger holds, in its own reverse order;</item>
        ///         <item>
        ///             anything refused while that walk was running, which is why the first step
        ///             is repeated afterwards instead of being left for the next call.
        ///         </item>
        ///     </list>
        ///     A nested call - an undo that calls this method again - does nothing and returns.
        ///     The walk already running reaches everything the nested call would have, and
        ///     joining it would start the ledger's teardown while an obligation is still
        ///     executing.
        ///     What is deliberately not guaranteed is a total reverse order over obligations that
        ///     are created during a teardown. One created after the walk passed its position is
        ///     undone in the trailing pass, later than its acquisition order would put it. That
        ///     is the cost of accepting work during a teardown at all, and it is why acquiring
        ///     against a scope that is being torn down still throws at the acquirer.
        /// </summary>
        public void Dispose()
        {
            if (_disposing) return;

            _disposing = true;
            try
            {
                DisposeOrphans();
                _ledger.Dispose();
                DisposeOrphans();
            }
            finally
            {
                _disposing = false;
            }
        }

        /// <summary>
        ///     Applies every patch declared by <paramref name="patchType" /> under this scope's
        ///     own Harmony id and registers their removal.
        ///     May be called more than once; a module with several patch classes does. The
        ///     removal is registered only on the first call, because a scope has exactly one
        ///     Harmony id and removing an id's patches removes all of them. A second registration
        ///     would therefore not remove a second thing - it would remove the first call's
        ///     patches as well, and do it at the wrong point in the reverse walk, taking the
        ///     patches off before the subscriptions and components that were acquired after them.
        ///     Registering once puts every patch this scope owns at the position of the earliest
        ///     one, which is where the acquisition contract wants them: undone last.
        ///     <para>
        ///         That contract is on the caller. <c>Enable</c> must acquire patches first, then
        ///         subscriptions, then components (R4.3), and the reverse walk then produces
        ///         R1.6's order by itself. A caller that subscribes or attaches before its first
        ///         <c>PatchAll</c> gets its patches removed before those, because the single
        ///         registration sits wherever the first call put it. Nothing here detects that;
        ///         the pilot's acquisition order is checked at its own source (plan I6).
        ///     </para>
        ///     In the degraded mode of R3.2 the patches are applied once and deliberately not
        ///     registered, because nothing available can remove them; turning the module off is a
        ///     soft gate from then on. Whether an id already carries a patch type is tracked here
        ///     rather than asked of <c>Harmony.HasAnyPatches</c>, since this branch is reached
        ///     precisely when the startup probe could not confirm that API exists.
        /// </summary>
        public void PatchAll(Type patchType)
        {
            if (patchType == null) throw new ArgumentNullException(nameof(patchType));

            if (!HarmonyCapabilities.CanUnpatch)
            {
                var applied = $"{HarmonyId}\n{patchType.FullName}";
                if (PermanentPatches.Contains(applied)) return;

                // Recorded only once the application has actually returned. Recording it first
                // would let a failed application look done, and the next activation would skip
                // the work and report a module that is running with none of its patches on.
                new Harmony(HarmonyId).PatchAll(patchType);
                PermanentPatches.Add(applied);
                return;
            }

            // The removal is registered before anything is applied. Nothing promises that a
            // type's patches are applied atomically, so an application that throws part way
            // through must still leave a removal behind. Registering first is free: removing
            // patches from an id that owns none does nothing.
            if (_harmony == null)
            {
                var harmony = new Harmony(HarmonyId);
                RegisterOrRelease($"patches of {ModuleName}", () => harmony.UnpatchSelf());
                _harmony = harmony;
            }

            _harmony.PatchAll(patchType);
        }

        /// <summary>
        ///     Adds <paramref name="handler" /> through <paramref name="add" /> and registers its
        ///     removal.
        ///     The removal is registered before the handler is added, for the same reason as the
        ///     patches above: an add delegate is free to subscribe and then throw.
        ///     <para>
        ///         Caller contract, because the code below cannot enforce any of it:
        ///     </para>
        ///     <list type="bullet">
        ///         <item>
        ///             Pass a named method, never an anonymous lambda. The delegate that is
        ///             removed has to be the delegate that was added.
        ///         </item>
        ///         <item>
        ///             <paramref name="remove" /> must be idempotent, because it runs even when
        ///             <paramref name="add" /> threw before it subscribed, and it runs again when
        ///             a previous removal failed.
        ///         </item>
        ///         <item>
        ///             <paramref name="remove" /> must own <paramref name="handler" />. Ordinary
        ///             delegate removal takes the last matching entry from the invocation list,
        ///             so a handler that some other owner also registered could be the one taken
        ///             off. Every pair in this codebase is <c>h => Hooks.X += h</c> against
        ///             <c>h => Hooks.X -= h</c> with a handler belonging to exactly one module,
        ///             which is what makes removing a never-added handler a no-op here rather
        ///             than in general.
        ///         </item>
        ///         <item>
        ///             <paramref name="add" /> must not tear this scope down and then subscribe.
        ///             The registration that would own the subscription was made before the
        ///             teardown started and has already run by then, so the subscription would
        ///             have no owner and nothing could give it one.
        ///         </item>
        ///     </list>
        /// </summary>
        public IDisposable Subscribe<T>(Action<T> add, Action<T> remove, T handler)
            where T : Delegate
        {
            if (add == null) throw new ArgumentNullException(nameof(add));

            var token = new SubscriptionToken<T>(remove, handler);
            RegisterOrRelease($"subscription of {DescribeHandler(handler)}", token.Dispose);
            add(handler);
            return token;
        }

        /// <summary>
        ///     Attaches <typeparamref name="TComponent" /> to <paramref name="target" /> and takes
        ///     ownership of it. Idempotent within one generation: a component this activation
        ///     already put there is tracked and returned as is.
        ///     A component left over from an older generation means the engine has not run its
        ///     deferred destruction yet, so this throws
        ///     <see cref="ScopeAttachDeferredException" /> and the registry retries on the next
        ///     tick. Use this only from the reconciliation pass of an activation; anything that
        ///     runs on a game call stack must use <see cref="TryAttach{TComponent}" />.
        /// </summary>
        public TComponent Attach<TComponent>(GameObject target)
            where TComponent : Component, IScopedComponent
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            var existing = target.GetComponent<TComponent>();
            if (existing != null)
            {
                if (existing.ScopeGeneration == Generation)
                {
                    Track(existing);
                    return existing;
                }

                throw new ScopeAttachDeferredException(
                    $"{target.name} still carries a {typeof(TComponent).Name} from generation " +
                    $"{existing.ScopeGeneration}; generation {Generation} has to wait for it to " +
                    "be destroyed.");
            }

            // AddComponent runs Awake synchronously, so the stamp lands a statement later and
            // Awake itself must never read it. Generation 0 is what it sees, and 0 means "not
            // live yet" to every per-frame guard.
            // Ownership is taken between the two, so a stamp that throws still leaves a
            // component that this scope will destroy rather than an untracked stray.
            var added = target.AddComponent<TComponent>();
            Track(added);
            added.ScopeGeneration = Generation;
            return added;
        }

        /// <summary>
        ///     The variant for the paths that run on a game call stack: a Harmony postfix, or a
        ///     hook raised from <c>Player.Awake</c>, where an escaping exception damages the game
        ///     rather than the mod.
        ///     What it guarantees is that no failure of the attach path is handed back to the
        ///     caller: every exception the path can produce, the ledger's refusal included, is
        ///     caught here and either reported as benign or recorded against the scope. That is
        ///     not the same as "never throws" in the absolute, and the difference is worth being
        ///     honest about - a runtime that cannot allocate inside a catch block is not
        ///     something any catch block can promise to survive.
        /// </summary>
        public bool TryAttach<TComponent>(GameObject target, out TComponent component)
            where TComponent : Component, IScopedComponent
        {
            try
            {
                component = Attach<TComponent>(target);
                return true;
            }
            catch (ScopeAttachDeferredException e)
            {
                // Expected and benign: a component from an older generation has not been
                // destroyed yet. The target is picked up by the reconciliation pass of the next
                // activation, so this is not an incomplete activation and must not fault the
                // module.
                component = null;
                LogDebug($"[{ModuleName}] deferred attaching {typeof(TComponent).Name}", e);
                return false;
            }
            catch (Exception e)
            {
                // Anything else means this activation is incomplete, and swallowing that would
                // leave the module reporting itself healthy with a component it never attached.
                // It cannot be thrown at the caller, so it is recorded instead: the failure list
                // is the only place a failure is allowed to make itself count.
                component = null;
                RecordFailureSafely($"TryAttach<{typeof(TComponent).Name}> failed", e);
                LogDebug($"[{ModuleName}] could not attach {typeof(TComponent).Name}", e);
                return false;
            }
        }

        /// <summary>
        ///     Takes ownership of a component this scope did not create. Registering the same
        ///     component twice is a no-op, so the reconciliation pass can call this freely.
        ///     Two registrations are made, undone last-in-first-out, so the pre-destroy hook runs
        ///     before the destruction. They are separate entries on purpose: the ledger isolates
        ///     and retries each one by itself, so a hook that throws stays outstanding - which is
        ///     what keeps the teardown unclean and makes recovery genuinely repeat it - while the
        ///     destruction below it still runs in the same pass.
        /// </summary>
        public void Track(Component component)
        {
            if (component == null) return;
            if (IsTracked(component)) return;

            var entries = new TrackedEntries();
            _components.Add(component, entries);

            // The destroy entry names a different release for the refusal path: a refusal
            // releases immediately, and releasing a component means the hook first and the
            // destruction second, which is the very order the two entries exist to produce.
            var label = component.GetType().Name;
            entries.Destroy = RegisterOrRelease($"destroy {label}", () => DestroyTracked(component),
                () => DetachThenDestroy(component));
            entries.Detach = RegisterOrRelease($"detach {label}", () => DetachTracked(component));

            // Reached only when the ledger took both entries, so no teardown has started and
            // no undo is running: nothing can be holding a position in either collection.
            if (_components.Count >= _pruneAt) PruneDestroyed();
        }

        /// <summary>
        ///     Stops tracking every component the engine has destroyed on its own - a zone that
        ///     unloaded, a rock that was mined out - and withdraws their ledger entries. Both
        ///     entries of such a component are already no-ops, since the hook and the
        ///     destruction each return early on a destroyed component, so dropping them changes
        ///     no teardown; it only stops a long session from holding every component it ever
        ///     attached. Run when the tracked count has doubled since the last prune, which keeps
        ///     the cost amortised constant per <see cref="Track" />.
        /// </summary>
        private void PruneDestroyed()
        {
            var destroyed = new List<Component>();
            foreach (var component in _components.Keys)
                if (component == null)
                    destroyed.Add(component);

            foreach (var component in destroyed)
            {
                var entries = _components[component];
                _components.Remove(component);
                _ledger.Withdraw(entries.Destroy);
                _ledger.Withdraw(entries.Detach);
            }

            _pruneAt = Math.Max(MinimumPruneAt, _components.Count * 2);
        }

        /// <summary>
        ///     Gives up ownership without destroying anything. Both registrations are withdrawn
        ///     from the ledger; once a teardown has started the ledger refuses that, and they
        ///     stay and complete as no-ops instead.
        /// </summary>
        public void Forget(Component component)
        {
            if (component == null) return;
            if (!_components.TryGetValue(component, out var entries)) return;

            _components.Remove(component);
            _ledger.Withdraw(entries.Destroy);
            _ledger.Withdraw(entries.Detach);
        }

        /// <summary>
        ///     Reports a teardown failure that happened outside this scope, such as an exception
        ///     thrown by the module's own <c>Disable</c>. It joins the same list the registry
        ///     tests, so there is no path that records a failure without affecting the outcome.
        /// </summary>
        public void RecordFailure(string description)
        {
            _ledger.RecordFailure(description);
        }

        /// <summary>
        ///     Drops the externally reported failures so that a retry can report itself clean.
        ///     Only recovery calls this, and only as part of actually redoing the work those
        ///     reports describe; leaving the previous attempt's report in place would otherwise
        ///     pin the module in the faulted state for good.
        ///     It cannot forgive an outstanding obligation: entries the ledger still holds, and
        ///     refused obligations parked here, are not "recorded failures" - they are work, and
        ///     they produce their failure text again from the retry itself.
        /// </summary>
        public void ClearRecordedFailures()
        {
            _ledger.ClearRecordedFailures();
        }

        /// <summary>
        ///     Registers the undo for something that is about to be, or has just been, acquired.
        ///     The ledger refuses registrations once its reverse walk has begun, because an entry
        ///     added behind the walk would never be undone. Its caller contract is that whoever
        ///     was refused still owns the resource. So the release is attempted here, and if that
        ///     release itself fails the obligation is kept - the delegate, not a note about it -
        ///     so <see cref="Dispose" /> can retry it and no clearing of failure text can lose
        ///     it. The refusal is then passed on: acquiring against a scope that is being torn
        ///     down is a mistake the registry has to see, not one to absorb.
        ///     <paramref name="releaseOnRefusal" /> exists for an acquisition whose undo is split
        ///     across more than one entry. Undoing it in one go is only the same thing as the
        ///     entries when they run in their intended order, and a refusal has no entries left
        ///     to order.
        /// </summary>
        private object RegisterOrRelease(string label, Action undo, Action releaseOnRefusal = null)
        {
            try
            {
                return _ledger.Add(label, undo);
            }
            catch
            {
                var release = releaseOnRefusal ?? undo;
                try
                {
                    release();
                }
                catch (Exception e)
                {
                    _orphans.Add(new Orphan(label, release, $"{label}: {Describe(e)}"));
                }

                throw;
            }
        }

        /// <summary>
        ///     Retries the obligations the ledger refused, newest first. An entry disappears only
        ///     when its undo returned; one that throws keeps both the delegate and the reason, so
        ///     the scope stays unclean and the next teardown tries again.
        ///     No list position is held across an undo. An undo is arbitrary code: it can add an
        ///     obligation, and an index taken before it means nothing afterwards. The entry to
        ///     try next is resolved from scratch each time and the one that completed is removed
        ///     by identity, so an obligation can never be dropped without having been undone.
        ///     One obligation is attempted at most once per pass, which is what terminates the
        ///     loop when an undo keeps failing; an obligation created during the pass has not
        ///     been attempted yet, so it is picked up before the pass ends.
        /// </summary>
        private void DisposeOrphans()
        {
            for (var i = 0; i < _orphans.Count; i++) _orphans[i].Attempted = false;

            while (true)
            {
                var orphan = NextUnattemptedOrphan();
                if (orphan == null) return;

                orphan.Attempted = true;
                try
                {
                    orphan.Undo();
                    RemoveOrphan(orphan);
                }
                catch (Exception e)
                {
                    orphan.Failure = $"{orphan.Label}: {Describe(e)}";
                }
            }
        }

        private Orphan NextUnattemptedOrphan()
        {
            for (var i = _orphans.Count - 1; i >= 0; i--)
                if (!_orphans[i].Attempted)
                    return _orphans[i];

            return null;
        }

        private void RemoveOrphan(Orphan orphan)
        {
            for (var i = _orphans.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_orphans[i], orphan))
                {
                    _orphans.RemoveAt(i);
                    return;
                }
        }

        /// <summary>
        ///     The pre-destroy half of a tracked component, run synchronously before the half
        ///     below. Engine destruction is deferred to the end of the frame, so whatever the
        ///     component is still doing - a running coroutine above all - has to be stopped now
        ///     rather than then.
        ///     No containment here: letting this throw is what keeps the ledger entry
        ///     outstanding, and an outstanding entry is exactly what an unfinished obligation
        ///     should look like.
        /// </summary>
        private void DetachTracked(Component component)
        {
            if (!IsTracked(component)) return;

            // Already destroyed: the window this hook exists to close has been closed by the
            // destruction itself, so there is nothing left for it to stop.
            if (component == null) return;

            if (component is IScopedComponent scoped) scoped.OnScopeDetaching();
        }

        /// <summary>
        ///     The whole obligation of one tracked component in the order the two ledger entries
        ///     would have run it, and with the same isolation between them. Used only when the
        ///     ledger refused to take those entries.
        ///     The halves are isolated because that is what the two entries give on the accepted
        ///     path: a hook that throws must not stop the destruction, and must still leave the
        ///     obligation outstanding. So the hook's failure is held, the destruction runs, and
        ///     the failure is raised afterwards - which is what parks this as an orphan and has
        ///     it retried. If the destruction fails as well, its failure is the one reported;
        ///     both halves are retried together, so nothing is lost but a line of text.
        /// </summary>
        private void DetachThenDestroy(Component component)
        {
            Exception detachFailure = null;
            try
            {
                DetachTracked(component);
            }
            catch (Exception e)
            {
                detachFailure = e;
            }

            DestroyTracked(component);

            if (detachFailure != null) throw detachFailure;
        }

        /// <summary>
        ///     Destroys one tracked component. Only the component: the object it sits on belongs
        ///     to the game (R4.4).
        /// </summary>
        private void DestroyTracked(Component component)
        {
            if (!IsTracked(component)) return;
            if (component == null) return;

            UnityEngine.Object.Destroy(component);
        }

        /// <summary>
        ///     Identity, not engine equality, and resolved afresh on every call. Holding a
        ///     lookup result across the execution of a module's own callback would be wrong:
        ///     that callback is free to call <see cref="Forget" />.
        /// </summary>
        private bool IsTracked(Component component)
        {
            return component is object && _components.ContainsKey(component);
        }

        private void RecordFailureSafely(string what, Exception e)
        {
            try
            {
                _ledger.RecordFailure($"{what}: {Describe(e)}");
            }
            catch
            {
                // The scope is already failing; losing the note about it is the lesser harm.
            }
        }

        private static string DescribeHandler(Delegate handler)
        {
            return handler?.Method == null ? "<unnamed>" : handler.Method.Name;
        }

        /// <summary>
        ///     Containment is worthless if describing the failure can itself throw.
        /// </summary>
        private static string Describe(Exception e)
        {
            try
            {
                return e.Message;
            }
            catch
            {
                return "<the failure could not describe itself>";
            }
        }

        private static void LogDebug(string message, Exception e)
        {
            try
            {
                Automatics.Logger?.Debug($"{message}: {Describe(e)}");
            }
            catch
            {
                // Losing a debug line is preferable to losing the containment around it.
            }
        }

        /// <summary>
        ///     The ledger handles of one tracked component's two entries. A handle stays null
        ///     when the ledger refused that entry.
        /// </summary>
        private sealed class TrackedEntries
        {
            public object Destroy;
            public object Detach;
        }

        /// <summary>
        ///     Reference identity for components. <c>ReferenceEqualityComparer</c> is not
        ///     available on this target framework.
        /// </summary>
        private sealed class IdentityComparer : IEqualityComparer<Component>
        {
            public static readonly IdentityComparer Instance = new IdentityComparer();

            public bool Equals(Component x, Component y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(Component obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        /// <summary>
        ///     An undo the ledger would not take. It is a delegate rather than a message because
        ///     a message cannot be retried.
        /// </summary>
        private sealed class Orphan
        {
            public Orphan(string label, Action undo, string failure)
            {
                Label = label;
                Undo = undo;
                Failure = failure;
            }

            public string Label { get; }

            public Action Undo { get; }

            public string Failure { get; set; }

            /// <summary>Tried already in the teardown pass that is running.</summary>
            public bool Attempted { get; set; }
        }
    }
}
