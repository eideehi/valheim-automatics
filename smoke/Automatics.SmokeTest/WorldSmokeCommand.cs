using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics;
using Automatics.AutomaticProcessing;
using Automatics.Valheim;
using BepInEx.Configuration;
using BepInEx.Logging;
using ModUtils;
using UnityEngine;
using Held = System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,
    Automatics.SmokeTest.SmokeSession.SettingRestoration>;

namespace Automatics.SmokeTest
{
    // In-game console command that drives the REAL Automatics entry points for
    // Storage / Door / Pickup / Processing against freshly spawned objects, asserts
    // observable results, cleans everything up, and logs PASS/FAIL/SKIP plus a
    // one-line Summary (mirroring SmokePlugin.cs). Out of scope: Farming/Feeding/Mining.
    // Two checks drive the container gate through Processing's store path.
    //
    // Registration: subscribe Hooks.OnInitTerminal += BuildCommand exactly once,
    // early (EnsureRegistered is called at the top of SmokePlugin's Start coroutine,
    // before the readiness wait). Terminal.m_terminalInitialized is private on the
    // Valheim Terminal type (Automatics reads it only via Harmony field injection),
    // so there is no immediate-build branch — the one-time terminal init fires the
    // subscribed callback. _commandBuilt keeps BuildCommand idempotent.
    //
    // The pickup check has to wait for vanilla to accept a fresh drop, so the command
    // runs as a coroutine on the smoke plugin rather than in the console call. The other
    // checks still run synchronously, each within a single frame, so nothing they
    // spawn lives long enough to be updated or saved. Only one smoke command runs at a
    // time, and what a run owes the game if it is cut short is held by SmokeSession.
    //
    // Item safety is asymmetric: a test item left behind is harmless, a real item removed
    // is not. Nothing is removed from an inventory unless it is provably the check's own.
    //
    // The run sets up what its checks need itself. Before the first check it turns on, for the
    // run, every module whose checks run (SmokeSetup.EnableModule) and moves the player to clear
    // ground when a container that is not its own is within WorldClearance of them
    // (SmokeSetup.Relocate); the player is moved back before the Summary. Each check writes the
    // settings it depends on (Set), the containers' search ranges among them, lowered so that
    // only the test chest is in reach, and the storage and pickup checks put inventory items
    // aside for themselves (SmokeSetup.Stash). Every setting goes back at the end and is reported
    // as world_settings_restored.
    //
    // Each check starts in a frame of its own and spawns in a spot of its own, relative to the
    // player, so nothing an earlier check placed is in its way: the storage chest 1.5 m ahead,
    // the door 2 m to the left, the pickup drop 1.5 m to the right, the smelter 3.5 m ahead
    // with its chest 1.5 m to the right of it, for the container checks a smelter 3.5 m
    // behind (the second one also 3.5 m to the left), each with its chest 1.5 m to the right
    // of it, and for the charging check the charging piece 3.5 m ahead and 3.5 m to the left
    // with its chest 1.5 m to the right of it.
    internal static class WorldSmokeCommand
    {
        private const string CommandName = "automatics_smoke_world";
        private const string Smelter = "$piece_smelter";

        // The storage check runs only when this argument is given: Store moves every item
        // Automatic Storage would store out of the player's inventory, not only the test Wood.
        private const string StorageArgument = "storage";

        private const string StorageOptIn =
            "opt-in: run `automatics_smoke_world storage` to include it. It moves every item Automatic Storage would store from your inventory into a test chest and hands them back; they may come back in different slots.";

        // The Container's m_name, which TryFindPrefabByObjectName compares against. The
        // GameObject is piece_chest_wood, but its m_name has no underscore; this is the
        // token the mod's own data matches (PieceChestWood in
        // Automatics/Data/Objects/AutomaticProcessing/container.json).
        private const string ChestWood = "$piece_chestwood";
        private const string WoodDoorObjectName = "$piece_wooddoor";
        private const string WoodPrefab = "Wood";

        // A bound on waiting for a fresh drop to become pickable, not an estimate of it:
        // vanilla refuses a drop for 0.5 s after it spawns.
        private const float PickupTimeoutSeconds = 5f;

        // The ranges the checks write for the run: storage_search_range just past the storage
        // chest, 1.5 m ahead, and the container search range of the smelter and the charging
        // piece just past their chest, 1.5 m beside them.
        private const int StorageRangeForRun = 2;
        private const int ContainerRangeForRun = 4;

        // How far from the player a container that is not the run's own is in reach of a check:
        // a station stands up to 3.5 m ahead or behind and 3.5 m aside (about 5 m), plus its
        // container range for the run, plus a metre. The storage range is well inside it.
        private const float WorldClearance = 10f;

        private const string SettingsRestored = "world_settings_restored";

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke World");

        private static MonoBehaviour _host;
        private static bool _registered;
        private static bool _commandBuilt;

        // The current run's settings (Set) and how turning on each module for it went. One run
        // at a time, so they are set at the start of Run.
        private static int _runId;
        private static Held _held = new Held();
        private static SmokeSetup.SetupResult _storageModule = new SmokeSetup.SetupResult();
        private static SmokeSetup.SetupResult _doorModule = new SmokeSetup.SetupResult();
        private static SmokeSetup.SetupResult _processingModule = new SmokeSetup.SetupResult();

        internal static void EnsureRegistered(MonoBehaviour host)
        {
            _host = host;
            if (_registered) return;
            _registered = true;

            // Terminal.m_terminalInitialized is private and cannot be referenced from
            // this assembly, so we cannot build immediately. Subscribe early (before
            // the readiness wait in SmokePlugin.Start) so the subscription is in place
            // before the terminal's one-time init fires. Mirrors Automatics.cs:131.
            Hooks.OnInitTerminal += BuildCommand;
        }

        private static void BuildCommand()
        {
            if (_commandBuilt) return;
            _commandBuilt = true;

            // 3-arg positional form; rely on ctor defaults for the cheat/network/etc.
            // flags. Valheim throws on duplicate command names, so the _commandBuilt
            // guard above keeps this single-shot even if OnInitTerminal fires twice.
            _ = new Terminal.ConsoleCommand(
                CommandName,
                "Run Automatics live-world smoke checks (door/pickup/processing; add 'storage' to include the storage check).",
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

            var arguments = args.Args.Skip(1).Where(x => !string.IsNullOrEmpty(x))
                .Select(x => x.ToLowerInvariant())
                .ToList();
            var argument = arguments.FirstOrDefault() ?? "";
            if (arguments.Count > 1 || (argument.Length > 0 && argument != StorageArgument))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] usage: {CommandName} [{StorageArgument}]");
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
            _host.StartCoroutine(SmokeSession.Guard(runId, CommandName,
                Run(context, runId, argument == StorageArgument)));
        }

        private static IEnumerator Run(Terminal context, int runId, bool includeStorage)
        {
            IEnumerator steps = null;
            try
            {
                _runId = runId;
                _held = new Held();
                var setUp = SetUp(context, includeStorage);
                while (setUp.MoveNext()) yield return setUp.Current;

                var results = new List<CheckResult>
                {
                    includeStorage
                        ? RunCheck("world_storage_store_moves_items", CheckStorageStoreMovesItems)
                        : CheckResult.Skip(StorageOptIn).WithName("world_storage_store_moves_items")
                };

                // Each check starts in a frame of its own. ZNetView.Destroy removes an object only
                // at the end of the frame, so a check that ran in the same frame would still find
                // the previous check's objects in the scene: the door's obstacle linecast hit the
                // storage check's chest and, correctly, did not open.
                yield return null;
                results.Add(SmokeSession.IsCurrent(runId)
                    ? RunCheck("world_door_open_sets_zdo_state", CheckDoorOpenSetsZdoState)
                    : CheckResult.Skip("the run was abandoned before this check")
                                 .WithName("world_door_open_sets_zdo_state"));

                yield return null;
                results.Add(SmokeSession.IsCurrent(runId)
                    ? RunCheck("world_door_interior_left_alone", CheckDoorInteriorLeftAlone)
                    : CheckResult.Skip("the run was abandoned before this check")
                                 .WithName("world_door_interior_left_alone"));

                yield return null;

                // The pickup check spans frames, so it is advanced here instead of by
                // RunCheck. A yield cannot sit inside a try that has a catch, so each step
                // is taken inside one and the yield happens outside it.
                CheckResult pickup = null;
                if (SmokeSession.IsCurrent(runId))
                    steps = CheckPickupItemDropToInventory(runId, result => pickup = result);
                else
                    pickup = CheckResult.Skip("the run was abandoned before this check");
                while (steps != null)
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
                        pickup = CheckResult.Fail("", e.GetType().Name + ": " + e.Message);
                        more = false;
                    }

                    if (!more) break;
                    yield return current;
                }

                results.Add((pickup ?? CheckResult.Fail("", "the check ended without a result"))
                    .WithName("world_pickup_itemdrop_to_inventory"));

                yield return null;
                results.Add(SmokeSession.IsCurrent(runId)
                    ? RunCheck("world_processing_smelter_refuel", CheckProcessingSmelterRefuel)
                    : CheckResult.Skip("the run was abandoned before this check")
                                 .WithName("world_processing_smelter_refuel"));

                yield return null;
                results.Add(SmokeSession.IsCurrent(runId)
                    ? RunCheck("world_container_store_after_spawn_keeps_stored_items",
                        CheckStoreAfterSpawnKeepsStoredItems)
                    : CheckResult.Skip("the run was abandoned before this check")
                                 .WithName("world_container_store_after_spawn_keeps_stored_items"));

                yield return null;
                results.Add(SmokeSession.IsCurrent(runId)
                    ? RunCheck("world_container_store_after_close_keeps_saved_changes",
                        CheckStoreAfterCloseKeepsSavedChanges)
                    : CheckResult.Skip("the run was abandoned before this check")
                                 .WithName("world_container_store_after_close_keeps_saved_changes"));

                yield return null;
                results.Add(SmokeSession.IsCurrent(runId)
                    ? RunCheck("world_processing_charge_takes_accepted_ammunition",
                        CheckChargeTakesAcceptedAmmunition)
                    : CheckResult.Skip("the run was abandoned before this check")
                                 .WithName("world_processing_charge_takes_accepted_ammunition"));

                // Every check's objects are gone by now: the settings go back, and the player, when
                // the setup moved them, before the Summary.
                var settings = RestoreSettings();
                if (settings != null) results.Add(settings);
                var back = new SmokeSetup.SetupResult();
                var home = SmokeSetup.ReturnToStart(runId, back);
                while (home.MoveNext()) yield return home.Current;
                if (!back.Ok)
                    Emit(context, LogLevel.Warning,
                        $"[Automatics Smoke] {CommandName}: you could not be moved back to where the command started yet ({back.Reason}); the smoke plugin tries again as the run ends");

                Report(context, results);
            }
            finally
            {
                // Reached when the body finishes or throws, not when the coroutine is stopped:
                // Unity never disposes a stopped coroutine. That path is SmokeSession's, from the
                // plugin's own hooks, and the drop the pickup check waits on is recorded there.
                // Guard hands back the run's obligations and releases the lock after this.
                (steps as IDisposable)?.Dispose();
            }
        }

        // -- Setup ---------------------------------------------------------------

        // Turns on, for the run, every module whose checks run, then moves the player to clear
        // ground when a container that is not the run's own is within WorldClearance. A step that
        // fails is left to the checks that need it, which SKIP and say why.
        private static IEnumerator SetUp(Terminal context, bool includeStorage)
        {
            const string noWorld = "no world was loaded when the run started";
            _storageModule = new SmokeSetup.SetupResult { Reason = noWorld };
            _doorModule = new SmokeSetup.SetupResult { Reason = noWorld };
            _processingModule = new SmokeSetup.SetupResult { Reason = noWorld };
            if (ZNetScene.instance == null || !Player.m_localPlayer) yield break;

            IEnumerator steps;
            if (includeStorage)
            {
                steps = SmokeSetup.EnableModule(_runId, _held,
                    global::Automatics.AutomaticStorage.Config.ModuleEntry, StorageRunning, _storageModule);
                while (steps.MoveNext()) yield return steps.Current;
            }

            steps = SmokeSetup.EnableModule(_runId, _held,
                global::Automatics.AutomaticDoor.Config.ModuleEntry, DoorRunning, _doorModule);
            while (steps.MoveNext()) yield return steps.Current;

            steps = SmokeSetup.EnableModule(_runId, _held,
                global::Automatics.AutomaticProcessing.Config.ModuleEntry, ProcessingRunning, _processingModule);
            while (steps.MoveNext()) yield return steps.Current;

            var player = Player.m_localPlayer;
            if (!player || ContainerNear(player.transform.position) == null) yield break;

            var moved = new SmokeSetup.SetupResult();
            steps = SmokeSetup.Relocate(_runId, ContainerNear, moved);
            while (steps.MoveNext()) yield return steps.Current;
            if (moved.Changed)
                Emit(context, LogLevel.Info,
                    $"[Automatics Smoke] {CommandName}: a container that is not the check's own was within {WorldClearance:0} m of you, so you were {moved.Reason}; you are moved back before the Summary");
            else if (!moved.Ok)
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {CommandName}: a container that is not the check's own is within {WorldClearance:0} m of you, and you could not be moved to clear ground ({moved.Reason}); the checks it is in reach of SKIP");
        }

        // The first container within WorldClearance of the site, or null.
        private static string ContainerNear(Vector3 site)
        {
            var container = global::Automatics.ContainerCache.GetAllInstance().FirstOrDefault(x =>
                x && Vector3.Distance(site, x.transform.position) <= WorldClearance);
            if (!container) return null;

            var p = container.transform.position;
            return $"{DisplayName(Objects.GetName(container))} at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) within {WorldClearance:0} m";
        }

        // The applied state, not the setting: the entry points the checks drive do nothing while
        // their module is not running.
        private static bool StorageRunning()
        {
            return ModuleRegistry.GetState(global::Automatics.AutomaticStorage.Module.Instance.Name) ==
                   ModuleState.Enabled && global::Automatics.AutomaticStorage.Module.Running;
        }

        private static bool DoorRunning()
        {
            return ModuleRegistry.GetState(global::Automatics.AutomaticDoor.Module.Instance.Name) ==
                   ModuleState.Enabled && global::Automatics.AutomaticDoor.Module.Running;
        }

        private static bool ProcessingRunning()
        {
            return ModuleRegistry.GetState(global::Automatics.AutomaticProcessing.Module.Instance.Name) ==
                   ModuleState.Enabled && global::Automatics.AutomaticProcessing.Module.Running;
        }

        private static CheckResult NotRunning(string module, SmokeSetup.SetupResult setUp)
        {
            return CheckResult.Skip(setUp.Ok
                ? $"{module} module is not running"
                : $"{module} module is not running: {setUp.Reason}");
        }

        // Writes a setting for the run when it does not hold value already; the session puts it
        // back at the end. Null when the setting holds value now; otherwise a SKIP that names the
        // setting and why it was not written. A setting this build does not have is left to the
        // check's own precondition.
        private static CheckResult Set(ConfigEntryBase entry, object value)
        {
            if (entry == null || Equals(entry.BoxedValue, value)) return null;
            return SmokeSetup.Write(_runId, _held, entry, value, out var refusal)
                ? null
                : CheckResult.Skip(
                    $"{entry.Definition.Section}/{entry.Definition.Key} could not be set for the run: {refusal}");
        }

        // An allow list with identifier added for the run.
        private static CheckResult Allow(ConfigEntry<StringList> entry, string identifier)
        {
            return entry == null || identifier == null || entry.Value.Contains(identifier)
                ? null
                : Set(entry, new StringList(entry.Value) { identifier });
        }

        private static string ChestIdentifier()
        {
            return ValheimObject.Container.GetIdentify(ChestWood, out var identifier) ? identifier : null;
        }

        // What a processing check sets for its piece: the process allowed, the wood chest allowed,
        // and the piece's container search range at ContainerRangeForRun.
        private static CheckResult SetUpProcessing(string piece, Process process)
        {
            var allowed = ProcessingSmokeCommand.PieceSetting<Process>("_allowProcessing", piece);
            return (allowed == null || (allowed.Value & process) != 0 ? null : Set(allowed, allowed.Value | process)) ??
                   Allow(SmokeSetup.Field<StringList>(typeof(global::Automatics.AutomaticProcessing.Config),
                       "_allowContainer"), ChestIdentifier()) ??
                   Set(ProcessingSmokeCommand.PieceSetting<int>("_containerSearchRange", piece), ContainerRangeForRun);
        }

        // A per-piece switch of Automatic Processing off for the run.
        private static CheckResult TurnOff(string field, string piece)
        {
            return Set(ProcessingSmokeCommand.PieceSetting<bool>(field, piece), false);
        }

        // Puts back every setting the run wrote and reports them as SmokeSetup.ReportSettings
        // does. Null when it wrote none.
        private static CheckResult RestoreSettings()
        {
            var results = SmokeSession.RestoreSettings(_runId);
            if (results.Count == 0) return null;

            var detail = string.Join("; ", results.Select(x => x.Value));
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                return CheckResult.Fail(SettingsRestored, detail);
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                 x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                return CheckResult.Skip("restoration skipped: " + detail).WithName(SettingsRestored);
            return CheckResult.Pass(detail).WithName(SettingsRestored);
        }

        private static void Report(Terminal context, List<CheckResult> results)
        {
            foreach (var result in results)
            {
                if (result.Status == CheckStatus.Pass)
                    Emit(context, LogLevel.Info, string.IsNullOrEmpty(result.Message)
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

        private static CheckResult RunCheck(string name, Func<CheckResult> check)
        {
            try
            {
                var result = check();
                return result.WithName(name);
            }
            catch (Exception e)
            {
                return CheckResult.Fail(name, e.GetType().Name + ": " + e.Message);
            }
        }

        // -- 4.1 Storage -----------------------------------------------------------

        private static CheckResult CheckStorageStoreMovesItems()
        {
            if (ZNetScene.instance == null) return CheckResult.Skip("ZNetScene.instance is null");
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");
            var playerInventory = player.GetInventory();
            if (playerInventory == null) return CheckResult.Skip("player inventory is null");

            if (!StorageRunning()) return NotRunning("automatic_storage", _storageModule);
            var storageConfig = typeof(global::Automatics.AutomaticStorage.Config);
            var range = Set(SmokeSetup.Field<int>(storageConfig, "_storageSearchRange"), StorageRangeForRun);
            if (range != null) return range;
            if (global::Automatics.AutomaticStorage.Config.StorageSearchRange < 2)
                return CheckResult.Skip("storage_search_range too small to reach a spawned chest");

            if (ObjectDB.instance == null) return CheckResult.Skip("ObjectDB.instance is null");

            if (!TryGetPrefab(WoodPrefab, out var woodPrefab))
                return CheckResult.Skip("Wood prefab not found");
            if (woodPrefab.GetComponent<ItemDrop>() == null)
                return CheckResult.Skip("Wood prefab lacks ItemDrop");

            var woodType = woodPrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_itemType;
            var allowed = Allow(SmokeSetup.Field<StringList>(storageConfig, "_allowedItemTypes"),
                              global::Automatics.AutomaticStorage.Config.GetItemTypeKey(woodType)) ??
                          Allow(SmokeSetup.Field<StringList>(storageConfig, "_allowContainer"), ChestIdentifier());
            if (allowed != null) return allowed;
            if (!global::Automatics.AutomaticStorage.Config.IsAllowedItemType(woodType))
                return CheckResult.Skip("Material item type not in allowed_item_types");

            if (!TryFindPrefabByObjectName<Container>(ChestWood, out var chestPrefab))
                return CheckResult.Skip($"{ChestWood} prefab not found");

            var forward = player.transform.forward;
            var origin = player.transform.position;

            using (var scope = new SpawnScope())
            {
                var chestGo = scope.Spawn(chestPrefab, origin + forward * 1.5f, Quaternion.identity);
                var chest = chestGo.GetComponent<Container>();
                if (chest == null) return CheckResult.Skip("spawned chest lacks Container");
                if (!Objects.GetZNetView(chest, out var chestNview) || chestNview == null ||
                    !chestNview.IsValid() || chestNview.GetZDO() == null)
                    return CheckResult.Skip("spawned chest ZNetView invalid");
                if (!chestNview.IsOwner())
                    return CheckResult.Skip("spawned chest not owned after claim");
                if (!ContainerAccess.IsAllowed(chest, global::Automatics.AutomaticStorage.Config.AllowContainer))
                    return CheckResult.Skip("spawned chest not in allow_container");

                var chestInventory = chest.GetInventory();
                if (chestInventory == null) return CheckResult.Skip("spawned chest inventory is null");

                // Everything in the chest after Store is handed back to the player below, so
                // it has to start empty or the hand-back would give the player items they
                // never had.
                if (chestInventory.NrOfItems() != 0)
                {
                    // Whatever it holds is not the check's, so the chest stays in the world.
                    scope.Keep(chestGo);
                    return CheckResult.Skip(
                        $"the spawned chest was not empty, so {ChestLocation(chestGo)} was left in the world holding: {ListItems(chestInventory.GetAllItems())}");
                }

                // AutomaticStorage.Store only routes to containers registered in
                // ContainerCache. If the chest's ContainerCache.Awake has not registered it
                // this frame (same-frame spawn timing / ZDO-at-Awake gating), Store cannot
                // see it — a benign prerequisite miss, so SKIP rather than FAIL (mirrors the
                // processing check's GetNearbyContainers pre-check).
                if (!global::Automatics.ContainerCache.GetAllInstance().Any(c => c == chest))
                    return CheckResult.Skip(
                        "spawned chest not registered in ContainerCache this frame; cannot drive Store");

                // Store can move eligible items other than the test Wood into allowed
                // containers in range, depending on their contents and routing settings. What
                // reached another container would stay there after the check, so it only
                // runs where this chest is the one container Store can reach.
                var otherTargets = CountOtherStorageTargets(player, chest);
                if (otherTargets > 0)
                    return CheckResult.Skip(
                        $"{otherTargets} other container(s) within storage_search_range would also receive items; run it away from other containers");

                var sample = PickableHarvester.CreatePickupItemData(woodPrefab, 1);
                if (sample == null) return CheckResult.Skip("could not build Wood sample item");

                // Everything outside the hotbar and every stack of Wood is put aside for the
                // check and put back at the end. So the test Wood gets a stack of its own outside
                // the hotbar, where Store may take it (the game adds Wood to a stack with room
                // first, a hotbar one included), and the chest cannot fill up with the player's
                // own items before the test Wood is reached.
                var stash = SmokeSetup.Stash(player,
                    match: x => x.m_gridPos.y > 0 || x.m_shared.m_name == sample.m_shared.m_name);
                try
                {
                    var result = StoreTestWood(player, playerInventory, woodPrefab, sample, scope, chestGo,
                        chestInventory);
                    return result.Status == CheckStatus.Pass && stash.Taken > 0
                        ? CheckResult.Pass(string.Join("; ", new[]
                        {
                            $"{stash.Taken} stack(s) of yours were put aside for the check and put back",
                            result.Message
                        }.Where(x => x.Length > 0)))
                        : result;
                }
                finally
                {
                    stash.Restore();
                }
            }
        }

        // The storage check from the test Wood on, with the player's items outside the hotbar
        // put aside.
        private static CheckResult StoreTestWood(Player player, Inventory playerInventory, GameObject woodPrefab,
            ItemDrop.ItemData sample, SpawnScope scope, GameObject chestGo, Inventory chestInventory)
        {
            // The player's own count of this exact Wood. The cleanup never takes them
            // below it, whatever else happens.
            var ownWood = PickableHarvester.CountExactItems(playerInventory, sample);

            var added = AddTestWood(player, playerInventory, woodPrefab, 5);
            if (added <= 0) return CheckResult.Skip("could not add test Wood outside the hotbar of the player inventory");

            // From here on the chest can hold the player's own items, so it is taken out
            // of the scope's destruction first and handed back only once it has been seen
            // empty again. Nothing between here and that point can destroy it.
            scope.Keep(chestGo);

            CheckResult result;
            string storedCounts = null;
            try
            {
                // Default routing needs the chest to already hold Wood. This extra test
                // item is included in the cleanup count after the chest is handed back.
                if (!global::Automatics.AutomaticStorage.Config.StoreIntoAnyContainer)
                {
                    var seeded = AddItemsLooped(chestInventory, woodPrefab, 1);
                    added += seeded;
                    Expect(seeded == 1, "could not seed test Wood in the chest");
                }

                result = StoreAndAssert(player, playerInventory, chestInventory, sample,
                    out storedCounts);
            }
            catch (Exception e)
            {
                // A failed assertion carries the counts already; when the fault came before
                // Store's result was recorded, what reached the chest is counted here.
                result = CheckResult.Fail("", storedCounts == null
                    ? $"{e.GetType().Name}: {e.Message} ({CountChest(chestInventory)}, result.StoredItems: not recorded)"
                    : e.GetType().Name + ": " + e.Message);
            }

            var leftInChest = ReturnChestContents(scope, chestGo, chestInventory, playerInventory);
            var testWoodLeft = RemoveTestWood(playerInventory, sample, added, ownWood);

            if (leftInChest != null)
                return CheckResult.Fail("", result.Status == CheckStatus.Fail
                    ? $"{leftInChest}; the check itself also failed: {result.Message}"
                    : storedCounts == null
                        ? leftInChest
                        : $"{leftInChest} ({storedCounts})");

            if (testWoodLeft > 0 && result.Status == CheckStatus.Pass)
                return CheckResult.Pass(
                    $"{testWoodLeft} test Wood could not be told apart from yours and was left in the inventory");

            return result;
        }

        // storedCounts is set as soon as Store has run, so that every FAIL message the caller
        // builds can carry what reached the chest.
        private static CheckResult StoreAndAssert(Player player, Inventory playerInventory,
            Inventory chestInventory, ItemDrop.ItemData sample, out string storedCounts)
        {
            storedCounts = null;

            // The sample has to be in a slot that Automatic Storage can take from.
            if (!HasEligibleTestItem(player, playerInventory, sample))
                return CheckResult.Skip("test item is in a hotbar or locked slot");

            var beforeCount = PickableHarvester.CountExactItems(playerInventory, sample);
            var beforeChestCount = chestInventory.GetAllItems().Sum(item => item.m_stack);
            var beforeWoodInChest = PickableHarvester.CountExactItems(chestInventory, sample);

            var result = global::Automatics.AutomaticStorage.AutomaticStorage.Store(player);

            var afterCount = PickableHarvester.CountExactItems(playerInventory, sample);
            var moved = beforeCount - afterCount;

            // The chest is the only container Store could reach. Exclude any seed Wood from
            // the counts; all newly stored items include the player's own eligible items,
            // which is why the total is not compared with the Wood delta.
            var storedInChest = chestInventory.GetAllItems().Sum(item => item.m_stack) - beforeChestCount;
            var woodInChest = PickableHarvester.CountExactItems(chestInventory, sample) - beforeWoodInChest;
            storedCounts =
                $"stored in the chest: {storedInChest}, result.StoredItems: {result.StoredItems}";

            Expect(result.StoredItems == storedInChest,
                $"result.StoredItems ({result.StoredItems}) should equal the items that reached the chest ({storedInChest})");
            Expect(result.TouchedContainers == 1,
                $"result.TouchedContainers ({result.TouchedContainers}) should be 1: the spawned chest is the only container in range ({storedCounts})");

            // Store takes the player's eligible items in inventory order, so with a full enough
            // inventory the one test chest can fill up with the player's own items before the
            // test Wood is reached. That says nothing about Store.
            if (moved <= 0 && woodInChest == 0 && storedInChest > 0 &&
                chestInventory.GetEmptySlots() == 0)
                return CheckResult.Skip(
                    $"the test chest filled up with your own items before any test Wood moved ({storedCounts}); run it with a near-empty inventory");

            Expect(moved > 0,
                $"Store should move at least one Wood out of the player inventory ({storedCounts})");
            Expect(woodInChest == moved,
                $"the chest should have gained the {moved} Wood that left the player (it gained {woodInChest}; {storedCounts})");

            return CheckResult.Pass();
        }

        private static string CountChest(Inventory chestInventory)
        {
            try
            {
                return $"stored in the chest: {chestInventory.GetAllItems().Sum(item => item.m_stack)}";
            }
            catch
            {
                return "stored in the chest: unreadable";
            }
        }

        // The selection AutomaticStorage.Store makes - range, allow_container, and whether the
        // player may write the container - applied to every container except the test chest.
        private static int CountOtherStorageTargets(Player player, Container testChest)
        {
            var origin = player.transform.position;
            var range = global::Automatics.AutomaticStorage.Config.StorageSearchRange;
            var allowed = global::Automatics.AutomaticStorage.Config.AllowContainer;

            return global::Automatics.ContainerCache.GetAllInstance().Count(container =>
                container != null &&
                container != testChest &&
                Vector3.Distance(origin, container.transform.position) <= range &&
                ContainerAccess.IsAllowed(container, allowed) &&
                ContainerAccess.CanWriteContainer(player, container));
        }

        // The chest is already kept when this runs. Everything in it goes back to the player,
        // and the chest is handed back to the scope for destruction only once it has been seen
        // empty. Returns null in that case, or a description of what was left and where - the
        // chest then stays in the world, so nothing in it is destroyed.
        private static string ReturnChestContents(SpawnScope scope, GameObject chestGo,
            Inventory chestInventory, Inventory playerInventory)
        {
            string moveFailure = null;
            try
            {
                playerInventory.MoveAll(chestInventory);
            }
            catch (Exception e)
            {
                moveFailure = e.GetType().Name + ": " + e.Message;
            }

            var where = ChestLocation(chestGo);

            List<ItemDrop.ItemData> left;
            try
            {
                left = new List<ItemDrop.ItemData>(chestInventory.GetAllItems());
            }
            catch (Exception e)
            {
                var unreadable =
                    $"{where} could not be read after its contents were returned ({e.GetType().Name}: {e.Message}); it was left in the world in case it still holds your items";
                Log.LogWarning($"[Automatics Smoke] world_storage_store_moves_items: {unreadable}");
                return unreadable;
            }

            if (left.Count == 0)
            {
                scope.Release(chestGo);
                return null;
            }

            var items = ListItems(left);
            var message =
                $"{left.Count} stack(s) could not be returned to your inventory{(moveFailure == null ? "" : $" ({moveFailure})")} and were left in {where}: {items}";
            Log.LogWarning($"[Automatics Smoke] world_storage_store_moves_items: {message}");
            return message;
        }

        // Removes the test Wood the storage check added, but never takes the player below the
        // count of that exact Wood they had before it started. Returns how many were left.
        private static int RemoveTestWood(Inventory inventory, ItemDrop.ItemData sample, int added,
            int ownWood)
        {
            try
            {
                var surplus = PickableHarvester.CountExactItems(inventory, sample) - ownWood;
                var removed = RemoveExactItems(inventory, sample, Mathf.Clamp(surplus, 0, added));
                return added - removed;
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    $"[Automatics Smoke] world_storage_store_moves_items: the test Wood was left in the inventory ({e.GetType().Name}: {e.Message})");
                return added;
            }
        }

        // -- 4.2 Door --------------------------------------------------------------

        private static CheckResult CheckDoorOpenSetsZdoState()
        {
            if (ZNetScene.instance == null) return CheckResult.Skip("ZNetScene.instance is null");
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");

            if (!DoorRunning()) return NotRunning("automatic_door", _doorModule);
            if (global::Automatics.AutomaticDoor.Config.DistanceForAutomaticOpening <= 0f)
                return CheckResult.Skip("distance_for_automatic_opening <= 0");
            var allowed = AllowWoodDoor();
            if (allowed != null) return allowed;
            if (!global::Automatics.AutomaticDoor.Config.AllowAutomaticDoor.Contains("WoodDoor"))
                return CheckResult.Skip("WoodDoor not in allow_automatic_door");

            if (!TryFindPrefabByObjectName<Door>(WoodDoorObjectName, out var doorPrefab))
                return CheckResult.Skip($"{WoodDoorObjectName} prefab not found");

            // To the left, where no other check spawns, so nothing another check placed can
            // stand between the player and the door.
            var left = -player.transform.right;
            var origin = player.transform.position;

            using (var scope = new SpawnScope())
            {
                var doorGo = scope.Spawn(doorPrefab, origin + left * 2f, Quaternion.identity);
                var door = doorGo.GetComponent<Door>();
                if (door == null) return CheckResult.Skip("spawned door lacks Door component");
                if (!Objects.HasValidOwnership(door, out var doorNview) || doorNview == null)
                    return CheckResult.Skip("spawned door ZNetView invalid or not owned");

                if (!global::Automatics.AutomaticDoor.AutomaticDoor.HasRegisteredDoor)
                    return CheckResult.Skip(
                        "no AutomaticDoor registered after spawn (Awake patch did not attach)");

                var before = doorNview.GetZDO().GetInt("state");
                global::Automatics.AutomaticDoor.AutomaticDoor.TryOpenNearby(player, Vector3.zero);
                var after = doorNview.GetZDO().GetInt("state");

                if (after == 0)
                {
                    // CloseGracePeriod only affects close, not open; retry once. A
                    // ward-denied area or an obstacle linecast can suppress the open.
                    global::Automatics.AutomaticDoor.AutomaticDoor.TryOpenNearby(player, Vector3.zero);
                    after = doorNview.GetZDO().GetInt("state");
                }

                Expect(before == 0,
                    $"freshly spawned door should start closed (state={before})");
                Expect(after != 0,
                    "door ZDO \"state\" should be non-zero after TryOpenNearby " +
                    "(possible ward/obstacle suppression if still 0)");

                return CheckResult.Pass();
            }
        }

        private static CheckResult AllowWoodDoor()
        {
            return Allow(SmokeSetup.Field<StringList>(typeof(global::Automatics.AutomaticDoor.Config),
                "_allowAutomaticDoor"), "WoodDoor");
        }

        // A door in a dungeon is left alone. Two open doors and no player in reach of either:
        // the one at ground level is the control that the close pass runs here, and the one
        // 5000 m up, where the game puts dungeon interiors, has to stay open. The player cannot
        // be in reach of a door up there, so the close pass is the side this check can drive.
        private static CheckResult CheckDoorInteriorLeftAlone()
        {
            if (ZNetScene.instance == null) return CheckResult.Skip("ZNetScene.instance is null");
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");
            if (player.InInterior())
                return CheckResult.Skip("the player is indoors; run it outdoors");

            if (!DoorRunning()) return NotRunning("automatic_door", _doorModule);
            var allowed = AllowWoodDoor();
            if (allowed != null) return allowed;
            if (!global::Automatics.AutomaticDoor.Config.AllowAutomaticDoor.Contains("WoodDoor"))
                return CheckResult.Skip("WoodDoor not in allow_automatic_door");

            if (!TryFindPrefabByObjectName<Door>(WoodDoorObjectName, out var doorPrefab))
                return CheckResult.Skip($"{WoodDoorObjectName} prefab not found");

            // Beyond the reach in which a player holds a door open (AutomaticDoor.TryCloseDoors).
            var holdOpenRange = Mathf.Max(
                global::Automatics.AutomaticDoor.Config.DistanceForAutomaticClosing,
                global::Automatics.AutomaticDoor.Config.DistanceForAutomaticOpening);
            var left = -player.transform.right;
            var groundPosition = player.transform.position + left * (holdOpenRange + 4f);
            var interiorPosition = player.transform.position + left * 2f + Vector3.up * 5000f;

            using (var scope = new SpawnScope())
            {
                var registeredBefore =
                    global::Automatics.AutomaticDoor.AutomaticDoor.RegisteredDoorCount;
                var groundDoor = scope.Spawn(doorPrefab, groundPosition, Quaternion.identity)
                                      .GetComponent<Door>();
                var interiorDoor = scope.Spawn(doorPrefab, interiorPosition, Quaternion.identity)
                                        .GetComponent<Door>();
                if (groundDoor == null || interiorDoor == null)
                    return CheckResult.Skip("spawned door lacks Door component");
                if (!Objects.HasValidOwnership(groundDoor, out var groundNview) ||
                    groundNview == null ||
                    !Objects.HasValidOwnership(interiorDoor, out var interiorNview) ||
                    interiorNview == null)
                    return CheckResult.Skip("spawned door ZNetView invalid or not owned");

                var registeredAfter =
                    global::Automatics.AutomaticDoor.AutomaticDoor.RegisteredDoorCount;
                Expect(registeredAfter == registeredBefore + 2,
                    "both spawned doors should be registered, the one up high included " +
                    $"(before={registeredBefore}, after={registeredAfter})");

                // Opened as a hand opens them.
                groundNview.InvokeRPC("UseDoor", true);
                interiorNview.InvokeRPC("UseDoor", true);
                var groundOpened = groundNview.GetZDO().GetInt("state");
                var interiorOpened = interiorNview.GetZDO().GetInt("state");
                if (groundOpened == 0 || interiorOpened == 0)
                    return CheckResult.Skip(
                        $"the spawned doors did not open (ground={groundOpened}, high={interiorOpened})");

                global::Automatics.AutomaticDoor.AutomaticDoor.TryCloseDoors();
                var groundAfter = groundNview.GetZDO().GetInt("state");
                var interiorAfter = interiorNview.GetZDO().GetInt("state");

                Expect(groundAfter == 0,
                    $"the open door at ground level with nobody near should close (state={groundAfter})");
                Expect(interiorAfter != 0,
                    $"the open door at dungeon height should stay open (state={interiorAfter})");

                return CheckResult.Pass();
            }
        }

        // -- 4.3 Pickup ------------------------------------------------------------

        // Reproduces AutomaticPickup.PickItemDrop: the harvester gate, then vanilla
        // ItemDrop.Pickup. ModuleDisabled is NOT a SKIP here — this drives the shared pickup
        // API directly, not the module.
        //
        // ItemDrop.Pickup(Humanoid) completes synchronously only when CanPickup() is true, and
        // CanPickup() is false for the first 0.5 s after the drop's Awake whoever owns it.
        // PickItemDrop runs over drops already lying in range, so for every drop older than
        // that - owned by the local peer, as every drop is in single player - it takes exactly
        // this synchronous branch. The check waits, bounded, for the drop to reach that state
        // and then makes the call PickItemDrop makes.
        //
        // Doing the call synchronously is also what lets the result be attributed. The two
        // inventory counts bracket one call with no frame boundary in between, so the only
        // Wood that can enter the inventory between them is what that call hands over, and
        // Wood is removed afterwards only when the drop was consumed within the call and
        // exactly its one Wood arrived. Anything short of that removes nothing.
        private static IEnumerator CheckPickupItemDropToInventory(int runId, Action<CheckResult> report)
        {
            if (ZNetScene.instance == null)
            {
                report(CheckResult.Skip("ZNetScene.instance is null"));
                yield break;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                report(CheckResult.Skip("Player.m_localPlayer is null"));
                yield break;
            }

            var playerInventory = player.GetInventory();
            if (playerInventory == null)
            {
                report(CheckResult.Skip("player inventory is null"));
                yield break;
            }

            if (ObjectDB.instance == null || ObjectDB.instance.m_items.Count == 0)
            {
                report(CheckResult.Skip("ObjectDB not ready"));
                yield break;
            }

            if (!TryGetPrefab(WoodPrefab, out var woodPrefab))
            {
                report(CheckResult.Skip("Wood prefab not found"));
                yield break;
            }

            if (woodPrefab.GetComponent<ItemDrop>() == null)
            {
                report(CheckResult.Skip("Wood prefab lacks ItemDrop"));
                yield break;
            }

            var sample = PickableHarvester.CreatePickupItemData(woodPrefab, 1);
            if (sample == null)
            {
                report(CheckResult.Skip("could not build Wood sample item"));
                yield break;
            }

            var right = player.transform.right;
            var origin = player.transform.position;

            // Room for the one Wood: a free slot and its weight, made by putting items of yours
            // aside for the check; they are put back at the end.
            var stash = SmokeSetup.Stash(player, 1, sample.GetWeight());
            GameObject go = null;
            var woodMayBeLeft = false;
            try
            {
                if (playerInventory.GetEmptySlots() == 0)
                {
                    report(CheckResult.Skip($"inventory full, and putting items aside left no free slot ({stash.Report})"));
                    yield break;
                }

                // Spawned outside a SpawnScope: the drop belongs to SmokeSession, which destroys
                // it only while it still holds the one Wood the check gives it (see finally).
                go = UnityEngine.Object.Instantiate(woodPrefab, origin + right * 1.5f,
                    Quaternion.identity);

                // Recorded before the first frame boundary, so a run that is cut short still
                // has the drop removed.
                SmokeSession.TrackSpawn(go, 1);
                if (Objects.GetZNetView(go.transform, out var spawnedNview) && spawnedNview != null &&
                    spawnedNview.IsValid() && !spawnedNview.IsOwner())
                    spawnedNview.ClaimOwnership();

                var spawnedAt = Time.time;
                var itemDrop = go.GetComponent<ItemDrop>();
                if (itemDrop == null)
                {
                    report(CheckResult.Skip("spawned object lacks ItemDrop"));
                    yield break;
                }

                itemDrop.m_itemData.m_stack = 1;
                itemDrop.m_itemData.m_worldLevel = (byte)Game.m_worldLevel;
                ItemDrop.OnCreateNew(itemDrop);

                if (!Objects.GetZNetView(itemDrop, out var dropNview) || dropNview == null ||
                    dropNview.GetZDO() == null)
                {
                    report(CheckResult.Skip("spawned ItemDrop ZNetView/ZDO null"));
                    yield break;
                }

                // Left out of every world save, so a drop that outlives an interrupted run can
                // never end up in the user's world.
                dropNview.GetZDO().Persistent = false;

                // Keeps vanilla auto-pickup and Automatic Pickup's own pass off this drop
                // while the check waits. Both skip a drop whose m_autoPickup is false
                // (vanilla's flag for an item the player dropped), and ItemDrop.Pickup never
                // reads it.
                itemDrop.m_autoPickup = false;

                // Keeps real drops from merging into this one. Vanilla ItemDrop.AutoStackItems -
                // run from SlowUpdate, and only while more than 200 drops exist - pulls nearby
                // drops of the same name and quality into this drop unless its m_haveAutoStacked
                // is already set. That relies on the private field keeping its 1.0.7 name and
                // meaning; if it is gone, the stack check before the drop is destroyed still
                // catches a merge. The other direction - this drop pulled into a real one - only
                // ends the check as "disappeared" and removes nothing.
                var haveAutoStacked = typeof(ItemDrop).GetField("m_haveAutoStacked",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (haveAutoStacked != null && haveAutoStacked.FieldType == typeof(bool))
                    haveAutoStacked.SetValue(itemDrop, true);

                // Pickability runs on game time, which stands still while the game is paused, so
                // a wait that saw a pause cannot tell anything about the drop.
                var pausedDuringWait = Game.IsPaused();
                var waitStarted = Time.realtimeSinceStartup;
                while (SmokeSession.IsCurrent(runId) && !IsConsumed(go, itemDrop) &&
                       !itemDrop.CanPickup() &&
                       Time.realtimeSinceStartup - waitStarted < PickupTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) pausedDuringWait = true;
                }

                if (!SmokeSession.IsCurrent(runId))
                {
                    report(CheckResult.Skip("inconclusive: the run was abandoned before the pickup"));
                    yield break;
                }

                if (!player)
                {
                    report(CheckResult.Skip("inconclusive: the local player went away while the check waited"));
                    yield break;
                }

                if (IsConsumed(go, itemDrop))
                {
                    report(CheckResult.Fail("",
                        "the test drop disappeared before it could be picked up; its m_autoPickup was false, so neither vanilla auto-pickup nor Automatic Pickup should have taken it. Nothing was removed from the inventory"));
                    yield break;
                }

                if (!itemDrop.CanPickup() && pausedDuringWait)
                {
                    report(CheckResult.Skip(
                        $"inconclusive: the game was paused while the check waited up to {PickupTimeoutSeconds:0.#}s for the drop to become pickable"));
                    yield break;
                }

                if (!itemDrop.CanPickup())
                {
                    report(CheckResult.Fail("",
                        $"the drop did not become pickable within {PickupTimeoutSeconds:0.#}s " +
                        $"(owner={dropNview.IsOwner()}, sinceSpawn={Time.time - spawnedAt:0.00}s, " +
                        $"timeScale={Time.timeScale:0.##}, paused={Game.IsPaused()})"));
                    yield break;
                }

                if (!PickableHarvester.CanAddItem(player, itemDrop.m_itemData))
                {
                    report(CheckResult.Skip($"harvester gate rejects add (weight/space) after items were put aside ({stash.Report})"));
                    yield break;
                }

                // No frame boundary from here to the end of the check.
                var before = PickableHarvester.CountExactItems(playerInventory, sample);
                woodMayBeLeft = true;
                itemDrop.Pickup(player);
                var delta = PickableHarvester.CountExactItems(playerInventory, sample) - before;
                var consumed = IsConsumed(go, itemDrop);
                if (delta <= 0) woodMayBeLeft = false;

                if (!consumed || delta != 1)
                {
                    report(CheckResult.Fail("",
                        $"ItemDrop.Pickup did not hand the drop over within the call although CanPickup() was true (consumed={consumed}, woodDelta={delta}); nothing was removed from the inventory"));
                    yield break;
                }

                // Proven to be the test drop's Wood: removed at once, in the same frame.
                if (RemoveExactItems(playerInventory, sample, 1) == 1) woodMayBeLeft = false;

                var room = stash.Taken > 0
                    ? $"{stash.Taken} stack(s) of yours were put aside to make room and put back"
                    : "";
                report(woodMayBeLeft
                    ? CheckResult.Pass(string.Join("; ", new[]
                        { room, "the test Wood could not be removed afterwards and was left in the inventory" }
                        .Where(x => x.Length > 0)))
                    : CheckResult.Pass(room));
            }
            finally
            {
                stash.Restore();
                if (woodMayBeLeft)
                    Log.LogWarning(
                        "[Automatics Smoke] world_pickup_itemdrop_to_inventory: the Wood this check picked up was not removed, because it could not be attributed to the test drop; at most one test Wood was left in the inventory");

                // A drop still there is destroyed only while it holds the one Wood the check gave
                // it; one that grew has absorbed real Wood and is left in the world. A removal
                // that fails stays recorded with the session for the next trigger.
                if (SmokeSession.DestroyTestObject(go, 1, "world_pickup_itemdrop_to_inventory"))
                    SmokeSession.ReleaseSpawn(go);
            }
        }

        // Humanoid.Pickup destroys the drop through ZNetScene.Destroy, which drops the ZDO
        // at once and the GameObject at the end of the frame.
        private static bool IsConsumed(GameObject go, ItemDrop itemDrop)
        {
            return !go || !itemDrop ||
                   !Objects.GetZNetView(itemDrop, out var nview) || nview == null ||
                   !nview.IsValid() || nview.GetZDO() == null;
        }

        // -- 4.4 Processing --------------------------------------------------------

        private static CheckResult CheckProcessingSmelterRefuel()
        {
            if (ZNetScene.instance == null) return CheckResult.Skip("ZNetScene.instance is null");
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");

            // The applied state, not the setting: SmelterProcess.QuickRefuel passes its input
            // through untouched while the module is not running.
            if (!ProcessingRunning()) return NotRunning("automatic_processing", _processingModule);
            var setUp = SetUpProcessing(Smelter, Process.Refuel) ??
                        TurnOff("_refuelOnlyWhenMaterialsSupplied", Smelter);
            if (setUp != null) return setUp;
            if (!global::Automatics.AutomaticProcessing.Logics.IsAllowProcessing(Smelter, Process.Refuel))
                return CheckResult.Skip("Refuel not allowed for smelter in config");

            if (!TryFindPrefabByObjectName<global::Smelter>(Smelter, out var smelterPrefab))
                return CheckResult.Skip($"{Smelter} prefab not found");
            if (!TryFindPrefabByObjectName<Container>(ChestWood, out var chestPrefab))
                return CheckResult.Skip($"{ChestWood} prefab not found");

            var forward = player.transform.forward;
            var right = player.transform.right;
            var origin = player.transform.position;

            using (var scope = new SpawnScope())
            {
                var smelterGo = scope.Spawn(smelterPrefab, origin + forward * 3.5f, Quaternion.identity);
                var smelter = smelterGo.GetComponent<global::Smelter>();
                if (smelter == null) return CheckResult.Skip("spawned smelter lacks Smelter component");
                if (!Objects.GetZNetView(smelter, out var smelterNview) || smelterNview == null ||
                    !smelterNview.IsValid() || smelterNview.GetZDO() == null)
                    return CheckResult.Skip("spawned smelter ZNetView invalid");
                if (!smelterNview.IsOwner())
                    return CheckResult.Skip("spawned smelter not owned after claim");
                if (smelter.m_fuelItem == null)
                    return CheckResult.Skip("smelter has no fuel item");

                var fuelPrefab = smelter.m_fuelItem.gameObject;
                var fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;

                // refuel_only_when_materials_supplied requires queued ore; fresh smelter has none.
                if (global::Automatics.AutomaticProcessing.Config.RefuelOnlyWhenMaterialsSupplied(Smelter) &&
                    smelterNview.GetZDO().GetInt("queued") == 0)
                    return CheckResult.Skip(
                        "refuel_only_when_materials_supplied requires queued ore");

                var smelterPos = smelter.transform.position;
                var chestGo = scope.Spawn(chestPrefab, smelterPos + right * 1.5f,
                    Quaternion.identity);
                var chest = chestGo.GetComponent<Container>();
                if (chest == null) return CheckResult.Skip("spawned chest lacks Container");
                if (!Objects.GetZNetView(chest, out var chestNview) || chestNview == null ||
                    !chestNview.IsValid() || chestNview.GetZDO() == null)
                    return CheckResult.Skip("spawned chest ZNetView invalid");
                if (!chestNview.IsOwner())
                    return CheckResult.Skip("spawned chest not owned after claim");
                if (!ContainerAccess.IsAllowed(chest,
                        global::Automatics.AutomaticProcessing.Config.AllowContainer))
                    return CheckResult.Skip("spawned chest not in allow_container");

                var chestInventory = chest.GetInventory();
                if (chestInventory == null) return CheckResult.Skip("spawned chest inventory is null");

                var minFuelCount =
                    global::Automatics.AutomaticProcessing.Config.FuelCountOfSuppressProcessing(Smelter);
                var seeded = AddItemsLooped(chestInventory, fuelPrefab, minFuelCount + 4);

                // The fuel just seeded is the check's own; the scope destroys this chest only
                // while it holds nothing else and no more of it.
                scope.AllowContents(chestGo, fuelName, seeded);
                if (seeded <= minFuelCount)
                    return CheckResult.Skip("could not seed enough fuel into the chest");

                // Reset the per-second container cache and skip-sets before driving the entry.
                global::Automatics.AutomaticProcessing.Logics.Cleanup();
                global::Automatics.AutomaticProcessing.SmelterProcess.Cleanup();

                var sources = global::Automatics.AutomaticProcessing.Logics
                    .GetNearbyContainers(Smelter, smelterPos)
                    .Select(x => x.container)
                    .ToList();
                if (!sources.Contains(chest))
                    return CheckResult.Skip("no fuel source container in range");

                // QuickRefuel can take its fuel from any container in range. Another one would
                // give up the user's own fuel and fail the delta below for a reason that is not
                // a regression, so the check only runs where the spawned chest is the only
                // source.
                if (sources.Count > 1)
                    return CheckResult.Skip(
                        $"{sources.Count - 1} other container(s) within the smelter's container range could supply fuel; run it away from other containers");

                var containerFuelBefore = chestInventory.CountItems(fuelName);
                var fuelBefore = smelterNview.GetZDO().GetFloat("fuel");

                var ret = global::Automatics.AutomaticProcessing.SmelterProcess
                    .QuickRefuel(smelter, smelterNview, 1f, fuelBefore);

                Expect(Mathf.Approximately(ret, fuelBefore + 1f),
                    $"QuickRefuel should return fuelBefore+1 ({fuelBefore + 1f}), got {ret}");
                Expect(Mathf.Approximately(smelterNview.GetZDO().GetFloat("fuel"), fuelBefore + 1f),
                    "smelter ZDO \"fuel\" should be fuelBefore+1 after QuickRefuel");

                var containerFuelAfter = chestInventory.CountItems(fuelName);
                const int consumedFuelPerRefuel = 1;
                Expect(containerFuelBefore - containerFuelAfter == consumedFuelPerRefuel,
                    $"container fuel should drop by {consumedFuelPerRefuel} (delta {containerFuelBefore - containerFuelAfter})");

                return CheckResult.Pass();
            }
            // Best-effort cache reset after the run so a second invocation re-reads fresh state.
        }

        // -- 4.5 Container gate ----------------------------------------------------

        // A chest whose saved data holds items its inventory has not loaded yet, as when it has
        // just spawned with stored items, keeps them when a module writes it: the write builds
        // on the saved contents, not on the empty inventory.
        private static CheckResult CheckStoreAfterSpawnKeepsStoredItems()
        {
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");
            var storeOnly = TurnOff("_storeOnlyIfProductExists", Smelter);
            if (storeOnly != null) return storeOnly;
            if (global::Automatics.AutomaticProcessing.Config.StoreOnlyIfProductExists(Smelter))
                return CheckResult.Skip(
                    "store_only_if_product_exists is on for the smelter, so it never stores into a chest whose inventory is still empty");

            using (var scope = new SpawnScope())
            {
                var transform = player.transform;
                var skip = SetUpStoreFixture(scope, transform.position - transform.forward * 3.5f,
                    out var fixture);
                if (skip != null) return skip;

                const int stored = 3;
                scope.AllowContents(fixture.ChestGo, fixture.ProductName, stored + 1);
                WriteSavedContents(fixture, stored);
                if (fixture.ChestInventory.NrOfItems() != 0)
                    return CheckResult.Skip(
                        "the chest loaded its saved items before the check could write it");

                var accepted = global::Automatics.AutomaticProcessing.SmelterProcess.Store(
                    fixture.Smelter, fixture.Ore, 1);
                var held = fixture.ChestInventory.CountItems(fixture.ProductName);
                var saved = CountSavedContents(fixture);

                Expect(accepted, "SmelterProcess.Store should store the product in the chest");
                Expect(held == stored + 1 && saved == stored + 1,
                    $"the chest should keep its {stored} stored items and gain 1: its inventory holds {held}, its saved data {saved}");
                return CheckResult.Pass();
            }
        }

        // While another game owns the chest, open there or closed, a store writes nothing to it
        // and does not take it. Once the chest is this game's again, a store in the same second
        // keeps what the other game saved instead of saving this game's older copy over it.
        private static CheckResult CheckStoreAfterCloseKeepsSavedChanges()
        {
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");

            using (var scope = new SpawnScope())
            {
                var transform = player.transform;
                var skip = SetUpStoreFixture(scope,
                    transform.position - transform.forward * 3.5f - transform.right * 3.5f,
                    out var fixture);
                if (skip != null) return skip;

                const int seeded = 2;
                const int changed = 5;
                scope.AllowContents(fixture.ChestGo, fixture.ProductName, changed + 1);
                if (AddItemsLooped(fixture.ChestInventory, fixture.Product, seeded) != seeded)
                    return CheckResult.Skip("could not seed the chest");

                var view = fixture.ChestView;
                var zdo = view.GetZDO();
                try
                {
                    // Another player's game opens the chest: it becomes the owner and marks the
                    // chest in use.
                    zdo.SetOwner(ZDOMan.GetSessionID() + 1);
                    zdo.Set(ZDOVars.s_inUse, 1);
                    var storedWhileOpen = global::Automatics.AutomaticProcessing.SmelterProcess.Store(
                        fixture.Smelter, fixture.Ore, 1);

                    // It saves a change and closes the chest, and still owns it.
                    WriteSavedContents(fixture, changed);
                    zdo.Set(ZDOVars.s_inUse, 0);
                    var storedAfterClose = global::Automatics.AutomaticProcessing.SmelterProcess.Store(
                        fixture.Smelter, fixture.Ore, 1);
                    var taken = view.IsOwner();
                    Expect(!storedWhileOpen && !storedAfterClose && !taken,
                        $"nothing should be stored in a chest another game owns, nor the chest taken: stored while open {storedWhileOpen}, after close {storedAfterClose}, taken {taken}");

                    // The chest is this game's again, and a store follows within the second.
                    view.ClaimOwnership();
                    var accepted = global::Automatics.AutomaticProcessing.SmelterProcess.Store(
                        fixture.Smelter, fixture.Ore, 1);
                    var held = fixture.ChestInventory.CountItems(fixture.ProductName);
                    var saved = CountSavedContents(fixture);

                    Expect(accepted, "SmelterProcess.Store should store the product once the chest is this game's");
                    Expect(held == changed + 1 && saved == changed + 1,
                        $"the chest should hold the other game's {changed} plus 1: its inventory holds {held}, its saved data {saved}");
                }
                finally
                {
                    // Only the owner removes the chest's saved data with it, so it is this game's
                    // again however the check ended.
                    if (view && view.IsValid())
                    {
                        if (!view.IsOwner()) view.ClaimOwnership();
                        zdo.Set(ZDOVars.s_inUse, 0);
                    }
                }

                return CheckResult.Pass();
            }
        }

        // A smelter and a chest 1.5 m to the right of it, the chest the only container in the
        // smelter's range. SmelterProcess.Store, the write both container checks drive, puts
        // the smelter's product into the nearest container in range through the container gate.
        private sealed class StoreFixture
        {
            public global::Smelter Smelter { get; set; }
            public string Ore { get; set; }
            public GameObject Product { get; set; }
            public string ProductName { get; set; }
            public GameObject ChestGo { get; set; }
            public Container Chest { get; set; }
            public ZNetView ChestView { get; set; }
            public Inventory ChestInventory { get; set; }
        }

        private static CheckResult SetUpStoreFixture(SpawnScope scope, Vector3 smelterPosition,
            out StoreFixture fixture)
        {
            fixture = null;
            if (ZNetScene.instance == null) return CheckResult.Skip("ZNetScene.instance is null");
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");

            // The applied state, not the setting: SmelterProcess.Store stores nothing while the
            // module is not running.
            if (!ProcessingRunning()) return NotRunning("automatic_processing", _processingModule);
            var setUp = SetUpProcessing(Smelter, Process.Store);
            if (setUp != null) return setUp;
            if (!global::Automatics.AutomaticProcessing.Logics.IsAllowProcessing(Smelter, Process.Store))
                return CheckResult.Skip("Store not allowed for smelter in config");

            if (!TryFindPrefabByObjectName<global::Smelter>(Smelter, out var smelterPrefab))
                return CheckResult.Skip($"{Smelter} prefab not found");
            if (!TryFindPrefabByObjectName<Container>(ChestWood, out var chestPrefab))
                return CheckResult.Skip($"{ChestWood} prefab not found");

            var smelterGo = scope.Spawn(smelterPrefab, smelterPosition, Quaternion.identity);
            var smelter = smelterGo.GetComponent<global::Smelter>();
            if (smelter == null) return CheckResult.Skip("spawned smelter lacks Smelter component");
            if (!Objects.HasValidOwnership(smelter))
                return CheckResult.Skip("spawned smelter not owned after claim");
            var conversion = smelter.m_conversion.FirstOrDefault(x => x != null && x.m_from && x.m_to);
            if (conversion == null) return CheckResult.Skip("smelter has no conversion");

            var smelterPos = smelter.transform.position;
            var chestGo = scope.Spawn(chestPrefab, smelterPos + player.transform.right * 1.5f,
                Quaternion.identity);
            var chest = chestGo.GetComponent<Container>();
            if (chest == null) return CheckResult.Skip("spawned chest lacks Container");
            if (!Objects.GetZNetView(chest, out var chestView) || chestView == null ||
                !chestView.IsValid() || chestView.GetZDO() == null)
                return CheckResult.Skip("spawned chest ZNetView invalid");
            if (!chestView.IsOwner())
                return CheckResult.Skip("spawned chest not owned after claim");
            if (!ContainerAccess.IsAllowed(chest,
                    global::Automatics.AutomaticProcessing.Config.AllowContainer))
                return CheckResult.Skip("spawned chest not in allow_container");
            var chestInventory = chest.GetInventory();
            if (chestInventory == null) return CheckResult.Skip("spawned chest inventory is null");
            if (chestInventory.NrOfItems() != 0)
            {
                scope.Keep(chestGo);
                return CheckResult.Skip(
                    $"the spawned chest was not empty, so {ChestLocation(chestGo)} was left in the world holding: {ListItems(chestInventory.GetAllItems())}");
            }

            // Reset the per-second container cache before reading it.
            global::Automatics.AutomaticProcessing.Logics.Cleanup();
            var sources = global::Automatics.AutomaticProcessing.Logics
                .GetNearbyContainers(Smelter, smelterPos)
                .Select(x => x.container)
                .ToList();
            if (!sources.Contains(chest))
                return CheckResult.Skip("the spawned chest is not within the smelter's container range");
            // Store puts the product into the nearest container in range, so the check only
            // runs where no container of yours could receive it.
            if (sources.Count > 1)
                return CheckResult.Skip(
                    $"{sources.Count - 1} other container(s) within the smelter's container range could receive the product; run it away from other containers");

            fixture = new StoreFixture
            {
                Smelter = smelter,
                Ore = conversion.m_from.gameObject.name,
                Product = conversion.m_to.gameObject,
                ProductName = conversion.m_to.m_itemData.m_shared.m_name,
                ChestGo = chestGo,
                Chest = chest,
                ChestView = chestView,
                ChestInventory = chestInventory
            };
            return null;
        }

        // Replaces the chest's saved data with amount of the product without loading it into the
        // chest's inventory, as a save by another game, or a chest streaming in, leaves it.
        private static void WriteSavedContents(StoreFixture fixture, int amount)
        {
            var saved = new Inventory("smoke", null, fixture.Chest.m_width, fixture.Chest.m_height);
            AddItemsLooped(saved, fixture.Product, amount);
            var package = new ZPackage();
            saved.Save(package);
            fixture.ChestView.GetZDO().Set(ZDOVars.s_items, package.GetArray());
        }

        // The product units in the chest's saved data, which is what the world keeps.
        private static int CountSavedContents(StoreFixture fixture)
        {
            var data = fixture.ChestView.GetZDO().GetByteArray(ZDOVars.s_items);
            if (data == null) return 0;
            var saved = new Inventory("smoke", null, fixture.Chest.m_width, fixture.Chest.m_height);
            saved.Load(new ZPackage(data));
            return saved.CountItems(fixture.ProductName);
        }

        // -- 4.6 Charging ----------------------------------------------------------

        // Charging takes only ammunition the piece accepts, and keeps each kind's reserve. Ahead
        // of the accepted kind above its reserve sit an item of the piece's ammunition type that
        // the piece refuses and an accepted kind at its reserve; both stay where they are, and
        // the piece gains one of the accepted kind above its reserve.
        private static CheckResult CheckChargeTakesAcceptedAmmunition()
        {
            if (ZNetScene.instance == null) return CheckResult.Skip("ZNetScene.instance is null");
            var player = Player.m_localPlayer;
            if (player == null) return CheckResult.Skip("Player.m_localPlayer is null");

            // The applied state, not the setting: TurretProcess.Charge does nothing while the
            // module is not running.
            if (!ProcessingRunning()) return NotRunning("automatic_processing", _processingModule);

            var prefab = ZNetScene.instance.m_prefabs.FirstOrDefault(x =>
                x && x.GetComponent<Turret>() && ChargeKinds(x.GetComponent<Turret>()).Count >= 3);
            if (!prefab)
                return CheckResult.Skip("no piece that charges with three kinds of ammunition was found");
            var pieceName = prefab.GetComponent<Turret>().m_name;

            // A reserve of 0 leaves no ammunition at its reserve, so it is raised to 1 for the run.
            var reserveSetting =
                ProcessingSmokeCommand.PieceSetting<int>("_numberOfItemsToStopCharge", pieceName);
            var setUp = SetUpProcessing(pieceName, Process.Charge) ??
                        (reserveSetting == null || reserveSetting.Value >= 1 ? null : Set(reserveSetting, 1));
            if (setUp != null) return setUp;
            if (!global::Automatics.AutomaticProcessing.Logics.IsAllowProcessing(pieceName, Process.Charge))
                return CheckResult.Skip("Charge not allowed for the piece in config");
            var reserve = global::Automatics.AutomaticProcessing.Config.NumberOfItemsToStopCharge(pieceName);
            if (reserve < 1)
                return CheckResult.Skip(
                    "number_of_items_to_stop_charge is 0 for the piece, so no ammunition can sit at its reserve");
            if (!TryFindPrefabByObjectName<Container>(ChestWood, out var chestPrefab))
                return CheckResult.Skip($"{ChestWood} prefab not found");

            using (var scope = new SpawnScope())
            {
                var transform = player.transform;
                var pieceGo = scope.Spawn(prefab,
                    transform.position + transform.forward * 3.5f - transform.right * 3.5f,
                    Quaternion.identity);
                var piece = pieceGo.GetComponent<Turret>();
                if (piece == null) return CheckResult.Skip("the spawned piece lacks its charging component");
                if (!Objects.GetZNetView(piece, out var pieceView) || pieceView == null ||
                    !pieceView.IsValid() || !pieceView.IsOwner())
                    return CheckResult.Skip("the spawned piece is not owned after claim");
                if (piece.GetAmmo() != 0) return CheckResult.Skip("the spawned piece came charged");

                // The accepted list belongs to this spawned piece only. One kind leaves it, so the
                // chest can hold an item of the right type that the piece refuses.
                var kinds = ChargeKinds(piece);
                var refused = kinds[0];
                var atReserve = kinds[1];
                var aboveReserve = kinds[2];
                piece.m_allowedAmmo.RemoveAll(x => x.m_ammo == refused);

                var chestGo = scope.Spawn(chestPrefab,
                    piece.transform.position + transform.right * 1.5f, Quaternion.identity);
                var chest = chestGo.GetComponent<Container>();
                if (chest == null) return CheckResult.Skip("spawned chest lacks Container");
                if (!Objects.GetZNetView(chest, out var chestView) || chestView == null ||
                    !chestView.IsValid() || !chestView.IsOwner())
                    return CheckResult.Skip("spawned chest not owned after claim");
                if (!ContainerAccess.IsAllowed(chest,
                        global::Automatics.AutomaticProcessing.Config.AllowContainer))
                    return CheckResult.Skip("spawned chest not in allow_container");
                var chestInventory = chest.GetInventory();
                if (chestInventory == null) return CheckResult.Skip("spawned chest inventory is null");
                if (chestInventory.NrOfItems() != 0)
                {
                    scope.Keep(chestGo);
                    return CheckResult.Skip(
                        $"the spawned chest was not empty, so {ChestLocation(chestGo)} was left in the world holding: {ListItems(chestInventory.GetAllItems())}");
                }

                // The refused kind is above the reserve too, so taking it would not be held back
                // by the reserve.
                var seeds = new[]
                {
                    (kind: refused, amount: reserve + 2),
                    (kind: atReserve, amount: reserve),
                    (kind: aboveReserve, amount: reserve + 2)
                };
                foreach (var (kind, amount) in seeds)
                {
                    var name = kind.m_itemData.m_shared.m_name;
                    scope.AllowContents(chestGo, name, amount);
                    if (AddItemsLooped(chestInventory, kind.gameObject, amount) != amount)
                        return CheckResult.Skip("could not seed the chest");
                }

                // The game's lookup returns the lowest slot of the type, and the fallback walks
                // the items in order, so the order has to be the one described above.
                var items = chestInventory.GetAllItems();
                int Slot(ItemDrop kind) =>
                    items.Where(x => x.m_dropPrefab == kind.gameObject)
                         .Min(x => x.m_gridPos.y * chest.m_width + x.m_gridPos.x);
                int Order(ItemDrop kind) => items.FindIndex(x => x.m_dropPrefab == kind.gameObject);
                if (Slot(refused) > Slot(atReserve) || Slot(refused) > Slot(aboveReserve) ||
                    Order(atReserve) > Order(aboveReserve))
                    return CheckResult.Skip("the chest did not keep the seeded order");

                // Reset the per-second container cache before reading it.
                global::Automatics.AutomaticProcessing.Logics.Cleanup();
                var sources = global::Automatics.AutomaticProcessing.Logics
                    .GetNearbyContainers(pieceName, piece.transform.position)
                    .Select(x => x.container)
                    .ToList();
                if (!sources.Contains(chest))
                    return CheckResult.Skip("the spawned chest is not within the piece's container range");
                if (sources.Count > 1)
                    return CheckResult.Skip(
                        $"{sources.Count - 1} other container(s) within the piece's container range could give ammunition; run it away from other containers");

                // A delta of a full second passes the charge interval at once.
                global::Automatics.AutomaticProcessing.TurretProcess.Charge(piece, pieceView, 1f);

                int Held(ItemDrop kind) => chestInventory.CountItems(kind.m_itemData.m_shared.m_name);
                var charged = piece.GetAmmo();
                var chargedKind = piece.GetAmmoType();
                Expect(charged == 1 && chargedKind == aboveReserve.gameObject.name,
                    $"the piece should hold one of the accepted kind above its reserve: it holds {charged}, of the expected kind {chargedKind == aboveReserve.gameObject.name}");
                Expect(Held(refused) == reserve + 2 && Held(atReserve) == reserve &&
                       Held(aboveReserve) == reserve + 1,
                    $"the chest should still hold the refused item ({Held(refused)}/{reserve + 2}) and the kind at its reserve ({Held(atReserve)}/{reserve}), and one less of the other ({Held(aboveReserve)}/{reserve + 1})");
                return CheckResult.Pass();
            }
        }

        // The kinds of ammunition the piece accepts that carry its ammunition type, so the
        // game's own lookup by type finds them, each with a name of its own.
        private static List<ItemDrop> ChargeKinds(Turret piece)
        {
            return piece.m_allowedAmmo
                .Where(x => x.m_ammo &&
                            x.m_ammo.m_itemData.m_shared.m_ammoType == piece.m_ammoType)
                .Select(x => x.m_ammo)
                .GroupBy(x => x.m_itemData.m_shared.m_name)
                .Select(x => x.First())
                .ToList();
        }

        // -- Spawn / prefab helpers ------------------------------------------------

        // A spawned container is destroyed only while it is empty, or holds nothing but what
        // the check registered with AllowContents; anything else keeps it in the world, and the
        // log says where and what. The rule covers every exit of every check that uses the
        // scope, the early SKIPs included.
        private sealed class SpawnScope : IDisposable
        {
            private readonly List<GameObject> _spawned = new List<GameObject>();

            private readonly Dictionary<GameObject, Dictionary<string, int>> _allowedContents =
                new Dictionary<GameObject, Dictionary<string, int>>();

            // Spawn a networked prefab and claim ownership so IsOwner() is true for the
            // synchronous mutation checks on a single-player host.
            public GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
            {
                var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
                _spawned.Add(go);
                if (Objects.GetZNetView(go.transform, out var nview) && nview != null &&
                    nview.IsValid())
                    if (!nview.IsOwner())
                        nview.ClaimOwnership();
                return go;
            }

            // Takes a spawned object out of Dispose's destruction.
            public void Keep(GameObject go)
            {
                _spawned.RemoveAll(x => ReferenceEquals(x, go));
            }

            // Hands a kept object back to Dispose's destruction.
            public void Release(GameObject go)
            {
                if (!_spawned.Exists(x => ReferenceEquals(x, go))) _spawned.Add(go);
            }

            // Items the check put into a spawned container itself: up to amount of itemName may
            // still be in it when it is destroyed. A check may register several items.
            public void AllowContents(GameObject go, string itemName, int amount)
            {
                if (!_allowedContents.TryGetValue(go, out var allowed))
                    _allowedContents[go] = allowed = new Dictionary<string, int>();
                allowed[itemName] = amount;
            }

            // Null when the object may be destroyed; otherwise why it has to stay.
            private string UnexpectedContents(GameObject go)
            {
                var inventory = go.GetComponent<Container>()?.GetInventory();
                if (inventory == null) return null;

                var items = inventory.GetAllItems();
                if (items.Count == 0) return null;

                if (_allowedContents.TryGetValue(go, out var allowed) &&
                    items.GroupBy(item => item.m_shared.m_name).All(group =>
                        allowed.TryGetValue(group.Key, out var amount) &&
                        group.Sum(item => item.m_stack) <= amount))
                    return null;

                return $"{ChestLocation(go)} was left in the world because it holds items the check did not put there: {ListItems(items)}";
            }

            public void Dispose()
            {
                foreach (var go in _spawned)
                {
                    try
                    {
                        if (!go) continue;

                        var unexpected = UnexpectedContents(go);
                        if (unexpected != null)
                        {
                            Log.LogWarning($"[Automatics Smoke] {unexpected}");
                            continue;
                        }

                        if (Objects.GetZNetView(go.transform, out var nview) && nview != null &&
                            nview.IsValid())
                            nview.Destroy();
                        else
                            UnityEngine.Object.Destroy(go);
                    }
                    catch (Exception e)
                    {
                        // Not destroyed: an object whose contents could not be checked stays.
                        Log.LogWarning(
                            $"[Automatics Smoke] a test object was left in the world because it could not be checked or removed ({e.GetType().Name}: {e.Message})");
                    }
                }

                // Clear processing caches so leftover spawned containers do not linger
                // in GetNearbyContainers for the next invocation.
                try
                {
                    global::Automatics.AutomaticProcessing.Logics.Cleanup();
                    global::Automatics.AutomaticProcessing.SmelterProcess.Cleanup();
                }
                catch
                {
                    // best effort
                }
            }
        }

        // Item / piece prefab by GameObject name (Wood, piece_chest_wood, ...).
        private static bool TryGetPrefab(string gameObjectName, out GameObject prefab)
        {
            prefab = ZNetScene.instance.GetPrefab(gameObjectName);
            return prefab != null;
        }

        // Door / smelter / chest: resolve by the component's object name (m_name via
        // Objects.GetName), scanning ZNetScene.m_prefabs, to dodge the GameObject-name
        // vs $token mismatch.
        private static bool TryFindPrefabByObjectName<TComponent>(string objectName,
            out GameObject prefab) where TComponent : Component
        {
            prefab = null;
            var prefabs = ZNetScene.instance.m_prefabs;
            if (prefabs == null) return false;

            foreach (var go in prefabs)
            {
                if (!go) continue;
                var comp = go.GetComponent<TComponent>();
                if (comp == null) continue;
                if (Objects.GetName(comp) != objectName) continue;
                prefab = go;
                return true;
            }

            return false;
        }

        // Inventories.AddItem caps each call at one max-stack, so loop for larger
        // amounts. Returns the total added-count delta.
        private static int AddItemsLooped(Inventory inventory, GameObject prefab, int amount)
        {
            var total = 0;
            var remaining = amount;
            while (remaining > 0)
            {
                var added = Inventories.AddItem(inventory, prefab, remaining);
                if (added <= 0) break;
                total += added;
                remaining -= added;
            }

            return total;
        }

        // Adds amount of the item as a stack of its own in the first empty, unlocked slot outside
        // the hotbar, where Automatic Storage takes from by default. The game's AddItem fills a
        // stack of the same item with room first, wherever it is, so the caller has put those
        // aside. Returns how many were added.
        private static int AddTestWood(Player player, Inventory inventory, GameObject prefab, int amount)
        {
            var name = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            var locked = global::Automatics.AutomaticStorage.SlotLocks.GetLockedSlots(player);
            for (var y = 1; y < inventory.GetHeight(); y++)
            for (var x = 0; x < inventory.GetWidth(); x++)
            {
                var slot = new Vector2i(x, y);
                if (inventory.GetItemAt(x, y) != null || locked.Contains(slot)) continue;

                var before = inventory.CountItems(name, -1, false);
                var item = PickableHarvester.CreatePickupItemData(prefab, amount);
                if (item != null) inventory.AddItem(item, slot);
                return inventory.CountItems(name, -1, false) - before;
            }

            return 0;
        }

        // Removes up to amount of this exact item - name, quality and world level, the
        // identity CountExactItems counts - and nothing else. Returns how many were removed.
        private static int RemoveExactItems(Inventory inventory, ItemDrop.ItemData sample, int amount)
        {
            var remaining = amount;
            foreach (var item in new List<ItemDrop.ItemData>(inventory.GetAllItems()))
            {
                if (remaining <= 0) break;
                if (item.m_shared.m_name != sample.m_shared.m_name) continue;
                if (item.m_quality != sample.m_quality) continue;
                if (item.m_worldLevel != sample.m_worldLevel) continue;

                var remove = Mathf.Min(remaining, item.m_stack);
                if (remove <= 0) continue;
                if (!inventory.RemoveItem(item, remove)) continue;

                remaining -= remove;
            }

            return amount - remaining;
        }

        private static string ChestLocation(GameObject chestGo)
        {
            try
            {
                var position = chestGo.transform.position;
                return $"the test chest at ({position.x:0.0}, {position.y:0.0}, {position.z:0.0})";
            }
            catch
            {
                return "the test chest next to where the check ran";
            }
        }

        private static string ListItems(IEnumerable<ItemDrop.ItemData> items)
        {
            return string.Join(", ",
                items.Select(item => $"{DisplayName(item.m_shared.m_name)} x{item.m_stack}"));
        }

        private static string DisplayName(string token)
        {
            try
            {
                return Localization.instance != null ? Localization.instance.Localize(token) : token;
            }
            catch
            {
                return token;
            }
        }

        private static bool HasEligibleTestItem(Player player, Inventory inventory,
            ItemDrop.ItemData sample)
        {
            var lockedSlots = global::Automatics.AutomaticStorage.SlotLocks.GetLockedSlots(player);
            foreach (var item in inventory.GetAllItems())
            {
                if (item.m_shared.m_name != sample.m_shared.m_name) continue;
                if (!global::Automatics.AutomaticStorage.Config.StoreHotbarItems &&
                    item.m_gridPos.y == 0) continue;
                if (lockedSlots.Contains(item.m_gridPos)) continue;
                return true;
            }

            return false;
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
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
            private CheckResult(CheckStatus status, string name, string message)
            {
                Status = status;
                Name = name ?? "";
                Message = message ?? "";
            }

            public CheckStatus Status { get; }
            public string Name { get; }
            public string Message { get; }

            // A pass can carry a note - a test item that had to be left behind, for instance.
            public static CheckResult Pass(string note = "")
            {
                return new CheckResult(CheckStatus.Pass, "", note);
            }

            public static CheckResult Fail(string name, string message)
            {
                return new CheckResult(CheckStatus.Fail, name, message);
            }

            public static CheckResult Skip(string message)
            {
                return new CheckResult(CheckStatus.Skip, "", message);
            }

            public CheckResult WithName(string name)
            {
                return new CheckResult(Status, name, Message);
            }
        }
    }
}
