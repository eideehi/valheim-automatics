using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using ModUtils;
using UnityEngine;

namespace Automatics.SmokeTest
{
    // In-game console command for the manual-assist features: `automatics_smoke_features all`
    // runs every section, `automatics_smoke_features <section>` one of them. Each section drives
    // the real game code path of one feature against objects it spawns next to the player, and
    // logs PASS/FAIL/SKIP lines; the run ends with one Summary line over every section it ran. A
    // section that has no checks yet logs one SKIP saying so.
    //
    // The command follows the rules every smoke command follows (smoke/README.md): one run at a
    // time through SmokeSession, test objects that live past a frame marked non-persistent and
    // recorded with the session before the first frame boundary, and nothing removed from the
    // inventory unless it is provably the check's own. It writes no setting: a section whose
    // feature is turned off, or whose settings keep a check from showing anything, SKIPs that
    // check and says which setting to change.
    //
    // Registration mirrors WorldSmokeCommand: subscribed to Hooks.OnInitTerminal before the
    // terminal's one-time init, and built once.
    internal static class FeaturesSmokeCommand
    {
        private const string CommandName = "automatics_smoke_features";
        private const string AllArgument = "all";

        // In the order `all` runs them. A null body is a section that has no checks yet.
        private static readonly KeyValuePair<string, Func<FeatureRun, IEnumerator>>[] Sections =
        {
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("pickup", PickupFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("pin", PinFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("mining", MiningFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("planting", PlantingFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("guide", null),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("supply", null)
        };

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Features");

        private static MonoBehaviour _host;
        private static bool _registered;
        private static bool _commandBuilt;

        internal static void EnsureRegistered(MonoBehaviour host)
        {
            _host = host;
            if (_registered) return;
            _registered = true;

            Hooks.OnInitTerminal += BuildCommand;
        }

        private static string Usage =>
            $"[Automatics Smoke] usage: {CommandName} {AllArgument}|{string.Join("|", Sections.Select(x => x.Key))}";

        private static void BuildCommand()
        {
            if (_commandBuilt) return;
            _commandBuilt = true;

            _ = new Terminal.ConsoleCommand(
                CommandName,
                $"Run the Automatics feature smoke checks: '{AllArgument}' for every section, or one of {string.Join(", ", Sections.Select(x => x.Key))}.",
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
            if (arguments.Count != 1 ||
                (arguments[0] != AllArgument && Sections.All(x => x.Key != arguments[0])))
            {
                Emit(context, LogLevel.Warning, Usage);
                return;
            }

            var selected = arguments[0] == AllArgument
                ? Sections
                : Sections.Where(x => x.Key == arguments[0]).ToArray();
            var runner = $"{CommandName} {arguments[0]}";
            if (!SmokeSession.TryBegin(runner, out var runId, out var refusal))
            {
                Emit(context, LogLevel.Warning,
                    $"[Automatics Smoke] {runner} was not started: {refusal}");
                return;
            }

            // Guard hands back the run's obligations and releases the lock however the body
            // ends, a throw included.
            _host.StartCoroutine(SmokeSession.Guard(runId, runner, Run(context, runId, runner, selected)));
        }

        private static IEnumerator Run(Terminal context, int runId, string runner,
            KeyValuePair<string, Func<FeatureRun, IEnumerator>>[] sections)
        {
            var run = new FeatureRun(runId);
            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {runner} started: {string.Join(", ", sections.Select(x => x.Key))}");

            foreach (var section in sections)
            {
                if (section.Value == null)
                {
                    run.Skip($"{section.Key}_section", "not implemented yet");
                    continue;
                }

                if (!run.IsCurrent)
                {
                    run.Skip($"{section.Key}_section", "the run was abandoned before this section");
                    continue;
                }

                // Each section starts in a frame of its own, so the objects the previous one
                // removed are gone from the scene.
                yield return null;

                var steps = Contain(section.Value(run), run, $"{section.Key}_section");
                while (steps.MoveNext()) yield return steps.Current;
            }

            Report(context, run.Results);
        }

        // Advances a section and turns an exception into a FAIL, so the sections after it still
        // run. A yield cannot sit inside a try that has a catch, so each step is taken inside one
        // and the yield happens outside it. A section that throws has already registered what it
        // spawned with SmokeSession, and its own finally blocks run when it is disposed here.
        internal static IEnumerator Contain(IEnumerator steps, FeatureRun run, string name)
        {
            try
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
            finally
            {
                (steps as IDisposable)?.Dispose();
            }
        }

        private static void Report(Terminal context, IReadOnlyList<FeatureResult> results)
        {
            foreach (var result in results)
            {
                if (result.Status == FeatureStatus.Pass)
                    Emit(context, LogLevel.Info, string.IsNullOrEmpty(result.Message)
                        ? $"[Automatics Smoke] PASS {result.Name}"
                        : $"[Automatics Smoke] PASS {result.Name}: {result.Message}");
                else if (result.Status == FeatureStatus.Skip)
                    Emit(context, LogLevel.Warning,
                        $"[Automatics Smoke] SKIP {result.Name}: {result.Message}");
                else
                    Emit(context, LogLevel.Error,
                        $"[Automatics Smoke] FAIL {result.Name}: {result.Message}");
            }

            var failed = results.Where(result => result.Status == FeatureStatus.Fail)
                                .Select(result => result.Name)
                                .ToArray();
            var skipped = results.Where(result => result.Status == FeatureStatus.Skip)
                                 .Select(result => result.Name)
                                 .ToArray();
            var passed = results.Count(result => result.Status == FeatureStatus.Pass);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] Summary: total={results.Count} passed={passed} failed={failed.Length} skipped={skipped.Length} failedChecks=[{string.Join(", ", failed)}] skippedChecks=[{string.Join(", ", skipped)}]");
        }

        internal static void Emit(Terminal context, LogLevel level, string line)
        {
            Log.Log(level, line);

            // The run can outlive the console that started it (a logout while it waits).
            if (context) context.AddString(line);
        }

        internal static void Warn(string line)
        {
            Log.LogWarning(line);
        }

        // -- Helpers the sections share -----------------------------------------------------

        // Spawns one item of the prefab as a drop, kept out of every world save and recorded with
        // the session before the first frame boundary, so a run cut short still has it removed
        // (only while it still holds the one item it was given). autoPickup false keeps vanilla
        // auto-pickup and Automatic Pickup's periodic pass off it: both skip a drop whose
        // m_autoPickup is false, the flag vanilla sets on an item the player dropped. It is kept
        // out of vanilla auto-stacking, so no real drop can merge into it. Returns null when the
        // drop could not be set up; anything spawned is then already recorded.
        internal static ItemDrop SpawnTestDrop(GameObject prefab, Vector3 position, bool autoPickup)
        {
            var go = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            SmokeSession.TrackSpawn(go, 1);
            if (Objects.GetZNetView(go.transform, out var nview) && nview != null &&
                nview.IsValid() && !nview.IsOwner())
                nview.ClaimOwnership();

            var itemDrop = go.GetComponent<ItemDrop>();
            if (itemDrop == null) return null;

            itemDrop.m_itemData.m_stack = 1;
            itemDrop.m_itemData.m_worldLevel = (byte)Game.m_worldLevel;
            ItemDrop.OnCreateNew(itemDrop);

            if (!Objects.GetZNetView(itemDrop, out var dropNview) || dropNview == null ||
                dropNview.GetZDO() == null)
                return null;

            dropNview.GetZDO().Persistent = false;
            itemDrop.m_autoPickup = autoPickup;

            // Vanilla ItemDrop.AutoStackItems pulls nearby drops of the same name into a drop
            // unless its m_haveAutoStacked is set (the field name as of Valheim 1.0.7). Should it
            // be gone, the stack check before the drop is destroyed still catches a merge.
            var haveAutoStacked = typeof(ItemDrop).GetField("m_haveAutoStacked",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (haveAutoStacked != null && haveAutoStacked.FieldType == typeof(bool))
                haveAutoStacked.SetValue(itemDrop, true);

            return itemDrop;
        }

        // Removes a test drop that is still in the world, unless real items merged into it.
        internal static void RemoveTestDrop(ItemDrop itemDrop, string label)
        {
            if (!itemDrop) return;
            var go = itemDrop.gameObject;
            if (SmokeSession.DestroyTestObject(go, 1, label))
                SmokeSession.ReleaseSpawn(go);
        }

        // Humanoid.Pickup destroys the drop through ZNetScene.Destroy, which drops the ZDO at
        // once and the GameObject at the end of the frame.
        internal static bool IsConsumed(ItemDrop itemDrop)
        {
            return !itemDrop ||
                   !Objects.GetZNetView(itemDrop, out var nview) || nview == null ||
                   !nview.IsValid() || nview.GetZDO() == null;
        }

        // Removes up to amount of this exact item - name, quality and world level, the identity
        // PickableHarvester.CountExactItems counts - and nothing else. Returns how many went.
        internal static int RemoveExactItems(Inventory inventory, ItemDrop.ItemData sample, int amount)
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
    }

    internal enum FeatureStatus
    {
        Pass,
        Fail,
        Skip
    }

    internal sealed class FeatureResult
    {
        public FeatureResult(FeatureStatus status, string name, string message)
        {
            Status = status;
            Name = name ?? "";
            Message = message ?? "";
        }

        public FeatureStatus Status { get; }
        public string Name { get; }
        public string Message { get; }
    }

    // One run of the command: its session id and the results every section adds to.
    internal sealed class FeatureRun
    {
        private readonly List<FeatureResult> _results = new List<FeatureResult>();

        public FeatureRun(int runId)
        {
            RunId = runId;
        }

        public int RunId { get; }
        public bool IsCurrent => SmokeSession.IsCurrent(RunId);
        public IReadOnlyList<FeatureResult> Results => _results;

        // A pass can carry a note - a test item that had to be left behind, for instance.
        public void Pass(string name, string note = "")
        {
            _results.Add(new FeatureResult(FeatureStatus.Pass, name, note));
        }

        public void Fail(string name, string message)
        {
            _results.Add(new FeatureResult(FeatureStatus.Fail, name, message));
        }

        public void Skip(string name, string message)
        {
            _results.Add(new FeatureResult(FeatureStatus.Skip, name, message));
        }
    }
}
