using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;
using PickableItemCache = Automatics.AutomaticPickup.PickableItemCache;
using PickupComponent = Automatics.AutomaticPickup.AutomaticPickup;
using PickupConfig = Automatics.AutomaticPickup.Config;
using PickupModule = Automatics.AutomaticPickup.Module;
using PickupPatches = Automatics.AutomaticPickup.Patches;
using Held = System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,
    Automatics.SmokeTest.SmokeSession.SettingRestoration>;

namespace Automatics.SmokeTest
{
    // In-game console command that takes Automatic Pickup - the module on the runtime
    // lifecycle - through a real off -> on -> off round trip in the running game and checks
    // what each state leaves behind: AC-R3 (patches), AC-R4 (components), AC-R5 (a
    // PickableItem that appeared while the module was off, and the Player that was already
    // there), plus the Hooks.OnPlayerAwake handler count of AC-R6.
    //
    // The toggle is driven the way a user drives it: by writing the module's config value,
    // which raises SettingChanged and reaches the registry through its own request path. The
    // registry applies one transition per UnityPlugin.Update and the engine destroys
    // components at the end of the frame, so the run is a coroutine on the smoke plugin that
    // waits across frames. Every wait is bounded; a timeout is a FAIL naming the state seen.
    //
    // The config is shared by every world the user plays, so the toggle is SmokeSession's to
    // hand back: it is put back to its starting value when the run ends or is cut short, but
    // only if it still holds the value the check itself wrote. A setting the user changed
    // while the check ran stays as the user set it, and stops the run.
    //
    // With the argument "mapping" the command takes Automatic Mapping through three off -> on
    // cycles instead, under the same rules; see LifecycleSmokeMapping.cs. With the name of another
    // migrated module, such as "storage", it runs the descriptor-driven cycles of
    // LifecycleSmokeModules.cs, under the same rules again.
    internal static partial class LifecycleSmokeCommand
    {
        private const string CommandName = "automatics_smoke_lifecycle";
        private const string MappingArgument = "mapping";

        // A requested transition normally lands on the next pump tick, or a few ticks later
        // after a deferral. This bounds the wait; it is not an estimate of it.
        private const float TransitionTimeoutSeconds = 10f;

        // Object.Destroy takes effect at the end of the frame the pump ran in, so two frames
        // after the registry reports Disabled every component it destroyed is gone.
        private const int DestroySettleFrames = 2;

        // Height above Automatic Pickup's own reach at which the test PickableItem is placed,
        // so the component attached on enable cannot pick it up and remove the evidence.
        private const float SpawnClearance = 10f;

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Lifecycle");

        private static MonoBehaviour _host;
        private static bool _registered;
        private static bool _commandBuilt;

        internal static void EnsureRegistered(MonoBehaviour host)
        {
            _host = host;
            if (_registered) return;
            _registered = true;

            // Same registration as WorldSmokeCommand: subscribed before the terminal's
            // one-time init, which then builds the command.
            Hooks.OnInitTerminal += BuildCommand;
        }

        private static void BuildCommand()
        {
            if (_commandBuilt) return;
            _commandBuilt = true;

            _ = new Terminal.ConsoleCommand(
                CommandName,
                $"Turn Automatic Pickup off, on and off through its setting, check the runtime lifecycle, then restore the setting. With a module name ({string.Join(", ", ModuleArguments())}), turn that module off and on three times instead.",
                OnCommand);
        }

        private static void OnCommand(Terminal.ConsoleEventArgs args)
        {
            var context = args.Context;
            if (!_host)
            {
                Emit(context, LogLevel.Error,
                    $"[Automatics Smoke] {CommandName}: the smoke plugin is not available to run it");
                return;
            }

            var argument = (args.Args.Skip(1).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "")
                .ToLowerInvariant();
            ModuleDescriptor descriptor = null;
            if (argument.Length != 0 && argument != MappingArgument)
            {
                if (!Descriptors.ContainsKey(argument))
                {
                    Emit(context, LogLevel.Warning,
                        $"[Automatics Smoke] usage: {CommandName} [{string.Join("|", ModuleArguments())}] ('{argument}' is not an argument)");
                    return;
                }

                if (!TryDescribe(argument, out descriptor, out var problem))
                {
                    Emit(context, LogLevel.Error,
                        $"[Automatics Smoke] {CommandName} {argument} was not started: {problem}");
                    return;
                }
            }

            var runner = argument.Length == 0 ? CommandName : $"{CommandName} {argument}";
            if (!SmokeSession.TryBegin(runner, out var runId, out var refusal))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {runner} was not started: {refusal}");
                return;
            }

            // Guard hands back the run's obligations and releases the lock however the body
            // ends, a throw included.
            IEnumerator body;
            if (argument.Length == 0) body = Run(context, runId);
            else if (argument == MappingArgument) body = RunMapping(context, runId, runner);
            else body = RunModule(context, runId, runner, descriptor);
            _host.StartCoroutine(SmokeSession.Guard(runId, runner, body));
        }

        private static IEnumerator Run(Terminal context, int runId)
        {
            var run = new LifecycleRun(runId);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {CommandName} started: automatic_pickup/module is turned off, on and off again, then restored");

            var steps = Contain(run.Execute(), run.Fail, "lifecycle_run");
            while (steps.MoveNext()) yield return steps.Current;

            run.RemoveSpawnedItem();

            var restore = Contain(run.Restore(), run.Fail, "lifecycle_restored_initial_state");
            while (restore.MoveNext()) yield return restore.Current;

            Report(context, run.Results);
        }

        // Advances a step sequence and turns an exception into a FAIL, so a throw in one phase
        // still lets the restore run. A yield cannot sit inside a try that has a catch, so the
        // step is taken inside one and the yield happens outside it.
        private static IEnumerator Contain(IEnumerator steps, Action<string, string> fail,
            string name)
        {
            while (true)
            {
                object current = null;
                bool more;
                try
                {
                    more = steps.MoveNext();
                    if (more) current = steps.Current;
                }
                catch (Exception e)
                {
                    fail(name, e.GetType().Name + ": " + e.Message);
                    more = false;
                }

                if (!more) yield break;
                yield return current;
            }
        }

        private static void Report(Terminal context, IReadOnlyList<CheckResult> results)
        {
            foreach (var result in results)
            {
                if (result.Status == CheckStatus.Pass)
                    Emit(context, LogLevel.Info,
                        string.IsNullOrEmpty(result.Message)
                            ? $"[Automatics Smoke] PASS {result.Name}"
                            : $"[Automatics Smoke] PASS {result.Name}: {result.Message}");
                else if (result.Status == CheckStatus.Skip)
                    Emit(context, LogLevel.Warning,
                        $"[Automatics Smoke] SKIP {result.Name}: {result.Message}");
                else
                    Emit(context, LogLevel.Error,
                        $"[Automatics Smoke] FAIL {result.Name}: {result.Message}");
            }

            var failed = results.Where(result => result.Status == CheckStatus.Fail)
                                .Select(result => result.Name)
                                .ToArray();
            var skipped = results.Where(result => result.Status == CheckStatus.Skip)
                                 .Select(result => result.Name)
                                 .ToArray();
            var passed = results.Count(result => result.Status == CheckStatus.Pass);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] Summary: total={results.Count} passed={passed} failed={failed.Length} skipped={skipped.Length} failedChecks=[{string.Join(", ", failed)}] skippedChecks=[{string.Join(", ", skipped)}]");
        }

        private static void Emit(Terminal context, LogLevel level, string line)
        {
            Log.Log(level, line);

            // The run can outlive the console that started it (a logout while it waits).
            if (context) context.AddString(line);
        }

        private sealed class LifecycleRun
        {
            private readonly List<CheckResult> _results = new List<CheckResult>();

            private ConfigEntry<AutomaticsModule> _toggle;
            private AutomaticsModule _initialValue;
            private string _moduleName;
            private string _harmonyId;
            private int _handlersWhileOff;

            private GameObject _spawned;
            private PickableItem _spawnedItem;
            private bool _spawnedWhileOff;
            private bool _controlHeld;

            // The outcome of the last WaitForState, and whether the last phase may continue.
            private WaitResult _wait;
            private string _waitReport = "";
            private bool _proceed;

            public LifecycleRun(int runId)
            {
                RunId = runId;
            }

            private int RunId { get; }

            public IReadOnlyList<CheckResult> Results => _results;

            public void Fail(string name, string message)
            {
                _results.Add(new CheckResult(CheckStatus.Fail, name, message));
            }

            private void Pass(string name)
            {
                _results.Add(new CheckResult(CheckStatus.Pass, name, ""));
            }

            private void Skip(string name, string message)
            {
                _results.Add(new CheckResult(CheckStatus.Skip, name, message));
            }

            private void Check(string name, bool condition, string failure)
            {
                if (condition)
                    Pass(name);
                else
                    Fail(name, failure);
            }

            // Reaching the state passes; a fault or a timeout is a failure of the module;
            // anything that took the run out of the check's hands makes it inconclusive.
            private void RecordWait(string name, string note = "")
            {
                var suffix = string.IsNullOrEmpty(note) ? "" : "; " + note;
                switch (_wait)
                {
                    case WaitResult.Reached:
                        Pass(name);
                        break;
                    case WaitResult.Faulted:
                    case WaitResult.TimedOut:
                        Fail(name, _waitReport + suffix);
                        break;
                    default:
                        Skip(name, "inconclusive: " + _waitReport + suffix);
                        break;
                }
            }

            public IEnumerator Execute()
            {
                if (ZNetScene.instance == null || !Player.m_localPlayer)
                {
                    Skip("lifecycle_run", "no world is loaded (ZNetScene or the local player is missing)");
                    yield break;
                }

                _toggle = PickupConfig.ModuleEntry;
                if (_toggle == null)
                {
                    Fail("lifecycle_run", "the automatic_pickup module toggle is not bound");
                    yield break;
                }

                _moduleName = PickupModule.Instance.Name;
                _harmonyId = global::Automatics.Automatics.GetHarmonyId(_moduleName);
                _initialValue = _toggle.Value;

                // From here on any write to the toggle that is not the check's own is the user
                // taking the setting back.
                if (!SmokeSession.BeginToggle(RunId, _toggle, out var refusal))
                {
                    Skip("lifecycle_run", "inconclusive: " + refusal);
                    yield break;
                }

                // The registry has to agree with the toggle before anything is asked of it,
                // or a transition still pending from before the command would be read as
                // the result of this one.
                var settle = WaitForState(StateFor(_initialValue), true, true);
                while (settle.MoveNext()) yield return settle.Current;
                RecordWait("lifecycle_initial_state_settled", "nothing was changed");
                if (_wait != WaitResult.Reached) yield break;

                // off
                var off = TurnOffAndCheck("lifecycle_off");
                while (off.MoveNext()) yield return off.Current;
                if (!_proceed) yield break;
                _handlersWhileOff = CountPlayerAwakeHandlers();

                // A PickableItem that appears while the module is off.
                if (!SpawnPickableItem()) yield break;

                // on
                if (!Write(AutomaticsModule.Enabled, "lifecycle_on_state_enabled")) yield break;
                var on = WaitForState(ModuleState.Enabled, true, true);
                while (on.MoveNext()) yield return on.Current;
                RecordWait("lifecycle_on_state_enabled");
                if (_wait != WaitResult.Reached) yield break;
                CheckOn();

                // off again
                var reoff = TurnOffAndCheck("lifecycle_reoff");
                while (reoff.MoveNext()) yield return reoff.Current;
                if (!_proceed) yield break;

                var handlers = CountPlayerAwakeHandlers();
                Check("lifecycle_reoff_player_awake_unsubscribed", handlers == _handlersWhileOff,
                    $"Hooks.OnPlayerAwake has {handlers} handler(s) after the second disable and {_handlersWhileOff} after the first (AC-R6)");
            }

            public IEnumerator Restore()
            {
                const string name = "lifecycle_restored_initial_state";

                var outcome = SmokeSession.RestoreToggle(RunId, out var detail);
                switch (outcome)
                {
                    case SmokeSession.ToggleOutcome.NothingWritten:
                        yield break;
                    case SmokeSession.ToggleOutcome.LeftForUser:
                        Skip(name, "restoration skipped: " + detail);
                        yield break;
                    case SmokeSession.ToggleOutcome.NotThisRun:
                        Skip(name, "inconclusive: " + detail);
                        yield break;
                    case SmokeSession.ToggleOutcome.Failed:
                        Fail(name, detail);
                        yield break;
                }

                // A fault is not final here: putting the value back is also what asks the
                // registry to recover, so the wait gives it the full bound.
                var wait = WaitForState(StateFor(_initialValue), false, false, _initialValue);
                while (wait.MoveNext()) yield return wait.Current;
                RecordWait(name, detail);
            }

            public void RemoveSpawnedItem()
            {
                var go = _spawned;
                _spawned = null;

                // Kept with the session when the removal fails, so a later trigger retries it.
                if (SmokeSession.DestroyTestObject(go, 0, CommandName)) SmokeSession.ReleaseSpawn(go);
            }

            private IEnumerator TurnOffAndCheck(string prefix)
            {
                _proceed = false;

                if (_toggle.Value != AutomaticsModule.Disabled &&
                    !Write(AutomaticsModule.Disabled, $"{prefix}_state_disabled"))
                    yield break;

                var wait = WaitForState(ModuleState.Disabled, true, true);
                while (wait.MoveNext()) yield return wait.Current;
                RecordWait($"{prefix}_state_disabled");
                if (_wait != WaitResult.Reached) yield break;

                for (var i = 0; i < DestroySettleFrames; i++) yield return null;

                if (!ModuleRegistry.RuntimeToggleSupported)
                    Skip($"{prefix}_patches_removed",
                        "this Harmony build cannot remove patches at runtime; they stay applied by design (R3.2)");
                else
                    Check($"{prefix}_patches_removed", !Harmony.HasAnyPatches(_harmonyId),
                        $"Harmony.HasAnyPatches(\"{_harmonyId}\") is still true after the disable (AC-R3)");

                var pickups = CountInScene<PickupComponent>();
                var caches = CountInScene<PickableItemCache>();
                var cached = PickableItemCache.GetAllInstance().Count();
                Check($"{prefix}_components_removed", pickups == 0 && caches == 0 && cached == 0,
                    $"{pickups} AutomaticPickup and {caches} PickableItemCache component(s) remain in the scene and the cache lists {cached} PickableItem(s) (AC-R4)");

                _proceed = true;
            }

            private void CheckOn()
            {
                CheckPatchSet();

                var player = Player.m_localPlayer;
                var component = player ? player.GetComponent<PickupComponent>() : null;
                string attachFailure;
                if (!player)
                    attachFailure = "the local player went away";
                else if (!component)
                    attachFailure = "the local player carries no AutomaticPickup after the enable (AC-R5)";
                else
                    attachFailure =
                        $"the local player's AutomaticPickup is stamped with generation {component.ScopeGeneration}, but the running activation is generation {PickupModule.Generation}";
                Check("lifecycle_on_local_player_attached",
                    component && component.ScopeGeneration != 0 &&
                    component.ScopeGeneration == PickupModule.Generation,
                    attachFailure);

                // A cache on the item only shows that the reconciliation scan reached it if the
                // item had none while the module was off. Without that control the result is
                // inconclusive, never a pass.
                const string cachedName = "lifecycle_on_item_spawned_while_off_cached";
                if (!_spawnedWhileOff)
                    Skip(cachedName, "no PickableItem was spawned while the module was off");
                else if (!_controlHeld)
                    Skip(cachedName,
                        "inconclusive: the control lifecycle_off_spawned_item_uncached failed, so a cache now would not show that the reconciliation scan reached the item");
                else
                    Check(cachedName,
                        _spawnedItem && _spawnedItem.GetComponent<PickableItemCache>() &&
                        PickableItemCache.GetAllInstance().Contains(_spawnedItem),
                        "the PickableItem spawned while the module was off is not in PickableItemCache after the enable; the reconciliation scan did not reach it (AC-R5)");

                var handlers = CountPlayerAwakeHandlers();
                Check("lifecycle_on_player_awake_subscribed", handlers == _handlersWhileOff + 1,
                    $"Hooks.OnPlayerAwake has {handlers} handler(s) while on and {_handlersWhileOff} while off; the module adds exactly one (AC-R6)");
            }

            // The patches installed under the module's Harmony id have to be exactly the ones
            // its patch class declares, compared as (target, patch method) pairs: a target the
            // module patches twice, with a prefix and a postfix, cannot hide a missing one.
            private void CheckPatchSet()
            {
                const string name = "lifecycle_on_patches_applied";

                var expected = ExpectedPatches(typeof(PickupPatches));
                if (expected.Count == 0)
                {
                    Fail(name, "no [HarmonyPatch] target could be read from Automatics.AutomaticPickup.Patches");
                    return;
                }

                var actual = PatchesUnder(_harmonyId, out _);
                var missing = expected.Where(x => !actual.Contains(x)).OrderBy(x => x).ToArray();
                var unexpected = actual.Where(x => !expected.Contains(x)).OrderBy(x => x).ToArray();
                Check(name, missing.Length == 0 && unexpected.Length == 0,
                    $"the patches under \"{_harmonyId}\" differ from Automatics.AutomaticPickup.Patches: missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", unexpected)}]");
            }

            // Returns false only when the run cannot go on. A world without any PickableItem
            // prefab skips the AC-R5 item check and still runs the rest.
            private bool SpawnPickableItem()
            {
                const string name = "lifecycle_off_spawned_item_uncached";

                var player = Player.m_localPlayer;
                if (!player)
                {
                    Skip(name, "inconclusive: the local player went away");
                    return false;
                }

                var prefab = FindPickableItemPrefab();
                if (!prefab)
                {
                    Skip(name, "no PickableItem prefab in ZNetScene; the AC-R5 item check cannot run");
                    return true;
                }

                var position = player.transform.position +
                               Vector3.up * (PickupConfig.AutomaticPickupRange + SpawnClearance);
                _spawned = Object.Instantiate(prefab, position, Quaternion.identity);

                // Recorded before the first frame boundary, so a run that is cut short still
                // has it removed (SmokeSession).
                SmokeSession.TrackSpawn(_spawned);
                _spawnedItem = _spawned.GetComponent<PickableItem>();

                if (!_spawnedItem || !Objects.GetZNetView(_spawnedItem, out var nview) ||
                    nview == null || !nview.IsValid() || nview.GetZDO() == null)
                {
                    Fail(name, $"the spawned {prefab.name} has no valid ZNetView/ZDO");
                    return false;
                }

                // Left out of every world save, so an item that outlives an interrupted run can
                // never end up in the user's world.
                nview.GetZDO().Persistent = false;

                _spawnedWhileOff = true;

                // The control for AC-R5: the item has to be one the module has not seen. A
                // cache here means the PickableItem.Awake postfix still ran while off.
                _controlHeld = !_spawned.GetComponent<PickableItemCache>();
                Check(name, _controlHeld,
                    $"the {prefab.name} spawned while the module was off already carries a PickableItemCache; the PickableItem.Awake postfix still ran");
                return true;
            }

            // The user's path: a config write raises SettingChanged, which the registry turns
            // into a request. The registry itself never writes this value (I8); a test acting
            // as the user may, through SmokeSession, which refuses once the user has changed it.
            private bool Write(AutomaticsModule value, string checkName)
            {
                if (SmokeSession.WriteToggle(RunId, value, out var refusal)) return true;

                Skip(checkName, "inconclusive: " + refusal + "; the run stopped");
                return false;
            }

            // The shared wait, with this run's toggle and module.
            private IEnumerator WaitForState(ModuleState expected, bool faultIsFinal,
                bool worldRequired, AutomaticsModule? heldValue = null)
            {
                var wait = new StateWait();
                var steps = AwaitState(RunId, _toggle, _moduleName, expected, faultIsFinal,
                    worldRequired, wait, heldValue);
                while (steps.MoveNext()) yield return steps.Current;

                _wait = wait.Result;
                _waitReport = wait.Report;
            }

            private static ModuleState StateFor(AutomaticsModule value)
            {
                return value == AutomaticsModule.Enabled ? ModuleState.Enabled : ModuleState.Disabled;
            }

            private static int CountPlayerAwakeHandlers()
            {
                var handlers = Hooks.OnPlayerAwake;
                return handlers == null ? 0 : handlers.GetInvocationList().Length;
            }

            private static int CountInScene<T>() where T : Object
            {
                return Object.FindObjectsByType<T>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None).Length;
            }

            // A PickableItem with a fixed item is preferred: its Awake has nothing random to
            // set up. One with only random items is the fallback.
            private static GameObject FindPickableItemPrefab()
            {
                GameObject fallback = null;
                foreach (var go in ZNetScene.instance.m_prefabs)
                {
                    if (!go || !go.GetComponent<ZNetView>()) continue;

                    var item = go.GetComponent<PickableItem>();
                    if (!item) continue;

                    var hasRandom = item.m_randomItemPrefabs != null &&
                                    item.m_randomItemPrefabs.Length > 0;
                    if (item.m_itemPrefab && !hasRandom) return go;
                    if (!fallback && hasRandom) fallback = go;
                }

                return fallback;
            }
        }

        // -- Shared by the pickup run and the mapping run ----------------------------

        private enum WaitResult
        {
            Reached,
            Faulted,
            TimedOut,
            EditedByUser,
            WorldGone,
            Abandoned
        }

        // The outcome of one AwaitState.
        private sealed class StateWait
        {
            public WaitResult Result = WaitResult.TimedOut;
            public string Report = "";
        }

        // Waits for the registry to report the expected state. Faulted is the failure itself
        // rather than a step on the way, unless the caller is asking for a recovery. heldValue,
        // when given, is the value the toggle must keep meanwhile. onFrame, when given, runs on
        // every frame of the wait, before the state is read.
        private static IEnumerator AwaitState(int runId, ConfigEntry<AutomaticsModule> toggle,
            string moduleName, ModuleState expected, bool faultIsFinal, bool worldRequired,
            StateWait wait, AutomaticsModule? heldValue = null, Action onFrame = null)
        {
            wait.Result = WaitResult.TimedOut;
            wait.Report = "";
            var setting = $"{toggle.Definition.Section}/{toggle.Definition.Key}";

            var started = Time.realtimeSinceStartup;
            var frames = 0;
            while (true)
            {
                if (!SmokeSession.IsCurrent(runId))
                {
                    wait.Result = WaitResult.Abandoned;
                    wait.Report = "the run was abandoned (the smoke plugin is shutting down)";
                    yield break;
                }

                if (SmokeSession.ToggleEditedByUser ||
                    (heldValue.HasValue && toggle.Value != heldValue.Value))
                {
                    wait.Result = WaitResult.EditedByUser;
                    wait.Report = $"{setting} was changed to {toggle.Value} outside the check";
                    yield break;
                }

                if (worldRequired && (ZNetScene.instance == null || !Player.m_localPlayer))
                {
                    wait.Result = WaitResult.WorldGone;
                    wait.Report = "the world was unloaded while the check ran";
                    yield break;
                }

                onFrame?.Invoke();

                var state = ModuleRegistry.GetState(moduleName);
                if (state == expected)
                {
                    wait.Result = WaitResult.Reached;
                    yield break;
                }

                if (faultIsFinal && state == ModuleState.Faulted)
                {
                    wait.Result = WaitResult.Faulted;
                    wait.Report =
                        $"the registry reports {state} while waiting for {expected}; the reason is in the Automatics log as a warning";
                    yield break;
                }

                var elapsed = Time.realtimeSinceStartup - started;
                if (elapsed >= TransitionTimeoutSeconds)
                {
                    wait.Result = WaitResult.TimedOut;
                    wait.Report =
                        $"timed out after {elapsed:0.0}s ({frames} frames) waiting for {expected}; the registry reports {state} and the toggle is {toggle.Value}";
                    yield break;
                }

                frames++;
                yield return null;
            }
        }

        // The (target, patch method) pairs the given patch classes declare. A patch method names
        // its target in its own [HarmonyPatch] attribute; a class without one resolves its target
        // with a [HarmonyTargetMethod] method, and each of its patch methods is paired with that
        // target. A class whose resolver finds no target declares nothing: Harmony skips it too.
        private static HashSet<string> ExpectedPatches(params Type[] patchTypes)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            var patches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var patchType in patchTypes)
            {
                var methods = patchType.GetMethods(flags);
                var resolver = methods.FirstOrDefault(x =>
                    x.IsDefined(typeof(HarmonyTargetMethod), false));
                var classTarget = resolver?.Invoke(null, Array.Empty<object>()) as MethodBase;

                foreach (var method in methods)
                {
                    var named = false;
                    foreach (var attribute in method.GetCustomAttributes(typeof(HarmonyPatch), false))
                    {
                        var info = ((HarmonyPatch)attribute).info;
                        if (info == null || info.declaringType == null ||
                            string.IsNullOrEmpty(info.methodName))
                            continue;

                        named = true;
                        var target = AccessTools.Method(info.declaringType, info.methodName,
                            info.argumentTypes);
                        var targetName = target == null
                            ? $"{info.declaringType.Name}.{info.methodName} (unresolved)"
                            : DescribeMethod(target);
                        patches.Add($"{targetName} <- {method.DeclaringType?.Name}.{method.Name}");
                    }

                    if (named || classTarget == null || !IsPatchMethod(method)) continue;
                    patches.Add(
                        $"{DescribeMethod(classTarget)} <- {method.DeclaringType?.Name}.{method.Name}");
                }
            }

            return patches;
        }

        private static bool IsPatchMethod(MethodInfo method)
        {
            return method.IsDefined(typeof(HarmonyPrefix), false) ||
                   method.IsDefined(typeof(HarmonyPostfix), false) ||
                   method.IsDefined(typeof(HarmonyTranspiler), false) ||
                   method.IsDefined(typeof(HarmonyFinalizer), false);
        }

        // The (target, patch method) pairs installed under harmonyId, and how many patches that
        // is in total: a pair applied twice appears once in the set and twice in the total.
        private static HashSet<string> PatchesUnder(string harmonyId, out int total)
        {
            total = 0;
            var patches = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null) continue;

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
                        total++;
                        patches.Add(
                            $"{DescribeMethod(target)} <- {patch.PatchMethod.DeclaringType?.Name}.{patch.PatchMethod.Name}");
                    }
                }
            }

            return patches;
        }

        private static string DescribeMethod(MethodBase method)
        {
            var parameters = string.Join(",",
                method.GetParameters().Select(p => p.ParameterType.Name).ToArray());
            return $"{method.DeclaringType?.Name}.{method.Name}({parameters})";
        }

        // -- What the mapping run and the module runner set up for themselves ---------

        // A setting a run writes for itself, with the value its checks need. Name is what a
        // setting that was not found is called: the config class's field or accessor.
        private sealed class RunSetting
        {
            public string Name;
            public ConfigEntryBase Entry;
            public object Value;
        }

        // A setting without an accessor, found by its config class's private field.
        private static RunSetting Setting<T>(Type config, string field, Func<ConfigEntry<T>, T> value)
        {
            return Setting(SmokeSetup.Field<T>(config, field), $"{config.FullName}.{field}", value);
        }

        private static RunSetting Setting<T>(ConfigEntry<T> entry, string name, Func<ConfigEntry<T>, T> value)
        {
            return new RunSetting { Name = name, Entry = entry, Value = entry == null ? null : (object)value(entry) };
        }

        // An interval a module keeps a timer for: the user's own when it is above 0, otherwise
        // the setting's default.
        private static float OnAnInterval(ConfigEntry<float> entry)
        {
            return entry.Value > 0f ? entry.Value : (float)entry.DefaultValue;
        }

        // Writes each setting through the session, once per run (held). Null when all were
        // written, or why one was not; setup gets one line naming them, for the run's setup note.
        private static string WriteSettings(int runId, Held held, IEnumerable<RunSetting> settings,
            List<string> setup)
        {
            var written = new List<string>();
            foreach (var setting in settings)
            {
                if (setting.Entry == null)
                    return $"{setting.Name} was not found, so it could not be set for the run";

                var key = setting.Entry.Definition.Key;
                if (!SmokeSetup.Write(runId, held, setting.Entry, setting.Value, out var refusal))
                    return $"{key} could not be set for the run: {refusal}";

                written.Add($"{key}={setting.Entry.GetSerializedValue()}");
            }

            if (written.Count > 0) setup.Add($"set {string.Join(", ", written)} for the run");
            return null;
        }

        // SmokeSession.RestoreToggle and RestoreSettings each put back every setting the run holds,
        // so a run that wrote other settings besides its toggle puts them all back in one call and
        // tells the toggle's result apart by its setting. others holds the rest; a run that is no
        // longer current gets NotThisRun for both.
        private static SmokeSession.ToggleOutcome RestoreAll(int runId, ConfigEntryBase toggle,
            out string detail, out List<KeyValuePair<SmokeSession.ToggleOutcome, string>> others)
        {
            var outcome = SmokeSession.ToggleOutcome.NothingWritten;
            detail = "";
            others = new List<KeyValuePair<SmokeSession.ToggleOutcome, string>>();
            foreach (var result in SmokeSession.RestoreEachSetting(runId))
            {
                if (result.Outcome == SmokeSession.ToggleOutcome.NotThisRun)
                {
                    detail = result.Detail;
                    others.Add(new KeyValuePair<SmokeSession.ToggleOutcome, string>(result.Outcome, result.Detail));
                    return result.Outcome;
                }

                if (ReferenceEquals(result.Entry, toggle))
                {
                    outcome = result.Outcome;
                    detail = result.Detail;
                }
                else
                {
                    others.Add(new KeyValuePair<SmokeSession.ToggleOutcome, string>(result.Outcome, result.Detail));
                }
            }

            return outcome;
        }

        // The settings the run wrote besides its toggle, put back, as SmokeSetup.ReportSettings
        // reports them: PASS when each is back, SKIP when one was left to the user or the run was
        // abandoned, FAIL when one could not be put back. Null when none was written.
        private static CheckResult SettingsResult(string name,
            List<KeyValuePair<SmokeSession.ToggleOutcome, string>> results)
        {
            if (results.Count == 0) return null;

            var detail = string.Join("; ", results.Select(x => x.Value));
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                return new CheckResult(CheckStatus.Fail, name, detail);
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                 x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                return new CheckResult(CheckStatus.Skip, name, "restoration skipped: " + detail);
            return new CheckResult(CheckStatus.Pass, name, detail);
        }

        // A test object on the ground ahead of the player, facing them: recorded with the session
        // before the first frame boundary and left out of every world save. Null, with the
        // reason, when it could not be spawned.
        private static GameObject SpawnAhead(string prefabName, float distance, out string problem)
        {
            problem = null;
            var player = Player.m_localPlayer;
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (!player || !prefab)
            {
                problem = $"the test {prefabName} could not be spawned: its prefab is not available";
                return null;
            }

            var forward = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up);
            forward = forward.sqrMagnitude < 0.01f ? Vector3.forward : forward.normalized;
            var position = player.transform.position + forward * distance;
            position.y = ZoneSystem.instance.GetGroundHeight(position);

            var go = Object.Instantiate(prefab, position, Quaternion.LookRotation(-forward));
            SmokeSession.TrackSpawn(go);
            if (!Objects.GetZNetView(go.transform, out var nview) || nview == null || !nview.IsValid() ||
                nview.GetZDO() == null)
            {
                problem = $"the test {prefabName} could not be spawned: it has no valid ZNetView/ZDO";
                RemoveTestObject(go);
                return null;
            }

            nview.GetZDO().Persistent = false;
            return go;
        }

        // Kept with the session when the removal fails, so a later trigger retries it.
        private static void RemoveTestObject(GameObject go)
        {
            if (SmokeSession.DestroyTestObject(go, 0, CommandName)) SmokeSession.ReleaseSpawn(go);
        }

        // -- Reporting (private copy mirroring SmokePlugin's shapes) ---------------

        private enum CheckStatus
        {
            Pass,
            Fail,
            Skip
        }

        private sealed class CheckResult
        {
            public CheckResult(CheckStatus status, string name, string message)
            {
                Status = status;
                Name = name ?? "";
                Message = message ?? "";
            }

            public CheckStatus Status { get; }
            public string Name { get; }
            public string Message { get; }
        }
    }
}
