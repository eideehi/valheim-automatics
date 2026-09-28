using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Process = Automatics.AutomaticProcessing.Process;
using ProcessingConfig = Automatics.AutomaticProcessing.Config;
using ProcessingModule = Automatics.AutomaticProcessing.Module;
using ProcessingPatches = Automatics.AutomaticProcessing.Patches;
using Processor = Automatics.AutomaticProcessing.Processor;
using SmelterProcess = Automatics.AutomaticProcessing.SmelterProcess;

namespace Automatics.SmokeTest
{
    // The toggle and config subcommands of automatics_smoke_processing.
    //
    // toggle: takes automatic_processing/module through one off -> on round trip by writing the
    // setting, which raises SettingChanged and reaches the module registry through its own
    // request path, and puts it back to its starting value at the end. After each transition
    // the patches under Processing's Harmony id are compared with what
    // Automatics.AutomaticProcessing.Patches declares: none while off, exactly those while on.
    // While on, a test smelter of the check's own has to be refuelled by the injected
    // SmelterProcess.QuickRefuel, called by the game's own Smelter.UpdateSmelter, which is
    // what shows the transpilers are back. Every error and exception Unity logs meanwhile is
    // captured. The setting is SmokeSession's to hand back, under the same rules as the
    // lifecycle command's: the change is recorded before it is made, one run at a time, a
    // change the user makes wins and stops the run, and the setting is put back only while it
    // still holds the value the check wrote.
    //
    // config: reads the live .cfg and compares its automatic_processing section with the
    // entries Automatic Processing binds. It writes nothing.
    internal static partial class ProcessingSmokeCommand
    {
        private const string ConfigKeysCheck = "processing_config_section_matches_bound_entries";
        private const string ConfigModuleCheck = "processing_config_module_line_matches_setting";

        // The entries Automatic Processing binds in this build: 3 of its own, 5 the fire pieces
        // share, and 100 across the other stations in Processor's list. The number changes with
        // that list.
        private const int ExpectedProcessingEntries = 108;

        private static IEnumerator RunToggle(Terminal context, int runId, string runner)
        {
            return RunBench(context, runner, () => new ToggleRun(runId, runner,
                line => Emit(context, LogLevel.Info, line),
                line => Emit(context, LogLevel.Warning, line)));
        }

        private static IEnumerator RunConfig(Terminal context, int runId, string runner)
        {
            var results = new List<CheckResult>();
            try
            {
                CheckConfig(results);
            }
            catch (Exception e)
            {
                results.Add(CheckResult.Fail(ConfigKeysCheck,
                    $"the check stopped on {e.GetType().Name}: {e.Message}"));
            }

            Report(context, results);
            yield break;
        }

        // Read-only: the file is read, nothing is written, and no setting is assigned.
        private static void CheckConfig(List<CheckResult> results)
        {
            var entry = ProcessingModule.Instance.ModuleToggle;
            if (entry == null)
            {
                results.Add(CheckResult.Fail(ConfigKeysCheck, "the automatic_processing module toggle is not bound"));
                return;
            }

            var section = entry.Definition.Section;
            var file = entry.ConfigFile;
            var bound = new HashSet<string>(
                file.Keys.Where(x => x.Section == section).Select(x => x.Key), StringComparer.Ordinal);

            var path = file.ConfigFilePath;
            var name = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                results.Add(CheckResult.Fail(ConfigKeysCheck, $"the config file {path} does not exist"));
                return;
            }

            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new List<string>();
            var inSection = false;
            var sectionLines = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = line.Substring(1, line.Length - 2).Trim() == section;
                    continue;
                }

                if (!inSection) continue;
                sectionLines++;
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line.Substring(0, separator).Trim();
                if (keys.ContainsKey(key))
                    duplicates.Add(key);
                else
                    keys[key] = line.Substring(separator + 1).Trim();
            }

            var missing = bound.Where(x => !keys.ContainsKey(x)).OrderBy(x => x).ToList();
            var extra = keys.Keys.Where(x => !bound.Contains(x)).OrderBy(x => x).ToList();
            if (missing.Count == 0 && extra.Count == 0 && duplicates.Count == 0 &&
                bound.Count == ExpectedProcessingEntries)
                results.Add(CheckResult.Pass(ConfigKeysCheck,
                    $"[{section}] in {name} has {keys.Count} keys, the same as the {bound.Count} entries Automatic Processing binds ({sectionLines} lines in the section)"));
            else
                results.Add(CheckResult.Fail(ConfigKeysCheck,
                    $"[{section}] in {name} has {keys.Count} keys and Automatic Processing binds {bound.Count} (this build expects {ExpectedProcessingEntries}); missing from the file: [{string.Join(", ", missing.Take(5))}]; in the file but not bound: [{string.Join(", ", extra.Take(5))}]{(duplicates.Count > 0 ? $"; written twice: [{string.Join(", ", duplicates.Take(5))}]" : "")} ({sectionLines} lines in the section)"));

            var setting = $"{section}/{entry.Definition.Key}";
            var expected = entry.Value.ToString();
            if (!keys.TryGetValue(entry.Definition.Key, out var written))
                results.Add(CheckResult.Fail(ConfigModuleCheck,
                    $"[{section}] in {name} has no {entry.Definition.Key} line; the setting is {expected}"));
            else if (written == expected)
                results.Add(CheckResult.Pass(ConfigModuleCheck,
                    $"{setting} = {written} in {name}, the same as the setting"));
            else
                results.Add(CheckResult.Fail(ConfigModuleCheck,
                    $"{setting} = {written} in {name}, but the setting is {expected}"));
        }

        // -- The toggle subcommand ----------------------------------------------------------

        private sealed class ToggleRun : BenchRun
        {
            private const string InitialSettled = "processing_toggle_initial_state_settled";
            private const string OffState = "processing_toggle_off_state_disabled";
            private const string OffPatches = "processing_toggle_off_patches_removed";
            private const string OnState = "processing_toggle_on_state_enabled";
            private const string OnPatches = "processing_toggle_on_patches_applied";
            private const string QuickRefuel = "processing_toggle_on_quickrefuel_acts";
            private const string Restored = "processing_toggle_restored_initial_state";
            private const string NoErrors = "processing_toggle_no_errors_logged";

            // A requested transition normally lands on the next pump tick, or a few ticks later
            // after a deferral. This bounds the wait; it is not an estimate of it.
            private const float TransitionTimeoutSeconds = 10f;

            // Object.Destroy takes effect at the end of the frame the pump ran in.
            private const int DestroySettleFrames = 2;

            // How long the enabled module has to show the injected QuickRefuel acting, and after
            // how long without it the test smelter's catch-up is set up once more.
            private const float QuickRefuelTimeoutSeconds = 10f;
            private const float QuickRefuelRetrySeconds = 5f;

            // Above fuel_count_of_suppress_processing, so QuickRefuel has fuel to take.
            private const int FuelSurplus = 3;

            private static readonly string[] Order =
            {
                InitialSettled, OffState, OffPatches, OnState, OnPatches, QuickRefuel, Restored, NoErrors
            };

            private readonly Dictionary<string, CheckResult> _checks =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly ErrorObserver _errors = new ErrorObserver(
                "AutomaticsObserverProbe-toggle", "automatics_smoke_processing toggle");
            private readonly FuelBench _fuel;

            private ConfigEntry<AutomaticsModule> _toggle;
            private AutomaticsModule _initialValue;
            private string _moduleName;
            private string _harmonyId;
            private bool _toggleOwned;
            private bool _benchReady;
            private float _enabledAt;
            private string _notReached = "not reached: an earlier step stopped the run";
            private Func<string, bool> _observersHandBack;

            private WaitResult _wait;
            private string _waitReport = "";

            public ToggleRun(int runId, string runner, Action<string> info, Action<string> warn)
                : this(runId, runner, info, warn, new FuelBench())
            {
            }

            private ToggleRun(int runId, string runner, Action<string> info, Action<string> warn,
                FuelBench fuel) : base(runId, runner, info, warn, new List<StationBench> { fuel })
            {
                _fuel = fuel;
            }

            private enum WaitResult
            {
                Reached,
                Faulted,
                TimedOut,
                EditedByUser,
                WorldGone,
                Abandoned
            }

            public override IReadOnlyList<CheckResult> Results
            {
                get
                {
                    var results = new List<CheckResult>();
                    foreach (var name in Order)
                        if (name == QuickRefuel)
                            results.Add(_fuel.Result ?? CheckResult.Skip(QuickRefuel, _notReached));
                        else
                            results.Add(_checks.TryGetValue(name, out var result)
                                ? result
                                : CheckResult.Skip(name, _notReached));
                    return results;
                }
            }

            public override IEnumerator Execute()
            {
                _info(
                    $"[Automatics Smoke] {_runner} started: automatic_processing/module is turned off and on again through its setting, a test smelter is watched while it is on, and the setting is restored");

                var player = Player.m_localPlayer;
                var problem = EnvironmentProblem(player, false);
                if (problem != null)
                {
                    SkipAll(problem);
                    yield break;
                }

                _toggle = ProcessingModule.Instance.ModuleToggle;
                if (_toggle == null)
                {
                    Fail(InitialSettled, "the automatic_processing module toggle is not bound");
                    yield break;
                }

                _moduleName = ProcessingModule.Instance.Name;
                _harmonyId = global::Automatics.Automatics.GetHarmonyId(_moduleName);
                _initialValue = _toggle.Value;

                // The check only ever pauses Automatic Processing, never turns it on for the user:
                // on, it would act on the user's own stations, and no watch can stop it in the
                // frame one of them loads.
                if (_initialValue != AutomaticsModule.Enabled)
                {
                    SkipAll(
                        "Automatic Processing is off in your settings. Turning it on for the check would let it act on your own stations, so the check did not change it. Turn it on yourself if you want this check, then run it again.");
                    yield break;
                }

                // From here on any write to the setting that is not the check's own is the user
                // taking it back.
                if (!SmokeSession.BeginToggle(_runId, _toggle, out var refusal))
                {
                    SkipAll("inconclusive: " + refusal);
                    yield break;
                }

                _toggleOwned = true;

                // The observers go back with the session too, however the run ends.
                _observersHandBack = ReleaseObservers;
                SmokeSession.TrackHandBack(_observersHandBack);
                _errors.Start();

                // The registry has to agree with the setting before anything is asked of it, or a
                // transition still pending from before the command would be read as this one's.
                var settle = WaitForState(StateFor(_initialValue), true, true);
                while (settle.MoveNext()) yield return settle.Current;
                RecordWait(InitialSettled, "nothing was changed");
                if (_wait != WaitResult.Reached) yield break;

                // off
                if (_toggle.Value != AutomaticsModule.Disabled &&
                    !Write(AutomaticsModule.Disabled, OffState))
                    yield break;
                var off = WaitForState(ModuleState.Disabled, true, true);
                while (off.MoveNext()) yield return off.Current;
                RecordWait(OffState);
                if (_wait != WaitResult.Reached) yield break;
                for (var i = 0; i < DestroySettleFrames; i++) yield return null;
                CheckPatchesRemoved();

                // The test smelter is placed while the module is off, so it already exists when
                // the module comes back, as the user's own stations do.
                PrepareBench(player);

                // on: back to the value the user had.
                if (!Write(AutomaticsModule.Enabled, OnState)) yield break;
                var on = WaitForState(ModuleState.Enabled, true, true);
                while (on.MoveNext()) yield return on.Current;
                RecordWait(OnState);
                if (_wait != WaitResult.Reached) yield break;
                _enabledAt = Time.realtimeSinceStartup;
                CheckPatchSet();

                if (!_benchReady) yield break;
                var watch = WatchQuickRefuel();
                while (watch.MoveNext()) yield return watch.Current;
            }

            // Puts the setting back and waits for the registry to follow, then judges the log.
            public override IEnumerator Finish()
            {
                if (_toggleOwned)
                {
                    var outcome = SmokeSession.RestoreToggle(_runId, out var detail);
                    switch (outcome)
                    {
                        case SmokeSession.ToggleOutcome.NothingWritten:
                            Skip(Restored, "nothing was written, so there was nothing to put back");
                            break;
                        case SmokeSession.ToggleOutcome.LeftForUser:
                            Skip(Restored, "restoration skipped: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.NotThisRun:
                            Skip(Restored, "inconclusive: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.Failed:
                            Fail(Restored, detail);
                            break;
                        default:
                            // A fault is not final here: putting the value back is also what asks
                            // the registry to recover, so the wait gives it the full bound.
                            var wait = WaitForState(StateFor(_initialValue), false, false, _initialValue);
                            while (wait.MoveNext()) yield return wait.Current;
                            RecordWait(Restored, detail);
                            break;
                    }
                }

                if (_errors.Started) JudgeLog();
            }

            public override void Stopped(Exception e)
            {
                // The step that was running fails; the ones after it were not reached.
                var message = $"the run stopped on {e.GetType().Name}: {e.Message}";
                var running = Order.FirstOrDefault(x =>
                    x == QuickRefuel ? _fuel.Result == null : !_checks.ContainsKey(x));
                if (running == QuickRefuel)
                    _fuel.Fail(message);
                else if (running != null)
                    Fail(running, message);
                _notReached = "not reached: " + message;
            }

            public override void HandBack(string reason)
            {
                base.HandBack(reason);
                try
                {
                    if (_observersHandBack != null && ReleaseObservers(reason))
                        SmokeSession.ReleaseHandBack(_observersHandBack);
                }
                catch (Exception e)
                {
                    _warn(
                        $"[Automatics Smoke] {reason}: the check's observers could not be removed yet ({e.GetType().Name}: {e.Message}); they are tried again at the next opportunity");
                }
            }

            // A patch that could not be removed is kept as an obligation: every hand-back trigger
            // (the run's end, the plugin's disable or destroy, the game's quit, the next command)
            // tries again.
            private bool ReleaseObservers(string reason)
            {
                _errors.Stop();
                if (!QuickRefuelObserver.Attached || QuickRefuelObserver.Detach(out var failure)) return true;

                _warn(
                    $"[Automatics Smoke] {reason}: the check's observer on SmelterProcess.QuickRefuel could not be removed yet ({failure}); it is tried again at the next opportunity");
                return false;
            }

            // -- The test smelter -----------------------------------------------------------

            // Placed, and its isolation checked the way rpc does, before the enable write, so the
            // first Smelter.UpdateSmelter after the enable can find only the test chest. When any
            // of that fails, whatever was placed is handed back at once, so nothing of the check's
            // stands while the module is on without the check watching it.
            private void PrepareBench(Player player)
            {
                SetUpBench(player);
                if (!_benchReady) ReleaseBenches($"{_runner} (the test smelter could not be set up)");
            }

            private void SetUpBench(Player player)
            {
                if (ProcessingConfig.RefuelOnlyWhenMaterialsSupplied(_fuel.ObjectName))
                {
                    _fuel.Skip(
                        $"{_fuel.ObjectName.Substring(1)}_refuel_only_when_materials_supplied is on, and the test smelter gets no ore");
                    return;
                }

                if (!FindChestPrefab(out var chestPrefab)) return;
                ResolveStations();
                ChooseItems();
                if (Active.Count == 0) return;
                if (!PlanLayout(player, out var chestPosition, out var distance)) return;
                if (!CheckSurroundings(chestPosition, distance)) return;
                if (!PlaceBenches(chestPrefab, chestPosition, distance)) return;
                if (!CheckSources()) return;

                var wanted = ProcessingConfig.FuelCountOfSuppressProcessing(_fuel.ObjectName) + FuelSurplus;
                _fuel.Seeded = SeedChest(_benches.ChestInventory, _fuel.MaterialPrefab, _fuel.MaterialName,
                    wanted);
                if (_fuel.Seeded < wanted)
                {
                    _fuel.Skip(
                        $"only {_fuel.Seeded} of the {wanted} {DisplayName(_fuel.MaterialName)} fit into the test chest");
                    return;
                }

                _fuel.Prepare();

                if (!QuickRefuelObserver.Attach(_fuel.Station, out var attachProblem))
                {
                    _fuel.NotRun(attachProblem);
                    return;
                }

                // The observer's positive control: a direct call, which returns at once while the
                // module is off, still has to be counted, or a count of 0 later would not show that
                // the game did not call the entry.
                SmelterProcess.QuickRefuel(_fuel.Station, _fuel.NView, 0f, 0f);
                if (QuickRefuelObserver.Calls != 1)
                {
                    _fuel.NotRun(
                        $"the observer on SmelterProcess.QuickRefuel counted {QuickRefuelObserver.Calls} of 1 direct call, so it could not show whether the game calls the entry");
                    return;
                }

                QuickRefuelObserver.Reset();
                _benchReady = true;
            }

            private IEnumerator WatchQuickRefuel()
            {
                var chest = _benches.ChestInventory;
                _fuel.ArmCatchUp();
                var started = Time.realtimeSinceStartup;
                var rearmed = false;
                var paused = Game.IsPaused();
                _fuel.Record(0, chest);

                var frame = 0;
                while (QuickRefuelObserver.Acted == 0)
                {
                    var elapsed = Time.realtimeSinceStartup - started;
                    if (elapsed >= QuickRefuelTimeoutSeconds) break;
                    if (!rearmed && elapsed >= QuickRefuelRetrySeconds)
                    {
                        _fuel.ArmCatchUp();
                        rearmed = true;
                    }

                    yield return null;
                    frame++;
                    if (Game.IsPaused()) paused = true;
                    if (!StillWatching(frame) || !KeepIsolated(frame)) yield break;
                    if (SmokeSession.ToggleEditedByUser)
                    {
                        _fuel.Skip("inconclusive: automatic_processing/module was changed outside the check");
                        yield break;
                    }
                    _fuel.Record(frame, chest);
                }

                // One more frame, so the ledger is also read after the refuel has settled.
                yield return null;
                frame++;
                if (!StillWatching(frame) || !KeepIsolated(frame)) yield break;
                _fuel.Record(frame, chest);

                var drops = _benches.NewDropsNear(new StationBench[] { _fuel });
                if (_fuel.VanishedAt >= 0)
                    _fuel.Fail($"the test smelter disappeared at frame {_fuel.VanishedAt}{drops}");
                else if (_fuel.FirstBreak != null)
                    _fuel.Fail(_fuel.DescribeBreak() + drops);
                else if (drops.Length > 0)
                    _fuel.Fail($"the ledger held, but test items appeared on the ground near the test objects{drops}");
                else if (QuickRefuelObserver.Acted == 0)
                    _fuel.NotRun(
                        $"in {QuickRefuelTimeoutSeconds:0} s after the enable the game called the injected SmelterProcess.QuickRefuel {QuickRefuelObserver.Calls} time(s) for the test smelter, and it never refuelled it, with the smelter's catch-up set {(rearmed ? "twice" : "once")}{(paused ? "; the game was paused meanwhile" : "")} ({_fuel.Diagnose()})");
                else
                    _fuel.Pass(
                        $"{QuickRefuelObserver.ActedAt - _enabledAt:0.0} s after the enable, the game's own Smelter.UpdateSmelter called the injected SmelterProcess.QuickRefuel for the test smelter ({QuickRefuelObserver.Calls} call(s)) and it moved {DisplayName(_fuel.MaterialName)} from the test chest into the smelter's fuel; the ledger held on every frame (chest {_fuel.Last.Chest} + smelter fuel {_fuel.Last.Station} = {_fuel.Seeded} put in)");
            }

            // -- The setting and the registry ---------------------------------------------------

            // The user's path: a config write raises SettingChanged, which the registry turns into
            // a request. SmokeSession records the change before it is made and refuses once the
            // user has changed the setting or the run no longer owns it.
            private bool Write(AutomaticsModule value, string checkName)
            {
                if (SmokeSession.WriteToggle(_runId, value, out var refusal)) return true;

                Skip(checkName, "inconclusive: " + refusal + "; the run stopped");
                return false;
            }

            private IEnumerator WaitForState(ModuleState expected, bool faultIsFinal,
                bool worldRequired, AutomaticsModule? heldValue = null)
            {
                _wait = WaitResult.TimedOut;
                _waitReport = "";

                var started = Time.realtimeSinceStartup;
                var frames = 0;
                while (true)
                {
                    if (!SmokeSession.IsCurrent(_runId))
                    {
                        _wait = WaitResult.Abandoned;
                        _waitReport = "the run was abandoned (the smoke plugin is shutting down)";
                        yield break;
                    }

                    if (SmokeSession.ToggleEditedByUser ||
                        (heldValue.HasValue && _toggle.Value != heldValue.Value))
                    {
                        _wait = WaitResult.EditedByUser;
                        _waitReport =
                            $"automatic_processing/module was changed to {_toggle.Value} outside the check";
                        yield break;
                    }

                    if (worldRequired && (ZNetScene.instance == null || !Player.m_localPlayer))
                    {
                        _wait = WaitResult.WorldGone;
                        _waitReport = "the world was unloaded while the check ran";
                        yield break;
                    }

                    // While the test smelter stands, Processing's own search for it has to find
                    // only the test chest on every frame, those of a wait included.
                    KeepIsolated(frames);

                    var state = ModuleRegistry.GetState(_moduleName);
                    if (state == expected)
                    {
                        _wait = WaitResult.Reached;
                        yield break;
                    }

                    if (faultIsFinal && state == ModuleState.Faulted)
                    {
                        _wait = WaitResult.Faulted;
                        _waitReport =
                            $"the registry reports {state} while waiting for {expected}; the reason is in the Automatics log as a warning";
                        yield break;
                    }

                    var elapsed = Time.realtimeSinceStartup - started;
                    if (elapsed >= TransitionTimeoutSeconds)
                    {
                        _wait = WaitResult.TimedOut;
                        _waitReport =
                            $"timed out after {elapsed:0.0}s ({frames} frames) waiting for {expected}; the registry reports {state} and the setting is {_toggle.Value}";
                        yield break;
                    }

                    frames++;
                    yield return null;
                }
            }

            // Reaching the state passes; a fault or a timeout is a failure of the module;
            // anything that took the run out of the check's hands makes it inconclusive.
            private void RecordWait(string name, string note = "")
            {
                var suffix = string.IsNullOrEmpty(note) ? "" : "; " + note;
                switch (_wait)
                {
                    case WaitResult.Reached:
                        Pass(name, name == Restored ? note : "");
                        break;
                    case WaitResult.Faulted:
                    case WaitResult.TimedOut:
                        Fail(name, _waitReport + suffix);
                        break;
                    default:
                        Skip(name, "inconclusive: " + _waitReport + suffix);
                        break;
                }

                // The test smelter cannot be watched when the module did not reach the state.
                if (_wait != WaitResult.Reached && name != Restored)
                    _fuel.Skip("inconclusive: " + _waitReport);
            }

            private static ModuleState StateFor(AutomaticsModule value)
            {
                return value == AutomaticsModule.Enabled ? ModuleState.Enabled : ModuleState.Disabled;
            }

            // -- Patches ------------------------------------------------------------------------

            private void CheckPatchesRemoved()
            {
                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    Skip(OffPatches,
                        "this Harmony build cannot remove patches at runtime; they stay applied by design (R3.2)");
                    return;
                }

                var left = CollectPatches(_harmonyId, out _, out _, out var methods);
                if (left.Count == 0)
                    Pass(OffPatches, $"no patch is left under \"{_harmonyId}\"");
                else
                    Fail(OffPatches,
                        $"{left.Count} patch(es) on {methods} method(s) are still under \"{_harmonyId}\" after the disable: [{string.Join(", ", left.OrderBy(x => x).Take(5))}] (AC-R3)");
            }

            // The patches under the module's Harmony id have to be exactly the ones its patch
            // class declares, compared as (target, patch method) pairs.
            private void CheckPatchSet()
            {
                var expected = ExpectedPatches();
                if (expected.Count == 0)
                {
                    Fail(OnPatches, "no [HarmonyPatch] target could be read from Automatics.AutomaticProcessing.Patches");
                    return;
                }

                // Compared as pairs and counted as patches, so a pair applied twice does not hide.
                var actual = CollectPatches(_harmonyId, out var total, out var transpilers, out var methods);
                var targets = expected.Select(x => x.Substring(0, x.IndexOf(" <- ", StringComparison.Ordinal)))
                    .Distinct().Count();
                var missing = expected.Where(x => !actual.Contains(x)).OrderBy(x => x).ToArray();
                var unexpected = actual.Where(x => !expected.Contains(x)).OrderBy(x => x).ToArray();
                if (missing.Length == 0 && unexpected.Length == 0 && total == expected.Count &&
                    methods == targets)
                    Pass(OnPatches,
                        $"{total} patches on {methods} methods ({transpilers} transpilers) under \"{_harmonyId}\", exactly the pairs Automatics.AutomaticProcessing.Patches declares");
                else
                    Fail(OnPatches,
                        $"the patches under \"{_harmonyId}\" differ from Automatics.AutomaticProcessing.Patches: {total} patch(es) on {methods} method(s) where it declares {expected.Count} on {targets}; missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", unexpected)}]");
            }

            private static HashSet<string> ExpectedPatches()
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                var patches = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in typeof(ProcessingPatches).GetMethods(flags))
                foreach (var attribute in method.GetCustomAttributes(typeof(HarmonyPatch), false))
                {
                    var info = ((HarmonyPatch)attribute).info;
                    if (info == null || info.declaringType == null ||
                        string.IsNullOrEmpty(info.methodName))
                        continue;

                    var target = AccessTools.Method(info.declaringType, info.methodName,
                        info.argumentTypes);
                    var targetName = target == null
                        ? $"{info.declaringType.Name}.{info.methodName} (unresolved)"
                        : DescribeMethod(target);
                    patches.Add($"{targetName} <- {method.DeclaringType?.Name}.{method.Name}");
                }

                return patches;
            }

            private static HashSet<string> CollectPatches(string harmonyId, out int total,
                out int transpilers, out int methods)
            {
                total = 0;
                transpilers = 0;
                methods = 0;
                var patches = new HashSet<string>(StringComparer.Ordinal);
                foreach (var target in Harmony.GetAllPatchedMethods())
                {
                    var info = Harmony.GetPatchInfo(target);
                    if (info == null) continue;

                    var found = false;
                    foreach (var list in new[]
                             {
                                 info.Prefixes, info.Postfixes, info.Transpilers, info.Finalizers,
                                 info.ILManipulators
                             })
                    {
                        if (list == null) continue;
                        foreach (var patch in list)
                        {
                            if (patch.owner != harmonyId) continue;
                            found = true;
                            total++;
                            if (ReferenceEquals(list, info.Transpilers)) transpilers++;
                            patches.Add(
                                $"{DescribeMethod(target)} <- {patch.PatchMethod.DeclaringType?.Name}.{patch.PatchMethod.Name}");
                        }
                    }

                    if (found) methods++;
                }

                return patches;
            }

            private static string DescribeMethod(MethodBase method)
            {
                var parameters = string.Join(",",
                    method.GetParameters().Select(p => p.ParameterType.Name).ToArray());
                return $"{method.DeclaringType?.Name}.{method.Name}({parameters})";
            }

            // -- The log --------------------------------------------------------------------------

            private void JudgeLog()
            {
                _errors.Stop();
                var unexpected = _errors.Unexpected;
                if (unexpected.Count > 0)
                {
                    foreach (var entry in unexpected.Take(10))
                        _warn($"[Automatics Smoke] {_runner}: logged while the check ran: {entry}");
                    Fail(NoErrors,
                        $"{unexpected.Count} error(s) or exception(s) were logged while the check ran (the full text is in the warnings above); the first: {FirstLine(unexpected[0])}");
                }
                else if (_errors.Probes != 1)
                {
                    NotRun(NoErrors,
                        $"the log observer saw its own probe {_errors.Probes} time(s) instead of once, so it cannot vouch for an empty log");
                }
                else
                {
                    Pass(NoErrors,
                        "no error or exception was logged while the check ran (the observer saw its own probe once)");
                }
            }

            private static string FirstLine(string text)
            {
                var end = text.IndexOf('\n');
                return end < 0 ? text : text.Substring(0, end);
            }

            // -- Results --------------------------------------------------------------------------

            private void Pass(string name, string note) => Settle(CheckResult.Pass(name, note));
            private void Fail(string name, string message) => Settle(CheckResult.Fail(name, message));
            private void Skip(string name, string message) => Settle(CheckResult.Skip(name, message));
            private void NotRun(string name, string message) => Settle(CheckResult.NotRun(name, message));

            private void Settle(CheckResult result)
            {
                if (!_checks.ContainsKey(result.Name)) _checks[result.Name] = result;
            }

            private void SkipAll(string message)
            {
                foreach (var name in Order)
                    if (name != QuickRefuel)
                        Skip(name, message);
                SkipRemaining(message);
            }

            // Processing's own search for the test smelter has to find only the test chest on every
            // frame the smelter stands. When it finds anything else, the smelter's check ends as
            // NOT-RUN and the test objects go through the keep rules at once, not at the end.
            private bool KeepIsolated(int frame)
            {
                if (!_benchReady || StillIsolated(frame)) return true;
                _benchReady = false;
                ReleaseBenches($"{_runner} (a container came into the test smelter's reach)");
                return false;
            }
        }

        // The toggle check's test smelter. Its material is the smelter's own fuel, which the
        // check puts into the chest; the injected SmelterProcess.QuickRefuel moves one of it into
        // the smelter's fuel when the smelter's update catches up on more than a second with no
        // fuel left.
        private sealed class FuelBench : StationBench
        {
            // Smelter.UpdateSmelter adds ZDOVars.s_accTime to the time since its last update and
            // runs its loop once per whole second; the injected QuickRefuel acts only on an
            // iteration after which a whole second is still left, so the smelter is given
            // several seconds to catch up.
            private const float CatchUpSeconds = 5f;

            private Smelter _prefab;
            private Smelter _station;
            private int _heldMaxFuel = -1;

            public FuelBench() : base("processing_toggle_on_quickrefuel_acts", "$piece_smelter", "smelter")
            {
            }

            protected override Type ComponentType => typeof(Smelter);

            // The fuel is this bench's material, not a fuel kept out of the chest.
            public override string FuelName => null;

            public override Process RequiredProcess => Process.Refuel;

            public Smelter Station => _station;

            public override IEnumerable<Conversion> Conversions()
            {
                if (_prefab && _prefab.m_fuelItem) yield return new Conversion(_prefab.m_fuelItem, null, 1);
            }

            protected override bool BindPrefab(GameObject prefab)
            {
                _prefab = prefab.GetComponent<Smelter>();
                return _prefab != null;
            }

            protected override Component BindStation(GameObject go)
            {
                _station = go.GetComponent<Smelter>();
                return _station;
            }

            public override bool CanPrepare(out string reason)
            {
                reason = null;
                if (!_prefab.m_fuelItem)
                    reason = "the smelter has no fuel item";
                else if (_prefab.m_conversion != null && _prefab.m_conversion.Any(x =>
                             x != null && x.m_from &&
                             x.m_from.m_itemData.m_shared.m_name ==
                             _prefab.m_fuelItem.m_itemData.m_shared.m_name))
                    reason = "the smelter also takes its fuel as ore, so the fuel in the test chest could not be told apart";
                return reason == null;
            }

            // SmelterProcess.Refuel, the ordinary refuel the smelter's update runs after its loop,
            // returns once fuel >= m_maxFuel - 1. Holding this smelter's m_maxFuel at 1 keeps it
            // out, so only the injected QuickRefuel can refuel it; neither QuickRefuel nor vanilla
            // RPC_AddFuel reads m_maxFuel.
            public override void Prepare()
            {
                if (_station.m_maxFuel <= 1 || _heldMaxFuel >= 0) return;
                _heldMaxFuel = _station.m_maxFuel;
                _station.m_maxFuel = 1;
            }

            public override void ReleaseToWorld()
            {
                if (_heldMaxFuel >= 0) _station.m_maxFuel = _heldMaxFuel;
            }

            public void ArmCatchUp()
            {
                NView.GetZDO().Set(ZDOVars.s_accTime, CatchUpSeconds);
            }

            // The game drives this one: its own Smelter.UpdateSmelter calls the injected entry.
            public override void Drive()
            {
            }

            // One piece of fuel is one unit of ZDOVars.s_fuel (RPC_AddFuel adds 1, and QuickRefuel
            // sets fuel + 1); with no ore queued the smelter burns none of it.
            public override StationContent ReadContent()
            {
                var zdo = NView.GetZDO();
                var content = new StationContent();
                var queued = zdo.GetInt(ZDOVars.s_queued);
                for (var i = 0; i < queued; i++) content.AddForeign(zdo.GetString("item" + i));

                var fuel = zdo.GetFloat(ZDOVars.s_fuel);
                var units = Mathf.RoundToInt(fuel);
                if (Mathf.Abs(fuel - units) > 0.001f)
                    content.AddForeign($"fuel {fuel:0.###}, not a whole number of pieces");
                else
                    content.Material = units;
                return content;
            }

            public override void ClearContent()
            {
                var zdo = NView.GetZDO();
                var queued = zdo.GetInt(ZDOVars.s_queued);
                for (var i = 0; i < queued; i++) zdo.Set("item" + i, "");
                zdo.Set(ZDOVars.s_queued, 0);
                zdo.Set(ZDOVars.s_fuel, 0f);
            }

            protected override string DiagnoseStation()
            {
                var zdo = NView.GetZDO();
                return
                    $"fuel={zdo.GetFloat(ZDOVars.s_fuel):0.##}, accTime={zdo.GetFloat(ZDOVars.s_accTime):0.##}, maxFuel={_station.m_maxFuel}, queued={zdo.GetInt(ZDOVars.s_queued)}";
            }
        }

        // Counts, for the test smelter, the calls the game makes to SmelterProcess.QuickRefuel and
        // those that refuelled it, from a postfix under the smoke plugin's own Harmony id. It
        // patches the mod's entry point, never a game method, and only while the toggle check
        // runs.
        private static class QuickRefuelObserver
        {
            private const string HarmonyId = "net.eidee.valheim.automatics.smoke.quickrefuel-observer";

            private static Harmony _harmony;
            private static Smelter _target;

            public static int Calls { get; private set; }
            public static int Acted { get; private set; }
            public static float ActedAt { get; private set; }
            public static bool Attached => _harmony != null;

            public static bool Attach(Smelter target, out string problem)
            {
                problem = null;
                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    problem =
                        "this Harmony build cannot remove patches at runtime, so the check does not add its observer to SmelterProcess.QuickRefuel";
                    return false;
                }

                var method = AccessTools.Method(typeof(SmelterProcess), nameof(SmelterProcess.QuickRefuel));
                var prefix = AccessTools.Method(typeof(QuickRefuelObserver), nameof(Prefix));
                var postfix = AccessTools.Method(typeof(QuickRefuelObserver), nameof(Postfix));
                if (method == null || prefix == null || postfix == null)
                {
                    problem = "SmelterProcess.QuickRefuel or the observer's patch methods were not found";
                    return false;
                }

                if (Attached && !Detach(out var stale))
                {
                    problem = $"an earlier observer patch on SmelterProcess.QuickRefuel could not be removed ({stale})";
                    return false;
                }

                _target = target;
                Reset();
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
                return true;
            }

            // The handle is dropped only once the patch is gone, so a removal that failed is still
            // known and can be tried again. Until then the postfix counts nothing: it has no target.
            public static bool Detach(out string failure)
            {
                failure = null;
                _target = null;
                if (_harmony == null) return true;

                try
                {
                    Unpatch(_harmony);
                }
                catch (Exception e)
                {
                    failure = $"{e.GetType().Name}: {e.Message}";
                    return false;
                }

                _harmony = null;
                return true;
            }

            // Apart from Detach, so the call is only compiled where a patch was made.
            private static void Unpatch(Harmony harmony)
            {
                harmony.UnpatchSelf();
            }

            public static void Reset()
            {
                Calls = 0;
                Acted = 0;
                ActedAt = 0f;
            }

            // SmelterProcess.QuickRefuel adds to its own fuel parameter before it returns, and a
            // postfix reads the parameter as it is then; the prefix keeps the value it was called
            // with.
            private static void Prefix(float fuel, out float __state)
            {
                __state = fuel;
            }

            private static void Postfix(Smelter smelter, float __state, float __result)
            {
                if (!_target || smelter != _target) return;
                Calls++;
                if (!(__result > __state)) return;
                if (Acted == 0) ActedAt = Time.realtimeSinceStartup;
                Acted++;
            }
        }
    }
}
