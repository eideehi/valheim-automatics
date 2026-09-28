using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;
using ContainerAccess = Automatics.Valheim.ContainerAccess;
using CookingStationProcess = Automatics.AutomaticProcessing.CookingStationProcess;
using FermenterProcess = Automatics.AutomaticProcessing.FermenterProcess;
using PickupConfig = Automatics.AutomaticPickup.Config;
using PickupModule = Automatics.AutomaticPickup.Module;
using Process = Automatics.AutomaticProcessing.Process;
using ProcessingConfig = Automatics.AutomaticProcessing.Config;
using ProcessingLogics = Automatics.AutomaticProcessing.Logics;
using ProcessingModule = Automatics.AutomaticProcessing.Module;
using Processor = Automatics.AutomaticProcessing.Processor;
using SmelterProcess = Automatics.AutomaticProcessing.SmelterProcess;

namespace Automatics.SmokeTest
{
    // In-game console command for Automatic Processing: `automatics_smoke_processing <sub>`.
    // Each subcommand is one SmokeSession run. It places test stations and a chest of its own
    // ahead of the player, drives Processing's real entry points against them, and logs one
    // PASS / FAIL / SKIP / NOT-RUN line per check plus a Summary line. SKIP means a
    // precondition of the world or the settings is missing; NOT-RUN means the preconditions
    // held but the entry under test never took anything. Neither counts as a pass.
    //
    // rpc: Processing moves an item from a chest into a station by removing it from the chest
    // and invoking the station's intake RPC - Smelter.RPC_AddOre(string, bool),
    // CookingStation.RPC_AddItem(string, bool) or Fermenter.RPC_AddItem(int, bool). The check
    // calls each station's Craft entry once and then, for 30 frames, requires on every frame
    // that what is left in the chest, plus what the station holds in its ZDO, plus anything
    // that turned into, equals what the check put into the chest (R8.11). The station's own
    // update may feed it more in the meantime; the ledger still has to hold. ZNetView.InvokeRPC
    // targets the ZDO owner - this peer, for objects the check spawned - and
    // ZRoutedRpc.InvokeRoutedRPC handles a call to its own peer inline, so the ledger already
    // has to balance on the frame of the call. The run changes no setting.
    //
    // toggle and config are in ProcessingSmokeToggle.cs: one round trip of
    // automatic_processing/module through its real setting, and a read-only look at the
    // automatic_processing section of the live .cfg.
    //
    // Safety follows the asymmetric rule of the other smoke commands (smoke/README.md): only
    // objects this command spawned are changed, test items go only into its own chest, and
    // whatever it cannot prove is its own is left where it is and reported. The one setting
    // it writes, automatic_processing/module (toggle only), is SmokeSession's to hand back.
    internal static partial class ProcessingSmokeCommand
    {
        private const string CommandName = "automatics_smoke_processing";

        // Station pieces stand this far from the shared chest: inside Processing's default
        // container search range, and close enough to read as one group.
        private const float StationOffset = 2.5f;

        // Where the chest goes, measured from the player. The nearest is used unless Automatic
        // Pickup could reach a station there; beyond the farthest the check does not place.
        private const float NearestPlacement = 4f;
        private const float FarthestPlacement = 16f;

        // Added to every configured reach (container search range, pickup range) before it is
        // compared with a distance, so that a position near the edge counts as within it.
        private const float RangeMargin = 2f;

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Processing");

        // The subcommands this build has, by name. Each is the body of one SmokeSession run.
        private static readonly Dictionary<string, Func<Terminal, int, string, IEnumerator>>
            Subcommands = new Dictionary<string, Func<Terminal, int, string, IEnumerator>>(
                StringComparer.Ordinal)
            {
                { "rpc", RunRpc },
                { "toggle", RunToggle },
                { "config", RunConfig }
            };

        private static MonoBehaviour _host;
        private static bool _registered;
        private static bool _commandBuilt;

        internal static void EnsureRegistered(MonoBehaviour host)
        {
            _host = host;
            if (_registered) return;
            _registered = true;

            // Same registration as the other smoke commands: subscribed before the terminal's
            // one-time init, which then builds the command.
            Hooks.OnInitTerminal += BuildCommand;
        }

        private static void BuildCommand()
        {
            if (_commandBuilt) return;
            _commandBuilt = true;

            _ = new Terminal.ConsoleCommand(
                CommandName,
                $"Drive Automatic Processing against stations and a chest it spawns ({string.Join(", ", Subcommands.Keys)}).",
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

            var sub = (args.Args.Skip(1).FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? "")
                .ToLowerInvariant();
            if (!Subcommands.TryGetValue(sub, out var body))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] usage: {CommandName} <{string.Join("|", Subcommands.Keys)}>{(sub.Length == 0 ? "" : $" ('{sub}' is not a subcommand)")}");
                return;
            }

            var runner = $"{CommandName} {sub}";
            if (!SmokeSession.TryBegin(runner, out var runId, out var refusal))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {runner} was not started: {refusal}");
                return;
            }

            // Guard hands back the run's obligations and releases the lock however the body
            // ends, a throw included.
            _host.StartCoroutine(SmokeSession.Guard(runId, runner, body(context, runId, runner)));
        }

        private static IEnumerator RunRpc(Terminal context, int runId, string runner)
        {
            return RunBench(context, runner, () => new RpcRun(runId, runner,
                line => Emit(context, LogLevel.Info, line),
                line => Emit(context, LogLevel.Warning, line)));
        }

        // Drives one run: its steps, then its finishing steps whether or not the steps
        // completed, then its hand-back, then its report. The run is built here, inside the
        // session's guard, so a run that cannot even be built still releases the lock.
        private static IEnumerator RunBench(Terminal context, string runner, Func<BenchRun> create)
        {
            BenchRun run = null;
            try
            {
                run = create();
                foreach (var phase in new Func<IEnumerator>[] { run.Execute, run.Finish })
                {
                    // A yield cannot sit inside a try that has a catch, so each step is taken
                    // inside one and the yield happens outside it.
                    var steps = phase();
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
                            run.Stopped(e);
                            more = false;
                        }

                        if (!more) break;
                        yield return current;
                    }
                }
            }
            finally
            {
                // Reached when the body completes or throws. When the plugin stops the
                // coroutine instead, SmokeSession runs the same hand-back.
                run?.HandBack(runner + " ended");
            }

            Report(context, run.Results);
        }

        // Whether Automatic Processing is running. This is the one place that decides it, and
        // it reads the applied state, not the setting: the registry's state for the module and
        // Module.Running, which every Processing entry point checks first, then
        // enable_automatic_processing.
        private static bool ProcessingRunning(out string reason)
        {
            reason = null;
            var state = ModuleRegistry.GetState(ProcessingModule.Instance.Name);
            if (state != ModuleState.Enabled || !ProcessingModule.Running)
                reason = $"automatic_processing is not running (the module registry reports {state})";
            else if (!ProcessingConfig.EnableAutomaticProcessing)
                reason = "enable_automatic_processing is off";
            return reason == null;
        }

        private static void Report(Terminal context, IReadOnlyList<CheckResult> results)
        {
            foreach (var result in results)
                switch (result.Status)
                {
                    case CheckStatus.Pass:
                        Emit(context, LogLevel.Info, string.IsNullOrEmpty(result.Message)
                            ? $"[Automatics Smoke] PASS {result.Name}"
                            : $"[Automatics Smoke] PASS {result.Name}: {result.Message}");
                        break;
                    case CheckStatus.Skip:
                        Emit(context, LogLevel.Warning,
                            $"[Automatics Smoke] SKIP {result.Name}: {result.Message}");
                        break;
                    case CheckStatus.NotRun:
                        Emit(context, LogLevel.Warning,
                            $"[Automatics Smoke] NOT-RUN {result.Name}: {result.Message}");
                        break;
                    default:
                        Emit(context, LogLevel.Error,
                            $"[Automatics Smoke] FAIL {result.Name}: {result.Message}");
                        break;
                }

            string[] Names(CheckStatus status)
            {
                return results.Where(x => x.Status == status).Select(x => x.Name).ToArray();
            }

            var failed = Names(CheckStatus.Fail);
            var skipped = Names(CheckStatus.Skip);
            var notRun = Names(CheckStatus.NotRun);
            var passed = results.Count(x => x.Status == CheckStatus.Pass);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] Summary: total={results.Count} passed={passed} failed={failed.Length} skipped={skipped.Length} notRun={notRun.Length} failedChecks=[{string.Join(", ", failed)}] skippedChecks=[{string.Join(", ", skipped)}] notRunChecks=[{string.Join(", ", notRun)}]");
        }

        private static void Emit(Terminal context, LogLevel level, string line)
        {
            Log.Log(level, line);

            // The run can outlive the console that started it (a logout while it waits).
            if (context) context.AddString(line);
        }

        // -- What the subcommands that place test objects share ----------------------------

        // One run that places test stations and a chest: the rules for where they may stand,
        // what must be true around them, and their hand-back. Each subcommand adds its steps.
        private abstract class BenchRun
        {
            // The Container's m_name (the GameObject is piece_chest_wood); the token the mod's
            // own container data matches.
            protected const string ChestObjectName = "$piece_chestwood";

            protected readonly int _runId;
            protected readonly string _runner;
            protected readonly Action<string> _info;
            protected readonly Action<string> _warn;
            protected readonly List<StationBench> _stations;

            protected BenchSet _benches;
            protected Func<string, bool> _sessionHandBack;

            protected BenchRun(int runId, string runner, Action<string> info, Action<string> warn,
                List<StationBench> stations)
            {
                _runId = runId;
                _runner = runner;
                _info = info;
                _warn = warn;
                _stations = stations;
            }

            public virtual IReadOnlyList<CheckResult> Results =>
                _stations.Select(x => x.Result ?? CheckResult.Fail(x.CheckName,
                    "the run ended before this check reached a result")).ToList();

            protected List<StationBench> Active => _stations.Where(x => x.Result == null).ToList();

            public abstract IEnumerator Execute();

            // Steps that run after Execute, whether it completed or threw; they may span frames.
            public virtual IEnumerator Finish()
            {
                yield break;
            }

            public virtual void Stopped(Exception e)
            {
                FailRemaining($"the run stopped on {e.GetType().Name}: {e.Message}");
            }

            public virtual void HandBack(string reason)
            {
                ReleaseBenches(reason);
            }

            // Hands the test objects back through the keep rules; a run may do it before it ends.
            protected void ReleaseBenches(string reason)
            {
                if (_benches != null && _benches.HandBack(reason))
                    SmokeSession.ReleaseHandBack(_sessionHandBack);
            }

            protected bool CheckEnvironment(Player player)
            {
                var reason = EnvironmentProblem(player, true);
                if (reason == null) return true;
                SkipRemaining(reason);
                return false;
            }

            // Null when the world allows the run; requireRunning also asks for Automatic
            // Processing to be running.
            protected static string EnvironmentProblem(Player player, bool requireRunning)
            {
                string reason = null;
                if (ZNetScene.instance == null || !player)
                    reason = "no world is loaded (ZNetScene or the local player is missing)";
                else if (ZNet.instance == null || !ZNet.instance.IsServer() ||
                         ZNet.instance.GetPeerConnections() > 0)
                    reason = "it runs only in a single-player world you host, with nobody else connected";
                else if (ObjectDB.instance == null || ObjectDB.instance.m_items.Count == 0)
                    reason = "ObjectDB is not ready";
                else if (requireRunning && !ProcessingRunning(out var notRunning))
                    reason = notRunning;
                return reason;
            }

            protected bool FindChestPrefab(out GameObject prefab)
            {
                if (!FindPrefab<Container>(ChestObjectName, out prefab))
                {
                    SkipRemaining($"{ChestObjectName} prefab not found");
                    return false;
                }

                if (!ContainerAccess.IsAllowed(prefab.GetComponent<Container>(),
                        ProcessingConfig.AllowContainer))
                {
                    SkipRemaining(
                        $"the wood chest ({ChestObjectName}) is not in automatic_processing allow_container");
                    return false;
                }

                return true;
            }

            protected void ResolveStations()
            {
                foreach (var station in _stations)
                    if (!station.FindPrefab())
                        station.Skip($"{station.ObjectName} prefab not found");
                    else if (!ProcessingLogics.IsAllowProcessing(station.ObjectName,
                                 station.RequiredProcess))
                        station.Skip(
                            $"allow_processing_by_{station.ObjectName.Substring(1)} does not include {station.RequiredProcess}");
                    else if (!station.CanPrepare(out var reason))
                        station.Skip(reason);
            }

            // Every station shares the chest, and each takes by display name whatever any of its
            // conversions accepts, and its fuel. A material is therefore only usable when no
            // other test station would take it, it is not the station's own fuel, and no test
            // station takes the product either; each station gets the first of its own
            // conversions that meets that.
            protected void ChooseItems()
            {
                var stations = Active;
                var takenByAny = new HashSet<string>(stations.SelectMany(x => x.TakenNames()));
                var used = new HashSet<string>();
                foreach (var station in stations)
                {
                    var takenByOthers = new HashSet<string>(stations.Where(x => x != station)
                        .SelectMany(x => x.TakenNames()));
                    foreach (var conversion in station.Conversions())
                    {
                        var material = conversion.MaterialName;
                        var product = conversion.ProductName;
                        if (material == product || material == station.FuelName) continue;
                        if (takenByOthers.Contains(material) || takenByAny.Contains(product)) continue;
                        if (used.Contains(material) || used.Contains(product)) continue;

                        station.Choose(conversion);
                        used.Add(material);
                        used.Add(product);
                        break;
                    }

                    if (station.Chosen == null)
                        station.Skip(
                            $"none of the {station.Label}'s conversions has a material that no other test station takes and a product that no test station takes, so its items cannot be kept apart in the shared chest");
                }
            }

            // The chest goes ahead of the player, the stations beside and beyond it, so every
            // test object is at least the chosen distance away. That distance clears Automatic
            // Pickup's reach whenever the module may be running.
            protected bool PlanLayout(Player player, out Vector3 chestPosition, out float distance)
            {
                chestPosition = default;
                distance = NearestPlacement;

                var pickupState = ModuleRegistry.GetState(PickupModule.Instance.Name);
                if (pickupState != ModuleState.Disabled)
                {
                    var reach = PickupConfig.AutomaticPickupRange + RangeMargin;
                    distance = Mathf.Max(distance, reach + 1f);
                    if (distance > FarthestPlacement)
                    {
                        SkipRemaining(
                            $"Automatic Pickup is {pickupState} with automatic_pickup_range {PickupConfig.AutomaticPickupRange:0.#} m, so the test stations would have to stand more than {reach:0.#} m from you, beyond the {FarthestPlacement:0} m this check places them; it does not run where Automatic Pickup could reach what a station drops");
                        return false;
                    }
                }

                var origin = player.transform.position;
                var forward = player.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                forward.Normalize();
                var right = Vector3.Cross(Vector3.up, forward);

                // The stations share the chest's height, so each stays StationOffset from it on
                // any slope.
                chestPosition = Grounded(origin + forward * distance, origin.y);
                var directions = new[] { right, -right, forward };
                for (var i = 0; i < _stations.Count; i++)
                    _stations[i].Position =
                        chestPosition + directions[i % directions.Length] * StationOffset;
                return true;
            }

            protected bool CheckSurroundings(Vector3 chestPosition, float distance)
            {
                var stations = Active;
                var conflicts = FindConflicts(chestPosition, stations);
                if (conflicts.Count == 0) return true;

                const int listed = 8;
                _warn(
                    $"[Automatics Smoke] {_runner}: not the check's own, and within reach of where its test objects go: {string.Join("; ", conflicts.Take(listed))}{(conflicts.Count > listed ? $"; and {conflicts.Count - listed} more" : "")}");

                // Any processing station's container range counts, not only the test stations':
                // a station of the user's reaches the test chest from its own range.
                var clearance = distance + StationOffset + RangeMargin +
                                Processor.GetAllInstance()
                                    .Max(x => ProcessingConfig.ContainerSearchRange(x.name));
                SkipRemaining(
                    $"{conflicts.Count} container(s) or processing station(s) that are not the check's own are within reach of where its test objects go (the first: {conflicts[0]}); stand where nothing of yours is within about {Mathf.CeilToInt(clearance)} m of you and run it again");
                return false;
            }

            // Containers the check did not spawn within a test station's container reach, and
            // processing stations it did not spawn whose own container reach includes the
            // test chest. Measured at the planned positions, before anything is spawned.
            protected static List<string> FindConflicts(Vector3 chestPosition,
                List<StationBench> stations)
            {
                var conflicts = new List<string>();

                var containers = new HashSet<Container>(Object.FindObjectsByType<Container>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None));
                foreach (var container in ContainerCache.GetAllInstance())
                    if (container)
                        containers.Add(container);

                foreach (var container in containers)
                {
                    if (!container) continue;
                    var position = container.transform.position;
                    foreach (var station in stations)
                    {
                        var distance = Vector3.Distance(position, station.Position);
                        if (distance > ProcessingConfig.ContainerSearchRange(station.ObjectName) +
                            RangeMargin)
                            continue;

                        conflicts.Add(
                            $"{DisplayName(Objects.GetName(container))} at {Where(position)}, {distance:0.0} m from where the test {station.Label} goes");
                        break;
                    }
                }

                var processors = new HashSet<string>(Processor.GetAllInstance().Select(x => x.name));
                foreach (var component in FindStations())
                {
                    if (!component) continue;
                    var name = Objects.GetName(component);
                    if (!processors.Contains(name)) continue;

                    var position = component.transform.position;
                    var distance = Vector3.Distance(position, chestPosition);
                    if (distance > ProcessingConfig.ContainerSearchRange(name) + RangeMargin)
                        continue;

                    conflicts.Add(
                        $"{DisplayName(name)} at {Where(position)}, whose container range reaches where the test chest goes ({distance:0.0} m)");
                }

                return conflicts;
            }

            protected static IEnumerable<Component> FindStations()
            {
                return Find<Smelter>().Concat(Find<CookingStation>()).Concat(Find<Fermenter>())
                    .Concat(Find<Fireplace>()).Concat(Find<Beehive>()).Concat(Find<SapCollector>())
                    .Concat(Find<WispSpawner>()).Concat(Find<Turret>());
            }

            protected static IEnumerable<Component> Find<T>() where T : Component
            {
                return Object.FindObjectsByType<T>(FindObjectsInactive.Include,
                    FindObjectsSortMode.None).Cast<Component>();
            }

            // What Processing itself finds for each spawned station has to be the test chest
            // and nothing else. Anything more would let a test station take from, or store
            // into, a container that is not the check's, so the whole run stops here, in the
            // frame of the spawn, and the test objects are handed back before any station has
            // run an update.
            protected bool CheckSources()
            {
                ProcessingLogics.Cleanup();
                SmelterProcess.Cleanup();

                foreach (var station in Active)
                {
                    var position = station.Go.transform.position;
                    var sources = ProcessingLogics.GetNearbyContainers(station.ObjectName, position)
                        .Select(x => x.container)
                        .ToList();
                    var others = sources.Where(x => x != _benches.Chest).ToList();
                    if (others.Count > 0)
                    {
                        SkipRemaining(
                            $"Automatic Processing finds {others.Count} container(s) besides the test chest for the test {station.Label} (the first: {DisplayName(Objects.GetName(others[0]))} at {Where(others[0].transform.position)}); run it away from other containers");
                        return false;
                    }

                    if (!sources.Contains(_benches.Chest))
                        station.Skip(
                            $"Automatic Processing does not find the test chest for the test {station.Label} (container_search_range_by_{station.ObjectName.Substring(1)} is {ProcessingConfig.ContainerSearchRange(station.ObjectName)} m and the chest is {Vector3.Distance(position, _benches.Chest.transform.position):0.0} m away; a ward or the chest's access rules can also hide it)");
                }

                return Active.Count > 0;
            }

            protected bool StillWatching(int frame)
            {
                if (!SmokeSession.IsCurrent(_runId))
                {
                    SkipRemaining("inconclusive: the run was abandoned (the smoke plugin is shutting down)");
                    return false;
                }

                if (ZNetScene.instance == null || !Player.m_localPlayer)
                {
                    SkipRemaining(
                        "inconclusive: the world was unloaded or the local player went away (died or logged out) while the stations were watched");
                    return false;
                }

                if (_benches.ChestAlive) return true;

                FailRemaining(
                    $"the test chest disappeared at frame {frame} while the stations were watched{_benches.NewDropsNear(Active)}");
                return false;
            }

            protected void SkipRemaining(string message)
            {
                foreach (var station in Active) station.Skip(message);
            }

            protected void FailRemaining(string message)
            {
                foreach (var station in Active) station.Fail(message);
            }

            protected void NotRunRemaining(string message)
            {
                foreach (var station in Active) station.NotRun(message);
            }

            // Processing searches for containers again every second (Logics.GetNearbyContainers),
            // so one that comes into reach while the stations are watched - a cart, a ship, a chest
            // just built - could become a station's source or target. Processing's own search is
            // repeated on every frame; anything but the test chest ends the run, and the test
            // objects then go through the hand-back's keep rules.
            protected bool StillIsolated(int frame)
            {
                foreach (var station in Active)
                {
                    if (!station.Go) continue;

                    var other = ProcessingLogics
                        .GetNearbyContainers(station.ObjectName, station.Go.transform.position)
                        .Select(x => x.container)
                        .FirstOrDefault(x => x != _benches.Chest);
                    if (!other) continue;

                    NotRunRemaining(
                        $"at frame {frame} Automatic Processing found {DisplayName(Objects.GetName(other))} at {Where(other.transform.position)} within reach of the test {station.Label}, so the check stopped; move away from other containers and run it again");
                    return false;
                }

                return true;
            }

            // Terrain height under the point, unless it is far from the player's own height
            // (a cave, a dungeon, a building), in which case the player's height is used.
            protected static Vector3 Grounded(Vector3 point, float playerHeight)
            {
                var ground = ZoneSystem.instance != null
                    ? ZoneSystem.instance.GetGroundHeight(point)
                    : playerHeight;
                point.y = Mathf.Abs(ground - playerHeight) <= 2f ? ground : playerHeight;
                return point;
            }

            // Adds the material through the vanilla Inventory.AddItem(GameObject, int), which
            // caps one call at a stack, and returns how many arrived.
            protected static int SeedChest(Inventory inventory, GameObject prefab, string itemName,
                int amount)
            {
                var before = inventory.CountItems(itemName, -1, false);
                for (var call = 0; call < amount; call++)
                {
                    var missing = amount - (inventory.CountItems(itemName, -1, false) - before);
                    if (missing <= 0 || !inventory.AddItem(prefab, missing)) break;
                }

                return inventory.CountItems(itemName, -1, false) - before;
            }

            // Places the chest and the stations, having registered their hand-back with the
            // session first, so a run cut short at any point still has them handed back.
            protected bool PlaceBenches(GameObject chestPrefab, Vector3 chestPosition, float distance)
            {
                _benches = new BenchSet(_runner, _warn, _stations);
                _sessionHandBack = _benches.HandBack;
                SmokeSession.TrackHandBack(_sessionHandBack);

                if (!_benches.PlaceChest(chestPrefab, chestPosition, out var chestProblem))
                {
                    SkipRemaining(chestProblem);
                    return false;
                }

                foreach (var station in Active)
                    if (!_benches.PlaceStation(station, out var stationProblem))
                        station.Skip(stationProblem);

                _info(
                    $"[Automatics Smoke] {_runner}: the test chest is at {Where(chestPosition)}, {distance:0.0} m ahead of you, with the test stations {StationOffset:0.0} m from it");
                return Active.Count > 0;
            }
        }

        // -- The rpc subcommand ------------------------------------------------------------

        private sealed class RpcRun : BenchRun
        {
            private const int ObservedFrames = 30;

            // Above material_count_of_suppress_processing, so the entry has something to take
            // and the station's own update can take more while it is watched.
            private const int MaterialSurplus = 3;

            public RpcRun(int runId, string runner, Action<string> info, Action<string> warn)
                : base(runId, runner, info, warn, new List<StationBench>
                {
                    new SmelterBench(),
                    new OvenBench(),
                    new FermenterBench()
                })
            {
            }

            public override IEnumerator Execute()
            {
                _info(
                    $"[Automatics Smoke] {_runner} started: it places a chest, a smelter, a stone oven and a fermenter ahead of you, drives each station's intake once from the chest, watches them for {ObservedFrames} frames and removes them; no setting is changed");

                var player = Player.m_localPlayer;
                if (!CheckEnvironment(player)) yield break;
                if (!FindChestPrefab(out var chestPrefab)) yield break;

                ResolveStations();
                ChooseItems();
                if (Active.Count == 0) yield break;

                if (!PlanLayout(player, out var chestPosition, out var distance)) yield break;
                if (!CheckSurroundings(chestPosition, distance)) yield break;

                // Registered before anything is spawned, so a run cut short at any point still
                // has its test objects handed back by the session.
                _benches = new BenchSet(_runner, _warn, _stations);
                _sessionHandBack = _benches.HandBack;
                SmokeSession.TrackHandBack(_sessionHandBack);

                if (!_benches.PlaceChest(chestPrefab, chestPosition, out var chestProblem))
                {
                    SkipRemaining(chestProblem);
                    yield break;
                }

                foreach (var station in Active)
                    if (!_benches.PlaceStation(station, out var stationProblem))
                        station.Skip(stationProblem);

                _info(
                    $"[Automatics Smoke] {_runner}: the test chest is at {Where(chestPosition)}, {distance:0.0} m ahead of you, with the test stations {StationOffset:0.0} m from it");

                // Everything from the spawn to the first sample below happens in this frame, so
                // no station has run an update of its own before the check has looked.
                if (!CheckSources()) yield break;
                Seed();
                foreach (var station in Active) Drive(station, _benches.ChestInventory);
                foreach (var station in Active) station.Record(0, _benches.ChestInventory);

                for (var frame = 1; frame <= ObservedFrames; frame++)
                {
                    yield return null;
                    if (!StillWatching(frame)) yield break;
                    if (!StillIsolated(frame)) yield break;
                    foreach (var station in Active) station.Record(frame, _benches.ChestInventory);
                }

                Evaluate();
            }

            private void Seed()
            {
                foreach (var station in Active)
                {
                    var wanted =
                        ProcessingConfig.MaterialCountOfSuppressProcessing(station.ObjectName) +
                        MaterialSurplus;
                    station.Seeded = SeedChest(_benches.ChestInventory, station.MaterialPrefab,
                        station.MaterialName, wanted);
                    if (station.Seeded < wanted)
                        station.Skip(
                            $"only {station.Seeded} of the {wanted} {DisplayName(station.MaterialName)} fit into the test chest");
                }
            }

            // Calls the station's Craft entry. When it takes nothing, the station's conditions
            // and Processing's per-second caches are set up once more and it is called again.
            private static void Drive(StationBench station, Inventory chest)
            {
                for (var attempt = 1; attempt <= 2; attempt++)
                {
                    if (attempt > 1)
                    {
                        ProcessingLogics.Cleanup();
                        SmelterProcess.Cleanup();
                    }

                    station.Prepare();
                    try
                    {
                        station.Drive();
                    }
                    catch (Exception e)
                    {
                        station.Fail($"the entry threw {e.GetType().Name}: {e.Message}");
                        return;
                    }

                    if (!station.TookAnything(chest)) continue;
                    station.DirectCallTook = true;
                    return;
                }
            }

            // A balanced ledger is not enough on its own: a copy of a test item on the ground
            // would leave it balanced, and so would a call that moved two items at once. On the
            // frame of the direct call no station has run an update of its own, so whatever
            // arrived by then is the direct call's, and one call moves exactly one item.
            private void Evaluate()
            {
                foreach (var station in Active)
                {
                    var drops = _benches.NewDropsNear(new[] { station });
                    if (station.VanishedAt >= 0)
                        station.Fail(
                            $"the test {station.Label} disappeared at frame {station.VanishedAt}{drops}");
                    else if (station.FirstBreak != null)
                        station.Fail(station.DescribeBreak() + drops);
                    else if (drops.Length > 0)
                        station.Fail(
                            $"the ledger held, but test items appeared on the ground near the test objects{drops}");
                    else if (station.DirectCallTook && station.DeliveredAtCall != 1)
                        station.Fail(
                            $"the direct call moved {station.DeliveredAtCall} {DisplayName(station.MaterialName)} into the {station.Label}, but one call of the entry moves exactly one");
                    else if (station.FirstDelivery >= 0)
                        station.Pass(station.DescribePass(ObservedFrames));
                    else
                        station.NotRun(
                            $"the entry returned without taking anything on both attempts, and nothing reached the {station.Label} within {ObservedFrames} frames (the chest still holds {station.Last?.Chest} of the {station.Seeded} put in; {station.Diagnose()})");
                }
            }
        }

        // -- Test objects -----------------------------------------------------------------

        // The chest and the stations one run spawned, and their hand-back. A station is removed
        // only after the content the check caused has been cleared from its ZDO, so nothing
        // drops, and only while everything it holds is accounted for by what the check put in.
        // The chest is removed only while it holds nothing but the test materials and what
        // they turned into, no more than were put in. Anything else stays in the world, is made
        // part of the save again so what it holds is not lost, and is reported with its
        // position and contents.
        private sealed class BenchSet
        {
            // Covers every station's output point (the stations stand StationOffset from the
            // chest) and stays inside the reach at which a station of the user's is a conflict.
            private const float DropRadius = 6f;

            private readonly string _runner;
            private readonly Action<string> _warn;
            private readonly List<StationBench> _stations;
            private readonly HashSet<int> _dropsBefore = new HashSet<int>();

            private GameObject _chestGo;
            private ZNetView _chestView;
            private Vector3 _chestPosition;
            private bool _chestDone;
            private bool _handedBack;
            private bool _decided;
            private bool _chestMustStay;

            public BenchSet(string runner, Action<string> warn, List<StationBench> stations)
            {
                _runner = runner;
                _warn = warn;
                _stations = stations;
            }

            public Container Chest { get; private set; }
            public Inventory ChestInventory { get; private set; }

            public bool ChestAlive =>
                _chestGo && _chestView != null && _chestView.IsValid() && ChestInventory != null;

            public bool PlaceChest(GameObject prefab, Vector3 position, out string problem)
            {
                _chestPosition = position;
                RememberDrops();

                _chestGo = Object.Instantiate(prefab, position, Quaternion.identity);
                Configure(_chestGo);

                problem = null;
                Chest = _chestGo.GetComponent<Container>();
                if (!Chest || !Objects.GetZNetView(Chest, out _chestView) || _chestView == null ||
                    !_chestView.IsValid() || _chestView.GetZDO() == null)
                    problem = "the spawned chest has no valid Container or ZNetView";
                else if ((ChestInventory = Chest.GetInventory()) == null)
                    problem = "the spawned chest has no inventory";
                else if (!_chestView.IsOwner())
                    problem = "the spawned chest is not owned by this peer after the claim";
                else if (ChestInventory.NrOfItems() != 0)
                    problem = "the spawned chest was not empty";

                return problem == null;
            }

            public bool PlaceStation(StationBench station, out string problem)
            {
                var go = Object.Instantiate(station.Prefab, station.Position, Quaternion.identity);
                station.Attach(go);
                Configure(go);
                return station.BindSpawned(out problem);
            }

            // Owned by this peer, kept out of every world save, and spared the support and rain
            // wear that would break a piece and drop what it holds (WearNTear.UpdateWear).
            private static void Configure(GameObject go)
            {
                if (Objects.GetZNetView(go.transform, out var nview) && nview != null &&
                    nview.IsValid())
                {
                    nview.GetZDO().Persistent = false;
                    if (!nview.IsOwner()) nview.ClaimOwnership();
                }

                var wear = go.GetComponent<WearNTear>();
                if (!wear) return;
                wear.m_noSupportWear = false;
                wear.m_noRoofWear = false;
            }

            private void RememberDrops()
            {
                foreach (var drop in Object.FindObjectsByType<ItemDrop>(FindObjectsInactive.Exclude,
                             FindObjectsSortMode.None))
                    if (drop && Vector3.Distance(drop.transform.position, _chestPosition) <= DropRadius)
                        _dropsBefore.Add(drop.GetInstanceID());
            }

            // Item drops of the stations' materials or products that appeared near the test
            // objects during the run. They are reported, never removed: none is expected.
            public string NewDropsNear(IEnumerable<StationBench> stations)
            {
                var names = new HashSet<string>();
                foreach (var station in stations.Where(x => x.Chosen != null))
                {
                    names.Add(station.MaterialName);
                    names.Add(station.ProductName);
                }

                var found = Object.FindObjectsByType<ItemDrop>(FindObjectsInactive.Exclude,
                        FindObjectsSortMode.None)
                    .Where(x => x && !_dropsBefore.Contains(x.GetInstanceID()) &&
                                Vector3.Distance(x.transform.position, _chestPosition) <= DropRadius &&
                                names.Contains(x.m_itemData.m_shared.m_name))
                    .Select(x =>
                        $"{DisplayName(x.m_itemData.m_shared.m_name)} x{x.m_itemData.m_stack} at {Where(x.transform.position)}")
                    .ToList();
                return found.Count == 0
                    ? ""
                    : $"; new item drops near the test objects, left where they are: {string.Join(", ", found)}";
            }

            // Returns true once every test object is removed or deliberately left; false keeps
            // the hand-back with the session, which tries again at its next trigger.
            public bool HandBack(string reason)
            {
                if (_handedBack) return true;

                if (ZNetScene.instance == null)
                {
                    // The world is gone and took the test objects with it; none was saved.
                    _handedBack = true;
                    return true;
                }

                // What must stay is decided before anything is removed: clearing a station changes
                // the figures the chest is judged by.
                if (!_decided) Decide();

                var done = true;
                foreach (var station in _stations)
                    if (!station.HandedBack && !HandBackStation(station, reason))
                        done = false;
                if (!_chestDone && !HandBackChest(reason)) done = false;

                try
                {
                    // Processing's caches would otherwise list the removed chest for a second.
                    ProcessingLogics.Cleanup();
                    SmelterProcess.Cleanup();
                }
                catch
                {
                    // best effort
                }

                // A hand-back kept for later decides again then: the stations stay live meanwhile.
                if (!done) _decided = false;
                _handedBack = done;
                return done;
            }

            // Removal needs each station's ledger to balance exactly at the moment of removal: the
            // chest's units of its material and product plus what the station holds equal what was
            // put in, with no remainder and nothing foreign. A deficit or a surplus keeps the
            // station and the chest, since either could hold the difference; a deficit is a FAIL
            // already, so keeping them costs nothing. When the figures cannot be read, everything
            // stays.
            private void Decide()
            {
                try
                {
                    foreach (var station in _stations)
                    {
                        if (station.HandedBack || station.Chosen == null) continue;

                        var alive = station.Go && station.NView != null && station.NView.IsValid();
                        var content = alive ? station.ReadContent() : new StationContent();
                        var balanced = station.LedgerBalances(ChestInventory, content);
                        if (!balanced) _chestMustStay = true;
                        if (!alive) continue;

                        station.MustStay = !balanced;
                        station.StayReason = station.DescribeContent(content) +
                                             (balanced
                                                 ? ""
                                                 : $" (its ledger does not balance: {station.DescribeLedger(ChestInventory, content)})");
                    }
                }
                catch (Exception e)
                {
                    foreach (var station in _stations)
                    {
                        station.MustStay = true;
                        station.StayReason =
                            $"contents that could not be read ({e.GetType().Name}: {e.Message})";
                    }

                    _chestMustStay = true;
                }

                _decided = true;
            }

            private bool HandBackStation(StationBench station, string reason)
            {
                try
                {
                    if (!station.Go)
                    {
                        station.HandedBack = true;
                        return true;
                    }

                    if (station.NView == null || !station.NView.IsValid())
                    {
                        // No station of its own could be bound, or it is already off the network;
                        // either way it holds nothing of the run's.
                        // A view turns invalid with its GameObject alive only once the ZDO holding the contents is destroyed; a zone unload destroys the GameObject itself (the branch above).
                        Remove(station.Go);
                        station.HandedBack = true;
                        return true;
                    }

                    if (station.MustStay)
                    {
                        station.ReleaseToWorld();
                        Keep(station.Go, $"test {station.Label}", station.StayReason);
                        station.HandedBack = true;
                        return true;
                    }

                    // ZNetScene.Destroy removes the ZDO only for its owner, and the object is the
                    // run's own.
                    if (!station.NView.IsOwner()) station.NView.ClaimOwnership();
                    station.ClearContent();
                    station.NView.Destroy();
                    station.HandedBack = true;
                    return true;
                }
                catch (Exception e)
                {
                    try
                    {
                        station.ReleaseToWorld();
                    }
                    catch
                    {
                        // It is left in the world either way.
                    }

                    return LeaveAfterFailure(station.Go, $"test {station.Label}", reason, e,
                        () => station.HandedBack = true);
                }
            }

            private bool HandBackChest(string reason)
            {
                try
                {
                    if (!_chestGo)
                    {
                        _chestDone = true;
                        return true;
                    }

                    if (_chestView == null || !_chestView.IsValid())
                    {
                        // No Container could be bound, or it is already off the network.
                        // A view turns invalid with its GameObject alive only once the ZDO holding the contents is destroyed; a zone unload destroys the GameObject itself (the branch above).
                        Remove(_chestGo);
                        _chestDone = true;
                        return true;
                    }

                    if (ChestInventory == null)
                    {
                        Keep(_chestGo, "test chest", "contents that could not be read");
                        _chestDone = true;
                        return true;
                    }

                    var items = new List<ItemDrop.ItemData>(ChestInventory.GetAllItems());
                    var allowed = new HashSet<string>();
                    foreach (var station in _stations.Where(x => x.Chosen != null))
                    {
                        allowed.Add(station.MaterialName);
                        allowed.Add(station.ProductName);
                    }

                    var foreign = items.Any(x => !allowed.Contains(x.m_shared.m_name));
                    if (foreign || _chestMustStay)
                    {
                        Keep(_chestGo, "test chest", ListItems(items));
                        _chestDone = true;
                        return true;
                    }

                    if (!_chestView.IsOwner()) _chestView.ClaimOwnership();
                    _chestView.Destroy();
                    _chestDone = true;
                    return true;
                }
                catch (Exception e)
                {
                    return LeaveAfterFailure(_chestGo, "test chest", reason, e,
                        () => _chestDone = true);
                }
            }

            // A removal that throws leaves the object where it is, saved with the world and
            // reported, rather than live and unsaved. Only when even that fails is the hand-back
            // kept for the session's next trigger.
            private bool LeaveAfterFailure(GameObject go, string label, string reason, Exception e,
                Action markDone)
            {
                try
                {
                    Keep(go, label,
                        $"contents that could not be checked or removed ({e.GetType().Name}: {e.Message})");
                    markDone();
                    return true;
                }
                catch (Exception again)
                {
                    _warn(
                        $"[Automatics Smoke] {reason}: the {label} could neither be removed nor left in place yet ({again.GetType().Name}: {again.Message}); it is tried again at the next opportunity");
                    return false;
                }
            }

            // Removes a test object through the network, as SmokeSession.DestroyTestObject does,
            // so its ZDO goes with it.
            private static void Remove(GameObject go)
            {
                if (Objects.GetZNetView(go.transform, out var nview) && nview != null &&
                    nview.IsValid())
                {
                    if (!nview.IsOwner()) nview.ClaimOwnership();
                    nview.Destroy();
                }
                else
                {
                    Object.Destroy(go);
                }
            }

            // The hand-back of a kept object is complete whether or not it could be made part of
            // the save: trying again would not change that, and the warning says what to do.
            private void Keep(GameObject go, string label, string contents)
            {
                string notSaved = null;
                try
                {
                    if (Objects.GetZNetView(go.transform, out var nview) && nview != null &&
                        nview.IsValid())
                        nview.GetZDO().Persistent = true;
                    else
                        notSaved = "it has no valid network view";
                }
                catch (Exception e)
                {
                    notSaved = $"{e.GetType().Name}: {e.Message}";
                }

                var where = Where(go.transform.position);
                _warn(notSaved == null
                    ? $"[Automatics Smoke] {_runner}: the {label} at {where} was left in the world because it holds what the check cannot prove it put there: {contents}; it is now saved with your world, so take out what is yours and then remove it"
                    : $"[Automatics Smoke] {_runner}: the {label} at {where} was left in the world because it holds what the check cannot prove it put there: {contents}; it could not be made part of the save ({notSaved}), so it will vanish when the world is saved or unloaded; take out what is yours before then");
            }
        }

        // One station of the run: its prefab, the conversion it is tested with, the ledger it
        // keeps over the watched frames, and its result.
        private abstract class StationBench
        {
            protected StationBench(string checkName, string objectName, string label)
            {
                CheckName = checkName;
                ObjectName = objectName;
                Label = label;
            }

            public string CheckName { get; }
            public string ObjectName { get; }
            public string Label { get; }
            public CheckResult Result { get; private set; }

            public GameObject Prefab { get; private set; }
            public GameObject Go { get; private set; }
            public ZNetView NView { get; private set; }
            public Vector3 Position { get; set; }
            public bool HandedBack { get; set; }
            public bool MustStay { get; set; }
            public string StayReason { get; set; }

            public Conversion Chosen { get; private set; }
            public GameObject MaterialPrefab { get; private set; }
            public string MaterialName => Chosen.MaterialName;
            public string ProductName => Chosen.ProductName;
            protected string MaterialPrefabName => Chosen.From.gameObject.name;
            protected string ProductPrefabName => Chosen.To ? Chosen.To.gameObject.name : null;

            public int Seeded { get; set; }
            public bool DirectCallTook { get; set; }
            public int FirstDelivery { get; private set; } = -1;
            public int DeliveredAtCall { get; private set; }
            public int VanishedAt { get; private set; } = -1;
            public LedgerSample FirstBreak { get; private set; }
            public LedgerSample Last { get; private set; }

            protected abstract Type ComponentType { get; }
            public abstract string FuelName { get; }

            // The kind of processing the settings have to allow for the entry under test.
            public virtual Process RequiredProcess => Process.Craft;

            public void Skip(string message) => Settle(CheckResult.Skip(CheckName, message));
            public void Fail(string message) => Settle(CheckResult.Fail(CheckName, message));
            public void Pass(string message) => Settle(CheckResult.Pass(CheckName, message));
            public void NotRun(string message) => Settle(CheckResult.NotRun(CheckName, message));

            private void Settle(CheckResult result)
            {
                if (Result == null) Result = result;
            }

            public bool FindPrefab()
            {
                foreach (var go in ZNetScene.instance.m_prefabs)
                {
                    if (!go) continue;
                    var component = go.GetComponent(ComponentType);
                    if (!component || Objects.GetName(component) != ObjectName) continue;

                    Prefab = go;
                    return BindPrefab(go);
                }

                return false;
            }

            public void Choose(Conversion conversion)
            {
                Chosen = conversion;
                var prefab = ObjectDB.instance.GetItemPrefab(conversion.From.gameObject.name);
                MaterialPrefab = prefab != null ? prefab : conversion.From.gameObject;
            }

            public IEnumerable<string> TakenNames()
            {
                foreach (var conversion in Conversions()) yield return conversion.MaterialName;
                if (!string.IsNullOrEmpty(FuelName)) yield return FuelName;
            }

            public void Attach(GameObject go)
            {
                Go = go;
            }

            public bool BindSpawned(out string problem)
            {
                problem = null;
                var component = BindStation(Go);
                if (!component || !Objects.GetZNetView(component, out var nview) || nview == null ||
                    !nview.IsValid() || nview.GetZDO() == null)
                {
                    problem = $"the spawned {Label} has no valid ZNetView";
                    return false;
                }

                // Kept whatever its ownership, so the hand-back removes it through the network.
                NView = nview;
                if (!nview.IsOwner())
                    problem = $"the spawned {Label} is not owned by this peer after the claim";
                return problem == null;
            }

            public bool TookAnything(Inventory chest)
            {
                return chest.CountItems(MaterialName, -1, false) < Seeded ||
                       ReadContent().Material > 0;
            }

            public void Record(int frame, Inventory chest)
            {
                if (VanishedAt >= 0) return;
                if (!Go || NView == null || !NView.IsValid())
                {
                    VanishedAt = frame;
                    return;
                }

                var content = ReadContent();
                var produced = Mathf.Max(1, Chosen.Produced);
                var productItems = CountProducts(chest);
                var sample = new LedgerSample(frame, chest.CountItems(MaterialName, -1, false),
                    content.Material, content.Products + productItems / produced,
                    productItems % produced, content.Foreign, content.ForeignDetail);

                if (frame == 0) DeliveredAtCall = sample.Station + sample.Products;
                if (FirstDelivery < 0 && sample.Station + sample.Products > 0) FirstDelivery = frame;
                if (FirstBreak == null && (sample.Sum != Seeded || sample.Remainder != 0 ||
                                           sample.Foreign > 0))
                    FirstBreak = sample;
                Last = sample;
            }

            // Product items in the chest; a station whose material turns into nothing has none.
            private int CountProducts(Inventory chest)
            {
                return ProductName == null ? 0 : chest.CountItems(ProductName, -1, false);
            }

            // Exactly what was put in, counted in material: the chest's material, its products as
            // whole batches (a partial batch does not balance), and what the station holds, with
            // nothing foreign in the station. A ceiling would let a foreign item stand in for a
            // missing test item.
            public bool LedgerBalances(Inventory chest, StationContent content)
            {
                if (content.Foreign > 0) return false;
                if (chest == null) return Seeded == 0 && content.Material + content.Products == 0;

                var produced = Mathf.Max(1, Chosen.Produced);
                var products = CountProducts(chest);
                return products % produced == 0 &&
                       chest.CountItems(MaterialName, -1, false) + products / produced +
                       content.Material + content.Products == Seeded;
            }

            public string DescribeLedger(Inventory chest, StationContent content)
            {
                if (chest == null)
                    return $"the chest cannot be read, {Label} {content.Material} + products {content.Products}, {Seeded} put in";

                var produced = Mathf.Max(1, Chosen.Produced);
                var products = CountProducts(chest);
                return
                    $"chest {chest.CountItems(MaterialName, -1, false)} + {Label} {content.Material} + products {products / produced + content.Products}{(products % produced != 0 ? $" and {products % produced} loose product item(s)" : "")}, {Seeded} put in";
            }

            public string DescribePass(int observedFrames)
            {
                var last = Last;
                return
                    $"{DisplayName(MaterialName)} reached the {Label} at frame {FirstDelivery}{(FirstDelivery == 0 ? " (the frame of the call)" : "")}; the ledger held on every frame from 0 to {observedFrames} (at frame {last.Frame}: chest {last.Chest} + {Label} {last.Station} + products {last.Products} = {Seeded} put in){(DirectCallTook ? "" : "; the direct call took nothing and the game's own call of the same entry delivered it")}";
            }

            public string DescribeBreak()
            {
                var at = FirstBreak;
                var difference = at.Sum < Seeded
                    ? $"{Seeded - at.Sum} missing"
                    : at.Sum > Seeded
                        ? $"{at.Sum - Seeded} more than were put in"
                        : "the count itself matches";
                var last = Last;
                return
                    $"the ledger broke at frame {at.Frame}: chest {at.Chest} + {Label} {at.Station} + products {at.Products} = {at.Sum}, but {Seeded} were put in ({difference})" +
                    (at.Remainder != 0
                        ? $"; {at.Remainder} product item(s) do not make up a whole batch"
                        : "") +
                    (at.Foreign > 0
                        ? $"; the {Label} also holds {at.ForeignDetail}, which the check did not put there"
                        : "") +
                    (last != null && last.Frame != at.Frame
                        ? $"; at frame {last.Frame}: chest {last.Chest} + {Label} {last.Station} + products {last.Products} = {last.Sum}"
                        : "") +
                    (FirstDelivery >= 0
                        ? $"; delivery first seen at frame {FirstDelivery}"
                        : "; no delivery was seen");
            }

            public string Diagnose()
            {
                return
                    $"owner={NView.IsOwner()}, {DiagnoseStation()}, material_count_of_suppress_processing={ProcessingConfig.MaterialCountOfSuppressProcessing(ObjectName)}, product_stacks_of_suppress_processing={ProcessingConfig.ProductStacksOfSuppressProcessing(ObjectName)}, supply_only_when_materials_run_out={ProcessingConfig.SupplyOnlyWhenMaterialsRunOut(ObjectName)}";
            }

            public string DescribeContent(StationContent content)
            {
                var parts = new List<string>();
                if (content.Material > 0)
                    parts.Add($"{DisplayName(MaterialName)} x{content.Material}");
                if (content.Products > 0)
                    parts.Add($"{DisplayName(ProductName)} x{content.Products}");
                if (content.Foreign > 0) parts.Add(content.ForeignDetail);
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
            }

            public abstract IEnumerable<Conversion> Conversions();
            protected abstract bool BindPrefab(GameObject prefab);
            protected abstract Component BindStation(GameObject go);

            public virtual bool CanPrepare(out string reason)
            {
                reason = null;
                return true;
            }

            // Sets the vanilla reaching conditions of the entry on this station alone.
            public abstract void Prepare();

            public abstract void Drive();

            public abstract StationContent ReadContent();

            // Removes from the ZDO what the run put into this station, so removing the piece
            // drops nothing.
            public abstract void ClearContent();

            // Undoes, on a station that is left in the world, the instance settings Prepare
            // changed only to let the entry run.
            public virtual void ReleaseToWorld()
            {
            }

            protected abstract string DiagnoseStation();
        }

        private sealed class SmelterBench : StationBench
        {
            private Smelter _prefab;
            private Smelter _station;

            public SmelterBench() : base("processing_rpc_smelter_add_ore", "$piece_smelter", "smelter")
            {
            }

            protected override Type ComponentType => typeof(Smelter);

            public override string FuelName =>
                _prefab && _prefab.m_fuelItem ? _prefab.m_fuelItem.m_itemData.m_shared.m_name : null;

            public override IEnumerable<Conversion> Conversions()
            {
                if (!_prefab || _prefab.m_conversion == null) yield break;
                foreach (var conversion in _prefab.m_conversion)
                    if (conversion != null && conversion.m_from && conversion.m_to)
                        yield return new Conversion(conversion.m_from, conversion.m_to, 1);
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

            // Nothing: the smelter gets no fuel, so what is queued stays queued.
            public override void Prepare()
            {
            }

            public override void Drive()
            {
                SmelterProcess.Craft(_station, NView);
            }

            // Smelter.QueueOre writes the ore's prefab name to "item" + index and the count to
            // ZDOVars.s_queued. The check gives the smelter no fuel and the chest holds none, so
            // any fuel came from somewhere else; removing the smelter would destroy it.
            public override StationContent ReadContent()
            {
                var zdo = NView.GetZDO();
                var content = new StationContent();
                var queued = zdo.GetInt(ZDOVars.s_queued);
                for (var i = 0; i < queued; i++)
                {
                    var name = zdo.GetString("item" + i);
                    if (name == MaterialPrefabName) content.Material++;
                    else content.AddForeign(name);
                }

                var fuel = zdo.GetFloat(ZDOVars.s_fuel);
                if (fuel > 0f) content.AddForeign($"fuel {fuel:0.##}");
                return content;
            }

            public override void ClearContent()
            {
                var zdo = NView.GetZDO();
                var queued = zdo.GetInt(ZDOVars.s_queued);
                for (var i = 0; i < queued; i++) zdo.Set("item" + i, "");
                zdo.Set(ZDOVars.s_queued, 0);
            }

            protected override string DiagnoseStation()
            {
                return $"queued={NView.GetZDO().GetInt(ZDOVars.s_queued)}/{_station.m_maxOre}";
            }
        }

        private sealed class OvenBench : StationBench
        {
            private CookingStation _prefab;
            private CookingStation _station;
            private bool _fireRequirementLifted;
            private float _fuelSet;

            public OvenBench() : base("processing_rpc_cooking_station_add_item", "$piece_oven",
                "stone oven")
            {
            }

            protected override Type ComponentType => typeof(CookingStation);

            public override string FuelName =>
                _prefab && _prefab.m_fuelItem ? _prefab.m_fuelItem.m_itemData.m_shared.m_name : null;

            private string OvercookedPrefabName =>
                _station && _station.m_overCookedItem ? _station.m_overCookedItem.gameObject.name : null;

            public override IEnumerable<Conversion> Conversions()
            {
                if (!_prefab || _prefab.m_conversion == null) yield break;
                foreach (var conversion in _prefab.m_conversion)
                    if (conversion != null && conversion.m_from && conversion.m_to)
                        yield return new Conversion(conversion.m_from, conversion.m_to, 1);
            }

            protected override bool BindPrefab(GameObject prefab)
            {
                _prefab = prefab.GetComponent<CookingStation>();
                return _prefab != null;
            }

            protected override Component BindStation(GameObject go)
            {
                _station = go.GetComponent<CookingStation>();
                return _station;
            }

            // CookingStationProcess.Craft returns before taking anything while a fire it needs
            // is out, or while its fuel is empty and Refuel is not allowed; the test oven gets
            // neither requirement in its way.
            public override void Prepare()
            {
                if (_station.m_requireFire)
                {
                    _station.m_requireFire = false;
                    _fireRequirementLifted = true;
                }

                if (!_station.m_useFuel) return;
                _fuelSet = 1f;
                NView.GetZDO().Set(ZDOVars.s_fuel, _fuelSet);
            }

            // The fuel the check set stays: when the oven is torn down it comes back as one fuel
            // item the user never had, which is harmless, whereas taking it away could take the
            // user's own.
            public override void ReleaseToWorld()
            {
                if (_fireRequirementLifted) _station.m_requireFire = true;
            }

            public override void Drive()
            {
                CookingStationProcess.Craft(_station, NView);
            }

            // CookingStation.SetSlot writes the item's prefab name to "slot" + index; a slot
            // holding the product or the overcooked item has already turned into a product. Fuel
            // only burns down from what the check set, and the chest holds none, so fuel above it
            // came from somewhere else; removing the oven would destroy it.
            public override StationContent ReadContent()
            {
                var zdo = NView.GetZDO();
                var content = new StationContent();
                var overcooked = OvercookedPrefabName;
                for (var i = 0; i < _station.m_slots.Length; i++)
                {
                    var name = zdo.GetString("slot" + i);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name == MaterialPrefabName) content.Material++;
                    else if (name == ProductPrefabName || name == overcooked) content.Products++;
                    else content.AddForeign(name);
                }

                var fuel = zdo.GetFloat(ZDOVars.s_fuel);
                if (fuel > _fuelSet + 0.01f)
                    content.AddForeign($"fuel {fuel:0.##} (the check set {_fuelSet:0.##})");
                return content;
            }

            public override void ClearContent()
            {
                var zdo = NView.GetZDO();
                for (var i = 0; i < _station.m_slots.Length; i++)
                {
                    zdo.Set("slot" + i, "");
                    zdo.Set("slot" + i, 0f);
                    zdo.Set("slotstatus" + i, 0);
                }

                if (_station.m_useFuel) zdo.Set(ZDOVars.s_fuel, 0f);
            }

            protected override string DiagnoseStation()
            {
                var zdo = NView.GetZDO();
                var free = 0;
                for (var i = 0; i < _station.m_slots.Length; i++)
                    if (string.IsNullOrEmpty(zdo.GetString("slot" + i)))
                        free++;
                return
                    $"requireFire={_station.m_requireFire}, useFuel={_station.m_useFuel}, fuel={zdo.GetFloat(ZDOVars.s_fuel):0.##}, free slots={free}/{_station.m_slots.Length}";
            }
        }

        private sealed class FermenterBench : StationBench
        {
            // Fermenter.UpdateCover recomputes m_exposed whenever m_updateCoverTimer runs out,
            // and FermenterProcess.Craft returns before taking anything while it is true.
            private const float CoverTimerHold = 1e6f;

            private static readonly FieldInfo ExposedField = typeof(Fermenter).GetField("m_exposed",
                BindingFlags.Instance | BindingFlags.NonPublic);

            private static readonly FieldInfo CoverTimerField = typeof(Fermenter).GetField(
                "m_updateCoverTimer", BindingFlags.Instance | BindingFlags.NonPublic);

            private Fermenter _prefab;
            private Fermenter _station;

            public FermenterBench() : base("processing_rpc_fermenter_add_item", "$piece_fermenter",
                "fermenter")
            {
            }

            protected override Type ComponentType => typeof(Fermenter);

            public override string FuelName => null;

            public override IEnumerable<Conversion> Conversions()
            {
                if (!_prefab || _prefab.m_conversion == null) yield break;
                foreach (var conversion in _prefab.m_conversion)
                    if (conversion != null && conversion.m_from && conversion.m_to)
                        yield return new Conversion(conversion.m_from, conversion.m_to,
                            conversion.m_producedItems);
            }

            protected override bool BindPrefab(GameObject prefab)
            {
                _prefab = prefab.GetComponent<Fermenter>();
                return _prefab != null;
            }

            protected override Component BindStation(GameObject go)
            {
                _station = go.GetComponent<Fermenter>();
                return _station;
            }

            public override bool CanPrepare(out string reason)
            {
                reason = ExposedField != null && ExposedField.FieldType == typeof(bool) &&
                         CoverTimerField != null && CoverTimerField.FieldType == typeof(float)
                    ? null
                    : "Fermenter.m_exposed or Fermenter.m_updateCoverTimer is not in this game build, so the test fermenter's exposure cannot be held";
                return reason == null;
            }

            public override void Prepare()
            {
                ExposedField.SetValue(_station, false);
                CoverTimerField.SetValue(_station, CoverTimerHold);
            }

            // Lets a fermenter left in the world recompute its exposure on its next update.
            public override void ReleaseToWorld()
            {
                CoverTimerField.SetValue(_station, 0f);
            }

            public override void Drive()
            {
                FermenterProcess.Craft(_station, NView);
            }

            // Fermenter.RPC_AddItem writes the stable hash of the material's prefab name to
            // ZDOVars.s_content; 0 is empty.
            public override StationContent ReadContent()
            {
                var content = new StationContent();
                var hash = NView.GetZDO().GetInt(ZDOVars.s_content);
                if (hash == 0) return content;
                if (hash == MaterialPrefabName.GetStableHashCode())
                {
                    content.Material = 1;
                    return content;
                }

                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                content.AddForeign(prefab ? prefab.name : $"content hash {hash}");
                return content;
            }

            public override void ClearContent()
            {
                var zdo = NView.GetZDO();
                zdo.Set(ZDOVars.s_content, 0);
                zdo.Set(ZDOVars.s_startTime, 0L);
            }

            protected override string DiagnoseStation()
            {
                return
                    $"content={NView.GetZDO().GetInt(ZDOVars.s_content)}, exposed={ExposedField.GetValue(_station)}";
            }
        }

        private sealed class Conversion
        {
            public Conversion(ItemDrop from, ItemDrop to, int produced)
            {
                From = from;
                To = to;
                Produced = produced;
            }

            public ItemDrop From { get; }
            public ItemDrop To { get; }

            // Product items one material turns into.
            public int Produced { get; }

            public string MaterialName => From.m_itemData.m_shared.m_name;
            // Null when the material turns into nothing, as fuel does.
            public string ProductName => To ? To.m_itemData.m_shared.m_name : null;
        }

        // What a station holds in its ZDO: the run's material, what it already turned into
        // (in material units), and anything else.
        private sealed class StationContent
        {
            private readonly List<string> _foreign = new List<string>();

            public int Material { get; set; }
            public int Products { get; set; }
            public int Foreign => _foreign.Count;
            public string ForeignDetail => string.Join(", ", _foreign);

            public void AddForeign(string name)
            {
                _foreign.Add(string.IsNullOrEmpty(name) ? "(an unnamed entry)" : name);
            }
        }

        // One frame of a station's ledger: the chest's material, the station's material and the
        // products (both in material units) have to add up to what was put in.
        private sealed class LedgerSample
        {
            public LedgerSample(int frame, int chest, int station, int products, int remainder,
                int foreign, string foreignDetail)
            {
                Frame = frame;
                Chest = chest;
                Station = station;
                Products = products;
                Remainder = remainder;
                Foreign = foreign;
                ForeignDetail = foreignDetail;
            }

            public int Frame { get; }
            public int Chest { get; }
            public int Station { get; }
            public int Products { get; }
            public int Remainder { get; }
            public int Foreign { get; }
            public string ForeignDetail { get; }
            public int Sum => Chest + Station + Products;
        }

        // -- Helpers ----------------------------------------------------------------------

        // Resolve a prefab by its component's object name (m_name via Objects.GetName), which
        // dodges the GameObject-name vs $token mismatch.
        private static bool FindPrefab<TComponent>(string objectName, out GameObject prefab)
            where TComponent : Component
        {
            prefab = null;
            var prefabs = ZNetScene.instance.m_prefabs;
            if (prefabs == null) return false;

            foreach (var go in prefabs)
            {
                if (!go) continue;
                var component = go.GetComponent<TComponent>();
                if (component == null || Objects.GetName(component) != objectName) continue;
                prefab = go;
                return true;
            }

            return false;
        }

        private static string Where(Vector3 position)
        {
            return $"({position.x:0.0}, {position.y:0.0}, {position.z:0.0})";
        }

        private static string ListItems(IEnumerable<ItemDrop.ItemData> items)
        {
            var listed = items.Select(x => $"{DisplayName(x.m_shared.m_name)} x{x.m_stack}").ToList();
            return listed.Count == 0 ? "nothing" : string.Join(", ", listed);
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

        // -- Reporting (private copy mirroring SmokePlugin's shapes, plus NOT-RUN) -----------

        private enum CheckStatus
        {
            Pass,
            Fail,
            Skip,
            NotRun
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

            public static CheckResult Pass(string name, string note)
            {
                return new CheckResult(CheckStatus.Pass, name, note);
            }

            public static CheckResult Fail(string name, string message)
            {
                return new CheckResult(CheckStatus.Fail, name, message);
            }

            public static CheckResult Skip(string name, string message)
            {
                return new CheckResult(CheckStatus.Skip, name, message);
            }

            public static CheckResult NotRun(string name, string message)
            {
                return new CheckResult(CheckStatus.NotRun, name, message);
            }
        }
    }
}
