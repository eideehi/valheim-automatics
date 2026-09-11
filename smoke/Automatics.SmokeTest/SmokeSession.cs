using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.SmokeTest
{
    // What the smoke console commands share: one run at a time, and what a run owes the
    // user's game - a module setting it changed, spawned test objects - until it is handed
    // back. HandBack does that:
    //   - when a command's body completes or throws (Guard; Unity stops a coroutine that
    //     throws without running its iterator's finally, so the body cannot do it itself);
    //   - when the smoke plugin is disabled or destroyed, or the game quits (SmokePlugin's
    //     OnDisable / OnDestroy / OnApplicationQuit);
    //   - at the start of the next run, for anything the triggers above could not hand back.
    // A logout does not end a run: the plugin object survives scene changes. A crash ends the
    // process, so the setting is changed only after a marker file records the change; the
    // next start logs a warning from it and never writes the config from it.
    //
    // Every hand-back follows the asymmetric rule: a test item left behind, or a setting left
    // as the user set it, is harmless; deleting a real item or overwriting the user's own
    // choice is not. What is not provably the check's own is left, and an obligation that
    // could not be discharged is kept rather than dropped.
    internal static class SmokeSession
    {
        // Named after the first command that used it. It is kept for every module toggle, so a
        // record the lifecycle command left behind is still found.
        private const string MarkerFile = "lifecycle-toggle-restore.txt";

        // A record that names no command was written by the lifecycle command, the only one
        // that wrote records before they carried the command's name.
        private const string DefaultMarkerCommand = "automatics_smoke_lifecycle";

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Session");

        private static readonly List<PendingSpawn> PendingSpawns = new List<PendingSpawn>();

        private static readonly List<Func<string, bool>> PendingHandBacks =
            new List<Func<string, bool>>();

        private static string _runner;
        private static int _runId;
        private static ToggleRestoration _toggle;
        private static bool _writingToggle;

        internal enum ToggleOutcome
        {
            NothingWritten,
            Restored,
            AlreadyOriginal,
            LeftForUser,
            NotThisRun,
            Failed
        }

        public static bool ToggleEditedByUser => _toggle != null && _toggle.EditedByUser;

        public static bool IsCurrent(int runId)
        {
            return _runner != null && _runId == runId;
        }

        // -- Runs ----------------------------------------------------------------------

        public static bool TryBegin(string command, out int runId, out string refusal)
        {
            runId = 0;
            refusal = null;
            if (_runner != null)
            {
                refusal = $"{_runner} is still running; wait for its Summary line";
            }
            else
            {
                // Anything an earlier trigger could not hand back is tried again. A setting that
                // still cannot be put back blocks the run: a second obligation on the same
                // setting would lose the value the user started with.
                HandBack("before a new smoke run");
                if (_toggle != null)
                    refusal =
                        $"an earlier run could not put {_toggle.Name} back (see the warning in the log); set it yourself, then run the command again";
                else if (PendingHandBacks.Count > 0)
                    refusal =
                        "an earlier run could not remove its test objects yet (see the warning in the log); run the command again later, or restart the game";
            }

            if (refusal != null) return false;

            _runner = command;
            runId = ++_runId;
            return true;
        }

        // Drives a command's body. Whether it completes or throws, the run's obligations are
        // handed back and the lock released before the coroutine stops. Each step is taken
        // inside a try and the yield happens outside it: a yield cannot sit in a try with a
        // catch.
        public static IEnumerator Guard(int runId, string command, IEnumerator body)
        {
            while (true)
            {
                object current = null;
                var more = false;
                try
                {
                    more = body.MoveNext();
                    if (more) current = body.Current;
                }
                catch (Exception e)
                {
                    Log.LogError($"[Automatics Smoke] {command} stopped on {e.GetType().Name}: {e.Message}");
                }

                if (more)
                {
                    yield return current;
                    continue;
                }

                if (IsCurrent(runId)) HandBack($"{command} ended");
                yield break;
            }
        }

        public static void HandBack(string reason)
        {
            var outcome = DischargeToggle(out var detail);
            if (outcome != ToggleOutcome.NothingWritten)
                Log.LogWarning($"[Automatics Smoke] {reason}: {detail}");

            PendingSpawns.RemoveAll(spawn => DestroyTestObject(spawn.Go, spawn.DropStack, reason));
            foreach (var handBack in PendingHandBacks.ToArray())
                if (RunHandBack(handBack, reason))
                    PendingHandBacks.Remove(handBack);
            _runner = null;
        }

        // -- Test objects whose removal depends on what they hold ----------------------

        // For test objects that can only be removed after looking at what they hold - a chest
        // and the stations it feeds, say - the command registers its own hand-back, before the
        // first frame boundary. Every trigger above runs it until it reports that it is done;
        // false keeps it for the next trigger.
        public static void TrackHandBack(Func<string, bool> handBack)
        {
            if (handBack != null && !PendingHandBacks.Contains(handBack))
                PendingHandBacks.Add(handBack);
        }

        public static void ReleaseHandBack(Func<string, bool> handBack)
        {
            PendingHandBacks.Remove(handBack);
        }

        private static bool RunHandBack(Func<string, bool> handBack, string reason)
        {
            try
            {
                return handBack(reason);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    $"[Automatics Smoke] {reason}: test objects could not be handed back yet ({e.GetType().Name}: {e.Message}); they are tried again at the next opportunity");
                return false;
            }
        }

        // -- Test objects that outlive a frame -----------------------------------------

        // Recorded before the first frame boundary, so a run cut short still has it removed.
        // dropStack is the stack the check gave an item drop, zero for anything else.
        public static void TrackSpawn(GameObject go, int dropStack = 0)
        {
            if (go && !PendingSpawns.Exists(x => ReferenceEquals(x.Go, go)))
                PendingSpawns.Add(new PendingSpawn { Go = go, DropStack = dropStack });
        }

        public static void ReleaseSpawn(GameObject go)
        {
            PendingSpawns.RemoveAll(x => ReferenceEquals(x.Go, go));
        }

        // Destroys a test object, unless it is an item drop whose stack is no longer the one
        // the check gave it: then real items merged into it, so it stays in the world and the
        // log says where. Returns false only when the removal itself failed, so the caller
        // keeps the object for the next trigger.
        public static bool DestroyTestObject(GameObject go, int dropStack, string label)
        {
            if (!go) return true;

            try
            {
                var drop = dropStack > 0 ? go.GetComponent<ItemDrop>() : null;
                if (drop)
                {
                    drop.Load();
                    if (drop.m_itemData.m_stack != dropStack)
                    {
                        var p = go.transform.position;
                        Log.LogWarning(
                            $"[Automatics Smoke] {label}: the test drop at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}) holds {drop.m_itemData.m_stack} instead of the {dropStack} the check gave it, so real items merged into it; it was left in the world");
                        return true;
                    }
                }

                if (ZNetScene.instance != null && Objects.GetZNetView(go.transform, out var nview) &&
                    nview != null && nview.IsValid())
                    nview.Destroy();
                else
                    Object.Destroy(go);
                return true;
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    $"[Automatics Smoke] {label}: a test object could not be removed yet ({e.GetType().Name}: {e.Message}); it is tried again at the next opportunity");
                return false;
            }
        }

        // -- A module toggle ---------------------------------------------------------------

        // Starts watching the toggle for writes that are not the check's own. Any such write
        // - the settings UI, a config reload - means the user has taken the setting back. The
        // setting is named after its own definition, "section/key": for Automatic Pickup that is
        // automatic_pickup/module, and for Automatic Processing automatic_processing/module.
        public static bool BeginToggle(int runId, ConfigEntry<AutomaticsModule> entry, out string refusal)
        {
            refusal = IsCurrent(runId) && _toggle == null ? null : "the run does not own the setting";
            if (refusal != null) return false;

            _toggle = new ToggleRestoration
            {
                Entry = entry,
                Original = entry.Value,
                Name = $"{entry.Definition.Section}/{entry.Definition.Key}",
                Command = _runner
            };
            entry.SettingChanged += OnToggleChanged;
            return true;
        }

        // Refused when the run no longer owns the setting, when the user has changed it, and
        // when the change cannot first be recorded in the marker: a change that a crash could
        // leave behind without a trace is not made.
        public static bool WriteToggle(int runId, AutomaticsModule value, out string refusal)
        {
            refusal = null;
            if (_toggle == null || !IsCurrent(runId))
                refusal = "the run no longer owns the setting";
            else if (_toggle.EditedByUser)
                refusal = $"{_toggle.Name} was changed outside the check";
            else if (!WriteMarker(_toggle, value, out var failure))
                refusal = $"the record of the change could not be written ({failure}), so {_toggle.Name} was not changed";

            if (refusal != null) return false;

            _toggle.Written = true;
            Assign(_toggle, value);
            return true;
        }

        // The toggling check's own restore. The lock and TryBegin mean a current run's
        // obligation is its own; a run that is no longer current leaves the setting to
        // whichever trigger handed it back.
        public static ToggleOutcome RestoreToggle(int runId, out string detail)
        {
            if (IsCurrent(runId)) return DischargeToggle(out detail);

            detail = "the run was abandoned, so the smoke plugin handled the setting";
            return ToggleOutcome.NotThisRun;
        }

        // Puts the setting back only when nobody else has written it and it still holds the
        // value the check last wrote. The obligation is dropped only once it is discharged: a
        // restore that fails keeps it, and the next trigger tries again.
        private static ToggleOutcome DischargeToggle(out string detail)
        {
            detail = "";
            var toggle = _toggle;
            if (toggle == null) return ToggleOutcome.NothingWritten;

            var outcome = ToggleOutcome.NothingWritten;
            try
            {
                var current = toggle.Entry.Value;
                if (toggle.Written && (toggle.EditedByUser || current != toggle.LastWritten))
                {
                    outcome = ToggleOutcome.LeftForUser;
                    detail =
                        $"{toggle.Name} was changed outside the check and is now {current}; it was left as it is instead of being put back to {toggle.Original}";
                }
                else if (toggle.Written && current == toggle.Original)
                {
                    outcome = ToggleOutcome.AlreadyOriginal;
                    detail = $"{toggle.Name} is already back at {toggle.Original}";
                }
                else if (toggle.Written)
                {
                    Assign(toggle, toggle.Original);
                    outcome = toggle.EditedByUser ? ToggleOutcome.LeftForUser : ToggleOutcome.Restored;
                    detail = toggle.EditedByUser
                        ? $"{toggle.Name} was changed outside the check while it was being put back and is now {toggle.Entry.Value}; it was left as it is"
                        : $"{toggle.Name} was put back from {current} to {toggle.Original}";
                }
            }
            catch (Exception e)
            {
                detail =
                    $"{toggle.Name} could not be put back to {toggle.Original} ({e.GetType().Name}: {e.Message}); the restoration is kept and tried again at the next opportunity";
                return ToggleOutcome.Failed;
            }

            _toggle = null;
            toggle.Entry.SettingChanged -= OnToggleChanged;
            DeleteMarker();
            return outcome;
        }

        // The bracket tells OnToggleChanged that the change is the check's own. BepInEx raises
        // SettingChanged synchronously on this thread, inside the assignment: the setter of
        // ConfigEntry<T>.Value calls ConfigFile.OnSettingChanged, which saves and then invokes
        // the file's handlers in a loop, and the entry's own event is raised from one of them
        // (its constructor subscribes it) - checked against BepInEx 5.4.23.5. So no
        // notification of this write arrives after the bracket closes. The bracket can hide a
        // write another handler makes in response, though, so the value is read back: anything
        // but what was written means someone else changed it, which counts as the user's edit.
        private static void Assign(ToggleRestoration toggle, AutomaticsModule value)
        {
            // Set first: if the assignment throws after the value changed, the next discharge
            // still recognises the value as the check's own.
            toggle.LastWritten = value;

            _writingToggle = true;
            try
            {
                toggle.Entry.Value = value;
            }
            finally
            {
                _writingToggle = false;
            }

            if (toggle.Entry.Value != value) toggle.EditedByUser = true;
        }

        private static void OnToggleChanged(object sender, EventArgs args)
        {
            // Raised from the settings UI and from a config reload: record, never throw.
            var toggle = _toggle;
            if (toggle != null && !_writingToggle) toggle.EditedByUser = true;
        }

        // -- The marker for a run the process did not survive --------------------------

        // From SmokePlugin.Start. The record is reported, never acted on: only the user knows
        // what the setting should be now, and the record cannot know what they did since.
        public static void WarnAboutInterruptedRun()
        {
            string path;
            try
            {
                path = MarkerPath();
                if (!File.Exists(path)) return;
            }
            catch
            {
                return;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var separator = line.IndexOf('=');
                    if (separator > 0)
                        values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
            }
            catch
            {
                // The warning still goes out, with whatever could be read.
            }

            var command = values.TryGetValue("command", out var recorded) && !string.IsNullOrEmpty(recorded)
                ? recorded
                : DefaultMarkerCommand;
            Log.LogWarning(
                $"[Automatics Smoke] an {command} run did not finish (the game stopped while it ran). At {Read(values, "at")} it changed {Read(values, "setting")} from {Read(values, "original")} to {Read(values, "written")}. It was not changed back: check that setting and set it to what you want.");

            // A deletion that fails only means this warning appears again at the next start.
            DeleteMarker();
        }

        private static string MarkerPath()
        {
            return Path.Combine(Path.Combine(Paths.ConfigPath, "AutomaticsSmoke"), MarkerFile);
        }

        // Written in place: a crash in the middle of it leaves a partial record, which only
        // makes the warning less specific, since the record is never acted on.
        private static bool WriteMarker(ToggleRestoration toggle, AutomaticsModule written,
            out string failure)
        {
            failure = null;
            try
            {
                var path = MarkerPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllLines(path, new[]
                {
                    "setting=" + toggle.Name,
                    "original=" + toggle.Original,
                    "written=" + written,
                    "at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
                    "command=" + toggle.Command
                });
            }
            catch (Exception e)
            {
                failure = e.GetType().Name + ": " + e.Message;
            }

            return failure == null;
        }

        private static void DeleteMarker()
        {
            try
            {
                var path = MarkerPath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    $"[Automatics Smoke] could not delete {MarkerFile} ({e.Message}); the next start will warn about a change that has already been handed back");
            }
        }

        private static string Read(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
                ? value
                : "(unknown)";
        }

        private sealed class PendingSpawn
        {
            public GameObject Go;
            public int DropStack;
        }

        private sealed class ToggleRestoration
        {
            public ConfigEntry<AutomaticsModule> Entry;
            public string Name;
            public string Command;
            public AutomaticsModule Original;
            public bool Written;
            public AutomaticsModule LastWritten;
            public bool EditedByUser;
        }
    }
}
