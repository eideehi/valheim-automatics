using System;
using System.Collections.Generic;

namespace Automatics
{
    /// <summary>
    ///     Offline checks for the dependency-free part of the module lifecycle. The project
    ///     compiles <c>Automatics/ModuleTransitionMachine.cs</c> directly and references nothing
    ///     but the base class library, so these are executed behaviours rather than source
    ///     shape approximations.
    ///     Every case that claims a temporal or ordering property is written so that it can
    ///     actually observe the violation: a case that asserts "no second transition on this
    ///     tick" leaves a request pending while it re-pumps, and a case that asserts "these two
    ///     steps do not share a tick" pumps twice on the same tick.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main()
        {
            Run("M1: a transition request never runs a callback", RequestNeverRunsCallbacks);
            Run("M2: a second transition does not run on the same tick", OneTransitionPerTick);
            Run("M2: repeated pumps on one tick do not spend a deferral", RepeatedPumpsDoNotSpendDeferrals);
            Run("M3: no enable starts on the tick a teardown completed", NoEnableOnTheTeardownTick);
            Run("M3: a successful recovery records the teardown tick", RecoveryRecordsTheTeardownTick);
            Run("M4: a deferred enable stays disabled and retries", DeferredEnableStaysDisabled);
            Run("M4: deferrals past the limit fault the module", DeferralsPastTheLimitFault);
            Run("M4: cancelling a pending transition clears its deferrals", CancellingClearsDeferrals);
            Run("MAJOR-1: a failure after the deferral limit faults, not disables", FailureAfterDeferralsFaults);
            Run("M5: a failed enable faults instead of reporting disabled", FailedEnableFaults);
            Run("M5: an enable that throws is reported as a failure", ThrowingEnableFaults);
            Run("M5: an exception that cannot describe itself is still contained", HostileFailureIsContained);
            Run("M6: a failed disable faults instead of reporting disabled", FailedDisableFaults);
            Run("M7: a faulted module recovers before it enables again", FaultedModuleRecoversFirst);
            Run("M7: a failed recovery stays faulted and does not retry itself", FailedRecoveryStaysFaulted);
            Run("M7: a deferred recovery retries on the next tick", DeferredRecoveryRetries);
            Run("M7: deferred recoveries past the limit fault", DeferredRecoveryPastTheLimitFaults);
            Run("M7: a pump from inside recovery does not re-enter it", NoReentrantRecovery);
            Run("M8: recovering a module that should stay off ends disabled", RecoveryWithoutEnableEndsDisabled);
            Run("R1.4: a request made during an enable survives its completion", RequestDuringEnableSurvives);
            Run("R1.4: a request made during a disable survives its completion", RequestDuringDisableSurvives);
            Run("R1.4: a request made during a failing transition survives the fault", RequestDuringFailureSurvives);
            Run("M9: the transition layer carries no game dependencies", TransitionLayerHasNoGameDependencies);

            Run("ledger: registrations are undone in reverse order", LedgerUndoesInReverseOrder);
            Run("ledger: one failing registration does not stop the others", LedgerFailureDoesNotStopTheOthers);
            Run("ledger: a second teardown does not repeat what succeeded", LedgerSecondTeardownSkipsSucceeded);
            Run("ledger: the outstanding registration can succeed on a retry", LedgerRetryCanSucceed);
            Run("ledger: a re-entrant teardown undoes each registration once", LedgerReentrantTeardownUndoesOnce);
            Run("ledger: a re-entrant teardown inside a failing registration", LedgerReentrantTeardownInsideFailure);
            Run("ledger: a failure that cannot describe itself is contained", LedgerContainsHostileFailure);
            Run("ledger: externally recorded failures surface and can be cleared", LedgerRecordsExternalFailures);
            Run("ledger: registrations are refused once teardown started", LedgerRefusesLateRegistrations);

            Run("token: dispose removes the handler exactly once", TokenRemovesHandlerExactlyOnce);
            Run("MAJOR-7: a removal that throws leaves the token outstanding", TokenStaysOutstandingWhenRemovalThrows);
            Run("token: a removal that disposes the same token removes once", TokenReentrantDisposeRemovesOnce);
            Run("token: a re-entrant dispose inside a throwing removal", TokenReentrantDisposeInsideThrowingRemoval);

            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine($"ok: {name}");
            }
            catch (Exception e)
            {
                _failures++;
                Console.Error.WriteLine($"FAIL: {name}");
                Console.Error.WriteLine(Describe(e));
            }
        }

        /// <summary>
        ///     The harness deliberately throws exceptions that refuse to describe themselves, so
        ///     its own reporting has to survive them.
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

        // --- ModuleTransitionMachine -----------------------------------------------------

        private static void RequestNeverRunsCallbacks()
        {
            var script = new Transitions();
            var machine = script.Machine();

            for (var i = 0; i < 100; i++)
                machine.Request(i % 2 == 0);

            AssertEqual(0, script.Calls.Count, "Request must not run a transition callback.");
            AssertEqual(0, machine.EnableCalls, "Request must not enable.");
            AssertEqual(0, machine.DisableCalls, "Request must not disable.");
            AssertEqual(0, machine.RecoverCalls, "Request must not recover.");
            AssertState(ModuleState.Disabled, machine);
            AssertTrue(machine.Desired == false, "The last request must be the pending one.");
        }

        private static void OneTransitionPerTick()
        {
            var script = new Transitions();
            var machine = script.Machine();

            machine.Request(true);
            AssertTrue(machine.Pump(1), "The first pump should have run the enable.");
            AssertEqual(1, script.Calls.Count, "A pump must run at most one transition.");
            AssertState(ModuleState.Enabled, machine);

            // A pending request is what makes the next assertion able to fail: without it the
            // pump would return early no matter how the tick barrier behaved.
            machine.Request(false);
            AssertFalse(machine.Pump(1), "A second transition must not run on the same tick.");
            AssertEqual(0, machine.DisableCalls, "A second transition must not run on the same tick.");
            AssertEqual(1, script.Calls.Count, "A second transition must not run on the same tick.");
            AssertState(ModuleState.Enabled, machine);

            AssertTrue(machine.Pump(2), "The next tick should run the disable.");
            AssertEqual(1, machine.DisableCalls, "The next tick should run the disable.");
            AssertState(ModuleState.Disabled, machine);
            AssertSequence(script.Calls, "enable", "disable");
        }

        private static void RepeatedPumpsDoNotSpendDeferrals()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Deferred, TransitionOutcome.Deferred);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            AssertEqual(1, machine.Deferrals, "The first attempt deferred once.");
            AssertEqual(1, machine.EnableCalls, "The first attempt ran once.");

            AssertFalse(machine.Pump(1), "A repeated pump on one tick must do nothing.");
            AssertEqual(1, machine.Deferrals, "A repeated pump must not spend a deferral.");
            AssertEqual(1, machine.EnableCalls, "A repeated pump must not retry the enable.");

            machine.Pump(2);
            AssertEqual(2, machine.Deferrals, "The next tick retries and defers again.");
            AssertEqual(2, machine.EnableCalls, "The next tick retries the enable.");
        }

        private static void NoEnableOnTheTeardownTick()
        {
            var script = new Transitions();
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(5);
            machine.Request(false);
            machine.Pump(7);

            AssertState(ModuleState.Disabled, machine);
            AssertEqual(7, (int)machine.LastTeardownTick, "The teardown tick should be recorded.");

            machine.Request(true);
            AssertFalse(machine.Pump(7), "No enable may start on the teardown tick.");
            AssertEqual(1, machine.EnableCalls, "No enable may start on the teardown tick.");
            AssertState(ModuleState.Disabled, machine);

            AssertTrue(machine.Pump(8), "The next tick should enable.");
            AssertEqual(2, machine.EnableCalls, "The next tick should enable.");
            AssertState(ModuleState.Enabled, machine);
        }

        private static void RecoveryRecordsTheTeardownTick()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed, TransitionOutcome.Succeeded)
                .Recovers(TransitionOutcome.Succeeded);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(true);
            machine.Pump(2);

            AssertState(ModuleState.Disabled, machine);
            AssertEqual(2, (int)machine.LastTeardownTick,
                "A recovery finishes the teardown, so it is subject to the same barrier.");
        }

        private static void DeferredEnableStaysDisabled()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Deferred, TransitionOutcome.Deferred,
                    TransitionOutcome.Succeeded);
            var machine = script.Machine();

            machine.Request(true);

            machine.Pump(1);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(1, machine.Deferrals, "A deferred enable should count a deferral.");

            machine.Pump(2);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(2, machine.Deferrals, "A deferred enable should count a deferral.");

            machine.Pump(3);
            AssertState(ModuleState.Enabled, machine);
            AssertEqual(0, machine.Deferrals, "A successful enable should clear the deferrals.");
            AssertEqual(3, machine.EnableCalls, "Every deferral should retry the enable.");
        }

        private static void DeferralsPastTheLimitFault()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Deferred, TransitionOutcome.Deferred,
                    TransitionOutcome.Deferred);
            var machine = script.Machine(2);

            machine.Request(true);
            machine.Pump(1);
            machine.Pump(2);
            AssertState(ModuleState.Disabled, machine);

            machine.Pump(3);
            AssertState(ModuleState.Faulted, machine);
            AssertEqual(3, machine.EnableCalls, "The limit should be reached by retrying.");
            AssertTrue(machine.Desired == null, "A fault must drop the request it was serving.");

            AssertFalse(machine.Pump(4), "A fault must not retry itself.");
            AssertEqual(0, machine.RecoverCalls, "A fault must not recover on its own.");
        }

        private static void CancellingClearsDeferrals()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Deferred, TransitionOutcome.Deferred,
                    TransitionOutcome.Succeeded);
            var machine = script.Machine(1);

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(1, machine.Deferrals, "The first attempt deferred once.");

            // Cancel the pending enable before it ever completed.
            machine.Request(false);
            machine.Pump(2);
            AssertTrue(machine.Desired == null, "The cancellation should be settled.");
            AssertEqual(0, machine.Deferrals,
                "A cancelled transition must not charge its deferrals to the next attempt.");

            machine.Request(true);
            machine.Pump(3);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(1, machine.Deferrals, "The new attempt starts its own deferral count.");

            machine.Pump(4);
            AssertState(ModuleState.Enabled, machine);
        }

        private static void FailureAfterDeferralsFaults()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Deferred, TransitionOutcome.Deferred,
                    TransitionOutcome.Deferred, TransitionOutcome.Deferred,
                    TransitionOutcome.Failed);
            var machine = script.Machine();

            machine.Request(true);
            for (var tick = 1; tick <= 4; tick++)
                machine.Pump(tick);

            AssertState(ModuleState.Disabled, machine);
            AssertEqual(4, machine.Deferrals, "Four deferrals are still within the limit.");

            machine.Pump(5);
            AssertState(ModuleState.Faulted, machine);
            AssertTrue(machine.State != ModuleState.Disabled,
                "A failure must not be reported as a clean disabled state.");
            AssertTrue(!string.IsNullOrEmpty(machine.LastFault), "The fault should be described.");
        }

        private static void FailedEnableFaults()
        {
            var script = new Transitions().Enables(TransitionOutcome.Failed);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);

            AssertState(ModuleState.Faulted, machine);
            AssertTrue(machine.Desired == null, "A fault must drop the request it was serving.");
        }

        private static void ThrowingEnableFaults()
        {
            var script = new Transitions { EnableThrows = new InvalidOperationException("boom") };
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);

            AssertState(ModuleState.Faulted, machine);
            AssertTrue(machine.LastFault != null && machine.LastFault.Contains("boom"),
                "The thrown reason should be kept, not swallowed.");
        }

        private static void HostileFailureIsContained()
        {
            var script = new Transitions { EnableThrows = new HostileException() };
            var machine = script.Machine();

            machine.Request(true);

            // The pump must contain this. If describing the failure escapes, it reaches Run.
            machine.Pump(1);

            AssertState(ModuleState.Faulted, machine);
            AssertTrue(!string.IsNullOrEmpty(machine.LastFault),
                "The fault should still be described, even by a placeholder.");
        }

        private static void FailedDisableFaults()
        {
            var script = new Transitions().Disables(TransitionOutcome.Failed);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            machine.Request(false);
            machine.Pump(2);

            AssertState(ModuleState.Faulted, machine);
            AssertTrue(machine.LastTeardownTick == long.MinValue,
                "A failed teardown must not record a teardown tick.");
        }

        private static void FaultedModuleRecoversFirst()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed, TransitionOutcome.Succeeded)
                .Recovers(TransitionOutcome.Succeeded);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(true);
            machine.Pump(2);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(1, machine.RecoverCalls, "Recovery should run before a retry.");

            // Pumping the same tick again is what makes the next assertion able to fail.
            AssertFalse(machine.Pump(2), "Recovery and the retry must not share a tick.");
            AssertEqual(1, machine.EnableCalls, "Recovery and the retry must not share a tick.");

            machine.Pump(3);
            AssertState(ModuleState.Enabled, machine);
            AssertEqual(2, machine.EnableCalls, "The enable should run on the next tick.");
            AssertSequence(script.Calls, "enable", "recover", "enable");
        }

        private static void FailedRecoveryStaysFaulted()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed)
                .Recovers(TransitionOutcome.Failed);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(true);
            machine.Pump(2);
            AssertState(ModuleState.Faulted, machine);
            AssertEqual(1, machine.RecoverCalls, "Recovery should have been attempted once.");

            AssertFalse(machine.Pump(3), "A failed recovery must not retry itself.");
            AssertEqual(1, machine.RecoverCalls, "A failed recovery must not retry itself.");
            AssertEqual(1, machine.EnableCalls, "A faulted module must not enable.");
        }

        private static void DeferredRecoveryRetries()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed, TransitionOutcome.Succeeded)
                .Recovers(TransitionOutcome.Deferred, TransitionOutcome.Succeeded);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(true);
            machine.Pump(2);
            AssertState(ModuleState.Faulted, machine);
            AssertEqual(1, machine.Deferrals, "A deferred recovery counts a deferral.");
            AssertTrue(machine.Desired == true,
                "A deferred recovery must keep the request, the same as any other deferral.");

            machine.Pump(3);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(2, machine.RecoverCalls, "The next tick retries the recovery.");
            AssertEqual(0, machine.Deferrals, "A successful recovery clears the deferrals.");

            machine.Pump(4);
            AssertState(ModuleState.Enabled, machine);
            AssertEqual(2, machine.EnableCalls, "The retry should follow the recovery.");
        }

        private static void DeferredRecoveryPastTheLimitFaults()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed)
                .Recovers(TransitionOutcome.Deferred, TransitionOutcome.Deferred);
            var machine = script.Machine(1);

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(true);
            machine.Pump(2);
            AssertState(ModuleState.Faulted, machine);
            AssertTrue(machine.Desired == true, "The first deferral keeps the request.");

            machine.Pump(3);
            AssertState(ModuleState.Faulted, machine);
            AssertEqual(2, machine.RecoverCalls, "Both recoveries should have been attempted.");
            AssertTrue(machine.Desired == null, "Passing the limit drops the request.");

            AssertFalse(machine.Pump(4), "A faulted recovery must not retry itself.");
            AssertEqual(2, machine.RecoverCalls, "A faulted recovery must not retry itself.");
        }

        private static void NoReentrantRecovery()
        {
            // Recovery is the one transition that runs while its own state is still current, so
            // it is the one a nested pump could re-enter. Enable and disable are already
            // unreachable that way, because they run in the transient Enabling / Disabling
            // states.
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed)
                .Recovers(TransitionOutcome.Succeeded, TransitionOutcome.Succeeded);
            var machine = script.Machine();
            var nested = new List<bool>();
            var depth = 0;

            script.DuringRecover = () =>
            {
                // Bounded on purpose: an unguarded re-entry would otherwise recurse until the
                // stack gave out, which no assertion could survive to report.
                if (depth++ > 0) return;
                nested.Add(machine.Pump(3));
            };

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(true);
            machine.Pump(2);

            AssertEqual(1, nested.Count, "The nested pump should have been attempted once.");
            AssertFalse(nested[0], "A pump from inside a transition must do nothing.");
            AssertEqual(1, machine.RecoverCalls, "A nested pump must not re-enter recovery.");
        }

        private static void RecoveryWithoutEnableEndsDisabled()
        {
            var script = new Transitions()
                .Disables(TransitionOutcome.Failed)
                .Recovers(TransitionOutcome.Succeeded);
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            machine.Request(false);
            machine.Pump(2);
            AssertState(ModuleState.Faulted, machine);

            machine.Request(false);
            machine.Pump(3);
            AssertState(ModuleState.Disabled, machine);
            AssertTrue(machine.Desired == null, "Nothing should be left to do.");

            AssertFalse(machine.Pump(4), "A satisfied request must not run anything.");
            AssertEqual(1, machine.EnableCalls, "Recovery must not enable the module.");
        }

        private static void RequestDuringEnableSurvives()
        {
            var script = new Transitions();
            var machine = script.Machine();

            // A setting change can arrive from the configuration UI or a config reload while a
            // transition is mid-flight.
            script.DuringEnable = () =>
            {
                script.DuringEnable = null;
                machine.Request(false);
            };

            machine.Request(true);
            machine.Pump(1);

            AssertState(ModuleState.Enabled, machine);
            AssertTrue(machine.Desired == false,
                "A request made while the enable ran is newer intent and must survive it.");

            machine.Pump(2);
            AssertState(ModuleState.Disabled, machine);
            AssertEqual(1, machine.DisableCalls, "The newer request should be served.");
        }

        private static void RequestDuringDisableSurvives()
        {
            var script = new Transitions();
            var machine = script.Machine();

            machine.Request(true);
            machine.Pump(1);
            AssertState(ModuleState.Enabled, machine);

            script.DuringDisable = () =>
            {
                script.DuringDisable = null;
                machine.Request(true);
            };

            machine.Request(false);
            machine.Pump(2);

            AssertState(ModuleState.Disabled, machine);
            AssertTrue(machine.Desired == true,
                "A request made while the disable ran is newer intent and must survive it.");

            AssertFalse(machine.Pump(2), "The teardown tick still bars the re-enable.");
            machine.Pump(3);
            AssertState(ModuleState.Enabled, machine);
            AssertEqual(2, machine.EnableCalls, "The newer request should be served.");
        }

        private static void RequestDuringFailureSurvives()
        {
            var script = new Transitions()
                .Enables(TransitionOutcome.Failed)
                .Recovers(TransitionOutcome.Succeeded);
            var machine = script.Machine();

            script.DuringEnable = () =>
            {
                script.DuringEnable = null;
                machine.Request(false);
            };

            machine.Request(true);
            machine.Pump(1);

            AssertState(ModuleState.Faulted, machine);
            AssertTrue(machine.Desired == false,
                "A fault drops the request it was serving, not one that arrived after it.");

            machine.Pump(2);
            AssertEqual(1, machine.RecoverCalls, "The newer request should drive the recovery.");
            AssertState(ModuleState.Disabled, machine);
        }

        private static void TransitionLayerHasNoGameDependencies()
        {
            var assembly = typeof(ModuleTransitionMachine).Assembly;
            AssertTrue(typeof(TeardownLedger).Assembly == assembly,
                "The ledger should come from the same compiled source.");
            AssertTrue(typeof(SubscriptionToken<Action>).Assembly == assembly,
                "The token should come from the same compiled source.");

            string[] forbidden = { "Harmony", "Unity", "BepInEx", "assembly_valheim", "ModUtils" };
            foreach (var reference in assembly.GetReferencedAssemblies())
            foreach (var name in forbidden)
                if (reference.Name != null &&
                    reference.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception(
                        $"The transition layer must not reference {reference.Name}.");

            foreach (var type in new[]
                     {
                         typeof(ModuleTransitionMachine), typeof(TeardownLedger),
                         typeof(SubscriptionToken<Action>)
                     })
            {
                foreach (var constructor in type.GetConstructors())
                foreach (var parameter in constructor.GetParameters())
                    AssertBaseLibraryType(parameter.ParameterType);

                foreach (var property in type.GetProperties())
                    AssertBaseLibraryType(property.PropertyType);

                foreach (var method in type.GetMethods())
                {
                    AssertBaseLibraryType(method.ReturnType);
                    foreach (var parameter in method.GetParameters())
                        AssertBaseLibraryType(parameter.ParameterType);
                }
            }
        }

        // --- TeardownLedger --------------------------------------------------------------

        private static void LedgerUndoesInReverseOrder()
        {
            var order = new List<string>();
            var ledger = new TeardownLedger();
            ledger.Add("first", () => order.Add("first"));
            ledger.Add("second", () => order.Add("second"));
            ledger.Add("third", () => order.Add("third"));

            AssertFalse(ledger.IsDisposed, "Nothing is undone before a teardown.");
            ledger.Dispose();

            AssertSequence(order, "third", "second", "first");
            AssertTrue(ledger.IsDisposed, "Every registration should be undone.");
            AssertEqual(0, ledger.Failures.Count, "A clean teardown reports no failure.");
        }

        private static void LedgerFailureDoesNotStopTheOthers()
        {
            var order = new List<string>();
            var ledger = new TeardownLedger();
            ledger.Add("first", () => order.Add("first"));
            ledger.Add("second", () =>
            {
                order.Add("second");
                throw new InvalidOperationException("second failed");
            });
            ledger.Add("third", () => order.Add("third"));

            ledger.Dispose();

            AssertSequence(order, "third", "second", "first");
            AssertEqual(1, ledger.Failures.Count, "The failure should surface.");
            AssertTrue(ledger.Failures[0].Contains("second"),
                "The failure should name the registration.");
            AssertFalse(ledger.IsDisposed, "An outstanding registration is not disposed.");
        }

        private static void LedgerSecondTeardownSkipsSucceeded()
        {
            var order = new List<string>();
            var ledger = new TeardownLedger();
            ledger.Add("first", () => order.Add("first"));
            ledger.Add("second", () =>
            {
                order.Add("second");
                throw new InvalidOperationException("second failed");
            });
            ledger.Add("third", () => order.Add("third"));

            ledger.Dispose();
            ledger.Dispose();

            AssertSequence(order, "third", "second", "first", "second");
            AssertEqual(1, ledger.Failures.Count, "The failure should still be reported.");
            AssertFalse(ledger.IsDisposed, "The outstanding registration is still outstanding.");
        }

        private static void LedgerRetryCanSucceed()
        {
            var order = new List<string>();
            var fail = true;
            var ledger = new TeardownLedger();
            ledger.Add("first", () => order.Add("first"));
            ledger.Add("second", () =>
            {
                order.Add("second");
                if (fail) throw new InvalidOperationException("second failed");
            });
            ledger.Add("third", () => order.Add("third"));

            ledger.Dispose();
            AssertFalse(ledger.IsDisposed, "The failing registration is still outstanding.");

            fail = false;
            ledger.Dispose();

            AssertSequence(order, "third", "second", "first", "second");
            AssertTrue(ledger.IsDisposed, "The retry should complete the teardown.");
            AssertEqual(0, ledger.Failures.Count, "A completed retry clears the failure.");
        }

        private static void LedgerReentrantTeardownUndoesOnce()
        {
            var order = new List<string>();
            var nested = false;
            var ledger = new TeardownLedger();

            ledger.Add("first", () => order.Add("first"));
            ledger.Add("second", () =>
            {
                order.Add("second");
                // Bounded on purpose: unguarded, this would recurse until the stack gave out.
                if (nested) return;
                nested = true;
                ledger.Dispose();
            });
            ledger.Add("third", () => order.Add("third"));

            ledger.Dispose();

            AssertSequence(order, "third", "second", "first");
            AssertTrue(ledger.IsDisposed, "The teardown should still complete.");
            AssertEqual(0, ledger.Failures.Count, "Nothing failed.");
        }

        private static void LedgerReentrantTeardownInsideFailure()
        {
            var order = new List<string>();
            var nested = false;
            var ledger = new TeardownLedger();

            ledger.Add("first", () => order.Add("first"));
            ledger.Add("second", () =>
            {
                order.Add("second");
                if (nested) return;
                nested = true;
                ledger.Dispose();
                throw new InvalidOperationException("second failed");
            });
            ledger.Add("third", () => order.Add("third"));

            ledger.Dispose();

            AssertSequence(order, "third", "second", "first");
            AssertFalse(ledger.IsDisposed && ledger.Failures.Count > 0,
                "A registration must not be reported as both undone and failed.");
            AssertEqual(1, ledger.Failures.Count, "The failure should surface.");
            AssertFalse(ledger.IsDisposed, "The failed registration is still outstanding.");
        }

        private static void LedgerContainsHostileFailure()
        {
            var ledger = new TeardownLedger();
            ledger.Add("only", () => throw new HostileException());

            // Isolation must survive a failure that cannot describe itself.
            ledger.Dispose();

            AssertEqual(1, ledger.Failures.Count, "The failure should still be recorded.");
            AssertFalse(ledger.IsDisposed, "The failed registration is still outstanding.");
        }

        private static void LedgerRecordsExternalFailures()
        {
            var ledger = new TeardownLedger();
            ledger.Add("only", () => { });
            ledger.RecordFailure("Disable threw");

            ledger.Dispose();

            AssertTrue(ledger.IsDisposed, "The registration itself succeeded.");
            AssertEqual(1, ledger.Failures.Count, "The recorded failure should surface.");
            AssertTrue(ledger.Failures[0].Contains("Disable threw"),
                "The recorded description should be kept.");

            ledger.ClearRecordedFailures();
            AssertEqual(0, ledger.Failures.Count, "Recovery can clear recorded failures.");
        }

        private static void LedgerRefusesLateRegistrations()
        {
            var ledger = new TeardownLedger();
            ledger.Add("only", () => { });
            ledger.Dispose();

            var refused = false;
            try
            {
                ledger.Add("late", () => { });
            }
            catch (ObjectDisposedException)
            {
                refused = true;
            }

            AssertTrue(refused, "A registration after teardown would never be undone.");
        }

        // --- SubscriptionToken -----------------------------------------------------------

        private static void TokenRemovesHandlerExactlyOnce()
        {
            var removals = 0;
            Action handler = () => { };
            Action observed = null;

            var token = new SubscriptionToken<Action>(h =>
            {
                removals++;
                observed = h;
            }, handler);

            AssertFalse(token.IsDisposed, "A fresh token is outstanding.");

            token.Dispose();
            AssertEqual(1, removals, "The handler should be removed once.");
            AssertTrue(ReferenceEquals(handler, observed),
                "The removal should be handed the subscribed handler.");
            AssertTrue(token.IsDisposed, "A completed removal marks the token spent.");

            token.Dispose();
            AssertEqual(1, removals, "A spent token must not remove twice.");
        }

        private static void TokenStaysOutstandingWhenRemovalThrows()
        {
            var removals = 0;
            var fail = true;
            Action handler = () => { };

            var token = new SubscriptionToken<Action>(h =>
            {
                removals++;
                if (fail) throw new InvalidOperationException("remove failed");
            }, handler);

            var threw = false;
            try
            {
                token.Dispose();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            AssertTrue(threw, "A failing removal should surface to the ledger.");
            AssertEqual(1, removals, "The removal should have been attempted.");
            AssertFalse(token.IsDisposed,
                "A removal that threw must leave the token outstanding for a retry.");

            fail = false;
            token.Dispose();
            AssertEqual(2, removals, "The retry should attempt the removal again.");
            AssertTrue(token.IsDisposed, "The successful retry marks the token spent.");

            token.Dispose();
            AssertEqual(2, removals, "A spent token must not remove again.");
        }

        private static void TokenReentrantDisposeRemovesOnce()
        {
            var removals = 0;
            var nested = false;
            Action handler = () => { };
            SubscriptionToken<Action> token = null;

            token = new SubscriptionToken<Action>(h =>
            {
                removals++;
                if (nested) return;
                nested = true;
                token.Dispose();
            }, handler);

            token.Dispose();

            AssertEqual(1, removals,
                "A removal that disposes the same token must not remove a second time.");
            AssertTrue(token.IsDisposed, "The removal completed.");
        }

        private static void TokenReentrantDisposeInsideThrowingRemoval()
        {
            var removals = 0;
            var nested = false;
            var fail = true;
            Action handler = () => { };
            SubscriptionToken<Action> token = null;

            token = new SubscriptionToken<Action>(h =>
            {
                removals++;
                if (!nested)
                {
                    nested = true;
                    token.Dispose();
                }

                if (fail) throw new InvalidOperationException("remove failed");
            }, handler);

            var threw = false;
            try
            {
                token.Dispose();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            AssertTrue(threw, "The failing removal should surface.");
            AssertEqual(1, removals, "The nested dispose must not remove a second time.");
            AssertFalse(token.IsDisposed, "A removal that threw stays retryable.");

            fail = false;
            token.Dispose();
            AssertEqual(2, removals, "The retry should attempt the removal again.");
            AssertTrue(token.IsDisposed, "The successful retry marks the token spent.");
        }

        // --- helpers ---------------------------------------------------------------------

        private static void AssertBaseLibraryType(Type type)
        {
            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments())
                    AssertBaseLibraryType(argument);

            var name = type.Namespace;
            if (string.IsNullOrEmpty(name)) return;
            if (name == "Automatics" || name == "System" || name.StartsWith("System.",
                    StringComparison.Ordinal))
                return;

            throw new Exception(
                $"The transition layer exposes {type.FullName}, which is outside the base " +
                "class library.");
        }

        private static void AssertState(ModuleState expected, ModuleTransitionMachine machine)
        {
            if (machine.State != expected)
                throw new Exception($"Expected state {expected}, got {machine.State}.");
        }

        private static void AssertSequence(IReadOnlyList<string> actual, params string[] expected)
        {
            if (actual.Count != expected.Length)
                throw new Exception(
                    $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");

            for (var i = 0; i < expected.Length; i++)
                if (actual[i] != expected[i])
                    throw new Exception(
                        $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
        }

        private static void AssertEqual(int expected, int actual, string message)
        {
            if (expected != actual)
                throw new Exception($"{message} Expected {expected}, got {actual}.");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertFalse(bool condition, string message)
        {
            if (condition) throw new Exception(message);
        }

        /// <summary>An exception that refuses to describe itself.</summary>
        private sealed class HostileException : Exception
        {
            public override string Message =>
                throw new InvalidOperationException("this failure has no message");

            public override string ToString()
            {
                throw new InvalidOperationException("this failure has no text");
            }
        }

        private sealed class Transitions
        {
            private readonly Queue<TransitionOutcome> _disable = new Queue<TransitionOutcome>();
            private readonly Queue<TransitionOutcome> _enable = new Queue<TransitionOutcome>();
            private readonly Queue<TransitionOutcome> _recover = new Queue<TransitionOutcome>();

            public readonly List<string> Calls = new List<string>();

            public Action DuringDisable;
            public Action DuringEnable;
            public Action DuringRecover;

            public Exception EnableThrows;

            public Transitions Enables(params TransitionOutcome[] outcomes)
            {
                return Enqueue(_enable, outcomes);
            }

            public Transitions Disables(params TransitionOutcome[] outcomes)
            {
                return Enqueue(_disable, outcomes);
            }

            public Transitions Recovers(params TransitionOutcome[] outcomes)
            {
                return Enqueue(_recover, outcomes);
            }

            public ModuleTransitionMachine Machine(int maxDeferrals = 4)
            {
                return new ModuleTransitionMachine("test-module", Enable, Disable, Recover,
                    maxDeferrals);
            }

            private Transitions Enqueue(Queue<TransitionOutcome> queue,
                TransitionOutcome[] outcomes)
            {
                foreach (var outcome in outcomes)
                    queue.Enqueue(outcome);
                return this;
            }

            private TransitionOutcome Enable()
            {
                Calls.Add("enable");
                DuringEnable?.Invoke();
                if (EnableThrows != null)
                {
                    var error = EnableThrows;
                    EnableThrows = null;
                    throw error;
                }

                return Take(_enable);
            }

            private TransitionOutcome Disable()
            {
                Calls.Add("disable");
                DuringDisable?.Invoke();
                return Take(_disable);
            }

            private TransitionOutcome Recover()
            {
                Calls.Add("recover");
                DuringRecover?.Invoke();
                return Take(_recover);
            }

            private static TransitionOutcome Take(Queue<TransitionOutcome> queue)
            {
                return queue.Count == 0 ? TransitionOutcome.Succeeded : queue.Dequeue();
            }
        }
    }
}
