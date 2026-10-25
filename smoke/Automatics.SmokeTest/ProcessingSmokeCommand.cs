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
using BeehiveProcess = Automatics.AutomaticProcessing.BeehiveProcess;
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
using SapCollectorProcess = Automatics.AutomaticProcessing.SapCollectorProcess;
using SmelterProcess = Automatics.AutomaticProcessing.SmelterProcess;
using ValheimObject = Automatics.Valheim.ValheimObject;
using WispSpawnerProcess = Automatics.AutomaticProcessing.WispSpawnerProcess;
using Held = System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,
    Automatics.SmokeTest.SmokeSession.SettingRestoration>;

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
    // has to balance on the frame of the call.
    //
    // toggle and config are in ProcessingSmokeToggle.cs: one round trip of
    // automatic_processing/module through its real setting, and a read-only look at the
    // automatic_processing section of the live .cfg. skill is in ProcessingSmokeSkill.cs: who
    // gains the skill and bonus food for what Processing does at a cooking station. store, below
    // rpc: a product is stored only where it fits. collect, below store: what Processing stores
    // from a piece that collects an item over time is what taking it out by hand yields, in
    // whole units. all: every subcommand, one after another, then one Summary over all of them
    // (RunAll).
    //
    // Every subcommand but config sets up what it needs itself before it places anything:
    // Automatic Processing turned on for the run (toggle turns it on through the setting it
    // round-trips), automatic_pickup_range at its minimum, so the test objects can stand at the
    // nearest placement, and the per-piece settings a check depends on (the chest allowed, the
    // process allowed, the counts it seeds above). When something that is not the check's own is
    // within reach of where the test objects go, the player is moved to clear ground first and
    // moved back at the end (SmokeSetup.Relocate).
    //
    // Safety follows the asymmetric rule of the other smoke commands (smoke/README.md): only
    // objects this command spawned are changed, test items go only into its own chest, and
    // whatever it cannot prove is its own is left where it is and reported. The settings it
    // writes are SmokeSession's to hand back; processing_settings_restored reports them.
    internal static partial class ProcessingSmokeCommand
    {
        private const string CommandName = "automatics_smoke_processing";
        private const string AllArgument = "all";

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

        // automatic_pickup_range's minimum, written for the run, so that NearestPlacement clears
        // Automatic Pickup's reach whatever the user's range.
        private const float PickupRangeForRun = 1f;

        // container_search_range_by_* of every test station, written for the run: past the test
        // chest, StationOffset away, so Processing finds it whatever the user's range, and short
        // enough that little else is in reach.
        private const int ContainerRangeForRun = 4;

        // The default of material_count_of_suppress_processing and of
        // fuel_count_of_suppress_processing. The checks seed above these counts, so a higher one
        // is lowered to it for the run, and what they seed fits into the test chest.
        private const int DefaultSuppressCount = 1;

        // The one check every subcommand that writes settings ends with.
        private const string SettingsRestored = "processing_settings_restored";

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Processing");

        // The subcommands this build has, by name. Each is the body of one SmokeSession run.
        private static readonly Dictionary<string, Func<Terminal, int, string, IEnumerator>>
            Subcommands = new Dictionary<string, Func<Terminal, int, string, IEnumerator>>(
                StringComparer.Ordinal)
            {
                { "rpc", RunRpc },
                { "toggle", RunToggle },
                { "config", RunConfig },
                { "skill", RunSkill },
                { "store", RunStore },
                { "collect", RunCollect }
            };

        private static MonoBehaviour _host;
        private static bool _registered;
        private static bool _commandBuilt;

        // The results the last Report printed, which `all` adds to its own Summary.
        private static IReadOnlyList<CheckResult> _reported;

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
                $"Drive Automatic Processing against stations and a chest it spawns ({string.Join(", ", Subcommands.Keys)}, or '{AllArgument}' for each of them, one after another).",
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
            if (sub == AllArgument)
            {
                // The first case begins here, so a run still going refuses `all` at once.
                var first = $"{CommandName} {Subcommands.Keys.First()}";
                if (!SmokeSession.TryBegin(first, out var firstRunId, out var firstRefusal))
                {
                    Emit(context, LogLevel.Warning,
                        $"[Automatics Smoke] {CommandName} {AllArgument} was not started: {firstRefusal}");
                    return;
                }

                _host.StartCoroutine(RunAll(context, firstRunId));
                return;
            }

            if (!Subcommands.TryGetValue(sub, out var body))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] usage: {CommandName} <{AllArgument}|{string.Join("|", Subcommands.Keys)}>{(sub.Length == 0 ? "" : $" ('{sub}' is not a subcommand)")}");
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

        // `all`: every subcommand, in the order the usage lists them. Each case is a run of its
        // own, begun, guarded and reported exactly as when it is started alone, so its setup,
        // restore and hand-backs are the ones it has alone, and the next case begins only once
        // TryBegin accepts it: once every obligation the case before left has been handed back,
        // the return to where it started included. A failed case does not stop the ones after it;
        // a case that cannot begin, or the player leaving the world, does. firstRunId is the first
        // case's run, begun by OnCommand.
        private static IEnumerator RunAll(Terminal context, int firstRunId)
        {
            var player = Player.m_localPlayer;
            var cases = Subcommands.Keys.ToArray();
            var results = new List<(string Case, CheckResult Result)>();

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {CommandName} {AllArgument} started: {string.Join(", ", cases)}, one after another, each a run of its own with its own Summary, then one Summary over all of them");

            var runId = firstRunId;
            var ran = 0;
            string stopped = null;
            for (; ran < cases.Length; ran++)
            {
                var name = cases[ran];
                var runner = $"{CommandName} {name}";
                if (ran > 0)
                {
                    var begin = new SmokeSession.BeginResult();
                    var wait = SmokeSession.AwaitBegin(runner, player, begin);
                    while (wait.MoveNext()) yield return wait.Current;
                    if (begin.RunId == 0)
                    {
                        stopped = begin.Refusal;
                        break;
                    }

                    runId = begin.RunId;
                }

                _reported = null;
                var steps = SmokeSession.Guard(runId, runner, Subcommands[name](context, runId, runner));
                while (steps.MoveNext()) yield return steps.Current;

                if (_reported == null)
                    results.Add((name, CheckResult.Fail("case_reported",
                        "the case stopped before its Summary; the error is in the log")));
                else
                    results.AddRange(_reported.Select(x => (name, x)));
            }

            var notStarted = cases.Skip(ran).ToArray();
            if (stopped != null)
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {CommandName} {AllArgument} stopped before {notStarted[0]}: {stopped}; not started: {string.Join(", ", notStarted)}");
            ReportAll(context, ran, results, notStarted);
        }

        private static IEnumerator RunRpc(Terminal context, int runId, string runner)
        {
            return RunBench(context, runner, () => new RpcRun(runId, runner,
                line => Emit(context, LogLevel.Info, line),
                line => Emit(context, LogLevel.Warning, line)));
        }

        private static IEnumerator RunStore(Terminal context, int runId, string runner)
        {
            return RunBench(context, runner, () => new StoreRun(runId, runner,
                line => Emit(context, LogLevel.Info, line),
                line => Emit(context, LogLevel.Warning, line)));
        }

        private static IEnumerator RunCollect(Terminal context, int runId, string runner)
        {
            return RunBench(context, runner, () => new CollectRun(runId, runner,
                line => Emit(context, LogLevel.Info, line),
                line => Emit(context, LogLevel.Warning, line)));
        }

        // Drives one run: its steps, then its finishing steps whether or not the steps
        // completed, then its hand-back, then its report. The run is built here, inside the
        // session's guard, so a run that cannot even be built still releases the lock.
        private static IEnumerator RunBench(Terminal context, string runner, Func<BenchRun> create)
        {
            // ToggleRun has its own setup. Guard the shared wrapper before either setup can
            // enable Processing, and let the session detach it after restoration/hand-back.
            if (!ProcessingIsolation.Attach(out var isolationProblem))
            {
                Report(context, new[] { CheckResult.Skip("processing_isolation_setup", isolationProblem) });
                yield break;
            }

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

            // Once the test objects are gone: the settings the run wrote go back, and so does
            // the player, when a relocation moved them, before the report ends with its Summary.
            run.RestoreSettings();
            var back = new SmokeSetup.SetupResult();
            var home = SmokeSetup.ReturnToStart(run.RunId, back);
            while (home.MoveNext()) yield return home.Current;
            if (!back.Ok)
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {runner}: you could not be moved back to where the command started yet ({back.Reason}); the smoke plugin tries again as the run ends");

            var results = run.Results.ToList();
            if (run.IsolationResult != null) results.Add(run.IsolationResult);
            if (run.SettingsResult != null) results.Add(run.SettingsResult);
            Report(context, results);
        }

        // Both console commands exercise the actual patched transfer gate with the same empty
        // synthetic chest. Registration must not hide anything from the real search/cache.
        internal static bool CheckIsolation(Container chest, string piece, Vector3 position, out string problem)
        {
            problem = null;
            ProcessingIsolation.Unregister(chest);
            try
            {
                ProcessingLogics.Cleanup();
                var before = ProcessingLogics.GetNearbyContainers(piece, position).Select(x => x.container).ToArray();
                Container askable = null;
                var denied = !ProcessingLogics.TryClaimContainer(chest, ref askable) && ReferenceEquals(askable, null);
                askable = chest;
                denied &= !ProcessingLogics.TryClaimContainer(chest, ref askable) && ReferenceEquals(askable, chest);
                ProcessingIsolation.Register(chest);
                ProcessingLogics.Cleanup();
                var after = ProcessingLogics.GetNearbyContainers(piece, position).Select(x => x.container).ToArray();
                askable = null;
                var allowed = ProcessingLogics.TryClaimContainer(chest, ref askable);
                var sameSearch = before.Any(x => ReferenceEquals(x, chest)) && before.Length == after.Length &&
                                 before.Zip(after, (a, b) => ReferenceEquals(a, b)).All(x => x);
                if (!denied || !allowed || !sameSearch)
                    problem = $"unregistered chest denied without changing askable={denied}; registered chest accepted={allowed}; real search unchanged={sameSearch}";
                return problem == null;
            }
            finally
            {
                ProcessingIsolation.Register(chest);
            }
        }

        // Automatic Processing keeps its per-piece settings in private dictionaries by the piece's
        // name and exposes only their values; field is the dictionary's name, for instance
        // "_allowProcessing". Null when the piece has none. The world command uses it too.
        internal static ConfigEntry<T> PieceSetting<T>(string field, string piece)
        {
            var entries = AccessTools.Field(typeof(ProcessingConfig), field)?.GetValue(null) as
                Dictionary<string, ConfigEntry<T>>;
            return entries != null && entries.TryGetValue(piece, out var entry) ? entry : null;
        }

        // Whether Automatic Processing is running. This is the one place that decides it, and
        // it reads the applied state, not the setting: the registry's state for the module and
        // Module.Running, which every Processing entry point checks first.
        private static bool ProcessingRunning(out string reason)
        {
            reason = null;
            var state = ModuleRegistry.GetState(ProcessingModule.Instance.Name);
            if (state != ModuleState.Enabled || !ProcessingModule.Running)
                reason = $"automatic_processing is not running (the module registry reports {state})";
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
            _reported = results;
        }

        // The Summary of `all`: how many cases ran, the totals over their checks, each check named
        // after its case, and the cases it did not start.
        private static void ReportAll(Terminal context, int cases,
            IReadOnlyList<(string Case, CheckResult Result)> results, IEnumerable<string> notStarted)
        {
            string[] Names(CheckStatus status)
            {
                return results.Where(x => x.Result.Status == status).Select(x => $"{x.Case}:{x.Result.Name}")
                              .ToArray();
            }

            var failed = Names(CheckStatus.Fail);
            var skipped = Names(CheckStatus.Skip);
            var notRun = Names(CheckStatus.NotRun);
            var passed = results.Count(x => x.Result.Status == CheckStatus.Pass);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] Summary ({AllArgument}): cases={cases} total={results.Count} passed={passed} failed={failed.Length} skipped={skipped.Length} notRun={notRun.Length} failedChecks=[{string.Join(", ", failed)}] skippedChecks=[{string.Join(", ", skipped)}] notRunChecks=[{string.Join(", ", notRun)}] casesNotStarted=[{string.Join(", ", notStarted)}]");
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

            // The settings the run writes for itself (SmokeSetup.Write); put back when it ends and
            // reported as processing_settings_restored.
            protected readonly Held _settings = new Held();

            protected BenchSet _benches;
            protected Func<string, bool> _sessionHandBack;

            // What SetUp and Lay came to: whether the run can go on, and where the chest goes.
            protected bool _ready;
            protected Vector3 _chestPosition;
            protected float _distance;

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

            public int RunId => _runId;
            public CheckResult IsolationResult { get; private set; }

            // processing_settings_restored, once RestoreSettings has run; null when the run wrote
            // no setting.
            public CheckResult SettingsResult { get; protected set; }

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

            // Puts back every setting the run wrote and judges them as SmokeSetup.ReportSettings
            // does: PASS when each is back, SKIP when one was left to the user or the run was
            // abandoned, FAIL when one could not be put back.
            public void RestoreSettings()
            {
                if (SettingsResult != null) return;
                var results = SmokeSession.RestoreSettings(_runId);
                if (results.Count == 0) return;

                var detail = string.Join("; ", results.Select(x => x.Value));
                if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                    SettingsResult = CheckResult.Fail(SettingsRestored, detail);
                else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                          x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                    SettingsResult = CheckResult.Skip(SettingsRestored, "restoration skipped: " + detail);
                else
                    SettingsResult = CheckResult.Pass(SettingsRestored, detail);
            }

            // What the run sets up before it plans anything: Automatic Processing turned on for
            // the run when it is off, and automatic_pickup_range at its minimum. _ready says the
            // run can go on; otherwise every check is settled as a SKIP that says what failed.
            protected IEnumerator SetUp(Player player)
            {
                _ready = false;
                var problem = EnvironmentProblem(player, false);
                if (problem != null)
                {
                    SkipRemaining(problem);
                    yield break;
                }

                var module = new SmokeSetup.SetupResult();
                var steps = SmokeSetup.EnableModule(_runId, _settings, ProcessingConfig.ModuleEntry,
                    () => ProcessingRunning(out _), module);
                while (steps.MoveNext()) yield return steps.Current;
                if (!module.Ok)
                {
                    SkipRemaining(module.Reason);
                    yield break;
                }

                if (module.Changed)
                    _info($"[Automatics Smoke] {_runner}: automatic_processing/module was off, so it is turned on for the run and put back afterwards");

                // Asked again after the wait: the world may have gone meanwhile.
                if (!CheckEnvironment(player)) yield break;
                _ready = Set(PickupConfig.AutomaticPickupRangeEntry, PickupRangeForRun);
            }

            // Writes a setting for the run when it does not hold value already. A refusal settles
            // the remaining checks as SKIPs that name it. A setting this build does not have is
            // left to the check's own precondition.
            protected bool Set(ConfigEntryBase entry, object value)
            {
                if (entry == null || Equals(entry.BoxedValue, value)) return true;
                if (SmokeSetup.Write(_runId, _settings, entry, value, out var refusal)) return true;

                SkipRemaining(
                    $"{entry.Definition.Section}/{entry.Definition.Key} could not be set for the run: {refusal}");
                return false;
            }

            // allow_processing_by_* of the piece, with process added for the run.
            protected bool AllowProcess(string piece, Process process)
            {
                var entry = PieceSetting<Process>("_allowProcessing", piece);
                return entry == null || (entry.Value & process) != 0 || Set(entry, entry.Value | process);
            }

            // A per-piece count the check seeds above or depends on, lowered to value for the run
            // when it is higher.
            protected bool AtMost(string field, string piece, int value)
            {
                var entry = PieceSetting<int>(field, piece);
                return entry == null || entry.Value <= value || Set(entry, value);
            }

            // A per-piece switch that would keep the entry under test from acting, off for the run.
            protected bool TurnOff(string field, string piece)
            {
                var entry = PieceSetting<bool>(field, piece);
                return entry == null || !entry.Value || Set(entry, false);
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

                // Allowed for the run when it is not.
                var allowed = SmokeSetup.Field<StringList>(typeof(ProcessingConfig), "_allowContainer");
                if (allowed != null &&
                    ValheimObject.Container.GetIdentify(ChestObjectName, out var identifier) &&
                    !allowed.Value.Contains(identifier) &&
                    !Set(allowed, new StringList(allowed.Value) { identifier }))
                    return false;

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
                    else if (!AllowProcess(station.ObjectName, station.RequiredProcess))
                        return;
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

                chestPosition = Arrange(player.transform.position, Facing(player), distance);
                return true;
            }

            private static Vector3 Facing(Player player)
            {
                var forward = player.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
                return forward.normalized;
            }

            // Sets every station's position for a player at origin and returns the chest's.
            private Vector3 Arrange(Vector3 origin, Vector3 forward, float distance)
            {
                var right = Vector3.Cross(Vector3.up, forward);

                // The stations share the chest's height, so each stays StationOffset from it on
                // any slope.
                var chestPosition = Grounded(origin + forward * distance, origin.y);
                var directions = new[] { right, -right, forward };
                for (var i = 0; i < _stations.Count; i++)
                    _stations[i].Position =
                        chestPosition + directions[i % directions.Length] * StationOffset;
                return chestPosition;
            }

            // Plans where the test objects go (_chestPosition, _distance) and checks what is
            // around them. When something that is not the check's own is within reach, the player
            // is first moved to clear ground where nothing is (SmokeSetup.Relocate, which keeps
            // their facing) and the plan is made again from there; RunBench moves them back when
            // the run ends. _ready says the run can place its objects. Each station's container
            // search range is set for the run first, since the plan's conflicts are measured by it.
            protected IEnumerator Lay(Player player)
            {
                _ready = false;
                foreach (var station in Active)
                    if (!Set(PieceSetting<int>("_containerSearchRange", station.ObjectName), ContainerRangeForRun))
                        yield break;
                if (!PlanLayout(player, out _chestPosition, out _distance)) yield break;

                string relocation = null;
                if (FindConflicts(_chestPosition, Active).Count > 0)
                {
                    var forward = Facing(player);
                    var distance = _distance;
                    var moved = new SmokeSetup.SetupResult();
                    var steps = SmokeSetup.Relocate(_runId, site =>
                    {
                        var conflicts = FindConflicts(Arrange(site, forward, distance), Active);
                        return conflicts.Count == 0 ? null : conflicts[0];
                    }, moved);
                    while (steps.MoveNext()) yield return steps.Current;

                    if (!moved.Ok) relocation = moved.Reason;
                    else if (moved.Changed)
                        _info(
                            $"[Automatics Smoke] {_runner}: things that are not the check's own were within reach of where its test objects go, so you were {moved.Reason}; you are moved back when the run ends");

                    // A failed move can still have left the player elsewhere, so the plan is
                    // made again from wherever they are.
                    var problem = EnvironmentProblem(player, false);
                    if (problem != null)
                    {
                        SkipRemaining(problem);
                        yield break;
                    }

                    if (!PlanLayout(player, out _chestPosition, out _distance)) yield break;
                }

                _ready = CheckSurroundings(_chestPosition, _distance, relocation);
            }

            // relocation is why no clear ground was found, when a move was tried.
            protected bool CheckSurroundings(Vector3 chestPosition, float distance, string relocation = null)
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
                    $"{conflicts.Count} container(s) or processing station(s) that are not the check's own are within reach of where its test objects go (the first: {conflicts[0]}){(relocation == null ? "" : $", and no clear ground to move you to was found ({relocation})")}; stand where nothing of yours is within about {Mathf.CeilToInt(clearance)} m of you and run it again");
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

                var control = Active.FirstOrDefault();
                if (control != null)
                {
                    const string check = "processing_container_isolated";
                    var isolated = CheckIsolation(_benches.Chest, control.ObjectName, control.Position, out var problem);
                    IsolationResult = isolated
                        ? CheckResult.Pass(check, "the unregistered test chest was denied with askable unchanged; registration allowed the real gate, with the real search unchanged")
                        : CheckResult.Fail(check, problem);
                    if (!isolated)
                    {
                        FailRemaining(problem);
                        return false;
                    }
                }

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
                    $"[Automatics Smoke] {_runner} started: it places a chest, a smelter, a stone oven and a fermenter ahead of you, drives each station's intake once from the chest, watches them for {ObservedFrames} frames and removes them; the settings it needs are set for the run and put back afterwards");

                var player = Player.m_localPlayer;
                var setUp = SetUp(player);
                while (setUp.MoveNext()) yield return setUp.Current;
                if (!_ready) yield break;
                if (!FindChestPrefab(out var chestPrefab)) yield break;

                ResolveStations();
                ChooseItems();
                foreach (var station in Active)
                    if (!AtMost("_materialCountOfSuppressProcessing", station.ObjectName, DefaultSuppressCount))
                        yield break;
                if (Active.Count == 0) yield break;

                var lay = Lay(player);
                while (lay.MoveNext()) yield return lay.Current;
                if (!_ready) yield break;
                if (!PlaceBenches(chestPrefab, _chestPosition, _distance)) yield break;

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

        // -- The store subcommand ----------------------------------------------------------

        // store: Processing stores a product only where it fits. The run places a chest and a
        // smelter as rpc does, fills the chest with the smelter's product and calls the smelter's
        // Store entry, as the game's Smelter.Spawn does, while a ContainerWriteObserver counts
        // every add to the chest's inventory and every save of the chest and records every
        // warning Unity logs. With the chest full, Store adds nothing, saves nothing, logs no
        // warning and returns false. With room for one of three, it puts one in, again without a
        // warning, and drops the other two at the smelter as it always has; the check removes
        // that drop at once. The filler and the stored product are taken out of the chest before
        // the run ends, so the chest and the smelter go through the rpc hand-back with their
        // ledger balanced. Everything happens in the frame of the spawn.
        private sealed class StoreRun : BenchRun
        {
            private const string Setup = "processing_store_setup";
            private const string FullChest = "processing_store_full_chest_untouched";
            private const string PartialFit = "processing_store_stores_what_fits";
            private const string ProbeMarker = "AUTOMATICS_SMOKE_PROCESSING_STORE_PROBE";

            // The units Store is asked to store: more than one, so a store into room for one
            // leaves a rest.
            private const int Offered = 3;

            private static readonly string[] Order = { FullChest, PartialFit };

            private readonly Dictionary<string, CheckResult> _checks =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly SmelterBench _smelter;
            private string _notReached = "not reached: an earlier step stopped the run";

            public StoreRun(int runId, string runner, Action<string> info, Action<string> warn)
                : this(runId, runner, info, warn, new SmelterBench(Setup))
            {
            }

            private StoreRun(int runId, string runner, Action<string> info, Action<string> warn,
                SmelterBench smelter) : base(runId, runner, info, warn,
                new List<StationBench> { smelter })
            {
                _smelter = smelter;
            }

            public override IReadOnlyList<CheckResult> Results
            {
                get
                {
                    // A run stopped before the first check reports why under the setup check.
                    if (_checks.Count == 0)
                        return new[]
                        {
                            _smelter.Result ?? CheckResult.Fail(Setup,
                                "the run ended before this check reached a result")
                        };

                    return Order.Select(name => _checks.TryGetValue(name, out var result)
                        ? result
                        : CheckResult.Skip(name, _notReached)).ToList();
                }
            }

            public override IEnumerator Execute()
            {
                _info(
                    $"[Automatics Smoke] {_runner} started: it places a chest and a smelter ahead of you, fills the chest with the smelter's product, calls the smelter's Store entry with the chest full and with room for one, and removes them; the settings it needs are set for the run and put back afterwards");

                var player = Player.m_localPlayer;
                var setUp = SetUp(player);
                while (setUp.MoveNext()) yield return setUp.Current;
                if (!_ready) yield break;
                if (!FindChestPrefab(out var chestPrefab)) yield break;

                ResolveStations();
                if (Active.Count > 0 && !AllowProcess(_smelter.ObjectName, Process.Store)) yield break;
                if (Active.Count > 0 &&
                    !ProcessingLogics.IsAllowProcessing(_smelter.ObjectName, Process.Store))
                    SkipRemaining(
                        $"allow_processing_by_{_smelter.ObjectName.Substring(1)} does not include Store");
                ChooseItems();
                if (Active.Count == 0) yield break;

                var lay = Lay(player);
                while (lay.MoveNext()) yield return lay.Current;
                if (!_ready) yield break;
                if (!PlaceBenches(chestPrefab, _chestPosition, _distance)) yield break;

                // Everything from here to the end happens in the frame of the spawn.
                if (!CheckSources()) yield break;

                var chest = _benches.ChestInventory;
                var productName = _smelter.ProductName;
                try
                {
                    if (!Fill(chest, out var problem))
                    {
                        SkipRemaining(problem);
                        yield break;
                    }

                    _checks[FullChest] = Judge(FullChest, Store(2),
                        x => !x.Stored && x.Delivered == 0 && x.Dropped == 0 &&
                             x.Observer.Adds == 0 && x.Observer.Saves == 0,
                        $"with the chest full of {DisplayName(productName)}, Store returned false and the chest got no add, no save and no warning");

                    if (!LeaveRoomForOne(chest, out problem))
                    {
                        _checks[PartialFit] = CheckResult.NotRun(PartialFit, problem);
                        yield break;
                    }

                    _checks[PartialFit] = Judge(PartialFit, Store(Offered),
                        x => x.Stored && x.Delivered == 1 && x.Dropped == Offered - 1 &&
                             x.Observer.Adds == 1,
                        $"with room for one {DisplayName(productName)}, Store put one of {Offered} into the chest with one add and no warning, and dropped the other {Offered - 1} at the smelter, which the check removed");
                }
                finally
                {
                    // The chest held none of the product before the run; whatever the run put in,
                    // filler or stored, goes, so the ledger balances.
                    var left = chest.CountItems(productName, -1, false);
                    if (left > 0) chest.RemoveItem(productName, left, -1, false);
                }
            }

            public override void Stopped(Exception e)
            {
                var message = $"the run stopped on {e.GetType().Name}: {e.Message}";
                var running = Order.FirstOrDefault(x => !_checks.ContainsKey(x));
                if (_checks.Count == 0) _smelter.Fail(message);
                else if (running != null) _checks[running] = CheckResult.Fail(running, message);
                _notReached = "not reached: " + message;
            }

            private sealed class StoreOutcome
            {
                public string Problem;
                public bool Stored;
                public int Delivered;
                public int Dropped;
                public ContainerWriteObserver Observer;
            }

            // Calls the smelter's Store entry for offered units of its product, observed. What it
            // drops at the smelter is the run's own and is removed at once.
            private StoreOutcome Store(int offered)
            {
                var outcome = new StoreOutcome();
                var chest = _benches.ChestInventory;
                var productName = _smelter.ProductName;
                var known = new HashSet<int>(FindDrops(productName).Select(x => x.GetInstanceID()));
                var before = chest.CountItems(productName, -1, false);

                var observer = new ContainerWriteObserver(ProbeMarker, _runner);
                if (!observer.Start(_benches.Chest, out outcome.Problem)) return outcome;
                try
                {
                    outcome.Stored = SmelterProcess.Store(_smelter.Station,
                        _smelter.Chosen.From.gameObject.name, offered);
                }
                finally
                {
                    observer.Stop();
                }

                outcome.Observer = observer;
                outcome.Delivered = chest.CountItems(productName, -1, false) - before;
                foreach (var drop in FindDrops(productName).Where(x => !known.Contains(x.GetInstanceID())))
                {
                    outcome.Dropped += drop.m_itemData.m_stack;
                    SmokeSession.DestroyTestObject(drop.gameObject, 0, _runner);
                }

                return outcome;
            }

            // Every check also needs no warning while Store ran, and the warning listener's own
            // probe received, without which an empty list would prove nothing.
            private static CheckResult Judge(string check, StoreOutcome outcome,
                Func<StoreOutcome, bool> expected, string pass)
            {
                if (outcome.Problem != null) return CheckResult.NotRun(check, outcome.Problem);

                var observer = outcome.Observer;
                var seen =
                    $"Store returned {outcome.Stored}, the chest got {outcome.Delivered}, {outcome.Dropped} dropped at the smelter; {observer.Describe()}";
                if (!observer.ProbeSeen)
                    return CheckResult.Fail(check,
                        $"the warning listener did not receive its own probe, so a warning could have gone unseen; {seen}");
                return expected(outcome) && observer.Warnings.Count == 0
                    ? CheckResult.Pass(check, pass)
                    : CheckResult.Fail(check, seen);
            }

            private static IEnumerable<ItemDrop> FindDrops(string name)
            {
                return Object.FindObjectsByType<ItemDrop>(FindObjectsInactive.Exclude,
                        FindObjectsSortMode.None)
                    .Where(x => x && x.m_itemData?.m_shared != null &&
                                x.m_itemData.m_shared.m_name == name);
            }

            // Fills every slot of the chest with full stacks of the product, never adding more
            // than an empty slot takes, so filling it logs nothing.
            private bool Fill(Inventory chest, out string problem)
            {
                problem = null;
                var product = _smelter.Chosen.To;
                var productName = _smelter.ProductName;
                var maxStack = Mathf.Max(1, product.m_itemData.m_shared.m_maxStackSize);
                for (var call = 0; call < 1000 && chest.GetEmptySlots() > 0; call++)
                {
                    var count = chest.CountItems(productName, -1, false);
                    chest.AddItem(product.gameObject, maxStack);
                    if (chest.CountItems(productName, -1, false) <= count) break;
                }

                if (chest.GetEmptySlots() > 0 || Room(chest) != 0)
                    problem =
                        $"the test chest could not be filled with {DisplayName(productName)} (stack size {maxStack}, {chest.GetEmptySlots()} empty slot(s), room for {Room(chest)})";
                return problem == null;
            }

            // Takes one product out of the full chest, so it has room for exactly one.
            private bool LeaveRoomForOne(Inventory chest, out string problem)
            {
                problem = null;
                var productName = _smelter.ProductName;
                var stack = chest.GetAllItems().FirstOrDefault(x => x.m_shared.m_name == productName);
                if (stack != null) chest.RemoveItem(stack, 1);
                if (Room(chest) != 1)
                    problem =
                        $"the test chest could not be left with room for exactly one {DisplayName(productName)} (room for {Room(chest)})";
                return problem == null;
            }

            // Room for the product counted here, apart from the code under test: the space left
            // in its stacks plus whole empty slots.
            private int Room(Inventory chest)
            {
                var productName = _smelter.ProductName;
                var maxStack = Mathf.Max(1, _smelter.Chosen.To.m_itemData.m_shared.m_maxStackSize);
                return chest.GetEmptySlots() * maxStack + chest.GetAllItems()
                    .Where(x => x.m_shared.m_name == productName)
                    .Sum(x => Mathf.Max(0, x.m_shared.m_maxStackSize - x.m_stack));
            }
        }

        // -- The collect subcommand --------------------------------------------------------

        // collect: what Processing stores from a piece that collects an item over time - a
        // beehive, a sap extractor, a wisp fountain - is what taking it out by hand yields, the
        // world's resource rate included, and only in whole units. The run places a chest and the
        // three pieces as rpc does and sets the game's resource rate (Game.m_resourceRate) to
        // TestRate, through a hand-back registered with the session first. For each piece it
        // takes one unit out by hand through the piece's own RPC (Beehive.RPC_Extract,
        // SapCollector.RPC_Extract, or Pickable.RPC_Pick on a wisp the fountain lures) and counts
        // what that drops: the yield of one unit at that rate. Then it drives the piece's Store
        // entry against the chest: empty, with room for one unit and all but one item of a
        // second, with more new units than a beehive holds, and, for the sap extractor, full at
        // its maximum level and then with room for one unit, through the piece's own update tick.
        // Everything happens in the frame of the spawn. The drops the hand extraction made are
        // removed at once; whatever the run stored is taken out of the chest and every piece is
        // emptied before the hand-back. The rate is put back however the run ends.
        private sealed class CollectRun : BenchRun
        {
            private const string Setup = "processing_collect_setup";
            private const string HiveYield = "processing_collect_beehive_yield";
            private const string HiveWholeUnits = "processing_collect_beehive_whole_units";
            private const string HiveClamped = "processing_collect_beehive_clamped_to_maximum";
            private const string SapYield = "processing_collect_sapcollector_yield";
            private const string SapWholeUnits = "processing_collect_sapcollector_whole_units";
            private const string SapResumes = "processing_collect_sapcollector_resumes_when_full";
            private const string WispYield = "processing_collect_wisplure_yield";
            private const string WispWholeUnit = "processing_collect_wisplure_whole_unit";
            private const string RateRestored = "processing_collect_rate_restored";

            // A world resource rate other than the default, so that one unit yields more than one
            // item and storing one item per unit shows.
            private const float TestRate = 3f;

            // New units offered with room for one unit and all but one item of a second: one is
            // stored, the rest stays in the piece.
            private const int OfferedUnits = 3;

            // New units beyond a beehive's maximum.
            private const int UnitsAboveMaximum = 3;

            private static readonly string[] Order =
            {
                HiveYield, HiveWholeUnits, HiveClamped, SapYield, SapWholeUnits, SapResumes,
                WispYield, WispWholeUnit, RateRestored
            };

            private readonly Dictionary<string, CheckResult> _checks =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly BeehiveBench _hive;
            private readonly SapCollectorBench _sap;
            private readonly WispLureBench _wisp;
            private string _notReached = "not reached: an earlier step stopped the run";

            // The resource rate the run changes, and what it was before.
            private Func<string, bool> _rateHandBack;
            private float _rate;
            private bool _rateTouched;

            public CollectRun(int runId, string runner, Action<string> info, Action<string> warn)
                : this(runId, runner, info, warn, new BeehiveBench(Setup),
                    new SapCollectorBench(Setup), new WispLureBench(Setup))
            {
            }

            private CollectRun(int runId, string runner, Action<string> info, Action<string> warn,
                BeehiveBench hive, SapCollectorBench sap, WispLureBench wisp)
                : base(runId, runner, info, warn, new List<StationBench> { hive, sap, wisp })
            {
                _hive = hive;
                _sap = sap;
                _wisp = wisp;
            }

            public override IReadOnlyList<CheckResult> Results
            {
                get
                {
                    // A run stopped before the first check reports why under the setup check.
                    if (_checks.Count == 0)
                        return new[]
                        {
                            _stations.Select(x => x.Result).FirstOrDefault(x => x != null) ??
                            CheckResult.Fail(Setup, "the run ended before this check reached a result")
                        };

                    return Order.Select(name => _checks.TryGetValue(name, out var result)
                        ? result
                        : NotReached(name)).ToList();
                }
            }

            // A check that was not judged carries what settled its piece (a SKIP before the
            // piece was placed, say), or else why the run did not reach it.
            private CheckResult NotReached(string check)
            {
                var settled = Owner(check)?.Result;
                if (settled == null) return CheckResult.Skip(check, _notReached);

                switch (settled.Status)
                {
                    case CheckStatus.Fail:
                        return CheckResult.Fail(check, settled.Message);
                    case CheckStatus.NotRun:
                        return CheckResult.NotRun(check, settled.Message);
                    default:
                        return CheckResult.Skip(check, settled.Message);
                }
            }

            // The piece a check is about; none for the rate check.
            private StationBench Owner(string check)
            {
                if (check == RateRestored) return null;
                if (check.StartsWith("processing_collect_beehive_", StringComparison.Ordinal)) return _hive;
                return check.StartsWith("processing_collect_sapcollector_", StringComparison.Ordinal)
                    ? (StationBench)_sap
                    : _wisp;
            }

            private IEnumerable<CollectorBench> Collectors =>
                Active.OfType<CollectorBench>();

            public override IEnumerator Execute()
            {
                _info(
                    $"[Automatics Smoke] {_runner} started: it places a chest, a beehive, a sap extractor and a wisp fountain ahead of you, sets the world's resource rate to {TestRate}, takes one unit out of each by hand, drives each one's Store entry into the chest, and removes them; the resource rate and the settings it needs for the run are put back afterwards");

                var player = Player.m_localPlayer;
                var setUp = SetUp(player);
                while (setUp.MoveNext()) yield return setUp.Current;
                if (!_ready) yield break;
                if (!FindChestPrefab(out var chestPrefab)) yield break;

                ResolveStations();
                ChooseCollectorItems();
                if (Active.Count == 0) yield break;

                var lay = Lay(player);
                while (lay.MoveNext()) yield return lay.Current;
                if (!_ready) yield break;
                if (!PlaceBenches(chestPrefab, _chestPosition, _distance)) yield break;

                // Everything from here to the end happens in the frame of the spawn.
                if (!CheckSources()) yield break;

                try
                {
                    SetRate();
                    foreach (var collector in Collectors.ToList()) MeasureByHand(collector);

                    if (Collectors.Contains(_hive))
                    {
                        CheckYield(_hive, HiveYield);
                        CheckWholeUnits(_hive, HiveWholeUnits);
                        CheckHiveClamped();
                    }

                    if (Collectors.Contains(_sap))
                    {
                        CheckYield(_sap, SapYield);
                        CheckWholeUnits(_sap, SapWholeUnits);
                        CheckSapResumes();
                    }

                    if (Collectors.Contains(_wisp))
                    {
                        CheckWispYield();
                        CheckWispWholeUnit();
                    }
                }
                finally
                {
                    // The chest held nothing before the run, and the pieces held nothing; whatever
                    // the run put in either goes, so the ledger balances.
                    EmptyAll();
                    RestoreRate();
                }
            }

            public override void Stopped(Exception e)
            {
                var message = $"the run stopped on {e.GetType().Name}: {e.Message}";
                // The check that was running is the first one neither judged nor settled with its
                // piece.
                var running = Order.FirstOrDefault(x => !_checks.ContainsKey(x) && Owner(x)?.Result == null);
                if (_checks.Count == 0) FailRemaining(message);
                else if (running != null) _checks[running] = CheckResult.Fail(running, message);
                _notReached = "not reached: " + message;
            }

            public override void HandBack(string reason)
            {
                base.HandBack(reason);
                if (_rateHandBack == null) return;
                PutBackRate(reason);
                SmokeSession.ReleaseHandBack(_rateHandBack);
                _rateHandBack = null;
            }

            // A piece is tried only when Store may run on it and nothing in the settings decides
            // what it stores besides the chest; the two settings that would are set for the run.
            private void ChooseCollectorItems()
            {
                foreach (var collector in Collectors.ToList())
                {
                    if (!TurnOff("_storeOnlyIfProductExists", collector.ObjectName) ||
                        !AtMost("_productStacksOfSuppressProcessing", collector.ObjectName, 0))
                        return;

                    var rawName = collector.ObjectName.Substring(1);
                    var limit = ProcessingConfig.ProductStacksOfSuppressProcessing(collector.ObjectName);
                    if (ProcessingConfig.StoreOnlyIfProductExists(collector.ObjectName))
                        collector.Skip(
                            $"{rawName}_store_only_if_product_exists is on, so the {collector.Label} stores only into a chest that already holds its item");
                    else if (limit > 0)
                        collector.Skip(
                            $"{rawName}_product_stacks_of_suppress_processing is {limit}, so that limit, not the room in the chest, would decide what the {collector.Label} stores");
                    else if (!collector.Item)
                        collector.Skip($"the {collector.Label} yields no item in this game build");
                    else
                        collector.Choose(new Conversion(collector.Item, collector.Item, 1));
                }
            }

            // Records the rate and registers its hand-back, and only then changes it.
            private void SetRate()
            {
                _rate = Game.m_resourceRate;
                _rateHandBack = PutBackRate;
                SmokeSession.TrackHandBack(_rateHandBack);
                _rateTouched = true;
                Game.m_resourceRate = TestRate;
            }

            private bool PutBackRate(string reason)
            {
                if (_rateTouched) Game.m_resourceRate = _rate;
                _rateTouched = false;
                return true;
            }

            private void RestoreRate()
            {
                if (_rateHandBack == null) return;
                PutBackRate(_runner);
                SmokeSession.ReleaseHandBack(_rateHandBack);
                _rateHandBack = null;

                _checks[RateRestored] = Mathf.Approximately(Game.m_resourceRate, _rate)
                    ? CheckResult.Pass(RateRestored, $"the world's resource rate is back at {_rate}")
                    : CheckResult.Fail(RateRestored,
                        $"the world's resource rate is {Game.m_resourceRate}, not {_rate}");
            }

            // One unit taken out by hand, through the piece's own RPC: what that drops is the
            // yield of one unit at the test rate. Nothing else runs while the RPC does, so every
            // drop that appears meanwhile is its own; all of them are removed at once.
            private void MeasureByHand(CollectorBench collector)
            {
                var known = DropIds();
                var taken = collector.TakeOneByHand(_runner, out var problem);

                var others = new List<string>();
                foreach (var drop in AllDrops().Where(x => !known.Contains(x.GetInstanceID())).ToList())
                {
                    if (drop.m_itemData.m_shared.m_name == collector.MaterialName)
                        collector.PerUnit += drop.m_itemData.m_stack;
                    else
                        others.Add($"{DisplayName(drop.m_itemData.m_shared.m_name)} x{drop.m_itemData.m_stack}");
                    SmokeSession.DestroyTestObject(drop.gameObject, 0, _runner);
                }

                var item = DisplayName(collector.MaterialName);
                if (!taken)
                    collector.NotRun(problem);
                else if (collector.PerUnit <= 0)
                    collector.NotRun(
                        $"taking one unit out of the test {collector.Label} by hand dropped no {item}{(others.Count > 0 ? $" (it dropped {string.Join(", ", others)})" : "")}");
                else
                    collector.HandNote = others.Count == 0
                        ? ""
                        : $"; by hand it also dropped {string.Join(", ", others)}, which Store does not take";
            }

            // A removed drop stays findable until the frame ends, so each call is compared with a
            // set of drops taken right before it.
            private static IEnumerable<ItemDrop> AllDrops()
            {
                return Object.FindObjectsByType<ItemDrop>(FindObjectsInactive.Exclude,
                        FindObjectsSortMode.None)
                    .Where(x => x && x.m_itemData?.m_shared != null);
            }

            private static HashSet<int> DropIds()
            {
                return new HashSet<int>(AllDrops().Select(x => x.GetInstanceID()));
            }

            // Item drops that appeared since known was taken; nothing else runs meanwhile, so
            // they are the call's. Reported, never removed: Store drops nothing.
            private static string NewDrops(HashSet<int> known)
            {
                var found = AllDrops().Where(x => !known.Contains(x.GetInstanceID()))
                    .Select(x =>
                        $"{DisplayName(x.m_itemData.m_shared.m_name)} x{x.m_itemData.m_stack} at {Where(x.transform.position)}")
                    .ToList();
                return found.Count == 0
                    ? ""
                    : $"; new item drops, left where they are: {string.Join(", ", found)}";
            }

            // At the test rate the yield must differ from one item per unit, or storing one item
            // per unit would pass as well.
            private bool RateShows(CollectorBench collector, string check)
            {
                if (collector.PerUnit > 1) return true;
                _checks[check] = CheckResult.Skip(check,
                    $"at a resource rate of {TestRate}, one unit of the {collector.Label} still yields one {DisplayName(collector.MaterialName)} by hand in this game build, so storing one per unit cannot be told apart");
                return false;
            }

            private sealed class CollectOutcome
            {
                public bool Stored;
                public int Delivered;
                public int Level;
                public string Drops;

                public string Describe(string item)
                {
                    return
                        $"Store returned {Stored}, the chest got {Delivered} {item}, {Level} unit(s) left in the piece{Drops}";
                }
            }

            private CollectOutcome Store(CollectorBench collector, int newUnits)
            {
                var chest = _benches.ChestInventory;
                var known = DropIds();
                var before = chest.CountItems(collector.MaterialName, -1, false);
                var outcome = new CollectOutcome { Stored = collector.Store(newUnits) };
                outcome.Delivered = chest.CountItems(collector.MaterialName, -1, false) - before;
                outcome.Level = collector.Level;
                outcome.Drops = NewDrops(known);
                return outcome;
            }

            // One unit already in the piece and one new one: both are stored, each with what one
            // unit yields by hand.
            private void CheckYield(CollectorBench collector, string check)
            {
                if (!RateShows(collector, check)) return;

                ClearChest();
                collector.Level = 1;
                var outcome = Store(collector, 1);
                var item = DisplayName(collector.MaterialName);
                var expected = 2 * collector.PerUnit;
                _checks[check] = outcome.Stored && outcome.Delivered == expected &&
                                 outcome.Level == 0 && outcome.Drops.Length == 0
                    ? CheckResult.Pass(check,
                        $"at a resource rate of {TestRate}, one unit of the {collector.Label} yields {collector.PerUnit} {item} by hand, and Store put the 2 units it held into the chest as {expected} {item} and emptied it{collector.HandNote}")
                    : CheckResult.Fail(check,
                        $"one unit yields {collector.PerUnit} {item} by hand, so 2 units should store {expected} and leave none; {outcome.Describe(item)}");
            }

            // Room for one unit and all but one item of a second: one unit goes in with its whole
            // yield, and the others stay in the piece; no part of a unit is stored.
            private void CheckWholeUnits(CollectorBench collector, string check)
            {
                if (!RateShows(collector, check)) return;

                var item = DisplayName(collector.MaterialName);
                var offered = Mathf.Min(OfferedUnits, collector.MaxLevel);
                if (offered < 2)
                {
                    _checks[check] = CheckResult.Skip(check,
                        $"the {collector.Label} holds at most {collector.MaxLevel} unit(s), so no unit can be left in it");
                    return;
                }

                var room = 2 * collector.PerUnit - 1;
                if (!LeaveRoom(collector, room, out var problem))
                {
                    _checks[check] = CheckResult.NotRun(check, problem);
                    return;
                }

                collector.Level = 0;
                var outcome = Store(collector, offered);
                var roomLeft = Room(collector);
                _checks[check] = outcome.Stored && outcome.Delivered == collector.PerUnit &&
                                 outcome.Level == offered - 1 && roomLeft == room - collector.PerUnit &&
                                 outcome.Drops.Length == 0
                    ? CheckResult.Pass(check,
                        $"with room for {room} {item} in the chest and {offered} new units, Store put one unit in as {collector.PerUnit} {item} and left {offered - 1} units in the {collector.Label}")
                    : CheckResult.Fail(check,
                        $"with room for {room} {item} and {offered} new units of {collector.PerUnit} {item} each, Store should put in {collector.PerUnit} and leave {offered - 1} units; {outcome.Describe(item)}, room for {roomLeft} left");
            }

            // More new units than the hive holds: it stores what vanilla's IncreseLevel would
            // have kept, its maximum, and drops the rest as vanilla does.
            private void CheckHiveClamped()
            {
                var check = HiveClamped;
                var item = DisplayName(_hive.MaterialName);
                var maximum = _hive.MaxLevel;
                var expected = maximum * _hive.PerUnit;
                var maxStack = _hive.Item.m_itemData.m_shared.m_maxStackSize;
                if (maximum < 1 || expected > maxStack)
                {
                    _checks[check] = CheckResult.Skip(check,
                        $"the beehive holds {maximum} unit(s) of {_hive.PerUnit} {item}, which is not one stack of {maxStack}");
                    return;
                }

                ClearChest();
                _hive.Level = 0;
                var offered = maximum + UnitsAboveMaximum;
                var outcome = Store(_hive, offered);
                _checks[check] = outcome.Stored && outcome.Delivered == expected &&
                                 outcome.Level == 0 && outcome.Drops.Length == 0
                    ? CheckResult.Pass(check,
                        $"with {offered} new units and room in the chest, Store put the beehive's maximum of {maximum} units in as {expected} {item} and kept none of the rest")
                    : CheckResult.Fail(check,
                        $"the beehive holds at most {maximum} units of {_hive.PerUnit} {item}, so {offered} new units should store {expected}; {outcome.Describe(item)}");
            }

            // At its maximum level the sap extractor stops producing, so no new unit reaches
            // Store. Its own update tick must still store what it holds once room appears.
            private void CheckSapResumes()
            {
                var check = SapResumes;
                var item = DisplayName(_sap.MaterialName);
                var maximum = _sap.MaxLevel;
                if (!SapCollectorBench.CanTick)
                {
                    _checks[check] = CheckResult.NotRun(check,
                        "SapCollector.UpdateTick was not found in this game build");
                    return;
                }

                if (maximum < 1)
                {
                    _checks[check] = CheckResult.Skip(check, "the sap extractor holds no unit");
                    return;
                }

                if (!LeaveRoom(_sap, 0, out var problem))
                {
                    _checks[check] = CheckResult.NotRun(check, problem);
                    return;
                }

                _sap.Level = maximum;
                var chest = _benches.ChestInventory;
                var known = DropIds();
                var before = chest.CountItems(_sap.MaterialName, -1, false);
                _sap.Tick();
                var heldWhileFull = _sap.Level;
                var deliveredWhileFull = chest.CountItems(_sap.MaterialName, -1, false) - before;

                chest.RemoveItem(_sap.MaterialName, _sap.PerUnit, -1, false);
                if (Room(_sap) != _sap.PerUnit)
                {
                    _checks[check] = CheckResult.NotRun(check,
                        $"the test chest could not be left with room for exactly {_sap.PerUnit} {item} (room for {Room(_sap)})");
                    return;
                }

                before = chest.CountItems(_sap.MaterialName, -1, false);
                _sap.Tick();
                var delivered = chest.CountItems(_sap.MaterialName, -1, false) - before;
                var drops = NewDrops(known);
                _checks[check] = heldWhileFull == maximum && deliveredWhileFull == 0 &&
                                 delivered == _sap.PerUnit && _sap.Level == maximum - 1 &&
                                 drops.Length == 0
                    ? CheckResult.Pass(check,
                        $"full at its maximum of {maximum} units with the chest full, the sap extractor kept them on a tick; with room for one unit, its next tick stored {delivered} {item} and left {maximum - 1}")
                    : CheckResult.Fail(check,
                        $"with the chest full, a tick left {heldWhileFull} of {maximum} units and stored {deliveredWhileFull}; with room for one unit of {_sap.PerUnit} {item}, the next tick stored {delivered} and left {_sap.Level} units (expected {_sap.PerUnit} stored and {maximum - 1} left){drops}");
            }

            // One wisp lured into the chest instead: the whole yield of picking it by hand.
            private void CheckWispYield()
            {
                var check = WispYield;
                if (!RateShows(_wisp, check)) return;

                ClearChest();
                var item = DisplayName(_wisp.MaterialName);
                var outcome = Store(_wisp, 1);
                _checks[check] = outcome.Stored && outcome.Delivered == _wisp.PerUnit &&
                                 outcome.Drops.Length == 0
                    ? CheckResult.Pass(check,
                        $"at a resource rate of {TestRate}, picking a lured wisp by hand yields {_wisp.PerUnit} {item}, and Store put {_wisp.PerUnit} into the chest{_wisp.HandNote}")
                    : CheckResult.Fail(check,
                        $"picking a lured wisp by hand yields {_wisp.PerUnit} {item}; Store returned {outcome.Stored} and the chest got {outcome.Delivered}{outcome.Drops}");
            }

            // Room for all but one item of a wisp's yield: nothing goes in, and Store leaves the
            // wisp to the game.
            private void CheckWispWholeUnit()
            {
                var check = WispWholeUnit;
                if (!RateShows(_wisp, check)) return;

                var item = DisplayName(_wisp.MaterialName);
                var room = _wisp.PerUnit - 1;
                if (!LeaveRoom(_wisp, room, out var problem))
                {
                    _checks[check] = CheckResult.NotRun(check, problem);
                    return;
                }

                var outcome = Store(_wisp, 1);
                _checks[check] = !outcome.Stored && outcome.Delivered == 0 && outcome.Drops.Length == 0
                    ? CheckResult.Pass(check,
                        $"with room for {room} {item} in the chest, Store put none of a wisp's {_wisp.PerUnit} in and returned false, so the fountain lures the wisp as usual")
                    : CheckResult.Fail(check,
                        $"with room for {room} {item}, a wisp's {_wisp.PerUnit} do not fit; Store returned {outcome.Stored} and the chest got {outcome.Delivered}{outcome.Drops}");
            }

            // Takes every collector's item out of the chest: the chest held nothing before the run.
            private void ClearChest()
            {
                var chest = _benches?.ChestInventory;
                if (chest == null) return;
                foreach (var collector in _stations.OfType<CollectorBench>().Where(x => x.Chosen != null))
                {
                    var left = chest.CountItems(collector.MaterialName, -1, false);
                    if (left > 0) chest.RemoveItem(collector.MaterialName, left, -1, false);
                }
            }

            private void EmptyAll()
            {
                ClearChest();
                foreach (var collector in _stations.OfType<CollectorBench>())
                    if (collector.Go && collector.NView != null && collector.NView.IsValid())
                        collector.Level = 0;
            }

            // Fills every slot of the chest with full stacks of the item and takes room out again,
            // so the chest has room for exactly that many. Filling never calls an add that does not
            // fit.
            private bool LeaveRoom(CollectorBench collector, int room, out string problem)
            {
                ClearChest();
                var chest = _benches.ChestInventory;
                var name = collector.MaterialName;
                var maxStack = Mathf.Max(1, collector.Item.m_itemData.m_shared.m_maxStackSize);
                for (var call = 0; call < 1000 && chest.GetEmptySlots() > 0; call++)
                {
                    var count = chest.CountItems(name, -1, false);
                    chest.AddItem(collector.MaterialPrefab, maxStack);
                    if (chest.CountItems(name, -1, false) <= count) break;
                }

                if (room > 0 && chest.GetEmptySlots() == 0) chest.RemoveItem(name, room, -1, false);
                var left = Room(collector);
                problem = left == room
                    ? null
                    : $"the test chest could not be left with room for exactly {room} {DisplayName(name)} (stack size {maxStack}, room for {left})";
                return problem == null;
            }

            // Room for the item counted here, apart from the code under test: the space left in its
            // stacks plus whole empty slots.
            private int Room(CollectorBench collector)
            {
                var chest = _benches.ChestInventory;
                var maxStack = Mathf.Max(1, collector.Item.m_itemData.m_shared.m_maxStackSize);
                return chest.GetEmptySlots() * maxStack + chest.GetAllItems()
                    .Where(x => x.m_shared.m_name == collector.MaterialName)
                    .Sum(x => Mathf.Max(0, x.m_shared.m_maxStackSize - x.m_stack));
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

                if (problem == null) ProcessingIsolation.Register(Chest);

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
                // Even an object deliberately kept in the world is no longer a test fixture.
                ProcessingIsolation.Unregister(Chest);
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
                    var component = FindComponent(go);
                    if (!component || Objects.GetName(component) != ObjectName) continue;

                    Prefab = go;
                    return BindPrefab(go);
                }

                return false;
            }

            // The station's component on a prefab or a spawned piece: on its root object, unless
            // the station keeps it further down.
            protected virtual Component FindComponent(GameObject go)
            {
                return go.GetComponent(ComponentType);
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

            public SmelterBench() : this("processing_rpc_smelter_add_ore")
            {
            }

            public SmelterBench(string checkName) : base(checkName, "$piece_smelter", "smelter")
            {
            }

            public Smelter Station => _station;

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

            public OvenBench() : this("processing_rpc_cooking_station_add_item")
            {
            }

            public OvenBench(string checkName) : base(checkName, "$piece_oven", "stone oven")
            {
            }

            public CookingStation Station => _station;

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

        // A piece that collects an item over time and holds it, in units, until it is taken out:
        // a beehive, a sap extractor, a wisp fountain. It takes no material, so its one conversion
        // is its own item to itself: the chest may hold that item, and the hand-back counts it.
        // The collect run takes everything it stored out of the chest and empties the piece before
        // the hand-back, so the ledger balances with nothing put in.
        private abstract class CollectorBench : StationBench
        {
            protected CollectorBench(string checkName, string objectName, string label)
                : base(checkName, objectName, label)
            {
            }

            public override string FuelName => null;

            public override Process RequiredProcess => Process.Store;

            // The item the piece yields, from its prefab; null when it yields none.
            public abstract ItemDrop Item { get; }

            // The units the piece holds at most.
            public abstract int MaxLevel { get; }

            // What one unit yields when taken out by hand, as the collect run measured it, and
            // anything else that drop brought.
            public int PerUnit { get; set; }
            public string HandNote { get; set; } = "";

            // The units the piece holds (ZDOVars.s_level, the "level" Processing reads).
            public virtual int Level
            {
                get => NView.GetZDO().GetInt(ZDOVars.s_level);
                set => NView.GetZDO().Set(ZDOVars.s_level, value);
            }

            public override IEnumerable<Conversion> Conversions()
            {
                var item = Item;
                if (item) yield return new Conversion(item, item, 1);
            }

            public override void Prepare()
            {
            }

            public override void Drive()
            {
                Store(0);
            }

            // The piece's Store entry, as the game reaches it when newUnits are made.
            public abstract bool Store(int newUnits);

            // Takes one unit out as a player's hand does, through the piece's own RPC.
            public abstract bool TakeOneByHand(string runner, out string problem);

            public override StationContent ReadContent()
            {
                return new StationContent { Products = Level };
            }

            public override void ClearContent()
            {
                Level = 0;
            }

            protected override string DiagnoseStation()
            {
                return $"level={Level}/{MaxLevel}";
            }
        }

        private sealed class BeehiveBench : CollectorBench
        {
            private Beehive _prefab;
            private Beehive _station;

            public BeehiveBench(string checkName) : base(checkName, "$piece_beehive", "beehive")
            {
            }

            protected override Type ComponentType => typeof(Beehive);

            public override ItemDrop Item => _prefab ? _prefab.m_honeyItem : null;

            public override int MaxLevel => _station ? _station.m_maxHoney : 0;

            protected override bool BindPrefab(GameObject prefab)
            {
                _prefab = prefab.GetComponent<Beehive>();
                return _prefab != null;
            }

            protected override Component BindStation(GameObject go)
            {
                _station = go.GetComponent<Beehive>();
                return _station;
            }

            public override bool Store(int newUnits)
            {
                return BeehiveProcess.Store(_station, NView, newUnits);
            }

            // Beehive.RPC_Extract drops each unit it holds, scaled by Game.ScaleDrops, and empties
            // the hive.
            public override bool TakeOneByHand(string runner, out string problem)
            {
                Level = 1;
                NView.InvokeRPC("RPC_Extract");
                problem = Level == 0 ? null : $"the beehive's own RPC_Extract left {Level} unit(s) in it";
                return problem == null;
            }
        }

        private sealed class SapCollectorBench : CollectorBench
        {
            private static readonly MethodInfo UpdateTickMethod =
                AccessTools.Method(typeof(SapCollector), "UpdateTick");

            private SapCollector _prefab;
            private SapCollector _station;

            public SapCollectorBench(string checkName)
                : base(checkName, "$piece_sapcollector", "sap extractor")
            {
            }

            public static bool CanTick => UpdateTickMethod != null;

            protected override Type ComponentType => typeof(SapCollector);

            public override ItemDrop Item => _prefab ? _prefab.m_spawnItem : null;

            public override int MaxLevel => _station ? _station.m_maxLevel : 0;

            protected override bool BindPrefab(GameObject prefab)
            {
                _prefab = prefab.GetComponent<SapCollector>();
                return _prefab != null;
            }

            protected override Component BindStation(GameObject go)
            {
                _station = go.GetComponent<SapCollector>();
                return _station;
            }

            public override bool Store(int newUnits)
            {
                return SapCollectorProcess.Store(_station, NView, newUnits);
            }

            // SapCollector.RPC_Extract drops each unit it holds, scaled by Game.ScaleDrops, and
            // empties the piece.
            public override bool TakeOneByHand(string runner, out string problem)
            {
                Level = 1;
                NView.InvokeRPC("RPC_Extract");
                problem = Level == 0 ? null : $"the sap extractor's own RPC_Extract left {Level} unit(s) in it";
                return problem == null;
            }

            // The piece's own update (SapCollector.UpdateTick, which the game runs every few
            // seconds), with whatever is patched into it.
            public void Tick()
            {
                UpdateTickMethod.Invoke(_station, null);
            }
        }

        private sealed class WispLureBench : CollectorBench
        {
            private WispSpawner _prefab;
            private WispSpawner _station;

            public WispLureBench(string checkName)
                : base(checkName, "$piece_wisplure", "wisp fountain")
            {
            }

            protected override Type ComponentType => typeof(WispSpawner);

            // The game's WispSpawner finds its network view among its parents (WispSpawner.Start),
            // so it need not sit on the piece's root.
            protected override Component FindComponent(GameObject go)
            {
                return go.GetComponentInChildren<WispSpawner>(true);
            }

            private Pickable Lured =>
                _prefab && _prefab.m_wispPrefab ? _prefab.m_wispPrefab.GetComponent<Pickable>() : null;

            public override ItemDrop Item
            {
                get
                {
                    var lured = Lured;
                    return lured && lured.m_itemPrefab ? lured.m_itemPrefab.GetComponent<ItemDrop>() : null;
                }
            }

            public override int MaxLevel => 0;

            // A wisp fountain holds nothing: each unit is a wisp it lures, or Store takes instead.
            public override int Level
            {
                get => 0;
                set { }
            }

            protected override bool BindPrefab(GameObject prefab)
            {
                _prefab = (WispSpawner)FindComponent(prefab);
                return _prefab != null;
            }

            // WispSpawner sets m_nview only in Start, after the frame of the spawn, so the bench
            // binds the view WispSpawner.Start would find and hands it to Store itself.
            protected override Component BindStation(GameObject go)
            {
                _station = (WispSpawner)FindComponent(go);
                return _station ? _station.GetComponentInParent<ZNetView>() : null;
            }

            public override bool Store(int newUnits)
            {
                return WispSpawnerProcess.Store(_station, NView);
            }

            // A wisp the fountain lures, picked as a player's hand picks it: Pickable.RPC_Pick
            // without a skill bonus, which drops its yield and removes the wisp. The wisp is the
            // run's own and is removed in any case.
            public override bool TakeOneByHand(string runner, out string problem)
            {
                problem = null;
                var prefab = _station.m_wispPrefab;
                if (!prefab || !prefab.GetComponent<Pickable>())
                {
                    problem = "the wisp fountain lures nothing that can be picked";
                    return false;
                }

                var wisp = Object.Instantiate(prefab, _station.transform.position + Vector3.up,
                    Quaternion.identity);
                try
                {
                    var nview = wisp.GetComponent<ZNetView>();
                    if (!nview || !nview.IsValid())
                    {
                        problem = "the lured wisp has no valid ZNetView";
                        return false;
                    }

                    nview.GetZDO().Persistent = false;
                    nview.InvokeRPC("RPC_Pick", 0);
                    return true;
                }
                finally
                {
                    SmokeSession.DestroyTestObject(wisp, 0, runner);
                }
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

    // Watches one container while a check calls the code under test: every call that adds to
    // its inventory (Inventory.AddItem(ItemDrop.ItemData), which the adds by prefab go through),
    // every save of its contents (Container.Save, which the inventory's change callback runs),
    // and every warning or error Unity logs meanwhile. Its Harmony prefixes are applied by Start
    // and removed by Stop, so nothing stays patched outside the check. Start logs a deliberate
    // warning that names the command; receiving it shows that the listener sees what Unity
    // logs, without which an empty list would prove nothing. The processing store check and the
    // farming deposit checks share it.
    internal sealed class ContainerWriteObserver
    {
        private const string HarmonyId = "net.eidee.valheim.automatics.smoke.container-writes";

        private static readonly MethodInfo AddMethod = AccessTools.Method(typeof(Inventory),
            nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) });

        private static readonly MethodInfo SaveMethod = AccessTools.Method(typeof(Container), "Save");

        private static ContainerWriteObserver _active;

        private readonly string _probeMarker;
        private readonly string _command;
        private readonly List<string> _warnings = new List<string>();
        private Harmony _harmony;
        private Container _container;
        private Inventory _inventory;

        public ContainerWriteObserver(string probeMarker, string command)
        {
            _probeMarker = probeMarker;
            _command = command;
        }

        public int Adds { get; private set; }
        public int Saves { get; private set; }
        public bool ProbeSeen { get; private set; }
        public IReadOnlyList<string> Warnings => _warnings;

        // False, with the reason, when there is nothing to watch, a method to patch is missing,
        // or another observer is still watching.
        public bool Start(Container container, out string problem)
        {
            problem = null;
            if (!container || container.GetInventory() == null)
                problem = "the container to watch has no inventory";
            else if (AddMethod == null || SaveMethod == null)
                problem = "Inventory.AddItem(ItemDrop.ItemData) or Container.Save was not found in this game build";
            else if (_active != null)
                problem = "another container observer is still watching";
            if (problem != null) return false;

            _container = container;
            _inventory = container.GetInventory();
            _active = this;
            try
            {
                _harmony = new Harmony(HarmonyId);
                _harmony.Patch(AddMethod,
                    new HarmonyMethod(typeof(ContainerWriteObserver), nameof(AddPrefix)));
                _harmony.Patch(SaveMethod,
                    new HarmonyMethod(typeof(ContainerWriteObserver), nameof(SavePrefix)));
            }
            catch (Exception e)
            {
                Stop();
                problem = $"the container observer could not patch the game ({e.GetType().Name}: {e.Message})";
                return false;
            }

            Application.logMessageReceived += OnLog;
            UnityEngine.Debug.LogWarning(
                $"{_probeMarker}: a deliberate test warning of {_command}, logged to show the check sees the warnings Unity logs; it is not a problem");
            return true;
        }

        public void Stop()
        {
            Application.logMessageReceived -= OnLog;
            if (ReferenceEquals(_active, this)) _active = null;
            if (_harmony == null) return;
            var harmony = _harmony;
            _harmony = null;
            harmony.UnpatchSelf();
        }

        public string Describe()
        {
            return $"adds {Adds}, saves {Saves}, warnings {_warnings.Count}" +
                   (_warnings.Count == 0 ? "" : $" ({string.Join(" | ", _warnings)})");
        }

        private static void AddPrefix(Inventory __instance)
        {
            var active = _active;
            if (active != null && ReferenceEquals(__instance, active._inventory)) active.Adds++;
        }

        private static void SavePrefix(Container __instance)
        {
            var active = _active;
            if (active != null && ReferenceEquals(__instance, active._container)) active.Saves++;
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Log) return;
            if (condition != null && condition.Contains(_probeMarker))
            {
                ProbeSeen = true;
                return;
            }

            _warnings.Add($"[{type}] {condition?.Trim()}");
        }
    }
}
