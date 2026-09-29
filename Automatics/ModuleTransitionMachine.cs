using System;
using System.Collections.Generic;

namespace Automatics
{
    /// <summary>
    ///     The lifecycle state of one module. <see cref="Enabling" /> and
    ///     <see cref="Disabling" /> are only observable from inside a transition callback.
    /// </summary>
    internal enum ModuleState
    {
        Disabled,
        Enabling,
        Enabled,
        Disabling,
        Faulted
    }

    /// <summary>
    ///     The result of one transition attempt. <see cref="Deferred" /> means "cannot be done
    ///     this frame, try again on the next one".
    /// </summary>
    internal enum TransitionOutcome
    {
        Succeeded,
        Failed,
        Deferred
    }

    /// <summary>
    ///     The transition rules of one module, with no dependency outside the base class
    ///     library. Everything the engine or the patch library touches lives in the scope and
    ///     the registry, so the rules below can be executed offline.
    ///     The machine only reads the toggle decision handed to <see cref="Request" />; it has
    ///     no way to reach a stored setting, which is how R1.3b is guaranteed structurally.
    /// </summary>
    internal sealed class ModuleTransitionMachine
    {
        private readonly Func<TransitionOutcome> _disable;
        private readonly Func<TransitionOutcome> _enable;
        private readonly int _maxDeferrals;
        private readonly Func<TransitionOutcome> _recover;

        /// <summary>The tick a transition last ran on, whatever its outcome was.</summary>
        private long? _lastTransitionTick;

        private string _pendingError;

        private bool _pumping;

        /// <summary>
        ///     Bumped by every <see cref="Request" />. A transition captures this before it
        ///     runs so that a request arriving while it runs is recognised as newer intent
        ///     instead of being cleared as if the transition had served it.
        /// </summary>
        private int _requestVersion;

        public ModuleTransitionMachine(string moduleName, Func<TransitionOutcome> enable,
            Func<TransitionOutcome> disable, Func<TransitionOutcome> recover,
            int maxDeferrals = 4)
        {
            if (string.IsNullOrEmpty(moduleName))
                throw new ArgumentException("A module name is required.", nameof(moduleName));
            if (enable == null) throw new ArgumentNullException(nameof(enable));
            if (disable == null) throw new ArgumentNullException(nameof(disable));
            if (recover == null) throw new ArgumentNullException(nameof(recover));
            if (maxDeferrals < 0)
                throw new ArgumentOutOfRangeException(nameof(maxDeferrals),
                    "The deferral limit cannot be negative.");

            ModuleName = moduleName;
            _enable = enable;
            _disable = disable;
            _recover = recover;
            _maxDeferrals = maxDeferrals;

            State = ModuleState.Disabled;
            LastTeardownTick = long.MinValue;
        }

        public string ModuleName { get; }

        public ModuleState State { get; private set; }

        /// <summary>Null means no outstanding request.</summary>
        public bool? Desired { get; private set; }

        public int Deferrals { get; private set; }

        /// <summary>
        ///     The tick the last teardown completed on, whether it completed through a disable
        ///     or through a recovery. No enable is started on that same tick, because the engine
        ///     defers object destruction to the end of the frame (R4.5).
        /// </summary>
        public long LastTeardownTick { get; private set; }

        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public int RecoverCalls { get; private set; }

        /// <summary>Why the machine last entered <see cref="ModuleState.Faulted" />.</summary>
        public string LastFault { get; private set; }

        /// <summary>
        ///     Records what the module should be. This never runs a transition callback (R1.4):
        ///     calling it any number of times only overwrites <see cref="Desired" />.
        /// </summary>
        public void Request(bool enable)
        {
            Desired = enable;
            _requestVersion++;
        }

        /// <summary>
        ///     Runs at most one transition and returns true when it did.
        ///     At most one transition runs per tick (M2 / R4.6), so calling this more than once
        ///     for the same tick is harmless: the later calls do nothing at all, and in
        ///     particular cannot spend a deferral.
        ///     Re-entrant calls, such as a pump made from inside a transition callback, do
        ///     nothing either.
        /// </summary>
        public bool Pump(long tick)
        {
            if (_pumping) return false;

            _pumping = true;
            try
            {
                return PumpOnce(tick);
            }
            finally
            {
                _pumping = false;
            }
        }

        private bool PumpOnce(long tick)
        {
            if (Desired == null) return false;

            var desired = Desired.Value;
            switch (State)
            {
                case ModuleState.Faulted:
                    if (!CanTransition(tick)) return false;
                    return PumpRecover(tick, desired);

                case ModuleState.Disabled:
                    if (!desired)
                    {
                        // The pending enable was cancelled before it ran, so the deferrals it
                        // spent must not be charged to the next attempt.
                        Desired = null;
                        Deferrals = 0;
                        return false;
                    }

                    // R4.5: never enable on the tick a teardown completed on. A teardown is
                    // itself a transition, so the one-per-tick guard below already covers this
                    // sequence; the explicit barrier is kept because R4.5 is about the engine's
                    // deferred destruction rather than about scan cost, and the two must not
                    // silently become one.
                    if (LastTeardownTick == tick) return false;
                    if (!CanTransition(tick)) return false;
                    return PumpEnable(tick);

                case ModuleState.Enabled:
                    if (desired)
                    {
                        Desired = null;
                        Deferrals = 0;
                        return false;
                    }

                    if (!CanTransition(tick)) return false;
                    return PumpDisable(tick);

                default:
                    // Enabling / Disabling are only reachable from inside a callback, which the
                    // re-entrancy guard already turns away.
                    return false;
            }
        }

        /// <summary>
        ///     M2 (R4.6): one transition per tick, whatever the previous one returned.
        /// </summary>
        private bool CanTransition(long tick)
        {
            return _lastTransitionTick != tick;
        }

        private bool PumpEnable(long tick)
        {
            var served = _requestVersion;
            _lastTransitionTick = tick;
            State = ModuleState.Enabling;
            EnableCalls++;

            switch (Invoke(_enable))
            {
                case TransitionOutcome.Succeeded:
                    State = ModuleState.Enabled;
                    Deferrals = 0;
                    CompleteRequest(served);
                    break;

                case TransitionOutcome.Deferred:
                    // Nothing was applied, so the module is still disabled and the request
                    // stands. The next tick retries it.
                    State = ModuleState.Disabled;
                    Deferrals++;
                    if (Deferrals > _maxDeferrals)
                        Fault(served, $"{ModuleName} could not be enabled within " +
                                      $"{_maxDeferrals} deferrals.");
                    break;

                default:
                    // R1.3: a failed enable is faulted, never reported as disabled.
                    Fault(served, $"{ModuleName} failed to enable.");
                    break;
            }

            return true;
        }

        private bool PumpDisable(long tick)
        {
            var served = _requestVersion;
            _lastTransitionTick = tick;
            State = ModuleState.Disabling;
            DisableCalls++;

            switch (Invoke(_disable))
            {
                case TransitionOutcome.Succeeded:
                    State = ModuleState.Disabled;
                    Deferrals = 0;
                    LastTeardownTick = tick;
                    CompleteRequest(served);
                    break;

                case TransitionOutcome.Deferred:
                    // The teardown did not happen, so the module is still enabled.
                    State = ModuleState.Enabled;
                    Deferrals++;
                    if (Deferrals > _maxDeferrals)
                        Fault(served, $"{ModuleName} could not be disabled within " +
                                      $"{_maxDeferrals} deferrals.");
                    break;

                default:
                    // R1.3a: a failed teardown is faulted, never reported as disabled.
                    Fault(served, $"{ModuleName} failed to disable.");
                    break;
            }

            return true;
        }

        private bool PumpRecover(long tick, bool desired)
        {
            var served = _requestVersion;
            _lastTransitionTick = tick;
            RecoverCalls++;

            switch (Invoke(_recover))
            {
                case TransitionOutcome.Succeeded:
                    State = ModuleState.Disabled;
                    Deferrals = 0;
                    LastFault = null;

                    // Recovery finishes the teardown that failed, so it is subject to the same
                    // end-of-frame destruction barrier as a plain disable (R4.5).
                    LastTeardownTick = tick;

                    // R1.3a: recovery only gets the module back to disabled. An outstanding
                    // enable request is served by a later tick, one transition per tick.
                    if (!desired) CompleteRequest(served);
                    break;

                case TransitionOutcome.Deferred:
                    // Same meaning as everywhere else: nothing happened, retry next tick. The
                    // module stays faulted and the request stands.
                    Deferrals++;
                    if (Deferrals > _maxDeferrals)
                        Fault(served, $"{ModuleName} could not recover within " +
                                      $"{_maxDeferrals} deferrals.");
                    break;

                default:
                    Fault(served, $"{ModuleName} could not recover from a failed transition.");
                    break;
            }

            return true;
        }

        private TransitionOutcome Invoke(Func<TransitionOutcome> callback)
        {
            _pendingError = null;

            try
            {
                return callback();
            }
            catch (Exception e)
            {
                _pendingError = Describe(e);
                return TransitionOutcome.Failed;
            }
        }

        /// <summary>
        ///     Enters the faulted state. The request this transition was serving is dropped, so
        ///     a fault is never retried on its own; the next <see cref="Request" /> is what asks
        ///     for recovery. A request that arrived while the callback ran is newer intent and
        ///     survives.
        /// </summary>
        private void Fault(int servedVersion, string reason)
        {
            State = ModuleState.Faulted;
            LastFault = _pendingError == null ? reason : $"{reason}\n{_pendingError}";
            CompleteRequest(servedVersion);
        }

        /// <summary>
        ///     Clears the pending request only when it is still the one this transition was
        ///     serving.
        /// </summary>
        private void CompleteRequest(int servedVersion)
        {
            if (_requestVersion == servedVersion) Desired = null;
        }

        /// <summary>
        ///     Containment is worthless if describing the failure can itself throw, which is
        ///     exactly what a callback that hands back a hostile exception would do.
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
    }

    /// <summary>
    ///     Everything one activation owns, undone in reverse registration order (R1.6).
    ///     Each entry is isolated, so one failure cannot stop the others, and a second
    ///     <see cref="Dispose" /> walks only the entries that are still outstanding. That is
    ///     what makes "undone exactly once" hold on the success path (R2.2) while still leaving
    ///     a failed teardown retryable.
    /// </summary>
    internal sealed class TeardownLedger : IDisposable
    {
        private readonly List<string> _recordedFailures = new List<string>();
        private readonly List<Registration> _registrations = new List<Registration>();

        private bool _teardownStarted;

        /// <summary>
        ///     Registrations marked by <see cref="Withdraw" /> but not yet removed from the list.
        ///     Always zero once a teardown has started.
        /// </summary>
        private int _withdrawn;

        /// <summary>True once a teardown ran and every registration is undone.</summary>
        public bool IsDisposed
        {
            get
            {
                if (!_teardownStarted) return false;

                for (var i = 0; i < _registrations.Count; i++)
                    if (!_registrations[i].Disposed)
                        return false;

                return true;
            }
        }

        /// <summary>
        ///     Failures recorded from outside, followed by the failure each still-outstanding
        ///     registration produced on its last attempt, in teardown order. A registration
        ///     that later succeeds drops out of this list.
        /// </summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                var failures = new List<string>(_recordedFailures);

                for (var i = _registrations.Count - 1; i >= 0; i--)
                {
                    var failure = _registrations[i].Failure;
                    if (failure != null) failures.Add(failure);
                }

                return failures;
            }
        }

        /// <summary>
        ///     Undoes every outstanding registration in reverse order. Safe to call again: the
        ///     entries that already succeeded are skipped, and an entry whose undo is still
        ///     running is skipped as well, so an undo callback that re-enters this method
        ///     cannot run itself a second time.
        /// </summary>
        public void Dispose()
        {
            if (!_teardownStarted)
            {
                _teardownStarted = true;

                // Only the first call compacts. A re-entrant call arrives while the walk below
                // holds an index into the list, and must not move the entries under it.
                if (_withdrawn > 0) Compact();
            }

            for (var i = _registrations.Count - 1; i >= 0; i--)
            {
                var registration = _registrations[i];
                if (registration.Disposed || registration.InProgress) continue;

                registration.InProgress = true;
                try
                {
                    registration.Undo();
                    registration.Failure = null;
                    registration.Disposed = true;
                }
                catch (Exception e)
                {
                    // The entry stays outstanding, so a later Dispose retries just this one.
                    registration.Failure = $"{registration.Label}: {Describe(e)}";
                }
                finally
                {
                    registration.InProgress = false;
                }
            }
        }

        /// <summary>
        ///     Registers something to undo. Refused once a teardown has started, because an
        ///     entry added after the reverse walk passed its position would never be undone.
        ///     Caller contract: whoever acquires a resource and is then refused here still owns
        ///     that resource and must release it itself. The scope that fills this ledger has to
        ///     honour that, since it acquires (patches, subscribes, attaches) before it
        ///     registers.
        /// </summary>
        public object Add(string label, Action dispose)
        {
            if (dispose == null) throw new ArgumentNullException(nameof(dispose));
            if (_teardownStarted)
                throw new ObjectDisposedException(nameof(TeardownLedger),
                    "The ledger is being torn down and cannot take new registrations.");

            var registration = new Registration(this,
                string.IsNullOrEmpty(label) ? "<unnamed>" : label, dispose);
            _registrations.Add(registration);
            return registration;
        }

        /// <summary>
        ///     Drops a registration that has become moot, identified by the handle
        ///     <see cref="Add" /> returned, so that neither the entry nor what its undo captures
        ///     is held any longer. Returns false, and changes nothing, once a teardown has
        ///     started: from then on an entry may be outstanding, running or carrying a failure,
        ///     and only its own undo is allowed to settle it. Before that point no entry has run,
        ///     so withdrawing one cannot affect the order, the retries or the failures of the
        ///     rest. A handle from another ledger, or one already withdrawn, is also refused.
        ///     Withdrawn entries are removed in batches once they outnumber the live ones, which
        ///     keeps both this call and the list's growth amortised constant.
        /// </summary>
        public bool Withdraw(object handle)
        {
            if (_teardownStarted) return false;
            if (!(handle is Registration registration) || registration.Owner != this ||
                registration.Withdrawn)
                return false;

            registration.Withdrawn = true;
            _withdrawn++;
            if (_withdrawn * 2 > _registrations.Count) Compact();
            return true;
        }

        /// <summary>
        ///     Reports a teardown failure that happened outside the ledger, so that it reaches
        ///     the same success decision.
        /// </summary>
        public void RecordFailure(string description)
        {
            if (string.IsNullOrEmpty(description)) return;
            _recordedFailures.Add(description);
        }

        /// <summary>
        ///     Drops the externally recorded failures. Recovery calls this before retrying, so
        ///     that a teardown that eventually completes can report itself clean.
        /// </summary>
        public void ClearRecordedFailures()
        {
            _recordedFailures.Clear();
        }

        private void Compact()
        {
            _registrations.RemoveAll(registration => registration.Withdrawn);
            _withdrawn = 0;
        }

        /// <summary>
        ///     Isolation is worthless if describing the failure can itself throw.
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

        private sealed class Registration
        {
            private readonly Action _dispose;

            public Registration(TeardownLedger owner, string label, Action dispose)
            {
                Owner = owner;
                Label = label;
                _dispose = dispose;
            }

            public TeardownLedger Owner { get; }

            public string Label { get; }

            public bool Withdrawn { get; set; }

            public bool Disposed { get; set; }

            public bool InProgress { get; set; }

            public string Failure { get; set; }

            public void Undo()
            {
                _dispose();
            }
        }
    }

    /// <summary>
    ///     One subscription owned by a scope. <see cref="Dispose" /> removes the handler first
    ///     and only marks the token spent once the removal returned (invoke-then-clear). A
    ///     removal that throws therefore leaves the token outstanding, so the teardown can be
    ///     retried; clearing first would make the token look spent while the subscription is
    ///     still live, and would lose the only handle on it.
    ///     Caller contract: because a failed removal is retried, the removal must be idempotent.
    ///     A remover that performs its side effect and only then throws will perform that effect
    ///     again on the retry. Removing a delegate from an event or from a settable delegate
    ///     property is idempotent, which is what makes the retry safe here.
    /// </summary>
    internal sealed class SubscriptionToken<T> : IDisposable where T : Delegate
    {
        private T _handler;
        private Action<T> _remove;
        private bool _removing;

        public SubscriptionToken(Action<T> remove, T handler)
        {
            if (remove == null) throw new ArgumentNullException(nameof(remove));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            _remove = remove;
            _handler = handler;
        }

        public bool IsDisposed => _remove == null;

        public void Dispose()
        {
            var remove = _remove;
            if (remove == null) return;

            // A removal callback that disposes this same token would otherwise find the token
            // still armed and remove a second time.
            if (_removing) return;

            _removing = true;
            try
            {
                remove(_handler);
            }
            finally
            {
                _removing = false;
            }

            _remove = null;
            _handler = null;
        }
    }
}
