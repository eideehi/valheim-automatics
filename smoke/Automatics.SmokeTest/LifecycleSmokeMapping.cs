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
using AimedObjectBar = Automatics.AutomaticMapping.AimedObjectBar;
using Compass = Automatics.AutomaticMapping.Compass;
using Navigation = Automatics.AutomaticMapping.Navigation;
using NavigationGuide = Automatics.AutomaticMapping.NavigationGuide;
using MappingMap = Automatics.AutomaticMapping.Map;
using MapFilter = Automatics.AutomaticMapping.MapFilter;
using BirdCache = Automatics.AutomaticMapping.BirdCache;
using DynamicObjectMapping = Automatics.AutomaticMapping.DynamicObjectMapping;
using FishCache = Automatics.AutomaticMapping.FishCache;
using FloraNode = Automatics.AutomaticMapping.FloraNode;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;
using MappingPatches = Automatics.AutomaticMapping.Patches;
using MappingScans = Automatics.AutomaticMapping.AutomaticMapping;
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
    //     navigation overlay, aimed label, filter row or guide swarm is left in the scene, the saved pins
    //     are the ones there were before the run, and no unsaved pin the module held is left on the map;
    //   - the compass, seen on the small map before each disable, is gone from the frame of the
    //     stop request on, and turning its option off alone removes it while the module runs;
    //   - while on, the patches are exactly the declared set, every eligible object in the scene
    //     carries its component from the running generation, the handler counts, component counts
    //     and minimap icon tables are what they were before the run, the scans run again, the
    //     saved pins are still the ones there were before the run, and those recognized as
    //     automatic before the run are recognized again.
    // A vehicle pin is a saved pin that follows its vehicle, and a disable moves it to where its
    // vehicle is, so the saved-pin comparison leaves the position of the vehicle pins out.
    //
    // It spawns no world object and records the
    // module toggle, aimed-bar, compass and filter settings through SmokeSession. It only ever turns
    // Automatic Mapping off and back on: when the module is
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
        private const string AimedOverlayName = "AutomaticsAimedObjectOverlay";
        private const string NavigationGuideName = "AutomaticsNavigationGuide";

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
            private const string InitialAwakeHandlers = "mapping_initial_player_awake_handlers_none";
            private const string OffAwakeHandlers = "mapping_off_player_awake_handlers_none";
            private const string OnAwakeHandlers = "mapping_on_player_awake_handlers_none";
            private const string OffState = "mapping_off_state_disabled";
            private const string OffStopped = "mapping_off_stopped_at_request";
            private const string OffPatches = "mapping_off_patches_removed";
            private const string OffComponents = "mapping_off_components_removed";
            private const string OffHeldSwarm = "mapping_off_held_swarm_removed";
            private const string OffCompass = "mapping_off_compass_removed";
            private const string OffFilterControls = "mapping_off_filter_controls_removed";
            private const string OffTooltip = "mapping_off_tooltip_removed";
            private const string OffFilterSearch = "mapping_off_filter_search_reset";
            private const string OffSavedPins = "mapping_off_saved_pins_kept";
            private const string OffUnsavedPins = "mapping_off_unsaved_pins_removed";
            private const string OnState = "mapping_on_state_enabled";
            private const string OnFilterSearch = "mapping_on_filter_search_empty";
            private const string OnCompassOption = "mapping_on_compass_option_off_removed";
            private const string OnPatches = "mapping_on_patches_applied";
            private const string OnComponents = "mapping_on_components_attached";
            private const string OnBaseline = "mapping_on_counts_at_baseline";
            private const string OnResumed = "mapping_on_scans_resumed";
            private const string OnSavedPins = "mapping_on_saved_pins_kept";
            private const string OnRecognizedPins = "mapping_on_saved_automatic_pins_recognized";
            private const string Restored = "mapping_restored_initial_state";
            private const string NoErrors = "mapping_no_errors_logged";

            // The order the checks are recorded in within a cycle, which is also the order they
            // are reported in.
            private static readonly string[] Order =
            {
                Settled, InitialAwakeHandlers, OffState, OffStopped, OffPatches, OffAwakeHandlers, OffComponents, OffHeldSwarm, OffCompass, OffFilterControls, OffFilterSearch,
                OffTooltip, OffSavedPins, OffUnsavedPins,
                OnState, OnFilterSearch, OnPatches, OnAwakeHandlers, OnComponents, OnBaseline, OnResumed, OnSavedPins,
                OnRecognizedPins, OnCompassOption,
                Restored, NoErrors
            };

            // Recorded once per cycle, and a PASS only when every cycle passed.
            private static readonly HashSet<string> PerCycle = new HashSet<string>(StringComparer.Ordinal)
            {
                OffState, OffStopped, OffPatches, OffAwakeHandlers, OffComponents, OffCompass, OffFilterControls,
                OffFilterSearch, OffTooltip, OffSavedPins, OffUnsavedPins,
                OnState, OnFilterSearch, OnPatches, OnAwakeHandlers, OnComponents, OnBaseline, OnResumed, OnSavedPins,
                OnRecognizedPins, OnCompassOption
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
            private Navigation.TargetState _originalNavigation;
            private string _originalSearch;
            private bool _originalSearchPinned;
            private HashSet<Minimap.PinData> _pinsBefore;
            private Func<string, bool> _navigationHandBack;
            private Func<string, bool> _filterHandBack;
            private SmokeSession.SettingRestoration _compassSetting;
            private string _notReached = "not reached: an earlier step stopped the run";

            // A guide swarm held as a map click that clears navigation holds it, made once in the
            // run just before a disable request, for that disable to remove too.
            private bool _heldSwarmTried;

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
                var initialAwake = MappingAwakeHandlers();
                Settle(initialAwake.Count == 0 ? CheckStatus.Pass : CheckStatus.Fail, InitialAwakeHandlers,
                    initialAwake.Count == 0
                        ? "before the run Automatic Mapping declares no handler on Hooks.OnPlayerAwake or Hooks.OnLocalPlayerAwake"
                        : $"before the run Automatic Mapping declares handler(s) on the player-awake hooks: {string.Join(", ", initialAwake)}");
                _pinsBefore = new HashSet<Minimap.PinData>(AllPins());
                _originalNavigation = Navigation.CaptureTarget();
                _originalSearch = MapFilter.SearchText;
                _originalSearchPinned = MapFilter.SearchPinned;
                _navigationHandBack = _ =>
                {
                    if (!MappingModule.Running || !Minimap.instance) return false;
                    Navigation.RestoreTarget(_originalNavigation);
                    if (MappingConfig.EnableMapEnhancements && (_originalSearch.Length != 0 || _originalSearchPinned))
                    {
                        var map = Minimap.instance;
                        var mode = map.m_mode;
                        map.SetMapMode(Minimap.MapMode.Large);
                        MapFilter.SetSearchText(_originalSearch);
                        if (MapFilter.SearchPinned != _originalSearchPinned) MapFilter.SearchToggle.InvokeClick();
                        map.SetMapMode(mode);
                    }
                    return true;
                };
                SmokeSession.TrackHandBack(_navigationHandBack);

                if (!SmokeSession.BeginSetting(RunId, MappingConfig.EnableAimedObjectBarEntry,
                        out var aimedEnabled, out var aimedRefusal) ||
                    !SmokeSession.BeginSetting(RunId, MappingConfig.AimedObjectBarRangeEntry,
                        out var aimedRange, out aimedRefusal) ||
                    !SmokeSession.WriteSetting(RunId, aimedEnabled, true, out aimedRefusal) ||
                    !SmokeSession.WriteSetting(RunId, aimedRange, 50f, out aimedRefusal))
                {
                    Stopped("aimed-label setting precondition failed: " + aimedRefusal);
                    yield break;
                }

                if (!SmokeSession.BeginSetting(RunId, MappingConfig.EnableCompassEntry,
                        out _compassSetting, out var compassRefusal) ||
                    !SmokeSession.WriteSetting(RunId, _compassSetting, true, out compassRefusal))
                {
                    Stopped("compass setting precondition failed: " + compassRefusal);
                    yield break;
                }

                foreach (var pair in new[]
                {
                    new KeyValuePair<ConfigEntryBase, object>(MappingConfig.EnableMapEnhancementsEntry, true),
                    new KeyValuePair<ConfigEntryBase, object>(MappingConfig.MapFilterHiddenCategoriesEntry, new StringList()),
                    new KeyValuePair<ConfigEntryBase, object>(MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false)
                })
                {
                    if (!SmokeSession.BeginSetting(RunId, pair.Key, out var setting, out var filterRefusal) ||
                        !SmokeSession.WriteSetting(RunId, setting, pair.Value, out filterRefusal))
                    {
                        Stopped("map-filter setting precondition failed: " + filterRefusal);
                        yield break;
                    }
                }

                for (_cycle = 1; _cycle <= MappingCycles; _cycle++)
                {
                    var off = TurnOff(_cycle);
                    while (off.MoveNext()) yield return off.Current;
                    if (!_proceed) yield break;

                    var on = TurnOn(_cycle);
                    while (on.MoveNext()) yield return on.Current;
                    if (!_proceed) yield break;
                }

                Settle(CheckStatus.Skip, OffHeldSwarm,
                    $"no guide swarm was shown just before any of the {MappingCycles} disable requests (for example enable_navigation_guide is off, or the looked-at label found no object to show for), so no held swarm was made");
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

                if (_navigationHandBack != null && _navigationHandBack("mapping lifecycle smoke"))
                    SmokeSession.ReleaseHandBack(_navigationHandBack);
                if (_filterHandBack != null && _filterHandBack("mapping lifecycle smoke"))
                    SmokeSession.ReleaseHandBack(_filterHandBack);
                AimedObjectBar.Cleanup();
                var aimedRestored = SmokeSession.RestoreSettings(RunId);
                var aimedFailures = aimedRestored.Where(x => x.Key != SmokeSession.ToggleOutcome.Restored &&
                    x.Key != SmokeSession.ToggleOutcome.AlreadyOriginal &&
                    x.Key != SmokeSession.ToggleOutcome.NothingWritten).ToList();
                if (aimedFailures.Count > 0)
                    Settle(CheckStatus.Fail, Restored, string.Join("; ", aimedFailures.Select(x => x.Value)));
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
                    // Made once in the run, so a stop elsewhere leaves it not reached.
                    if (name == OffHeldSwarm) continue;

                    var prefix = PerCycle.Contains(name) ? $"cycle {_cycle}: " : "";
                    Settle(CheckStatus.Fail, name, prefix + "the run stopped on " + message);
                    return;
                }
            }

            private IEnumerator TurnOff(int cycle)
            {
                _proceed = false;

                Navigation.RestoreTarget(new Navigation.TargetState
                {
                    Pin = new Minimap.PinData { m_type = Minimap.PinType.Icon3, m_name = "" },
                    Temporary = true,
                    LastPosition = Player.m_localPlayer.transform.position + Vector3.forward * 60f,
                    CreatureId = ZDOID.None,
                    AimedId = ZDOID.None
                });
                var aimedActive = AimBarFeatureSmoke.ActivateExistingLabel(Player.m_localPlayer, out var aimedPrecondition);
                // The compass shows only on the small map, so it is seen before the large map opens.
                Compass.Update(Player.m_localPlayer);
                var compassShown = Compass.Root && Compass.Root.activeInHierarchy;
                var marker = Navigation.TemporaryMarker;
                var markerActive = marker != null && !marker.m_save && AllPins().Contains(marker);

                var map = Minimap.instance;
                var originalMode = map.m_mode;
                var originalCenter = AccessTools.Method(typeof(Minimap), "GetViewCenterWorldPoint").Invoke(map, null);
                _filterHandBack = _ =>
                {
                    if (!map || Minimap.instance != map) return true;
                    AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new[] { originalCenter });
                    map.SetMapMode(originalMode);
                    MappingMap.RefreshPins();
                    return true;
                };
                SmokeSession.TrackHandBack(_filterHandBack);
                map.SetMapMode(Minimap.MapMode.Large);
                AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases")
                    .Invoke(null, null);
                MappingMap.RefreshPins();
                var controlsActive = MapFilter.Root && MapFilter.Root.activeInHierarchy && MapFilter.Controls.Count == 10 &&
                    MapFilter.SearchInput && MapFilter.SearchToggle != null;
                MapFilter.SetSearchText("lifecycle search");
                if (!MapFilter.SearchPinned) MapFilter.SearchToggle.InvokeClick();
                var searchActive = MapFilter.SearchText == "lifecycle search" && MapFilter.SearchPinned;
                var tooltipActive = PinTooltipFeatureSmoke.ActivateExisting(map, out var tooltipPrecondition);

                // What the module holds unsaved now is what has to be off the map once it is off.
                var held = HeldPins().Where(pin => !pin.m_save).ToList();

                // The looked-at label's update above showed the swarm for the temporary target. Once
                // in the run it is hidden and held through the call a map click that clears
                // navigation makes, so the disable has a held swarm to remove too.
                var heldSwarmTried = false;
                var heldSwarmSet = false;
                GameObject heldSwarm = null;
                if (!_heldSwarmTried && NavigationGuide.Shown)
                {
                    _heldSwarmTried = heldSwarmTried = true;
                    var swarm = NavigationGuide.Root;
                    NavigationGuide.Hide();
                    NavigationGuide.Scatter(NavigationGuide.ColumnCentre, NavigationGuide.StreamLine, true);
                    heldSwarmSet = NavigationGuide.HeldRoot && NavigationGuide.HeldRoot == swarm;
                    if (heldSwarmSet) heldSwarm = swarm;
                }

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

                // The update the player's next frame makes, run in the frame of the request: the
                // compass goes there, before the pump takes anything apart.
                var compassBeforeRequest = Compass.Root != null;
                Compass.Update(Player.m_localPlayer);
                var compassAtRequest = Compass.Root != null || CountCompasses(true) > 0;

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

                var compassesLeft = CountCompasses(false);
                Cycle(OffCompass, cycle, compassShown && compassBeforeRequest && !compassAtRequest && compassesLeft == 0 &&
                    !Compass.Root,
                    "the compass seen on the small map was gone in the frame of the disable request, and none is left",
                    $"compass cleanup failed: shown on the small map before={compassShown}, present at the request={compassBeforeRequest}, still there after the update in that frame={compassAtRequest}, left after disabling={compassesLeft}");

                var filterRoots = FindAll<RectTransform>().Count(x => x.name == MapFilter.ObjectName);
                Cycle(OffFilterControls, cycle, controlsActive && !MapFilter.Root &&
                    MapFilter.Controls.Count == 0 && filterRoots == 0,
                    "ten categories and both search controls existed before disable; no filter row or controls remain",
                    $"filter control precondition/cleanup failed: active before={controlsActive}, row count after={filterRoots}, control count after={MapFilter.Controls.Count}");
                Cycle(OffFilterSearch, cycle, searchActive && MapFilter.SearchText == "" &&
                    !MapFilter.SearchPinned && !MapFilter.SearchInput && MapFilter.SearchToggle == null,
                    "the active search text and minimap toggle were cleared while stopped",
                    $"search precondition/cleanup failed: active before={searchActive}, text after={MapFilter.SearchText}, pinned after={MapFilter.SearchPinned}");
                var tooltipRoots = FindAll<RectTransform>().Count(x => x.name == global::Automatics.AutomaticMapping.PinTooltip.ObjectName);
                Cycle(OffTooltip, cycle, tooltipActive && !global::Automatics.AutomaticMapping.PinTooltip.Root &&
                    global::Automatics.AutomaticMapping.PinTooltip.HoveredPin == null && tooltipRoots == 0,
                    "tooltip shown before disable; no owned tooltip or hovered pin remains",
                    $"tooltip precondition/cleanup failed: shown before={tooltipActive}, precondition={tooltipPrecondition}, roots after={tooltipRoots}");
                if (_filterHandBack("mapping cycle ended"))
                    SmokeSession.ReleaseHandBack(_filterHandBack);
                _filterHandBack = null;

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

                var offAwake = MappingAwakeHandlers();
                Cycle(OffAwakeHandlers, cycle, offAwake.Count == 0,
                    "after the disable Automatic Mapping declares no handler on Hooks.OnPlayerAwake or Hooks.OnLocalPlayerAwake",
                    $"after the disable Automatic Mapping declares handler(s) on the player-awake hooks: {string.Join(", ", offAwake)}");

                var kinds = CountComponents();
                var overlays = CountNavigationOverlays();
                var guides = CountNavigationGuides();
                var aimedLabels = FindAll<RectTransform>().Count(x => x.name == AimedOverlayName);
                var extraPins = ExtraUnsavedPins().Count();
                Cycle(OffComponents, cycle, kinds.Sum(x => x.Total) == 0 && overlays == 0 && guides == 0 && aimedLabels == 0 &&
                    aimedActive && !AimedObjectBar.Label && !AimedObjectBar.GetHudTarget() &&
                    markerActive && Navigation.TemporaryMarker == null && !AllPins().Contains(marker) &&
                    extraPins == 0 && NavigationDestroyHandlers() == 0 && DestroyedObjectPinHandlers() == 0,
                    "no FishCache, BirdCache, ShipCache, MineRock5CacheBinding, FloraNode, navigation overlay, aimed label, guide swarm, temporary marker or destruction handler is left after disabling with a marker and aimed label active",
                    $"left in the scene: {string.Join(", ", kinds.Select(x => $"{x.Total} {x.Name}"))}, {overlays} navigation overlay(s), {aimedLabels} aimed label(s), aimed active before={aimedActive}, aimed precondition={aimedPrecondition}, {guides} guide swarm(s), marker active before={markerActive}, marker left={Navigation.TemporaryMarker != null || AllPins().Contains(marker)}, extra unsaved pins={extraPins}, navigation destruction handlers={NavigationDestroyHandlers()}, destroyed-object pin handlers={DestroyedObjectPinHandlers()}");

                if (heldSwarmTried)
                {
                    if (!heldSwarmSet)
                        Settle(CheckStatus.Fail, OffHeldSwarm,
                            $"cycle {cycle}: a guide swarm was shown just before the disable request, but hiding it and clearing navigation the way a map click does left no held swarm (the held swarm was not the swarm that was shown)");
                    else if (guides == 0 && !heldSwarm)
                        Settle(CheckStatus.Pass, OffHeldSwarm,
                            $"cycle {cycle}: the guide swarm shown just before the disable request was held the way a map click that clears navigation holds it (the held swarm was that swarm), and no guide object was left after the disable");
                    else
                        Settle(CheckStatus.Fail, OffHeldSwarm,
                            $"cycle {cycle}: a held guide swarm was made just before the disable request, and after the disable {guides} guide object(s) were left, the held swarm {(heldSwarm ? "among them" : "gone")}");
                }

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
                Cycle(OnFilterSearch, cycle, MapFilter.SearchText == "" && !MapFilter.SearchPinned,
                    "search starts empty/off after re-enabling the module",
                    $"search reset failed after enable: text={MapFilter.SearchText}, pinned={MapFilter.SearchPinned}");
                var enabledAt = Time.realtimeSinceStartup;

                // Read as soon as the module reports Enabled, before the waits below give the scans
                // time to take saved pins back into their caches.
                CheckRecognizedPins(cycle);

                CheckPatchSet(cycle);

                var onAwake = MappingAwakeHandlers();
                Cycle(OnAwakeHandlers, cycle, onAwake.Count == 0,
                    "with the module running it declares no handler on Hooks.OnPlayerAwake or Hooks.OnLocalPlayerAwake",
                    $"with the module running it declares handler(s) on the player-awake hooks: {string.Join(", ", onAwake)}");

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

                var option = CompassOption(cycle);
                while (option.MoveNext()) yield return option.Current;

                _proceed = true;
            }

            // The saved pins recognized as automatic before the run are recognized again once the
            // module is back on, so their filters, adoption and destroyed-object removal still
            // reach them.
            private void CheckRecognizedPins(int cycle)
            {
                if (_baseline.RecognizedPins.Count == 0)
                {
                    CycleSkip(OnRecognizedPins, cycle,
                        "no saved pin was recognized as automatic before the run, so there was nothing to see recognized again");
                    return;
                }

                var onMap = new HashSet<Minimap.PinData>(AllPins());
                var kept = _baseline.RecognizedPins.Where(onMap.Contains).ToList();
                if (kept.Count == 0)
                {
                    CycleSkip(OnRecognizedPins, cycle,
                        $"inconclusive: none of the {_baseline.RecognizedPins.Count} saved pin(s) recognized as automatic before the run is on the map");
                    return;
                }

                var lost = kept.Count(pin => !MappingMap.IsAutomaticPin(pin));
                Cycle(OnRecognizedPins, cycle, lost == 0,
                    $"the {kept.Count} saved pin(s) recognized as automatic before the run are recognized again right after the enable",
                    $"{lost} of the {kept.Count} saved pin(s) recognized as automatic before the run are not recognized right after the enable");
            }

            // With the module running, turning the compass option off alone removes the compass
            // in that frame; the option is then turned back on for the next cycle.
            private IEnumerator CompassOption(int cycle)
            {
                var player = Player.m_localPlayer;
                Compass.Update(player);
                var shown = Compass.Root && Compass.Root.activeInHierarchy;
                if (!SmokeSession.WriteSetting(RunId, _compassSetting, false, out var refusal))
                {
                    CycleSkip(OnCompassOption, cycle, "inconclusive: enable_compass could not be turned off: " + refusal);
                    yield break;
                }

                Compass.Update(player);
                var goneInFrame = !Compass.Root && CountCompasses(true) == 0;
                for (var i = 0; i < DestroySettleFrames; i++) yield return null;
                var left = CountCompasses(false);
                var restored = SmokeSession.WriteSetting(RunId, _compassSetting, true, out refusal);
                Cycle(OnCompassOption, cycle, shown && goneInFrame && left == 0 && restored,
                    "the compass seen on the small map was gone in the frame enable_compass was turned off, and none was left",
                    $"compass option cleanup failed: shown before={shown}, gone in that frame={goneInFrame}, left afterwards={left}, option turned back on={restored}{(restored ? "" : " (" + refusal + ")")}");
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
                var recognized = new HashSet<Minimap.PinData>(
                    AllPins().Where(pin => pin != null && pin.m_save && MappingMap.IsAutomaticPin(pin)));
                return new Baseline(HandlerCounts(), components, IconCount(), IconTypeCount(),
                    vehicles, SavedPins(vehicles), recognized, StaticHeldCount());
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

            private IEnumerable<Minimap.PinData> ExtraUnsavedPins()
            {
                return AllPins().Where(pin => !pin.m_save && !MappingMap.IsAutomaticPin(pin) &&
                    !_pinsBefore.Contains(pin));
            }

            // The pins either scan holds, plus every new unsaved navigation marker.
            private List<Minimap.PinData> HeldPins()
            {
                return AllPins().Where(pin =>
                    pin != null && (DynamicObjectMapping.OwnsPin(pin) || StaticObjectMapping.OwnsPin(pin) ||
                                    (!pin.m_save && !MappingMap.IsAutomaticPin(pin) &&
                                     !_pinsBefore.Contains(pin)))).ToList();
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

            // The handlers on the two player-awake hooks that a type of Automatic Mapping declares,
            // a nested type or a closure included, whatever the method is called. The module has
            // none: its session reset does not run from either hook.
            private static List<string> MappingAwakeHandlers()
            {
                var found = new List<string>();
                foreach (var pair in new[]
                         {
                             ("Hooks.OnPlayerAwake", Hooks.OnPlayerAwake as Delegate),
                             ("Hooks.OnLocalPlayerAwake", Hooks.OnLocalPlayerAwake as Delegate)
                         })
                {
                    if (pair.Item2 == null) continue;
                    foreach (var handler in pair.Item2.GetInvocationList())
                        if (IsMappingType(handler.Method.DeclaringType))
                            found.Add($"{pair.Item1} -> {handler.Method.DeclaringType?.Name}.{handler.Method.Name}");
                }

                return found;
            }

            private static bool IsMappingType(Type type)
            {
                while (type?.DeclaringType != null) type = type.DeclaringType;
                return type != null && type.Namespace == "Automatics.AutomaticMapping";
            }

            private static int NavigationDestroyHandlers()
            {
                return ZDOMan.instance?.m_onZDODestroyed?.GetInvocationList()
                    .Count(handler => handler.Method.DeclaringType == typeof(Navigation)) ?? 0;
            }

            // The handlers through which the scans remove the pins of destroyed objects, on the
            // current world's ZDOMan.
            private static int DestroyedObjectPinHandlers()
            {
                return ZDOMan.instance?.m_onZDODestroyed?.GetInvocationList()
                    .Count(handler => handler.Method.DeclaringType == typeof(MappingScans)) ?? 0;
            }

            private static Dictionary<string, int> HandlerCounts()
            {
                return new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["Hooks.OnPlayerAwake"] = Invocations(Hooks.OnPlayerAwake),
                    ["Hooks.OnLocalPlayerAwake"] = Invocations(Hooks.OnLocalPlayerAwake),
                    ["Hooks.OnPlayerUpdate"] = Invocations(Hooks.OnPlayerUpdate),
                    ["Navigation destruction handlers"] = NavigationDestroyHandlers(),
                    ["Destroyed-object pin handlers"] = DestroyedObjectPinHandlers(),
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

            private static int CountNavigationGuides()
            {
                return FindAll<Transform>().Count(x => x.name == NavigationGuideName);
            }

            private static int CountCompasses(bool activeOnly)
            {
                return FindAll<RectTransform>().Count(x =>
                    x.name == Compass.ObjectName && (!activeOnly || x.gameObject.activeInHierarchy));
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
                    HashSet<Minimap.PinData> recognizedPins, int staticHeld)
                {
                    Handlers = handlers;
                    Components = components;
                    Icons = icons;
                    IconTypes = iconTypes;
                    VehiclePins = vehiclePins;
                    SavedPins = savedPins;
                    RecognizedPins = recognizedPins;
                    StaticHeld = staticHeld;
                }

                public Dictionary<string, int> Handlers { get; }
                public List<Kind> Components { get; }
                public int Icons { get; }
                public int IconTypes { get; }
                public HashSet<Minimap.PinData> VehiclePins { get; }
                public List<string> SavedPins { get; }
                public HashSet<Minimap.PinData> RecognizedPins { get; }
                public int StaticHeld { get; }
            }
        }
    }
}
