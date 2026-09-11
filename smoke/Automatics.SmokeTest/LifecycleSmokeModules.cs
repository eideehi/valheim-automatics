using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;
using StorageModule = Automatics.AutomaticStorage.Module;

namespace Automatics.SmokeTest
{
    // automatics_smoke_lifecycle <module>: the runtime lifecycle check for a migrated module, driven
    // by a descriptor instead of code of its own. It takes the module through three off -> on cycles
    // by writing its `module` setting, the way a user does, and checks after each transition:
    //   - at the disable request, in the same frame, the module is still applied but no longer
    //     running, which is the predicate its entry points open with;
    //   - while off, no patch is left under its Harmony id and every probe - a handler count, a
    //     component count or a counter - reads what the descriptor says it should while off;
    //   - while on, the module is applied and running, the patches are exactly the declared set,
    //     and every probe is back at the value it had before the run (or, for a component probe
    //     with a target, at the number of objects it should be on right now).
    // A module adds what only it can show as extra checks, at the request, while off or after the
    // enable. Each extra reports PASS, FAIL or SKIP on its own.
    //
    // It spawns nothing and writes only the setting, through SmokeSession, under the lifecycle
    // command's rules. It only ever turns a module off and back on: when the module is off in the
    // user's settings, every check SKIPs and nothing is written.
    internal static partial class LifecycleSmokeCommand
    {
        private const int ModuleCycles = 3;

        // The migrated modules this runner takes, by the argument that names them. A descriptor is
        // built only when its argument is run, so a module whose types cannot be read stops its own
        // run and nothing else.
        private static readonly Dictionary<string, Func<ModuleDescriptor>> Descriptors =
            new Dictionary<string, Func<ModuleDescriptor>>(StringComparer.Ordinal)
            {
                ["storage"] = DescribeStorage
            };

        // Automatic Storage moves items only when its key is pressed, and the check never presses
        // it: the module's one subscription is what comes and goes. The other two player hooks are
        // counted to show that it touches nothing else.
        private static ModuleDescriptor DescribeStorage()
        {
            return new ModuleDescriptor
            {
                Argument = "storage",
                Module = StorageModule.Instance,
                Active = () => StorageModule.Active,
                Running = () => StorageModule.Running,
                PatchTypes = Type.EmptyTypes,
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerUpdate", () => Hooks.OnPlayerUpdate, 1),
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 0),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 0)
                },
                Precondition = null,
                Extras = new ExtraCheck[0]
            };
        }

        // Every argument the command takes besides none at all, in the order the usage lists them.
        private static IEnumerable<string> ModuleArguments()
        {
            return new[] { MappingArgument }.Concat(Descriptors.Keys.OrderBy(x => x, StringComparer.Ordinal));
        }

        // Builds the descriptor before the run takes the session, so a descriptor that cannot be
        // built never holds the lock.
        private static bool TryDescribe(string argument, out ModuleDescriptor descriptor, out string problem)
        {
            descriptor = null;
            problem = "";
            if (!Descriptors.TryGetValue(argument, out var describe))
            {
                problem = $"'{argument}' names no module";
                return false;
            }

            try
            {
                descriptor = describe();
            }
            catch (Exception e)
            {
                problem = $"the {argument} module could not be described: {e.GetType().Name}: {e.Message}";
                return false;
            }

            if (descriptor == null || descriptor.Module == null || descriptor.Active == null ||
                descriptor.Running == null || descriptor.Probes == null)
            {
                problem = $"the {argument} descriptor leaves out the module, its flags or its probes";
                descriptor = null;
                return false;
            }

            return true;
        }

        private static IEnumerator RunModule(Terminal context, int runId, string runner,
            ModuleDescriptor descriptor)
        {
            var run = new ModuleRun(runId, runner, descriptor);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {runner} started: {descriptor.Module.ConfigSection}/module is turned off and on {ModuleCycles} times through its setting, nothing is spawned, and the setting is restored");

            var steps = Contain(run.Execute(), (_, message) => run.Stopped(message), "");
            while (steps.MoveNext()) yield return steps.Current;

            var finish = Contain(run.Finish(), (_, message) => run.Stopped(message), "");
            while (finish.MoveNext()) yield return finish.Current;

            Report(context, run.Results);
        }

        private static int CountHandlers(Delegate handlers)
        {
            return handlers == null ? 0 : handlers.GetInvocationList().Length;
        }

        // An event is read through the field behind it; -1 when there is no such field.
        private static int CountEventHandlers(Type owner, string eventName)
        {
            var field = AccessTools.Field(owner, eventName);
            return field == null ? -1 : CountHandlers(field.GetValue(null) as Delegate);
        }

        private static int CountObjects<T>() where T : Object
        {
            return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        }

        // -- The descriptor ------------------------------------------------------------------

        private sealed class ModuleDescriptor
        {
            // The module's name in lower case, as the command takes it; the checks are named after it.
            public string Argument;

            // The registered module. Its name is the registry's, and its toggle is read when the
            // run starts.
            public IAutomaticsModule Module;

            // The module's static applied state and its Running predicate.
            public Func<bool> Active;
            public Func<bool> Running;

            // The patch classes the module applies; empty when it applies none.
            public Type[] PatchTypes;

            public Probe[] Probes;

            // Null when the check can run, or why it cannot. Asked before anything is written.
            public Func<string> Precondition;

            public ExtraCheck[] Extras;
        }

        // What a probe should read while the module is off: nothing at all, its value before the
        // run less its Delta, or anything.
        private enum OffExpectation
        {
            Zero,
            BelowBaseline,
            Unchecked
        }

        private sealed class Probe
        {
            public string Name;

            // -1 when the value cannot be read, which fails the check that reads it.
            public Func<int> Read;

            public OffExpectation Off;

            // How far below its value before the run a BelowBaseline probe reads while off; 0 for
            // one the module must not touch.
            public int Delta;

            // A probe that reads 0 before the run shows nothing either way, so its checks are
            // inconclusive rather than passed.
            public bool NeedsNonZeroBaseline;

            // When set, what the probe has to read once the module is back on, in place of its
            // value before the run: the number of objects it should be on right now. Objects come
            // and go while the check runs, so a count taken before the run can be out of date. A
            // target of 0 shows nothing, and makes the check inconclusive.
            public Func<int> Target;

            // The handler count of a delegate property, such as Hooks.OnPlayerUpdate.
            public static Probe Handlers(string name, Func<Delegate> read, int delta)
            {
                return new Probe
                {
                    Name = name, Read = () => CountHandlers(read()), Off = OffExpectation.BelowBaseline,
                    Delta = delta
                };
            }

            // The handler count of an event, read through the field behind it.
            public static Probe Event(Type owner, string eventName, int delta)
            {
                return new Probe
                {
                    Name = $"{owner.Name}.{eventName}", Read = () => CountEventHandlers(owner, eventName),
                    Off = OffExpectation.BelowBaseline, Delta = delta
                };
            }

            // The number of components of one type in the scene, which is 0 while the module is off.
            public static Probe Components<T>() where T : Object
            {
                return new Probe
                {
                    Name = typeof(T).Name, Read = CountObjects<T>, Off = OffExpectation.Zero,
                    NeedsNonZeroBaseline = true
                };
            }

            // The same, compared once the module is back on with the objects it should be on then.
            public static Probe Components<T>(Func<int> eligible) where T : Object
            {
                return new Probe
                {
                    Name = typeof(T).Name, Read = CountObjects<T>, Off = OffExpectation.Zero,
                    Target = eligible
                };
            }

            // Any other number, such as a private static read by reflection that returns -1 when the
            // field is not there.
            public static Probe Counter(string name, Func<int> read, OffExpectation off, int delta)
            {
                return new Probe { Name = name, Read = read, Off = off, Delta = delta };
            }
        }

        private enum Phase
        {
            // Straight after the disable is written, before any frame passes. A check here may not
            // wait for a frame.
            AtRequest,

            // Once the registry reports Disabled and the probes were compared.
            WhileOff,

            // Once the module is back on and the probes were compared.
            AfterOn
        }

        // A check only one module can make. It records its own result for the cycle it is given -
        // PASS, FAIL or SKIP - under recorder.Check(Name).
        private sealed class ExtraCheck
        {
            public ExtraCheck(string name, Phase when, Func<CycleRecorder, int, IEnumerator> run)
            {
                Name = name;
                When = when;
                Run = run;
            }

            public string Name { get; }
            public Phase When { get; }
            public Func<CycleRecorder, int, IEnumerator> Run { get; }
        }

        // -- Recording -----------------------------------------------------------------------

        // The results of a cycled run. A check made once is settled once; a check made in every
        // cycle passes only when every cycle passed, and the first cycle that does not pass decides
        // it. A check the run never reached SKIPs.
        private sealed class CycleRecorder
        {
            private readonly string _prefix;
            private readonly int _cycles;
            private readonly List<string> _order;
            private readonly HashSet<string> _perCycle;

            private readonly Dictionary<string, CheckResult> _settled =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly Dictionary<string, int> _passes = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _passNotes = new Dictionary<string, string>(StringComparer.Ordinal);
            private string _notReached = "not reached: an earlier step stopped the run";

            public CycleRecorder(string prefix, int cycles, IEnumerable<string> order, IEnumerable<string> perCycle)
            {
                _prefix = prefix;
                _cycles = cycles;
                _order = order.ToList();
                _perCycle = new HashSet<string>(perCycle, StringComparer.Ordinal);
            }

            public IReadOnlyList<CheckResult> Results
            {
                get
                {
                    var results = new List<CheckResult>();
                    foreach (var name in _order)
                        if (_settled.TryGetValue(name, out var result))
                        {
                            results.Add(result);
                        }
                        else if (_perCycle.Contains(name) && Passes(name) == _cycles)
                        {
                            _passNotes.TryGetValue(name, out var note);
                            results.Add(new CheckResult(CheckStatus.Pass, name,
                                string.IsNullOrEmpty(note)
                                    ? $"all {_cycles} cycles"
                                    : $"all {_cycles} cycles; {note}"));
                        }
                        else
                        {
                            results.Add(new CheckResult(CheckStatus.Skip, name, _notReached));
                        }

                    return results;
                }
            }

            // The full name of a check: the module's argument, an underscore and the suffix.
            public string Check(string suffix)
            {
                return _prefix + "_" + suffix;
            }

            public void Settle(CheckStatus status, string name, string message)
            {
                if (!_settled.ContainsKey(name)) _settled[name] = new CheckResult(status, name, message);
            }

            public void Cycle(string name, int cycle, bool passed, string pass, string failure)
            {
                if (_settled.ContainsKey(name)) return;
                if (!passed)
                {
                    _settled[name] = new CheckResult(CheckStatus.Fail, name, $"cycle {cycle}: {failure}");
                    return;
                }

                _passes[name] = Passes(name) + 1;
                _passNotes[name] = pass;
            }

            public void CycleSkip(string name, int cycle, string reason)
            {
                Settle(CheckStatus.Skip, name, $"cycle {cycle}: {reason}");
            }

            public int Passes(string name)
            {
                return _passes.TryGetValue(name, out var passes) ? passes : 0;
            }

            public bool IsSettled(string name)
            {
                return _settled.ContainsKey(name);
            }

            public void SkipAll(string message)
            {
                foreach (var name in _order) Settle(CheckStatus.Skip, name, message);
            }

            // The step that was running fails; the ones after it were not reached.
            public void Stopped(string message, int cycle)
            {
                _notReached = "not reached: the run stopped on " + message;
                foreach (var name in _order)
                {
                    if (_settled.ContainsKey(name)) continue;
                    if (_perCycle.Contains(name) && Passes(name) >= cycle) continue;

                    var prefix = _perCycle.Contains(name) ? $"cycle {cycle}: " : "";
                    Settle(CheckStatus.Fail, name, prefix + "the run stopped on " + message);
                    return;
                }
            }
        }

        // What one comparison of the probes found.
        private sealed class ProbeReading
        {
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> Inconclusive = new List<string>();
            public readonly List<string> Seen = new List<string>();
        }

        // -- The run -------------------------------------------------------------------------

        private sealed class ModuleRun
        {
            private readonly ModuleDescriptor _descriptor;
            private readonly CycleRecorder _record;
            private readonly StateWait _wait = new StateWait();
            private readonly ErrorObserver _errors;
            private readonly Func<string, bool> _observerHandBack;
            private readonly string _runner;

            private readonly string _settled;
            private readonly string _offStopped;
            private readonly string _offState;
            private readonly string _offPatches;
            private readonly string _offProbes;
            private readonly string _onState;
            private readonly string _onRunning;
            private readonly string _onPatches;
            private readonly string _onProbes;
            private readonly string _restored;
            private readonly string _noErrors;

            private ConfigEntry<AutomaticsModule> _toggle;
            private string _moduleName;
            private string _section;
            private string _harmonyId;
            private bool _toggleOwned;
            private bool _proceed;
            private int _cycle;
            private int[] _baseline;

            public ModuleRun(int runId, string runner, ModuleDescriptor descriptor)
            {
                RunId = runId;
                _runner = runner;
                _descriptor = descriptor;

                var prefix = descriptor.Argument;
                _settled = prefix + "_initial_state_settled";
                _offStopped = prefix + "_off_stopped_at_request";
                _offState = prefix + "_off_state_disabled";
                _offPatches = prefix + "_off_patches_removed";
                _offProbes = prefix + "_off_probes_as_expected";
                _onState = prefix + "_on_state_enabled";
                _onRunning = prefix + "_on_running";
                _onPatches = prefix + "_on_patches_applied";
                _onProbes = prefix + "_on_probes_at_baseline";
                _restored = prefix + "_restored_initial_state";
                _noErrors = prefix + "_no_errors_logged";

                // The order the checks are recorded in, which is also the order they are reported
                // in: the module's extra checks sit after the checks of the phase they run in.
                var order = new List<string> { _settled, _offStopped };
                order.AddRange(ExtraNames(Phase.AtRequest));
                order.AddRange(new[] { _offState, _offPatches, _offProbes });
                order.AddRange(ExtraNames(Phase.WhileOff));
                order.AddRange(new[] { _onState, _onRunning, _onPatches, _onProbes });
                order.AddRange(ExtraNames(Phase.AfterOn));
                order.AddRange(new[] { _restored, _noErrors });

                var once = new HashSet<string>(StringComparer.Ordinal) { _settled, _restored, _noErrors };
                _record = new CycleRecorder(prefix, ModuleCycles, order, order.Where(x => !once.Contains(x)));

                _errors = new ErrorObserver("AutomaticsObserverProbe-" + prefix, runner);

                // The observer goes back with the session too, however the run ends.
                _observerHandBack = _ =>
                {
                    _errors.Stop();
                    return true;
                };
            }

            private int RunId { get; }

            public IReadOnlyList<CheckResult> Results => _record.Results;

            public IEnumerator Execute()
            {
                if (ZNetScene.instance == null || !Player.m_localPlayer)
                {
                    _record.SkipAll("no world is loaded (ZNetScene or the local player is missing)");
                    yield break;
                }

                var module = _descriptor.Module;
                _moduleName = module.Name;
                _section = module.ConfigSection;
                if (!module.SupportsRuntimeToggle)
                {
                    _record.Settle(CheckStatus.Fail, _settled,
                        $"{_moduleName} does not support runtime toggling, so there is no lifecycle to check");
                    yield break;
                }

                _toggle = module.ModuleToggle;
                if (_toggle == null)
                {
                    _record.Settle(CheckStatus.Fail, _settled, $"the {_section} module toggle is not bound");
                    yield break;
                }

                _harmonyId = global::Automatics.Automatics.GetHarmonyId(_moduleName);

                // Asked before anything is written, so a module the check cannot use is left as it is.
                var precondition = _descriptor.Precondition?.Invoke();
                if (precondition != null)
                {
                    _record.SkipAll(precondition);
                    yield break;
                }

                // The check only ever turns the module off and back on. Turning it on for the user
                // would let it act in their world, so when it is off it stays off.
                if (_toggle.Value != AutomaticsModule.Enabled)
                {
                    _record.SkipAll(
                        $"{_section}/module is off in your settings, so the check changed nothing. It only ever turns the module off and back on; turn it on yourself if you want this check, then run it again.");
                    yield break;
                }

                // From here on any write to the setting that is not the check's own is the user
                // taking it back.
                if (!SmokeSession.BeginToggle(RunId, _toggle, out var refusal))
                {
                    _record.SkipAll("inconclusive: " + refusal);
                    yield break;
                }

                _toggleOwned = true;
                SmokeSession.TrackHandBack(_observerHandBack);
                _errors.Start();

                // The registry has to agree with the setting before anything is asked of it, or a
                // transition still pending from before the command would be read as this one's.
                var settle = Await(ModuleState.Enabled, true, true);
                while (settle.MoveNext()) yield return settle.Current;
                if (!RecordWait(_settled, 0, "nothing was changed")) yield break;

                if (_descriptor.Probes.Length == 0)
                {
                    _record.Settle(CheckStatus.Fail, _offProbes,
                        "the descriptor declares no probe, so nothing would show what the module took away and brought back");
                    _record.Settle(CheckStatus.Fail, _onProbes,
                        "the descriptor declares no probe, so nothing would show what the module took away and brought back");
                }

                // Taken once two frames in a row agree, for the reason given at ComponentSettleFrames.
                var baseline = ReadProbes();
                for (var frame = 1; frame < ComponentSettleFrames; frame++)
                {
                    yield return null;
                    var again = ReadProbes();
                    var agreed = again.SequenceEqual(baseline);
                    baseline = again;
                    if (agreed) break;
                }

                _baseline = baseline;

                for (_cycle = 1; _cycle <= ModuleCycles; _cycle++)
                {
                    var off = TurnOff(_cycle);
                    while (off.MoveNext()) yield return off.Current;
                    if (!_proceed) yield break;

                    var on = TurnOn(_cycle);
                    while (on.MoveNext()) yield return on.Current;
                    if (!_proceed) yield break;
                }
            }

            // Puts the setting back and waits for the registry to follow, then judges the log.
            public IEnumerator Finish()
            {
                if (_toggleOwned)
                {
                    var outcome = SmokeSession.RestoreToggle(RunId, out var detail);
                    switch (outcome)
                    {
                        case SmokeSession.ToggleOutcome.NothingWritten:
                            _record.Settle(CheckStatus.Skip, _restored,
                                "nothing was written, so there was nothing to put back");
                            break;
                        case SmokeSession.ToggleOutcome.LeftForUser:
                            _record.Settle(CheckStatus.Skip, _restored, "restoration skipped: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.NotThisRun:
                            _record.Settle(CheckStatus.Skip, _restored, "inconclusive: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.Failed:
                            _record.Settle(CheckStatus.Fail, _restored, detail);
                            break;
                        default:
                            // A fault is not final here: putting the value back is also what asks
                            // the registry to recover, so the wait gives it the full bound.
                            var wait = Await(ModuleState.Enabled, false, false, AutomaticsModule.Enabled);
                            while (wait.MoveNext()) yield return wait.Current;
                            RecordWait(_restored, 0, detail);
                            break;
                    }
                }

                if (_errors.Started) JudgeLog();
                SmokeSession.ReleaseHandBack(_observerHandBack);
            }

            // The step that was running fails; the ones after it were not reached.
            public void Stopped(string message)
            {
                _record.Stopped(message, Math.Min(Math.Max(_cycle, 1), ModuleCycles));
            }

            private IEnumerator TurnOff(int cycle)
            {
                _proceed = false;

                if (!Write(AutomaticsModule.Disabled, _offStopped, cycle)) yield break;

                // The write itself raises the stop request, frames before the pump takes anything
                // apart: the module is still applied but no longer running, and its entry points
                // open with that predicate. A coroutine runs after Update, where the pump runs, so
                // the wait below cannot see the frame before the pump; this is what shows the stop
                // takes hold at the request.
                var applied = _descriptor.Active();
                var running = _descriptor.Running();
                _record.Cycle(_offStopped, cycle, applied && !running,
                    "right after the disable request the module was still applied but no longer running",
                    $"right after the disable request the module was {(applied ? "applied" : "not applied")} and {(running ? "still running" : "not running")} (R1.5a)");
                RunExtrasAtRequest(cycle);

                var wait = Await(ModuleState.Disabled, true, true);
                while (wait.MoveNext()) yield return wait.Current;
                if (!RecordWait(_offState, cycle)) yield break;

                // Object.Destroy takes effect at the end of the frame the pump ran in.
                for (var i = 0; i < DestroySettleFrames; i++) yield return null;

                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    _record.CycleSkip(_offPatches, cycle,
                        "this Harmony build cannot remove patches at runtime; they stay applied by design (R3.2)");
                }
                else
                {
                    var left = PatchesUnder(_harmonyId, out var total);
                    _record.Cycle(_offPatches, cycle, total == 0, $"no patch is left under \"{_harmonyId}\"",
                        $"{total} patch(es) are still under \"{_harmonyId}\" after the disable: [{string.Join(", ", left.OrderBy(x => x).Take(5))}]");
                }

                var reading = CompareProbes(true);
                for (var frame = 1; frame < ComponentSettleFrames && reading.Failures.Count > 0; frame++)
                {
                    yield return null;
                    reading = CompareProbes(true);
                }

                RecordProbes(_offProbes, cycle, reading, "while the module is off");

                var extras = RunExtras(Phase.WhileOff, cycle);
                while (extras.MoveNext()) yield return extras.Current;

                _proceed = true;
            }

            private IEnumerator TurnOn(int cycle)
            {
                _proceed = false;

                if (!Write(AutomaticsModule.Enabled, _onState, cycle)) yield break;
                var wait = Await(ModuleState.Enabled, true, true);
                while (wait.MoveNext()) yield return wait.Current;
                if (!RecordWait(_onState, cycle)) yield break;

                var applied = _descriptor.Active();
                var running = _descriptor.Running();
                _record.Cycle(_onRunning, cycle, applied && running,
                    "once the registry reported Enabled the module was applied and running",
                    $"the registry reports Enabled, but the module is {(applied ? "applied" : "not applied")} and {(running ? "running" : "not running")}");

                CheckPatchSet(cycle);

                var reading = CompareProbes(false);
                for (var frame = 1; frame < ComponentSettleFrames && reading.Failures.Count > 0; frame++)
                {
                    yield return null;
                    reading = CompareProbes(false);
                }

                RecordProbes(_onProbes, cycle, reading, "once the module is back on");

                var extras = RunExtras(Phase.AfterOn, cycle);
                while (extras.MoveNext()) yield return extras.Current;

                _proceed = true;
            }

            // The patches under the module's Harmony id have to be exactly the ones its patch
            // classes declare, compared as (target, patch method) pairs and counted as patches, so
            // a pair applied twice does not hide. A module that declares none must have none.
            private void CheckPatchSet(int cycle)
            {
                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    _record.CycleSkip(_onPatches, cycle,
                        "this Harmony build cannot remove patches at runtime, so the patches were never taken off (R3.2)");
                    return;
                }

                var types = _descriptor.PatchTypes ?? Type.EmptyTypes;
                var expected = ExpectedPatches(types);
                var declared = string.Join(", ", types.Select(x => x.FullName));
                if (types.Length > 0 && expected.Count == 0)
                {
                    _record.Cycle(_onPatches, cycle, false, "",
                        $"no [HarmonyPatch] target could be read from {declared}");
                    return;
                }

                var actual = PatchesUnder(_harmonyId, out var total);
                var missing = expected.Where(x => !actual.Contains(x)).OrderBy(x => x).ToArray();
                var unexpected = actual.Where(x => !expected.Contains(x)).OrderBy(x => x).ToArray();
                _record.Cycle(_onPatches, cycle,
                    missing.Length == 0 && unexpected.Length == 0 && total == expected.Count,
                    types.Length == 0
                        ? $"no patch is under \"{_harmonyId}\", and the module declares none"
                        : $"{total} patches under \"{_harmonyId}\", exactly the pairs {declared} declare",
                    $"the patches under \"{_harmonyId}\" differ from what the module declares: {total} patch(es) where it declares {expected.Count}; missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", unexpected)}]");
            }

            private int[] ReadProbes()
            {
                return _descriptor.Probes.Select(ReadProbe).ToArray();
            }

            // A probe that throws is one that cannot be read.
            private static int ReadProbe(Probe probe)
            {
                try
                {
                    return probe.Read();
                }
                catch (Exception)
                {
                    return -1;
                }
            }

            // Every probe against what it should read now: while off, what its OffExpectation says;
            // once on, its target when it has one and its value before the run otherwise.
            private ProbeReading CompareProbes(bool whileOff)
            {
                var reading = new ProbeReading();
                for (var i = 0; i < _descriptor.Probes.Length; i++)
                {
                    var probe = _descriptor.Probes[i];
                    var before = _baseline[i];
                    var now = ReadProbe(probe);
                    if (now < 0 || before < 0)
                    {
                        reading.Failures.Add($"{probe.Name} could not be read");
                        continue;
                    }

                    int? target = null;
                    if (probe.Target != null)
                    {
                        var eligible = -1;
                        try
                        {
                            eligible = probe.Target();
                        }
                        catch (Exception)
                        {
                            // Left at -1, which is reported below.
                        }

                        if (eligible < 0)
                        {
                            reading.Failures.Add($"the objects {probe.Name} should be on could not be counted");
                            continue;
                        }

                        target = eligible;
                    }

                    var showsNothing = target.HasValue ? target.Value == 0 : probe.NeedsNonZeroBaseline && before == 0;

                    int expected;
                    string expectation;
                    if (!whileOff)
                    {
                        expected = target ?? before;
                        expectation = target.HasValue
                            ? $"the {expected} object(s) it should be on"
                            : $"{before}, its value before the run";
                    }
                    else if (probe.Off == OffExpectation.Unchecked)
                    {
                        reading.Seen.Add($"{probe.Name} {now} (not checked while off)");
                        continue;
                    }
                    else if (probe.Off == OffExpectation.Zero)
                    {
                        expected = 0;
                        expectation = "0";
                    }
                    else
                    {
                        expected = before - probe.Delta;
                        expectation = probe.Delta == 0
                            ? $"{before}, its value before the run"
                            : $"{expected}, {probe.Delta} below its value before the run";
                    }

                    if (now != expected)
                        reading.Failures.Add($"{probe.Name} reads {now} where it should read {expectation}");
                    else if (showsNothing)
                        reading.Inconclusive.Add(target.HasValue
                            ? $"{probe.Name} has no object to be on, so 0 could not be told from a miss"
                            : $"{probe.Name} read 0 before the run, so 0 could not be told from a miss");
                    else
                        reading.Seen.Add($"{probe.Name} {now}");
                }

                return reading;
            }

            // Any probe that does not read what it should fails the check; with none failing, any
            // probe that could show nothing makes it inconclusive.
            private void RecordProbes(string name, int cycle, ProbeReading reading, string when)
            {
                if (_descriptor.Probes.Length == 0) return;

                if (reading.Failures.Count > 0)
                    _record.Cycle(name, cycle, false, "", $"{when}: {string.Join("; ", reading.Failures)}");
                else if (reading.Inconclusive.Count > 0)
                    _record.CycleSkip(name, cycle, $"inconclusive {when}: {string.Join("; ", reading.Inconclusive)}");
                else
                    _record.Cycle(name, cycle, true, $"{when}: {string.Join(", ", reading.Seen)}", "");
            }

            private IEnumerable<ExtraCheck> ExtrasIn(Phase phase)
            {
                return (_descriptor.Extras ?? new ExtraCheck[0]).Where(x => x != null && x.When == phase);
            }

            private IEnumerable<string> ExtraNames(Phase phase)
            {
                return ExtrasIn(phase).Select(x => _descriptor.Argument + "_" + x.Name);
            }

            // At the request nothing may wait for a frame: the frame the request was made in is
            // what these checks are there to see.
            private void RunExtrasAtRequest(int cycle)
            {
                foreach (var extra in ExtrasIn(Phase.AtRequest))
                {
                    var name = _record.Check(extra.Name);
                    var passes = _record.Passes(name);
                    try
                    {
                        var steps = extra.Run(_record, cycle);
                        if (steps != null && steps.MoveNext())
                            _record.Cycle(name, cycle, false, "",
                                "a check made at the disable request waited for a frame, so it no longer saw the frame of the request");
                    }
                    catch (Exception e)
                    {
                        _record.Cycle(name, cycle, false, "", e.GetType().Name + ": " + e.Message);
                    }

                    RequireAResult(name, cycle, passes);
                }
            }

            private IEnumerator RunExtras(Phase phase, int cycle)
            {
                foreach (var extra in ExtrasIn(phase).ToList())
                {
                    var name = _record.Check(extra.Name);
                    var passes = _record.Passes(name);
                    var steps = Contain(extra.Run(_record, cycle) ?? Enumerable.Empty<object>().GetEnumerator(),
                        (_, message) => _record.Cycle(name, cycle, false, "", message), name);
                    while (steps.MoveNext()) yield return steps.Current;

                    RequireAResult(name, cycle, passes);
                }
            }

            // An extra check that records nothing for a cycle would read as not reached; it fails
            // instead, so a check that forgot to report cannot hide.
            private void RequireAResult(string name, int cycle, int passesBefore)
            {
                if (_record.IsSettled(name) || _record.Passes(name) != passesBefore) return;
                _record.Cycle(name, cycle, false, "", "the check recorded no result");
            }

            private void JudgeLog()
            {
                _errors.Stop();
                var unexpected = _errors.Unexpected;
                if (unexpected.Count > 0)
                {
                    foreach (var entry in unexpected.Take(10))
                        Log.LogWarning($"[Automatics Smoke] {_runner}: logged while the check ran: {entry}");
                    var first = unexpected[0];
                    var end = first.IndexOf('\n');
                    _record.Settle(CheckStatus.Fail, _noErrors,
                        $"{unexpected.Count} error(s) or exception(s) were logged while the check ran (the full text is in the warnings above); the first: {(end < 0 ? first : first.Substring(0, end))}");
                }
                else if (_errors.Probes != 1)
                {
                    _record.Settle(CheckStatus.Skip, _noErrors,
                        $"the log observer saw its own probe {_errors.Probes} time(s) instead of once, so it cannot vouch for an empty log");
                }
                else
                {
                    _record.Settle(CheckStatus.Pass, _noErrors,
                        "no error or exception was logged while the check ran (the observer saw its own probe once)");
                }
            }

            private IEnumerator Await(ModuleState expected, bool faultIsFinal, bool worldRequired,
                AutomaticsModule? heldValue = null)
            {
                return AwaitState(RunId, _toggle, _moduleName, expected, faultIsFinal, worldRequired,
                    _wait, heldValue);
            }

            // The user's path: a config write raises SettingChanged, which the registry turns into
            // a request. SmokeSession refuses once the user has changed the setting.
            private bool Write(AutomaticsModule value, string name, int cycle)
            {
                if (SmokeSession.WriteToggle(RunId, value, out var refusal)) return true;

                _record.CycleSkip(name, cycle, "inconclusive: " + refusal + "; the run stopped");
                return false;
            }

            // Reaching the state passes; a fault or a timeout is a failure of the module; anything
            // that took the run out of the check's hands makes it inconclusive. Cycle 0 is a check
            // that is recorded once.
            private bool RecordWait(string name, int cycle, string note = "")
            {
                var suffix = string.IsNullOrEmpty(note) ? "" : "; " + note;
                switch (_wait.Result)
                {
                    case WaitResult.Reached:
                        if (cycle == 0) _record.Settle(CheckStatus.Pass, name, note);
                        else _record.Cycle(name, cycle, true, note, "");
                        return true;
                    case WaitResult.Faulted:
                    case WaitResult.TimedOut:
                        if (cycle == 0) _record.Settle(CheckStatus.Fail, name, _wait.Report + suffix);
                        else _record.Cycle(name, cycle, false, "", _wait.Report + suffix);
                        return false;
                    default:
                        if (cycle == 0) _record.Settle(CheckStatus.Skip, name, "inconclusive: " + _wait.Report + suffix);
                        else _record.CycleSkip(name, cycle, "inconclusive: " + _wait.Report + suffix);
                        return false;
                }
            }
        }
    }
}
