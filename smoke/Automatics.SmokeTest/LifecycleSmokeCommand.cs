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
    internal static class LifecycleSmokeCommand
    {
        private const string CommandName = "automatics_smoke_lifecycle";

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
                "Turn Automatic Pickup off, on and off through its setting, check the runtime lifecycle, then restore the setting.",
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

            if (!SmokeSession.TryBegin(CommandName, out var runId, out var refusal))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {CommandName} was not started: {refusal}");
                return;
            }

            // Guard hands back the run's obligations and releases the lock however the body
            // ends, a throw included.
            _host.StartCoroutine(SmokeSession.Guard(runId, CommandName, Run(context, runId)));
        }

        private static IEnumerator Run(Terminal context, int runId)
        {
            var run = new LifecycleRun(runId);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {CommandName} started: automatic_pickup/module is turned off, on and off again, then restored");

            var steps = Contain(run.Execute(), run, "lifecycle_run");
            while (steps.MoveNext()) yield return steps.Current;

            run.RemoveSpawnedItem();

            var restore = Contain(run.Restore(), run, "lifecycle_restored_initial_state");
            while (restore.MoveNext()) yield return restore.Current;

            Report(context, run.Results);
        }

        // Advances a step sequence and turns an exception into a FAIL, so a throw in one phase
        // still lets the restore run. A yield cannot sit inside a try that has a catch, so the
        // step is taken inside one and the yield happens outside it.
        private static IEnumerator Contain(IEnumerator steps, LifecycleRun run, string name)
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
                    run.Fail(name, e.GetType().Name + ": " + e.Message);
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
                    Emit(context, LogLevel.Info, $"[Automatics Smoke] PASS {result.Name}");
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

            private enum WaitResult
            {
                Reached,
                Faulted,
                TimedOut,
                EditedByUser,
                WorldGone,
                Abandoned
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

                var expected = ExpectedPatches();
                if (expected.Count == 0)
                {
                    Fail(name, "no [HarmonyPatch] target could be read from Automatics.AutomaticPickup.Patches");
                    return;
                }

                var actual = PatchesUnder(_harmonyId);
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

            // Waits for the registry to report the expected state. Faulted is the failure
            // itself rather than a step on the way, unless the caller is asking for a
            // recovery. heldValue, when given, is the value the toggle must keep meanwhile.
            private IEnumerator WaitForState(ModuleState expected, bool faultIsFinal,
                bool worldRequired, AutomaticsModule? heldValue = null)
            {
                _wait = WaitResult.TimedOut;
                _waitReport = "";

                var started = Time.realtimeSinceStartup;
                var frames = 0;
                while (true)
                {
                    if (!SmokeSession.IsCurrent(RunId))
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
                            $"automatic_pickup/module was changed to {_toggle.Value} outside the check";
                        yield break;
                    }

                    if (worldRequired && (ZNetScene.instance == null || !Player.m_localPlayer))
                    {
                        _wait = WaitResult.WorldGone;
                        _waitReport = "the world was unloaded while the check ran";
                        yield break;
                    }

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
                            $"timed out after {elapsed:0.0}s ({frames} frames) waiting for {expected}; the registry reports {state} and the toggle is {_toggle.Value}";
                        yield break;
                    }

                    frames++;
                    yield return null;
                }
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

            private static HashSet<string> ExpectedPatches()
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                                           BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                var patches = new HashSet<string>(StringComparer.Ordinal);
                foreach (var method in typeof(PickupPatches).GetMethods(flags))
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

            private static HashSet<string> PatchesUnder(string harmonyId)
            {
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
                            if (patch.owner == harmonyId)
                                patches.Add(
                                    $"{DescribeMethod(target)} <- {patch.PatchMethod.DeclaringType?.Name}.{patch.PatchMethod.Name}");
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
