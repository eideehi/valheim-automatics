using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;
using BirdCache = Automatics.AutomaticMapping.BirdCache;
using DynamicObjectMapping = Automatics.AutomaticMapping.DynamicObjectMapping;
using FishCache = Automatics.AutomaticMapping.FishCache;
using FloraNode = Automatics.AutomaticMapping.FloraNode;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;
using MappingPatches = Automatics.AutomaticMapping.Patches;
using MineRock5CacheBinding = Automatics.AutomaticMapping.MineRock5CacheBinding;
using ShipCache = Automatics.AutomaticMapping.ShipCache;
using StaticObjectMapping = Automatics.AutomaticMapping.StaticObjectMapping;
using ValheimObject = Automatics.Valheim.ValheimObject;

namespace Automatics.SmokeTest
{
    // automatics_smoke_lifecycle mapping: takes Automatic Mapping through three off -> on cycles
    // by writing automatic_mapping/module, the way a user does, and checks after each transition
    // what the module leaves behind and what it brings back:
    //   - at the disable request, the module is still applied but no longer running, which is
    //     the predicate every entry point opens with, and the static scan makes no pass until the
    //     teardown;
    //   - while off, no patch is left under its Harmony id, no component of its own and no
    //     navigation overlay is left in the scene, the saved pins are the ones there were before
    //     the run, and no unsaved pin the module held is left on the map;
    //   - while on, the patches are exactly the declared set, every eligible object in the scene
    //     carries its component from the running generation, the handler counts, component counts
    //     and minimap icon tables are what they were before the run, the scans run again, and the
    //     saved pins are still the ones there were before the run.
    // A vehicle pin is a saved pin that follows its vehicle, and a disable moves it to where its
    // vehicle is, so the saved-pin comparison leaves the position of the vehicle pins out.
    //
    // It spawns nothing and writes only the toggle, through SmokeSession, under the lifecycle
    // command's rules. It only ever turns Automatic Mapping off and back on: when the module is
    // off in the user's settings, every check SKIPs and nothing is written.
    internal static partial class LifecycleSmokeCommand
    {
        private const int MappingCycles = 3;

        // How long the scans have after an enable to show they run again. This bounds the wait;
        // it is not an estimate of it.
        private const float MappingResumeTimeoutSeconds = 10f;

        // A fish that appeared this frame is attached at its own Start, and an object the game
        // despawned this frame has lost its ZDO before its components go. The component counts
        // are therefore sampled again for up to this many frames before a mismatch is called.
        private const int ComponentSettleFrames = 10;

        // The flora networks are built 1 to 2 s after an enable and their pins placed by the next
        // static pass, so the saved pins are compared this long after the enable.
        private const float PinSettleSeconds = 4f;

        private const string NavigationOverlayName = "AutomaticsNavigationOverlay";

        private static IEnumerator RunMapping(Terminal context, int runId, string runner)
        {
            var run = new MappingRun(runId, runner);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {runner} started: automatic_mapping/module is turned off and on {MappingCycles} times through its setting, nothing is spawned, and the setting is restored");

            var steps = Contain(run.Execute(), (_, message) => run.Stopped(message), "");
            while (steps.MoveNext()) yield return steps.Current;

            var finish = Contain(run.Finish(), (_, message) => run.Stopped(message), "");
            while (finish.MoveNext()) yield return finish.Current;

            Report(context, run.Results);
        }

        private sealed class MappingRun
        {
            private const string Settled = "mapping_initial_state_settled";
            private const string OffState = "mapping_off_state_disabled";
            private const string OffStopped = "mapping_off_stopped_at_request";
            private const string OffPatches = "mapping_off_patches_removed";
            private const string OffComponents = "mapping_off_components_removed";
            private const string OffSavedPins = "mapping_off_saved_pins_kept";
            private const string OffUnsavedPins = "mapping_off_unsaved_pins_removed";
            private const string OnState = "mapping_on_state_enabled";
            private const string OnPatches = "mapping_on_patches_applied";
            private const string OnComponents = "mapping_on_components_attached";
            private const string OnBaseline = "mapping_on_counts_at_baseline";
            private const string OnResumed = "mapping_on_scans_resumed";
            private const string OnSavedPins = "mapping_on_saved_pins_kept";
            private const string Restored = "mapping_restored_initial_state";
            private const string NoErrors = "mapping_no_errors_logged";

            // The order the checks are recorded in within a cycle, which is also the order they
            // are reported in.
            private static readonly string[] Order =
            {
                Settled, OffState, OffStopped, OffPatches, OffComponents, OffSavedPins, OffUnsavedPins,
                OnState, OnPatches, OnComponents, OnBaseline, OnResumed, OnSavedPins, Restored, NoErrors
            };

            // Recorded once per cycle, and a PASS only when every cycle passed.
            private static readonly HashSet<string> PerCycle = new HashSet<string>(StringComparer.Ordinal)
            {
                OffState, OffStopped, OffPatches, OffComponents, OffSavedPins, OffUnsavedPins,
                OnState, OnPatches, OnComponents, OnBaseline, OnResumed, OnSavedPins
            };

            private static readonly FieldInfo PinsField = AccessTools.Field(typeof(Minimap), "m_pins");

            private static readonly FieldInfo VisibleIconTypesField =
                AccessTools.Field(typeof(Minimap), "m_visibleIconTypes");

            // The static scan's pass counter, which advances once per scan pass and is reset to zero
            // with the rest of the session when the module is turned off.
            private static readonly FieldInfo StaticPassField =
                AccessTools.Field(typeof(StaticObjectMapping), "_currentPassId");

            private readonly Dictionary<string, CheckResult> _settled =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly Dictionary<string, int> _passes = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _passNotes = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly StateWait _wait = new StateWait();
            private readonly ErrorObserver _errors;
            private readonly Func<string, bool> _observerHandBack;
            private readonly string _runner;

            private ConfigEntry<AutomaticsModule> _toggle;
            private string _moduleName;
            private string _harmonyId;
            private bool _toggleOwned;
            private bool _proceed;
            private int _cycle;
            private Baseline _baseline;
            private string _notReached = "not reached: an earlier step stopped the run";

            public MappingRun(int runId, string runner)
            {
                RunId = runId;
                _runner = runner;
                _errors = new ErrorObserver("AutomaticsObserverProbe-mapping", runner);

                // The observer goes back with the session too, however the run ends.
                _observerHandBack = _ =>
                {
                    _errors.Stop();
                    return true;
                };
            }

            private int RunId { get; }

            public IReadOnlyList<CheckResult> Results
            {
                get
                {
                    var results = new List<CheckResult>();
                    foreach (var name in Order)
                        if (_settled.TryGetValue(name, out var result))
                            results.Add(result);
                        else if (PerCycle.Contains(name) && Passes(name) == MappingCycles)
                            results.Add(new CheckResult(CheckStatus.Pass, name,
                                string.IsNullOrEmpty(_passNotes[name])
                                    ? $"all {MappingCycles} cycles"
                                    : $"all {MappingCycles} cycles; {_passNotes[name]}"));
                        else
                            results.Add(new CheckResult(CheckStatus.Skip, name, _notReached));
                    return results;
                }
            }

            public IEnumerator Execute()
            {
                if (ZNetScene.instance == null || !Player.m_localPlayer || !Minimap.instance)
                {
                    SkipAll("no world is loaded (ZNetScene, the local player or the minimap is missing)");
                    yield break;
                }

                _toggle = MappingConfig.ModuleEntry;
                if (_toggle == null)
                {
                    Settle(CheckStatus.Fail, Settled, "the automatic_mapping module toggle is not bound");
                    yield break;
                }

                _moduleName = MappingModule.Instance.Name;
                _harmonyId = global::Automatics.Automatics.GetHarmonyId(_moduleName);

                // The check only ever turns Automatic Mapping off and back on. Turning it on for
                // the user would add and remove pins on their map, so when it is off it stays off.
                if (_toggle.Value != AutomaticsModule.Enabled)
                {
                    SkipAll(
                        "Automatic Mapping is off in your settings, so the check changed nothing. It only ever turns the module off and back on; turn it on yourself if you want this check, then run it again.");
                    yield break;
                }

                // From here on any write to the setting that is not the check's own is the user
                // taking it back.
                if (!SmokeSession.BeginToggle(RunId, _toggle, out var refusal))
                {
                    SkipAll("inconclusive: " + refusal);
                    yield break;
                }

                _toggleOwned = true;
                SmokeSession.TrackHandBack(_observerHandBack);
                _errors.Start();

                // The registry has to agree with the setting before anything is asked of it, or a
                // transition still pending from before the command would be read as this one's.
                var settle = Await(ModuleState.Enabled, true, true);
                while (settle.MoveNext()) yield return settle.Current;
                if (!RecordWait(Settled, 0, "nothing was changed")) yield break;

                // Taken once the component counts agree, for the reason given at
                // ComponentSettleFrames.
                var components = CountComponents();
                for (var frame = 1; frame < ComponentSettleFrames && !ComponentsMatch(components); frame++)
                {
                    yield return null;
                    components = CountComponents();
                }

                _baseline = TakeBaseline(components);

                for (_cycle = 1; _cycle <= MappingCycles; _cycle++)
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
                            Settle(CheckStatus.Skip, Restored, "nothing was written, so there was nothing to put back");
                            break;
                        case SmokeSession.ToggleOutcome.LeftForUser:
                            Settle(CheckStatus.Skip, Restored, "restoration skipped: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.NotThisRun:
                            Settle(CheckStatus.Skip, Restored, "inconclusive: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.Failed:
                            Settle(CheckStatus.Fail, Restored, detail);
                            break;
                        default:
                            // A fault is not final here: putting the value back is also what asks
                            // the registry to recover, so the wait gives it the full bound.
                            var wait = Await(ModuleState.Enabled, false, false, AutomaticsModule.Enabled);
                            while (wait.MoveNext()) yield return wait.Current;
                            RecordWait(Restored, 0, detail);
                            break;
                    }
                }

                if (_errors.Started) JudgeLog();
                SmokeSession.ReleaseHandBack(_observerHandBack);
            }

            // The step that was running fails; the ones after it were not reached.
            public void Stopped(string message)
            {
                _notReached = "not reached: the run stopped on " + message;
                foreach (var name in Order)
                {
                    if (_settled.ContainsKey(name)) continue;
                    if (PerCycle.Contains(name) && Passes(name) >= _cycle) continue;

                    var prefix = PerCycle.Contains(name) ? $"cycle {_cycle}: " : "";
                    Settle(CheckStatus.Fail, name, prefix + "the run stopped on " + message);
                    return;
                }
            }

            private IEnumerator TurnOff(int cycle)
            {
                _proceed = false;

                // What the module holds unsaved now is what has to be off the map once it is off.
                var held = HeldPins().Where(pin => !pin.m_save).ToList();

                // Read before the write, with no frame in between, so no scan can run between the
                // two. From the write on, the stop request alone has to hold the scan until the
                // pump reports the module disabled.
                var passAtRequest = StaticPassCount();
                var passSeen = passAtRequest;
                if (!Write(AutomaticsModule.Disabled, OffState, cycle)) yield break;

                // The write itself raises the stop request, a frame before the pump takes anything
                // apart: the module is still applied but no longer running, and every entry point
                // opens with that predicate. The wait below cannot see the frame before the pump -
                // a coroutine runs after Update, where the pump runs - so this is what shows the stop
                // takes hold at the request.
                var appliedAtRequest = MappingModule.Active;
                var runningAtRequest = MappingModule.Running;

                var wait = Await(ModuleState.Disabled, true, true, null, () =>
                {
                    if (ModuleRegistry.GetState(_moduleName) == ModuleState.Disabled) return;
                    var now = StaticPassCount();
                    if (now != passAtRequest) passSeen = now;
                });
                while (wait.MoveNext()) yield return wait.Current;
                if (!RecordWait(OffState, cycle)) yield break;

                // Object.Destroy takes effect at the end of the frame the pump ran in.
                for (var i = 0; i < DestroySettleFrames; i++) yield return null;

                if (!passAtRequest.HasValue)
                    Cycle(OffStopped, cycle, false, "",
                        "the static scan's pass counter (StaticObjectMapping._currentPassId) could not be read");
                else
                    Cycle(OffStopped, cycle,
                        appliedAtRequest && !runningAtRequest && passSeen == passAtRequest,
                        $"right after the disable request the module was still applied but no longer running, and the static scan's pass counter stayed at {passAtRequest} until the registry reported Disabled",
                        $"right after the disable request the module was {(appliedAtRequest ? "applied" : "not applied")} and {(runningAtRequest ? "still running" : "not running")}, and the static scan's pass counter went from {passAtRequest} to {passSeen} before the registry reported Disabled (R1.5a)");

                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    CycleSkip(OffPatches, cycle,
                        "this Harmony build cannot remove patches at runtime; they stay applied by design (R3.2)");
                }
                else
                {
                    var left = PatchesUnder(_harmonyId, out var total);
                    Cycle(OffPatches, cycle, total == 0, $"no patch is left under \"{_harmonyId}\"",
                        $"{total} patch(es) are still under \"{_harmonyId}\" after the disable: [{string.Join(", ", left.OrderBy(x => x).Take(5))}] (AC-R3)");
                }

                var kinds = CountComponents();
                var overlays = CountNavigationOverlays();
                Cycle(OffComponents, cycle, kinds.Sum(x => x.Total) == 0 && overlays == 0,
                    "no FishCache, BirdCache, ShipCache, MineRock5CacheBinding, FloraNode or navigation overlay is left in the scene",
                    $"left in the scene: {string.Join(", ", kinds.Select(x => $"{x.Total} {x.Name}"))}, {overlays} navigation overlay(s) (AC-R4)");

                var saved = SavedPins(_baseline.VehiclePins);
                Cycle(OffSavedPins, cycle, saved.SequenceEqual(_baseline.SavedPins),
                    $"the {saved.Count} saved pin(s) are the ones there were before the run",
                    DescribeDifference(_baseline.SavedPins, saved));

                if (held.Count == 0)
                {
                    CycleSkip(OffUnsavedPins, cycle,
                        "the module held no unsaved pin before the disable, so there was nothing to see removed");
                }
                else
                {
                    var onMap = new HashSet<Minimap.PinData>(AllPins());
                    var remaining = held.Count(onMap.Contains);
                    Cycle(OffUnsavedPins, cycle, remaining == 0,
                        $"none of the {held.Count} unsaved pin(s) the module held before the disable is on the map",
                        $"{remaining} of the {held.Count} unsaved pin(s) the module held before the disable are still on the map");
                }

                _proceed = true;
            }

            private IEnumerator TurnOn(int cycle)
            {
                _proceed = false;

                if (!Write(AutomaticsModule.Enabled, OnState, cycle)) yield break;
                var wait = Await(ModuleState.Enabled, true, true);
                while (wait.MoveNext()) yield return wait.Current;
                if (!RecordWait(OnState, cycle)) yield break;
                var enabledAt = Time.realtimeSinceStartup;

                CheckPatchSet(cycle);

                // The scan in Enable attaches synchronously, so the components are all there now,
                // up to the same-frame timing described at ComponentSettleFrames.
                var kinds = CountComponents();
                var differences = BaselineDifferences(kinds);
                for (var frame = 1;
                     frame < ComponentSettleFrames && !(ComponentsMatch(kinds) && differences.Count == 0);
                     frame++)
                {
                    yield return null;
                    kinds = CountComponents();
                    differences = BaselineDifferences(kinds);
                }

                var generation = MappingModule.Generation;
                var attached = string.Join(", ",
                    kinds.Select(x => $"{x.Name} {x.Attached}/{x.Eligible} ({x.Total} in total)"));
                if (kinds.Sum(x => x.Eligible) == 0)
                    CycleSkip(OnComponents, cycle,
                        "inconclusive: the scene holds no fish, bird, ship, MineRock5 or configured flora for the module to attach to, so an empty scene could not be told from a missed one");
                else
                    Cycle(OnComponents, cycle, ComponentsMatch(kinds),
                        $"every eligible object carries its component from generation {generation}: {attached}",
                        $"the components do not match the eligible objects (from generation {generation} / eligible): {attached} (AC-R5)");

                Cycle(OnBaseline, cycle, differences.Count == 0,
                    "the handler counts, the component counts and the minimap's icon tables are what they were before the run",
                    $"not what they were before the run: {string.Join("; ", differences)} (AC-R6)");

                var resume = WatchResume(cycle);
                while (resume.MoveNext()) yield return resume.Current;

                // Compared once the scans have had time to place every pin again, so a pin they
                // duplicated would show (see PinSettleSeconds).
                while (Time.realtimeSinceStartup - enabledAt < PinSettleSeconds && SmokeSession.IsCurrent(RunId))
                    yield return null;

                var saved = SavedPins(_baseline.VehiclePins);
                Cycle(OnSavedPins, cycle, saved.SequenceEqual(_baseline.SavedPins),
                    $"the {saved.Count} saved pin(s) are the ones there were before the run",
                    DescribeDifference(_baseline.SavedPins, saved));

                _proceed = true;
            }

            // The static scan's counter was reset with the session, so a pass counted after the
            // enable shows the scan runs again. When the static scan held pins before the run, it
            // has to hold one again too. Static pins only: a creature whose pin the module held can
            // have wandered out of range by now.
            private IEnumerator WatchResume(int cycle)
            {
                var precondition = StaticScanPrecondition();
                if (precondition != null)
                {
                    CycleSkip(OnResumed, cycle, precondition);
                    yield break;
                }

                var started = Time.realtimeSinceStartup;
                while (true)
                {
                    if (!SmokeSession.IsCurrent(RunId))
                    {
                        CycleSkip(OnResumed, cycle,
                            "inconclusive: the run was abandoned (the smoke plugin is shutting down)");
                        yield break;
                    }

                    if (ZNetScene.instance == null || !Player.m_localPlayer)
                    {
                        CycleSkip(OnResumed, cycle, "inconclusive: the world was unloaded while the check ran");
                        yield break;
                    }

                    var passes = StaticPassCount();
                    var held = StaticHeldCount();
                    var elapsed = Time.realtimeSinceStartup - started;
                    if (passes.HasValue && passes.Value > 0 && (_baseline.StaticHeld == 0 || held > 0))
                    {
                        Cycle(OnResumed, cycle, true,
                            $"{elapsed:0.0} s after the enable the static scan had made {passes} pass(es) and held {held} pin(s)",
                            "");
                        yield break;
                    }

                    if (elapsed >= MappingResumeTimeoutSeconds)
                    {
                        var counted = passes.HasValue
                            ? passes.Value.ToString(CultureInfo.InvariantCulture)
                            : "an unreadable number of";
                        Cycle(OnResumed, cycle, false, "",
                            $"{elapsed:0.0} s after the enable the static scan had made {counted} pass(es) and held {held} pin(s), against {_baseline.StaticHeld} before the run");
                        yield break;
                    }

                    yield return null;
                }
            }

            // The patches under the module's Harmony id have to be exactly the ones its three
            // patch classes declare, compared as (target, patch method) pairs and counted as
            // patches, so a pair applied twice does not hide.
            private void CheckPatchSet(int cycle)
            {
                var expected = ExpectedPatches(typeof(MappingPatches),
                    typeof(MappingPatches.RandomFlyingBird_Initialize_Patch),
                    typeof(MappingPatches.Minimap_OnDestroy_Patch));
                if (expected.Count == 0)
                {
                    Cycle(OnPatches, cycle, false, "",
                        "no [HarmonyPatch] target could be read from the Automatics.AutomaticMapping patch classes");
                    return;
                }

                var actual = PatchesUnder(_harmonyId, out var total);
                var missing = expected.Where(x => !actual.Contains(x)).OrderBy(x => x).ToArray();
                var unexpected = actual.Where(x => !expected.Contains(x)).OrderBy(x => x).ToArray();
                Cycle(OnPatches, cycle,
                    missing.Length == 0 && unexpected.Length == 0 && total == expected.Count,
                    $"{total} patches under \"{_harmonyId}\", exactly the pairs the three Automatics.AutomaticMapping patch classes declare",
                    $"the patches under \"{_harmonyId}\" differ from the three Automatics.AutomaticMapping patch classes: {total} patch(es) where they declare {expected.Count}; missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", unexpected)}]");
            }

            private List<string> BaselineDifferences(List<Kind> kinds)
            {
                var differences = new List<string>();
                foreach (var pair in HandlerCounts())
                {
                    _baseline.Handlers.TryGetValue(pair.Key, out var before);
                    if (pair.Value < 0 || before < 0)
                        differences.Add($"{pair.Key} could not be read");
                    else if (pair.Value != before)
                        differences.Add($"{pair.Key} has {pair.Value} handler(s), {before} before");
                }

                for (var i = 0; i < kinds.Count; i++)
                {
                    var now = kinds[i];
                    var before = _baseline.Components[i];
                    if (now.Total - now.Eligible != before.Total - before.Eligible)
                        differences.Add(
                            $"{now.Name} is on {now.Total} object(s) for {now.Eligible} eligible, {before.Total} for {before.Eligible} before");
                }

                var icons = IconCount();
                var iconTypes = IconTypeCount();
                if (icons < 0 || icons != _baseline.Icons)
                    differences.Add($"Minimap.m_icons has {icons} entries, {_baseline.Icons} before");
                if (iconTypes < 0 || iconTypes != _baseline.IconTypes)
                    differences.Add($"Minimap.m_visibleIconTypes has {iconTypes} entries, {_baseline.IconTypes} before");

                return differences;
            }

            private void JudgeLog()
            {
                _errors.Stop();
                var unexpected = _errors.Unexpected;
                if (unexpected.Count > 0)
                {
                    foreach (var entry in unexpected.Take(10))
                        Log.LogWarning($"[Automatics Smoke] {_runner}: logged while the check ran: {entry}");
                    Settle(CheckStatus.Fail, NoErrors,
                        $"{unexpected.Count} error(s) or exception(s) were logged while the check ran (the full text is in the warnings above); the first: {FirstLine(unexpected[0])}");
                }
                else if (_errors.Probes != 1)
                {
                    Settle(CheckStatus.Skip, NoErrors,
                        $"the log observer saw its own probe {_errors.Probes} time(s) instead of once, so it cannot vouch for an empty log");
                }
                else
                {
                    Settle(CheckStatus.Pass, NoErrors,
                        "no error or exception was logged while the check ran (the observer saw its own probe once)");
                }
            }

            // -- Recording ---------------------------------------------------------------------

            private IEnumerator Await(ModuleState expected, bool faultIsFinal, bool worldRequired,
                AutomaticsModule? heldValue = null, Action onFrame = null)
            {
                return AwaitState(RunId, _toggle, _moduleName, expected, faultIsFinal, worldRequired,
                    _wait, heldValue, onFrame);
            }

            // The user's path: a config write raises SettingChanged, which the registry turns into
            // a request. SmokeSession refuses once the user has changed the setting.
            private bool Write(AutomaticsModule value, string name, int cycle)
            {
                if (SmokeSession.WriteToggle(RunId, value, out var refusal)) return true;

                CycleSkip(name, cycle, "inconclusive: " + refusal + "; the run stopped");
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
                        if (cycle == 0) Settle(CheckStatus.Pass, name, note);
                        else Cycle(name, cycle, true, note, "");
                        return true;
                    case WaitResult.Faulted:
                    case WaitResult.TimedOut:
                        if (cycle == 0) Settle(CheckStatus.Fail, name, _wait.Report + suffix);
                        else Cycle(name, cycle, false, "", _wait.Report + suffix);
                        return false;
                    default:
                        if (cycle == 0) Settle(CheckStatus.Skip, name, "inconclusive: " + _wait.Report + suffix);
                        else CycleSkip(name, cycle, "inconclusive: " + _wait.Report + suffix);
                        return false;
                }
            }

            private void Settle(CheckStatus status, string name, string message)
            {
                if (!_settled.ContainsKey(name)) _settled[name] = new CheckResult(status, name, message);
            }

            // The first cycle that does not pass decides the check.
            private void Cycle(string name, int cycle, bool passed, string pass, string failure)
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

            private void CycleSkip(string name, int cycle, string reason)
            {
                Settle(CheckStatus.Skip, name, $"cycle {cycle}: {reason}");
            }

            private int Passes(string name)
            {
                return _passes.TryGetValue(name, out var passes) ? passes : 0;
            }

            private void SkipAll(string message)
            {
                foreach (var name in Order) Settle(CheckStatus.Skip, name, message);
            }

            // -- What is observed ------------------------------------------------------------------

            private static Baseline TakeBaseline(List<Kind> components)
            {
                var vehicles = new HashSet<Minimap.PinData>(
                    AllPins().Where(pin => pin != null && pin.m_save && DynamicObjectMapping.OwnsPin(pin)));
                return new Baseline(HandlerCounts(), components, IconCount(), IconTypeCount(),
                    vehicles, SavedPins(vehicles), StaticHeldCount());
            }

            // Every kind is on exactly its eligible objects, all from the running generation.
            private static bool ComponentsMatch(List<Kind> kinds)
            {
                return kinds.All(x => x.Attached == x.Eligible && x.Total == x.Eligible);
            }

            private static int StaticHeldCount()
            {
                return AllPins().Count(pin => pin != null && StaticObjectMapping.OwnsPin(pin));
            }

            private static List<Minimap.PinData> AllPins()
            {
                var map = Minimap.instance;
                var pins = map && PinsField != null ? PinsField.GetValue(map) as List<Minimap.PinData> : null;
                return pins ?? new List<Minimap.PinData>();
            }

            // The pins either scan holds, saved or not.
            private static List<Minimap.PinData> HeldPins()
            {
                return AllPins().Where(pin =>
                    pin != null && (DynamicObjectMapping.OwnsPin(pin) || StaticObjectMapping.OwnsPin(pin))).ToList();
            }

            // Every saved pin as the game saves it: type, name, position to 0.1 m, owner and checked
            // flag. A vehicle pin's position is left out, because it follows its vehicle.
            private static List<string> SavedPins(HashSet<Minimap.PinData> vehicles)
            {
                var fingerprint = new List<string>();
                foreach (var pin in AllPins())
                {
                    if (pin == null || !pin.m_save) continue;

                    var position = vehicles.Contains(pin)
                        ? "vehicle"
                        : string.Format(CultureInfo.InvariantCulture, "({0:0.0}, {1:0.0}, {2:0.0})",
                            pin.m_pos.x, pin.m_pos.y, pin.m_pos.z);
                    fingerprint.Add($"{pin.m_type}|{pin.m_name}|{position}|{pin.m_ownerID}|{pin.m_checked}");
                }

                fingerprint.Sort(StringComparer.Ordinal);
                return fingerprint;
            }

            private static string DescribeDifference(List<string> before, List<string> now)
            {
                var missing = Subtract(before, now);
                var added = Subtract(now, before);
                return
                    $"the saved pins are not the ones there were before the run ({before.Count} then, {now.Count} now): gone [{string.Join("; ", missing.Take(3))}], new [{string.Join("; ", added.Take(3))}] (AC-R12)";
            }

            private static List<string> Subtract(List<string> from, List<string> take)
            {
                var rest = new List<string>(take);
                var result = new List<string>();
                foreach (var item in from)
                    if (!rest.Remove(item))
                        result.Add(item);
                return result;
            }

            private static Dictionary<string, int> HandlerCounts()
            {
                return new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["Hooks.OnPlayerAwake"] = Invocations(Hooks.OnPlayerAwake),
                    ["Hooks.OnPlayerUpdate"] = Invocations(Hooks.OnPlayerUpdate),
                    ["Hooks.OnPlayerFixedUpdate"] = Invocations(Hooks.OnPlayerFixedUpdate),
                    ["PickableCache.OnCacheAdded"] = Invocations(PickableCache.OnCacheAdded),
                    ["ShipCache.OnCacheAdded"] = Invocations(ShipCache.OnCacheAdded),
                    ["ValheimObject.RegistryChanged"] = EventInvocations(typeof(ValheimObject), "RegistryChanged"),
                    ["Config.StaticAllowlistChanged"] = EventInvocations(typeof(MappingConfig), "StaticAllowlistChanged")
                };
            }

            private static int Invocations(Delegate handlers)
            {
                return handlers == null ? 0 : handlers.GetInvocationList().Length;
            }

            // An event is read through the field behind it; -1 when there is no such field.
            private static int EventInvocations(Type type, string name)
            {
                var field = AccessTools.Field(type, name);
                return field == null ? -1 : Invocations(field.GetValue(null) as Delegate);
            }

            // Each kind of component the module attaches, with the objects it attaches to by the
            // postfixes' own rules: fish, birds, ships and MineRock5 with a ZDO, and every pickable
            // in the root cache whose name is a configured flora.
            private static List<Kind> CountComponents()
            {
                var generation = MappingModule.Generation;
                return new List<Kind>
                {
                    Count<FishCache>(generation, WithZdo(FindAll<Fish>())),
                    Count<BirdCache>(generation, WithZdo(FindAll<RandomFlyingBird>())),
                    Count<ShipCache>(generation, WithZdo(FindAll<Ship>())),
                    Count<MineRock5CacheBinding>(generation, WithZdo(FindAll<MineRock5>())),
                    Count<FloraNode>(generation, PickableCache.GetAllInstance()
                        .Count(x => x && ValheimObject.Flora.IsDefined(Objects.GetName(x))))
                };
            }

            private static Kind Count<T>(int generation, int eligible) where T : Component, IScopedComponent
            {
                var found = FindAll<T>();
                var attached = generation == 0 ? 0 : found.Count(x => x.ScopeGeneration == generation);
                return new Kind(typeof(T).Name, found.Length, attached, eligible);
            }

            private static T[] FindAll<T>() where T : Object
            {
                return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            }

            private static int WithZdo(IEnumerable<Component> components)
            {
                return components.Count(x => Objects.GetZNetView(x, out var zNetView) && zNetView.GetZDO() != null);
            }

            private static int CountNavigationOverlays()
            {
                return FindAll<RectTransform>().Count(x => x.name == NavigationOverlayName);
            }

            private static int IconCount()
            {
                var map = Minimap.instance;
                return map ? map.m_icons.Count : -1;
            }

            private static int IconTypeCount()
            {
                var map = Minimap.instance;
                var types = map && VisibleIconTypesField != null
                    ? VisibleIconTypesField.GetValue(map) as bool[]
                    : null;
                return types?.Length ?? -1;
            }

            private static int? StaticPassCount()
            {
                return StaticPassField?.GetValue(null) as int?;
            }

            // Null when the static scan runs on its own with the user's settings, or why it does not.
            private static string StaticScanPrecondition()
            {
                if (!MappingConfig.EnableAutomaticMapping)
                    return "enable_automatic_mapping is off, so the scans stop before they run and their resume cannot be seen";
                if (MappingConfig.StaticObjectMappingRange <= 0)
                    return "static_object_mapping_range is 0, so the static scan does not run";
                if (MappingConfig.StaticObjectMappingKey.MainKey != KeyCode.None)
                    return "static_object_mapping_key is set, so the static scan runs only when that key is pressed";
                if (MappingConfig.StaticObjectMappingInterval <= 0)
                    return "static_object_mapping_interval is 0, so the static scan does not run on its own";
                return null;
            }

            private static string FirstLine(string text)
            {
                var end = text.IndexOf('\n');
                return end < 0 ? text : text.Substring(0, end);
            }

            private sealed class Kind
            {
                public Kind(string name, int total, int attached, int eligible)
                {
                    Name = name;
                    Total = total;
                    Attached = attached;
                    Eligible = eligible;
                }

                public string Name { get; }
                public int Total { get; }
                public int Attached { get; }
                public int Eligible { get; }
            }

            private sealed class Baseline
            {
                public Baseline(Dictionary<string, int> handlers, List<Kind> components, int icons,
                    int iconTypes, HashSet<Minimap.PinData> vehiclePins, List<string> savedPins,
                    int staticHeld)
                {
                    Handlers = handlers;
                    Components = components;
                    Icons = icons;
                    IconTypes = iconTypes;
                    VehiclePins = vehiclePins;
                    SavedPins = savedPins;
                    StaticHeld = staticHeld;
                }

                public Dictionary<string, int> Handlers { get; }
                public List<Kind> Components { get; }
                public int Icons { get; }
                public int IconTypes { get; }
                public HashSet<Minimap.PinData> VehiclePins { get; }
                public List<string> SavedPins { get; }
                public int StaticHeld { get; }
            }
        }
    }
}
