using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Automatics.SmokeTest
{
    // What the smoke console commands share: one run at a time, and what a run owes the
    // user's game - the settings it changed, spawned test objects - until it is handed
    // back. HandBack does that:
    //   - when a command's body completes or throws (Guard; Unity stops a coroutine that
    //     throws without running its iterator's finally, so the body cannot do it itself);
    //   - when the smoke plugin is disabled or destroyed, or the game quits (SmokePlugin's
    //     OnDisable / OnDestroy / OnApplicationQuit);
    //   - at the start of the next run, for anything the triggers above could not hand back.
    // A logout does not end a run: the plugin object survives scene changes. A crash ends the
    // process, so a setting is changed only after a marker file records the change; the next
    // start logs a warning from it and never writes the config from it. A run may hold several
    // settings (a module toggle, or the settings a feature check needs); the marker then holds
    // one record per setting.
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

        // Every setting a run has taken on and not yet handed back. The module toggle the
        // lifecycle and processing commands drive is one of them, and _toggle names it.
        private static readonly List<SettingRestoration> Settings = new List<SettingRestoration>();

        private static string _runner;
        private static int _runId;
        private static SettingRestoration _toggle;
        private static bool _writingSetting;

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
                if (Settings.Count > 0)
                    refusal =
                        $"an earlier run could not put {Settings[0].Name} back (see the warning in the log); set it yourself, then run the command again";
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
            foreach (var result in DischargeAll())
                if (result.Outcome != ToggleOutcome.NothingWritten)
                    Log.LogWarning($"[Automatics Smoke] {reason}: {result.Detail}");

            PendingSpawns.RemoveAll(spawn => DestroyTestObject(spawn.Go, spawn.DropStack, reason));
            // The later obligation first: a later step may sit on what an earlier one made room
            // for - a test item in a slot that items put aside had freed, say - so it is undone
            // before that is put back.
            foreach (var handBack in PendingHandBacks.ToArray().Reverse())
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

        // -- Settings a run changes ---------------------------------------------------------

        // Starts watching the toggle for writes that are not the check's own. Any such write
        // - the settings UI, a config reload - means the user has taken the setting back. The
        // setting is named after its own definition, "section/key": for Automatic Pickup that is
        // automatic_pickup/module, and for Automatic Processing automatic_processing/module.
        public static bool BeginToggle(int runId, ConfigEntry<AutomaticsModule> entry, out string refusal)
        {
            refusal = IsCurrent(runId) && _toggle == null ? null : "the run does not own the setting";
            if (refusal != null) return false;
            if (!BeginSetting(runId, entry, out var setting, out refusal)) return false;

            _toggle = setting;
            return true;
        }

        // Refused when the run no longer owns the setting, when the user has changed it, and
        // when the change cannot first be recorded in the marker: a change that a crash could
        // leave behind without a trace is not made.
        public static bool WriteToggle(int runId, AutomaticsModule value, out string refusal)
        {
            if (_toggle == null || !IsCurrent(runId))
            {
                refusal = "the run no longer owns the setting";
                return false;
            }

            return WriteSetting(runId, _toggle, value, out refusal);
        }

        // The toggling check's own restore. The lock and TryBegin mean a current run's
        // obligation is its own; a run that is no longer current leaves the setting to
        // whichever trigger handed it back.
        public static ToggleOutcome RestoreToggle(int runId, out string detail)
        {
            if (!IsCurrent(runId))
            {
                detail = "the run was abandoned, so the smoke plugin handled the setting";
                return ToggleOutcome.NotThisRun;
            }

            var toggle = _toggle;
            var results = DischargeAll();
            var own = results.FirstOrDefault(x => ReferenceEquals(x.Setting, toggle));
            if (own == null)
            {
                detail = "";
                return ToggleOutcome.NothingWritten;
            }

            detail = own.Detail;
            return own.Outcome;
        }

        // Any setting, the same way as the toggle: the run takes it on before it writes it,
        // writes it only through WriteSetting, and hands it back through RestoreSettings or any
        // of the triggers above.
        public static bool BeginSetting(int runId, ConfigEntryBase entry, out SettingRestoration setting,
            out string refusal)
        {
            setting = null;
            refusal = IsCurrent(runId) && entry != null && Settings.All(x => x.Entry != entry)
                ? null
                : "the run does not own the setting";
            if (refusal != null) return false;

            setting = new SettingRestoration
            {
                Entry = entry,
                Original = entry.BoxedValue,
                Name = $"{entry.Definition.Section}/{entry.Definition.Key}",
                Command = _runner
            };
            var watched = setting;
            setting.Handler = (sender, args) =>
            {
                // Raised from the settings UI and from a config reload: record, never throw.
                if (!_writingSetting && args != null && ReferenceEquals(args.ChangedSetting, watched.Entry))
                    watched.EditedByUser = true;
            };
            entry.ConfigFile.SettingChanged += setting.Handler;
            Settings.Add(setting);
            return true;
        }

        public static bool WriteSetting(int runId, SettingRestoration setting, object value, out string refusal)
        {
            refusal = null;
            if (setting == null || !IsCurrent(runId) || !Settings.Contains(setting))
                refusal = "the run no longer owns the setting";
            else if (setting.EditedByUser)
                refusal = $"{setting.Name} was changed outside the check";
            else if (!WriteMarker(setting, value, out var failure))
                refusal = $"the record of the change could not be written ({failure}), so {setting.Name} was not changed";

            if (refusal != null) return false;

            setting.Written = true;
            Assign(setting, value);
            return true;
        }

        public static bool EditedByUser(SettingRestoration setting)
        {
            return setting != null && setting.EditedByUser;
        }

        // Records a control's expected write before invoking its real callback, and treats
        // the synchronous SettingChanged notification as the smoke run's own change.
        internal static bool InvokeSettingControl(int runId, SettingRestoration setting,
            object expected, Action click, out string refusal)
        {
            refusal = null;
            if (setting == null || !IsCurrent(runId) || !Settings.Contains(setting))
                refusal = "the run no longer owns the setting";
            else if (setting.EditedByUser)
                refusal = $"{setting.Name} was changed outside the check";
            else if (!WriteMarker(setting, expected, out var failure))
                refusal = $"the control's setting write could not be recorded: {failure}";
            if (refusal != null) return false;

            setting.Written = true;
            setting.LastWritten = expected;
            _writingSetting = true;
            try
            {
                click();
            }
            finally
            {
                setting.LastWritten = setting.Entry.BoxedValue;
                _writingSetting = false;
            }
            var actual = setting.Entry.BoxedValue;
            var matches = expected is StringList expectedList && actual is StringList actualList
                ? new HashSet<string>(expectedList).SetEquals(actualList)
                : Equals(expected, actual);
            if (!matches)
                refusal = $"{setting.Name} control wrote {Format(setting, actual)} instead of {Format(setting, expected)}";
            return matches;
        }

        // Every setting the run holds, put back; one entry per setting that had been written. A
        // run that is no longer current leaves them to whichever trigger handed them back.
        public static List<KeyValuePair<ToggleOutcome, string>> RestoreSettings(int runId)
        {
            return RestoreEachSetting(runId)
                .Select(x => new KeyValuePair<ToggleOutcome, string>(x.Outcome, x.Detail))
                .ToList();
        }

        // RestoreSettings with the setting each result belongs to, for a run that judges one of
        // them apart from the rest. The one NotThisRun result names no setting.
        public static List<(ConfigEntryBase Entry, ToggleOutcome Outcome, string Detail)> RestoreEachSetting(
            int runId)
        {
            if (!IsCurrent(runId))
                return new List<(ConfigEntryBase Entry, ToggleOutcome Outcome, string Detail)>
                {
                    (null, ToggleOutcome.NotThisRun, "the run was abandoned, so the smoke plugin handled its settings")
                };

            return DischargeAll()
                .Where(x => x.Outcome != ToggleOutcome.NothingWritten)
                .Select(x => (x.Setting.Entry, x.Outcome, x.Detail))
                .ToList();
        }

        private static List<Discharge> DischargeAll()
        {
            var results = new List<Discharge>();
            if (Settings.Count == 0) return results;

            foreach (var setting in Settings.ToArray())
                results.Add(new Discharge
                {
                    Setting = setting,
                    Outcome = DischargeSetting(setting, out var detail),
                    Detail = detail
                });

            // What could not be put back stays recorded for a crash; the rest is dropped.
            if (Settings.Any(x => x.Written))
                WriteMarker(null, null, out _);
            else
                DeleteMarker();
            return results;
        }

        // Puts the setting back only when nobody else has written it and it still holds the
        // value the check last wrote. The obligation is dropped only once it is discharged: a
        // restore that fails keeps it, and the next trigger tries again.
        private static ToggleOutcome DischargeSetting(SettingRestoration setting, out string detail)
        {
            detail = "";
            var outcome = ToggleOutcome.NothingWritten;
            try
            {
                var current = setting.Entry.BoxedValue;
                if (setting.Written && (setting.EditedByUser || !Equals(current, setting.LastWritten)))
                {
                    outcome = ToggleOutcome.LeftForUser;
                    detail =
                        $"{setting.Name} was changed outside the check and is now {Format(setting, current)}; it was left as it is instead of being put back to {Format(setting, setting.Original)}";
                }
                else if (setting.Written && Equals(current, setting.Original))
                {
                    outcome = ToggleOutcome.AlreadyOriginal;
                    detail = $"{setting.Name} is already back at {Format(setting, setting.Original)}";
                }
                else if (setting.Written)
                {
                    Assign(setting, setting.Original);
                    outcome = setting.EditedByUser ? ToggleOutcome.LeftForUser : ToggleOutcome.Restored;
                    detail = setting.EditedByUser
                        ? $"{setting.Name} was changed outside the check while it was being put back and is now {Format(setting, setting.Entry.BoxedValue)}; it was left as it is"
                        : $"{setting.Name} was put back from {Format(setting, current)} to {Format(setting, setting.Original)}";
                }
            }
            catch (Exception e)
            {
                detail =
                    $"{setting.Name} could not be put back to {Format(setting, setting.Original)} ({e.GetType().Name}: {e.Message}); the restoration is kept and tried again at the next opportunity";
                return ToggleOutcome.Failed;
            }

            Settings.Remove(setting);
            if (ReferenceEquals(_toggle, setting)) _toggle = null;
            setting.Entry.ConfigFile.SettingChanged -= setting.Handler;
            return outcome;
        }

        // The bracket tells the SettingChanged handler that the change is the check's own.
        // BepInEx raises SettingChanged synchronously on this thread, inside the assignment: the
        // setter of ConfigEntry<T>.Value calls ConfigFile.OnSettingChanged, which saves and then
        // invokes the file's handlers in a loop - checked against BepInEx 5.4.23.5. So no
        // notification of this write arrives after the bracket closes. The bracket can hide a
        // write another handler makes in response, though, so the value is read back: anything
        // but what was written means someone else changed it, which counts as the user's edit.
        private static void Assign(SettingRestoration setting, object value)
        {
            // Set first: if the assignment throws after the value changed, the next discharge
            // still recognises the value as the check's own.
            setting.LastWritten = value;

            _writingSetting = true;
            try
            {
                setting.Entry.BoxedValue = value;
            }
            finally
            {
                _writingSetting = false;
            }

            if (!Equals(setting.Entry.BoxedValue, value)) setting.EditedByUser = true;
        }

        // The value the way the config file writes it: an enum by its name, a shortcut as
        // "E + LeftShift".
        private static string Format(SettingRestoration setting, object value)
        {
            try
            {
                return TomlTypeConverter.ConvertToString(value, setting.Entry.SettingType);
            }
            catch
            {
                return value?.ToString() ?? "(null)";
            }
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

            // One record per setting; a record starts at its "setting=" line. A marker written
            // before records were grouped holds a single record.
            var records = new List<Dictionary<string, string>>();
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    var key = line.Substring(0, separator).Trim();
                    if (records.Count == 0 || (key == "setting" && records[records.Count - 1].ContainsKey("setting")))
                        records.Add(new Dictionary<string, string>(StringComparer.Ordinal));
                    records[records.Count - 1][key] = line.Substring(separator + 1).Trim();
                }
            }
            catch
            {
                // The warning still goes out, with whatever could be read.
            }

            if (records.Count == 0) records.Add(new Dictionary<string, string>(StringComparer.Ordinal));
            foreach (var values in records)
            {
                var command = values.TryGetValue("command", out var recorded) && !string.IsNullOrEmpty(recorded)
                    ? recorded
                    : DefaultMarkerCommand;
                Log.LogWarning(
                    $"[Automatics Smoke] an {command} run did not finish (the game stopped while it ran). At {Read(values, "at")} it changed {Read(values, "setting")} from {Read(values, "original")} to {Read(values, "written")}. It was not changed back: check that setting and set it to what you want.");
            }

            // A deletion that fails only means this warning appears again at the next start.
            DeleteMarker();
        }

        private static string MarkerPath()
        {
            return Path.Combine(Path.Combine(Paths.ConfigPath, "AutomaticsSmoke"), MarkerFile);
        }

        // One record for every setting the run has written, and one for the write about to be
        // made (pending, with its value), which replaces that setting's earlier record. Written in
        // place: a crash in the middle of it leaves a partial record, which only makes the
        // warning less specific, since the record is never acted on.
        private static bool WriteMarker(SettingRestoration pending, object written, out string failure)
        {
            failure = null;
            try
            {
                var lines = new List<string>();
                foreach (var setting in Settings)
                {
                    var isPending = ReferenceEquals(setting, pending);
                    if (!isPending && !setting.Written) continue;
                    if (lines.Count > 0) lines.Add("");
                    lines.Add("setting=" + setting.Name);
                    lines.Add("original=" + Format(setting, setting.Original));
                    lines.Add("written=" + Format(setting, isPending ? written : setting.LastWritten));
                    lines.Add("at=" + (isPending || setting.WrittenAt == null
                        ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)
                        : setting.WrittenAt));
                    lines.Add("command=" + setting.Command);
                    if (isPending)
                        setting.WrittenAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
                }

                var path = MarkerPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllLines(path, lines);
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

        private sealed class Discharge
        {
            public SettingRestoration Setting;
            public ToggleOutcome Outcome;
            public string Detail;
        }

        // A setting a run has taken on: what it was, what the run last wrote, and whether the
        // user has written it since.
        internal sealed class SettingRestoration
        {
            public ConfigEntryBase Entry;
            public string Name;
            public string Command;
            public object Original;
            public bool Written;
            public object LastWritten;
            public string WrittenAt;
            public bool EditedByUser;
            public EventHandler<SettingChangedEventArgs> Handler;
        }
    }
}
