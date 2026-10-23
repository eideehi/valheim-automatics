using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using Automatics.AutomaticMapping;
using Automatics.Valheim;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using MappingConfig = Automatics.AutomaticMapping.Config;
using MappingModule = Automatics.AutomaticMapping.Module;

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
    // inventory unless it is provably the check's own. Sections record their temporary setting
    // changes through SmokeSession and put them back when they end. A section whose required
    // world or module is unavailable SKIPs and names the missing precondition.
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
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("navhotkey", NavHotkeyFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("aimbar", AimBarFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("mapfilter", MapFilterFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("pintooltip", PinTooltipFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("mining", MiningFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("planting", PlantingFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("farming", FarmingFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("guide", GuideFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("compass", CompassFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("supply", SupplyFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("woodcutting", WoodcuttingFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("woodcuttingarea", WoodcuttingAreaFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("repair", RepairFeatureSmoke.Run)
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

                // A section that moved the player returns them at its end, but one that threw
                // never got there; the next section starts where the command did either way.
                // Nothing moves when the player is already there.
                if (!run.IsCurrent) continue;
                var back = new SmokeSetup.SetupResult();
                steps = SmokeSetup.ReturnToStart(runId, back);
                while (steps.MoveNext()) yield return steps.Current;
                if (!back.Ok)
                    Warn(
                        $"[Automatics Smoke] {section.Key}: the player was not moved back to the command's starting spot yet ({back.Reason}); the run's hand-back tries again when the command ends");
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

    // Runs synchronously after opening the map so the scans cannot retire the synthetic
    // associations. Only this section's pins are removed, including its saved test pins.
    internal static class MapFilterFeatureSmoke
    {
        internal static IEnumerator Run(FeatureRun run)
        {
            var map = Minimap.instance;
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !map || !player)
            {
                run.Skip("mapfilter_section", "requires a loaded world");
                yield break;
            }
            if (Game.m_noMap)
            {
                run.Skip("mapfilter_section", "the world's no-map modifier is on, so there is no map to filter; run it in a world with the map");
                yield break;
            }

            var originalMode = map.m_mode;
            var originalTarget = Navigation.CaptureTarget();
            var originalSearch = MapFilter.SearchText;
            var originalSearchPinned = MapFilter.SearchPinned;
            var created = new List<Minimap.PinData>();
            var settings = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            var zoomField = AccessTools.Field(typeof(Minimap), "m_largeZoom");
            var originalZoom = zoomField.GetValue(map);
            var sharedFadeField = AccessTools.Field(typeof(Minimap), "m_sharedMapDataFade");
            var originalSharedFade = sharedFadeField.GetValue(map);
            Func<string, bool> handBack = _ =>
            {
                if (!map || Minimap.instance != map) return true;
                if (MapFilter.SearchInput) MapFilter.SetSearchText("");
                foreach (var pin in created.ToArray())
                    if (Map.ContainsPin(pin)) map.RemovePin(pin);
                created.Clear();
                Navigation.RestoreTarget(originalTarget);
                if (MappingModule.Running && MappingConfig.EnableMapEnhancements)
                {
                    map.SetMapMode(Minimap.MapMode.Large);
                    MapFilter.Update(map);
                    MapFilter.SetSearchText(originalSearch);
                    if (MapFilter.SearchPinned != originalSearchPinned) MapFilter.SearchToggle.InvokeClick();
                }
                zoomField.SetValue(map, originalZoom);
                sharedFadeField.SetValue(map, originalSharedFade);
                map.SetMapMode(originalMode);
                Map.RefreshPins();
                return true;
            };
            SmokeSession.TrackHandBack(handBack);
            try
            {
                var entries = new ConfigEntryBase[]
                {
                    MappingConfig.EnableMapEnhancementsEntry, MappingConfig.MapFilterHiddenCategoriesEntry,
                    MappingConfig.HideUnexploredAutomaticMappingPinsEntry, MappingConfig.ModuleEntry
                };
                foreach (var entry in entries)
                {
                    if (!SmokeSession.BeginSetting(run.RunId, entry, out var setting, out var refusal))
                    {
                        run.Fail("mapfilter_settings", "setting ownership precondition failed: " + refusal);
                        yield break;
                    }
                    settings.Add(entry, setting);
                }

                // Automatic Mapping on for the run when it is off, through the module toggle the
                // section already holds; the settings restore turns it off again.
                var module = new SmokeSetup.SetupResult();
                var enable = SmokeSetup.EnableModule(run.RunId, settings, MappingConfig.ModuleEntry,
                    () => MappingModule.Running, module);
                while (enable.MoveNext()) yield return enable.Current;
                if (!module.Ok)
                {
                    run.Skip("mapfilter_section", "Automatic Mapping is not running: " + module.Reason);
                    yield break;
                }
                if (module.Changed) run.Pass("mapfilter_setup", "Automatic Mapping " + module.Reason);

                Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, true);
                Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
                Write(run, settings, MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false);
                map.SetMapMode(Minimap.MapMode.Large);
                if (MapFilter.SearchFocused) MapFilter.DefocusSearch();
                AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases")
                    .Invoke(null, null);
                yield return null;
                if (!run.IsCurrent) yield break;
                zoomField.SetValue(map, (float)AccessTools.Field(typeof(Minimap), "m_showNamesZoom").GetValue(map) / 2f);
                sharedFadeField.SetValue(map, 1f);

                var origin = player.transform.position;
                if (!ValheimObject.Flora.GetName("Mushroom", out var floraLabel))
                {
                    run.Fail("mapfilter_registry", "registry precondition failed: Mushroom has no Flora label");
                    yield break;
                }
                CheckAllCategories(run, map, origin);
                var registries = new[]
                {
                    ValheimObject.Animal, ValheimObject.Monster, ValheimObject.Flora,
                    ValheimObject.Mineral, ValheimObject.Spawner, ValheimObject.Dungeon,
                    ValheimObject.Spot, MappingObject.Vehicle, MappingObject.Other
                };
                var ambiguousLabel = registries.SelectMany((registry, index) =>
                        registry.GetAllElements().Select(element => new
                        { Registry = index, Name = Map.StoredPinName(element.label) }))
                    .GroupBy(element => element.Name)
                    .FirstOrDefault(group => group.Select(element => element.Registry).Distinct().Count() > 1)?.Key;
                if (ambiguousLabel == null)
                {
                    run.Fail("mapfilter_ambiguous_saved_precondition", "no live registry label belongs to two categories");
                    yield break;
                }
                Minimap.PinData Automatic(string name, int offset, bool save)
                {
                    var pin = Map.AddPin(origin + Vector3.right * offset, name, save, new Target { name = name });
                    created.Add(pin);
                    return pin;
                }
                var flora = Automatic("Automatics smoke flora", 1, false);
                var mineral = Automatic("Automatics smoke mineral", 2, false);
                Associate(flora, PinKind.Flora, 1);
                Associate(mineral, PinKind.Mineral, 2);
                var manual = map.AddPin(origin + Vector3.right * 3, Minimap.PinType.Icon3,
                    Map.StoredPinName(floraLabel), false, false);
                created.Add(manual);
                var saved = Automatic(floraLabel, 4, true);
                var merged = Automatic(StaticObjectMapping.ComputeFloraPinName(floraLabel, 3), 5, true);
                var unknown = Automatic("Automatics smoke unmatched " + run.RunId, 6, true);
                var ambiguous = Automatic(ambiguousLabel, 8, true);
                var shared = map.AddPin(origin + Vector3.right * 7, Minimap.PinType.Icon3,
                    "Automatics smoke shared", false, false, long.MinValue);
                created.Add(shared);
                var visibleTypes = (bool[])AccessTools.Field(typeof(Minimap), "m_visibleIconTypes").GetValue(map);
                foreach (var pin in created)
                    if (!visibleTypes[(int)pin.m_type] || pin.m_NamePinData == null)
                    {
                        run.Fail("mapfilter_pin_display_precondition",
                            $"{pin.m_name}: vanilla icon filter {(visibleTypes[(int)pin.m_type] ? "on" : "off")}, name data {(pin.m_NamePinData == null ? "missing (icon pack may hide it)" : "present")}");
                        yield break;
                    }
                Map.RefreshPins();
                MapFilter.SetSearchText("");
                if (MapFilter.SearchPinned) MapFilter.SearchToggle.InvokeClick();
                Check(run, "mapfilter_controls", MapFilter.Root && MapFilter.Controls.Count == 10 &&
                    MapFilter.Root.transform.parent == map.m_largeRoot.transform &&
                    MapFilter.Controls.All(x => x.Category.HasValue) && MapFilter.SearchInput && MapFilter.SearchToggle != null,
                    "control creation precondition failed: expected ten categories, search field and Apply to minimap under m_largeRoot");
                CheckLayout(run, map);
                Check(run, "mapfilter_mouse_only_controls", MapFilter.Controls.Concat(new[] { MapFilter.SearchToggle }).All(control =>
                    control.Root.GetComponentsInChildren<UIGamePad>(true).All(gamepad => !gamepad.enabled)) &&
                    MapFilter.SearchInput.GetComponentsInChildren<UIGamePad>(true).All(gamepad => !gamepad.enabled),
                    "a cloned control retained an enabled UIGamePad");
                CheckModes(run, map, origin, created, "baseline", _ => true);
                Check(run, "mapfilter_saved_classification",
                    PinCategories.TryClassify(saved, out var savedCategory) && savedCategory == PinCategory.Flora &&
                    PinCategories.TryClassify(merged, out var mergedCategory) && mergedCategory == PinCategory.Flora &&
                    !PinCategories.TryClassify(unknown, out _),
                    "classification precondition failed: registry and merged Flora names must classify; unmatched must not");
                Check(run, "mapfilter_ambiguous_saved_unclassified", !PinCategories.TryClassify(ambiguous, out _),
                    "a saved name belonging to two registries was classified");

                foreach (var control in MapFilter.Controls.ToArray())
                {
                    var hidden = new StringList(MappingConfig.MapFilterHiddenCategories);
                    hidden.Add(control.Category.Value.ToString());
                    Click(run, settings[MappingConfig.MapFilterHiddenCategoriesEntry], control, hidden);
                    Check(run, "mapfilter_click_" + control.Category.Value,
                        MappingConfig.MapFilterHiddenCategories.Contains(control.Category.Value.ToString()) &&
                        !control.Shown && control.Tint == Color.gray,
                        "control callback did not persist its off state in config");
                }
                CheckModes(run, map, origin, created, "all_off",
                    pin => pin == manual || pin == shared || pin == unknown || pin == ambiguous);
                Check(run, "mapfilter_pins_retained", created.All(Map.ContainsPin),
                    "display-only precondition failed: a test pin was removed");
                CheckConfigWritten(run, entries.Take(2));

                var floraControl = MapFilter.Controls.Single(x => x.Category == PinCategory.Flora);
                var shownFlora = new StringList(MappingConfig.MapFilterHiddenCategories);
                shownFlora.Remove("Flora");
                Click(run, settings[MappingConfig.MapFilterHiddenCategoriesEntry], floraControl, shownFlora);
                CheckModes(run, map, origin, created, "flora_on", pin => pin != mineral);
                foreach (var control in MapFilter.Controls.Where(x => x.Category.HasValue && !x.Shown).ToArray())
                {
                    var hidden = new StringList(MappingConfig.MapFilterHiddenCategories);
                    hidden.Remove(control.Category.Value.ToString());
                    Click(run, settings[MappingConfig.MapFilterHiddenCategoriesEntry], control, hidden);
                    Check(run, "mapfilter_click_on_" + control.Category.Value,
                        !MappingConfig.MapFilterHiddenCategories.Contains(control.Category.Value.ToString()) &&
                        control.Tint == Color.white, "control callback did not restore its on state and white tint");
                }
                CheckModes(run, map, origin, created, "categories_on", _ => true);
                Click(run, settings[MappingConfig.MapFilterHiddenCategoriesEntry],
                    MapFilter.Controls.Single(x => x.Category == PinCategory.Mineral),
                    new StringList(new[] { "Mineral" }));
                CheckModes(run, map, origin, created, "mineral_off", pin => pin != mineral);

                MapFilter.SetSearchText("not a smoke pin label");
                MapFilter.SearchToggle.InvokeClick();
                var hiddenBefore = new HashSet<string>(MappingConfig.MapFilterHiddenCategories);
                var oldRoot = MapFilter.Root;
                Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, false);
                Check(run, "mapfilter_enhancements_off_controls", !MapFilter.Root && MapFilter.Controls.Count == 0 &&
                    oldRoot && !oldRoot.activeSelf, "enhancements off left active controls");
                Check(run, "mapfilter_enhancements_off_search_reset", MapFilter.SearchText == "" && !MapFilter.SearchPinned,
                    "enhancements off did not clear search text and the session toggle");
                CheckModes(run, map, origin, created, "enhancements_off", _ => true);
                Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, true);
                Check(run, "mapfilter_enhancements_on_controls", MapFilter.Root && MapFilter.Root != oldRoot &&
                    MapFilter.Controls.Count == 10 && MapFilter.SearchInput && MapFilter.SearchToggle != null &&
                    hiddenBefore.SetEquals(MappingConfig.MapFilterHiddenCategories) && MapFilter.SearchText == "" &&
                    !MapFilter.SearchPinned, "enhancements on did not recreate controls with saved categories and empty/off search");
                CheckModes(run, map, origin, created, "enhancements_on", pin => pin != mineral);

                Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry,
                    new StringList(new[] { "unknown smoke category" }));
                Check(run, "mapfilter_unknown_category_ignored", !MappingConfig.HasHiddenMapCategories,
                    "unknown category precondition failed: an unknown name activated filtering");
                CheckModes(run, map, origin, created, "unknown_category", _ => true);

                CheckSearch(run, map, origin, created, flora, mineral, manual, shared, saved, settings);
                CheckFocusedInput(run, map, player, settings);
                CheckUnexplored(run, map, origin, flora, mineral, manual, settings);
                Check(run, "mapfilter_final_pins_retained", created.All(Map.ContainsPin),
                    "a filter transition deleted a test pin");

                foreach (var pin in created.ToArray())
                    if (Map.ContainsPin(pin)) map.RemovePin(pin);
                created.Clear();
                Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, true);
                map.SetMapMode(Minimap.MapMode.Large);
                MapFilter.SetSearchText("session reset probe");
                MapFilter.SearchToggle.InvokeClick();
                Write(run, settings, MappingConfig.ModuleEntry, AutomaticsModule.Disabled);
                var deadline = Time.realtimeSinceStartup + 10f;
                while (run.IsCurrent && MappingModule.Active && Time.realtimeSinceStartup < deadline) yield return null;
                Check(run, "mapfilter_module_off", !MappingModule.Active && !MapFilter.Root &&
                    MapFilter.SearchText == "" && !MapFilter.SearchPinned,
                    "module disable precondition/cleanup failed: module still applied, controls remain or search was not cleared");
                Write(run, settings, MappingConfig.ModuleEntry, AutomaticsModule.Enabled);
                deadline = Time.realtimeSinceStartup + 10f;
                while (run.IsCurrent && !MappingModule.Running && Time.realtimeSinceStartup < deadline) yield return null;
                MapFilter.Update(map);
                Check(run, "mapfilter_module_on_search_empty", MappingModule.Running && MapFilter.SearchInput &&
                    MapFilter.SearchText == "" && !MapFilter.SearchPinned &&
                    (string)AccessTools.Property(MapFilter.SearchInput.GetType(), "text").GetValue(MapFilter.SearchInput) == "",
                    "module enable precondition/reset failed: no input, non-empty text or Apply to minimap on");
                CheckLayout(run, map);
            }
            finally
            {
                handBack("map filter section ended");
                SmokeSession.ReleaseHandBack(handBack);
                var restored = SmokeSession.RestoreSettings(run.RunId);
                Check(run, "mapfilter_settings_restored", restored.All(x =>
                        x.Key == SmokeSession.ToggleOutcome.Restored ||
                        x.Key == SmokeSession.ToggleOutcome.AlreadyOriginal ||
                        x.Key == SmokeSession.ToggleOutcome.NothingWritten),
                    "settings restoration failed: " + string.Join("; ", restored.Select(x => x.Value)));
                Check(run, "mapfilter_mode_restored", !map || map.m_mode == originalMode,
                    "map mode restoration failed");
            }
        }

        private static readonly Func<Minimap.PinNameData, Behaviour> NameTextGetter =
            (Func<Minimap.PinNameData, Behaviour>)Delegate.CreateDelegate(typeof(Func<Minimap.PinNameData, Behaviour>),
                AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").GetGetMethod());
        private static int _pinActions;
        private static int _navigationActions;
        private static Component _compositionInput;
        private static string _compositionPreview;

        private static Behaviour GetNameText(Minimap.PinData pin)
        {
            return pin.m_NamePinData == null ? null : NameTextGetter(pin.m_NamePinData);
        }

        private static void CheckSearch(FeatureRun run, Minimap map, Vector3 origin, List<Minimap.PinData> pins,
            Minimap.PinData flora, Minimap.PinData mineral, Minimap.PinData manual, Minimap.PinData shared,
            Minimap.PinData saved, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings)
        {
            Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
            Navigation.ClearTarget();
            var labels = pins.ToDictionary(pin => pin, pin =>
            {
                var text = GetNameText(pin);
                if (!text) throw new InvalidOperationException("search label precondition failed: missing text for " + pin.m_name);
                return ((string)AccessTools.Property(text.GetType(), "text").GetValue(text)).ToLowerInvariant();
            });
            var special = map.AddPin(origin + Vector3.forward * 3f, Minimap.PinType.Death,
                "Automatics smoke special", false, false);
            var unnamed = map.AddPin(origin + Vector3.forward * 5f, Minimap.PinType.Icon3, "", false, false);
            pins.Add(special);
            pins.Add(unnamed);
            labels.Add(special, special.m_name.ToLowerInvariant());
            labels.Add(unnamed, "");
            var textType = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
            var toggleLabel = (string)AccessTools.Property(textType, "text").GetValue(
                MapFilter.SearchToggle.Root.GetComponentInChildren(textType, true));
            Check(run, "mapfilter_search_toggle_text", toggleLabel == "Apply to minimap" || toggleLabel == "ミニマップにも反映",
                "minimap toggle does not show the requested translated label: " + toggleLabel);
            MapFilter.SetSearchText("  SMOKE FLORA  ");
            var specialTypes = Enum.GetValues(typeof(Minimap.PinType)).Cast<Minimap.PinType>().Where(type =>
                type == Minimap.PinType.Death || type == Minimap.PinType.Bed || type == Minimap.PinType.Shout ||
                type == Minimap.PinType.Player || type == Minimap.PinType.RandomEvent || type == Minimap.PinType.Ping ||
                (int)type >= (int)Minimap.PinType.EventArea);
            foreach (var type in specialTypes)
                Check(run, "mapfilter_search_special_" + (int)type,
                    !Map.ShouldHideAutomaticPin(new Minimap.PinData { m_type = type, m_name = "" }),
                    "search hid a vanilla special marker");
            Check(run, "mapfilter_search_custom_icon", Map.ShouldHideAutomaticPin(new Minimap.PinData
                { m_type = (Minimap.PinType)Enum.GetValues(typeof(Minimap.PinType)).Length, m_name = "unmatched custom icon" }),
                "the special-marker exemption incorrectly includes custom icon types");
            Check(run, "mapfilter_search_value_changed", MapFilter.SearchText == "  SMOKE FLORA  " && MapFilter.HasSearch,
                "the cloned input's value-changed callback did not record the search");
            CheckModes(run, map, origin, pins, "search_large", pin => map.m_mode == Minimap.MapMode.Small ||
                pin == flora || pin == special);
            MapFilter.SearchToggle.InvokeClick();
            Check(run, "mapfilter_search_toggle_callback", MapFilter.SearchPinned && MapFilter.SearchToggle.Shown &&
                MapFilter.SearchToggle.Tint == Color.white, "Apply to minimap's callback did not turn on its session value and tint");
            CheckModes(run, map, origin, pins, "search_pinned", pin => pin == flora || pin == special);

            var originalName = flora.m_name;
            flora.m_name = "Automatics smoke renamed";
            CheckModes(run, map, origin, pins, "search_renamed_pin", pin => pin == special);
            flora.m_name = originalName;
            CheckModes(run, map, origin, pins, "search_name_restored", pin => pin == flora || pin == special);

            AccessTools.Method(typeof(Navigation), "SetTarget").Invoke(null, new object[] { mineral });
            CheckModes(run, map, origin, pins, "search_navigation_kept",
                pin => pin == flora || pin == mineral || pin == special);
            for (var i = 0; i < 3; i++)
            {
                AccessTools.Field(typeof(Navigation), "_nextTargetPinValidationTime").SetValue(null, 0f);
                AccessTools.Method(typeof(Navigation), "ValidateTargetPin").Invoke(null, null);
            }
            Check(run, "mapfilter_search_navigation_survives", Navigation.TargetPin == mineral,
                "search cleared the non-matching navigation target during validation");
            Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList(new[] { "Flora" }));
            CheckModes(run, map, origin, pins, "search_category_combined",
                pin => pin == mineral || pin == special);
            Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
            Navigation.ClearTarget();
            MapFilter.SetSearchText("  " + labels[saved].ToUpperInvariant() + "  ");
            Check(run, "mapfilter_search_localized_precondition", labels[saved].Length != 0,
                "the saved registry pin has no displayed localized label");
            CheckModes(run, map, origin, pins, "search_localized_case_insensitive",
                pin => pin == special || labels[pin].Contains(labels[saved]));
            Check(run, "mapfilter_search_manual_match", !Map.ShouldHideAutomaticPin(manual) &&
                Map.ShouldHideAutomaticPin(shared), "matching manual pin or non-matching shared pin was not filtered by label");
            MapFilter.SetSearchText("SMOKE SHARED");
            CheckModes(run, map, origin, pins, "search_shared_match", pin => pin == shared || pin == special);
            MapFilter.SetSearchText("nothing matches this smoke query");
            CheckModes(run, map, origin, pins, "search_nonmatching", pin => pin == special);
            Check(run, "mapfilter_search_unnamed", Map.ShouldHideAutomaticPin(unnamed),
                "active search left an unnamed ordinary pin visible");
            MapFilter.SetSearchText("   ");
            CheckModes(run, map, origin, pins, "search_whitespace_restores", _ => true);
            MapFilter.SetSearchText("");
            MapFilter.SearchToggle.InvokeClick();
            Check(run, "mapfilter_search_toggle_off", !MapFilter.SearchPinned && MapFilter.SearchToggle.Tint == Color.gray,
                "Apply to minimap's callback did not restore its off value and tint");
            CheckModes(run, map, origin, pins, "search_clear_restores", _ => true);
            map.RemovePin(special);
            map.RemovePin(unnamed);
            pins.Remove(special);
            pins.Remove(unnamed);
        }

        private static void CheckLayout(FeatureRun run, Minimap map)
        {
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases").Invoke(null, null);
            var rects = MapFilter.Controls.Select(control => (RectTransform)control.Root.transform)
                .Concat(new[] { (RectTransform)MapFilter.SearchInput.transform, (RectTransform)MapFilter.SearchToggle.Root.transform })
                .ToArray();
            var textType = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
            foreach (var control in MapFilter.Controls.Concat(new[] { MapFilter.SearchToggle }))
            {
                var label = control.Root.GetComponentInChildren(textType, true);
                var labelRect = (RectTransform)label.transform;
                Check(run, "mapfilter_label_fits_" + control.Root.name,
                    (float)AccessTools.Property(textType, "preferredWidth").GetValue(label) <= labelRect.rect.width,
                    "translated label exceeds its control's text rect: " + control.Root.name);
            }
            var placeholder = (Component)AccessTools.Property(MapFilter.SearchInput.GetType(), "placeholder")
                .GetValue(MapFilter.SearchInput);
            Check(run, "mapfilter_placeholder_fits", (float)AccessTools.Property(textType, "preferredWidth")
                .GetValue(placeholder) <= ((RectTransform)placeholder.transform).rect.width,
                "translated search placeholder exceeds the input's text rect");
            var icons = typeof(Minimap).GetFields().Where(field => field.Name.StartsWith("m_selectedIcon"))
                .Select(field => field.GetValue(map) as Component).Where(icon => icon)
                .Select(icon => MapFilter.ScreenRect((RectTransform)icon.transform.parent)).ToArray();
            var column = Rect.MinMaxRect(icons.Min(rect => rect.xMin), icons.Min(rect => rect.yMin),
                icons.Max(rect => rect.xMax), icons.Max(rect => rect.yMax));
            foreach (var rect in rects)
            {
                var screen = MapFilter.ScreenRect(rect);
                Check(run, "mapfilter_layout_" + rect.name + "_filter_column", !screen.Overlaps(column),
                    "screen rect overlaps the vanilla filter column: " + rect.name);
                Check(run, "mapfilter_layout_size_" + rect.name, screen.width > 0 && screen.height > 0,
                    "layout precondition failed: empty screen rect for " + rect.name);
                foreach (var other in rects.Where(other => other != rect))
                    Check(run, "mapfilter_layout_" + rect.name + "_" + other.name,
                        !screen.Overlaps(MapFilter.ScreenRect(other)), "screen rects overlap: " + rect.name + " and " + other.name);
                foreach (var field in typeof(Minimap).GetFields())
                    if (field.Name.StartsWith("m_selectedIcon") && field.GetValue(map) is Component icon && icon)
                        Check(run, "mapfilter_layout_" + rect.name + "_" + field.Name,
                            !screen.Overlaps(MapFilter.ScreenRect((RectTransform)icon.transform.parent)),
                            "screen rect overlaps the vanilla filter column: " + rect.name + " and " + field.Name);
                Check(run, "mapfilter_layout_" + rect.name + "_public_position",
                    !screen.Overlaps(MapFilter.ScreenRect((RectTransform)((Component)AccessTools.Field(typeof(Minimap),
                        "m_publicPosition").GetValue(map)).transform)), "screen rect overlaps public position: " + rect.name);
            }
        }

        private static void CheckFocusedInput(FeatureRun run, Minimap map, Player player,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings)
        {
            if (!GameCamera.instance)
                throw new InvalidOperationException("focused-hotkey precondition failed: no game camera");
            foreach (var field in new[] { "_pinObjectKey", "_navigateToAimedObjectKey", "_staticObjectMappingKey", "_staticObjectMappingInterval" })
            {
                var entry = (ConfigEntryBase)AccessTools.Field(typeof(MappingConfig), field).GetValue(null);
                if (!SmokeSession.BeginSetting(run.RunId, entry, out var setting, out var refusal))
                    throw new InvalidOperationException("focused-hotkey setting precondition failed: " + refusal);
                settings[entry] = setting;
                Write(run, settings, entry, field.EndsWith("Interval") ? (object)0f : new KeyboardShortcut(KeyCode.F8));
            }
            var input = MapFilter.SearchInput;
            var inputType = input.GetType();
            var focusedField = AccessTools.Field(typeof(Minimap), "m_wasFocused");
            var originalFocused = focusedField.GetValue(map);
            var timerField = AccessTools.Field(typeof(StaticObjectMapping), "_mappingTimer");
            var originalTimer = timerField.GetValue(null);
            var patcher = new Harmony("automatics.smoke.mapfilter.input." + run.RunId);
            try
            {
                focusedField.SetValue(map, false);
                patcher.Patch(AccessTools.Method(typeof(HotkeyInput), nameof(HotkeyInput.IsDown)),
                    prefix: new HarmonyMethod(typeof(MapFilterFeatureSmoke), nameof(ForceKeyDown)));
                patcher.Patch(AccessTools.Method(typeof(ObjectPinning), "TryPinFromRay"),
                    prefix: new HarmonyMethod(typeof(MapFilterFeatureSmoke), nameof(CountPinAction)));
                patcher.Patch(AccessTools.Method(typeof(Navigation), "TryNavigateFromRay"),
                    prefix: new HarmonyMethod(typeof(MapFilterFeatureSmoke), nameof(CountNavigationAction)));
                _pinActions = _navigationActions = 0;
                ObjectPinning.OnPlayerUpdate(player, true);
                Navigation.OnPlayerUpdate(player, true);
                var mapping = AccessTools.Method(typeof(StaticObjectMapping), "CanMapping");
                Check(run, "mapfilter_hotkey_probe_precondition", _pinActions == 1 && _navigationActions == 1 &&
                    (bool)mapping.Invoke(null, new object[] { 0f, true }),
                    "hotkey probe precondition failed: forced keys did not reach all three unfocused entry points");

                AccessTools.Method(inputType, "Select").Invoke(input, null);
                AccessTools.Method(inputType, "ActivateInputFieldInternal").Invoke(input, null);
                Check(run, "mapfilter_search_focus", MapFilter.SearchFocused && Minimap.InTextInput(),
                    "search focus precondition failed: GuiInputField focused but InTextInput did not report true");
                _pinActions = _navigationActions = 0;
                ObjectPinning.OnPlayerUpdate(player, true);
                Navigation.OnPlayerUpdate(player, true);
                Check(run, "mapfilter_focused_hotkeys_blocked", _pinActions == 0 && _navigationActions == 0 &&
                    !(bool)mapping.Invoke(null, new object[] { 0f, true }),
                    "focused search allowed pin, navigation or static-mapping key with takeInput=true");
                CheckImeInput(run, map, input);
                ((UnityEngine.Events.UnityEvent<string>)AccessTools.Property(inputType, "onSubmit").GetValue(input))
                    .Invoke(MapFilter.SearchText);
                Check(run, "mapfilter_search_submit_defocus", !MapFilter.SearchFocused && map.m_mode == Minimap.MapMode.Large,
                    "search submit failed to defocus while leaving the map open");
                AccessTools.Method(inputType, "Select").Invoke(input, null);
                AccessTools.Method(inputType, "ActivateInputFieldInternal").Invoke(input, null);
                MapFilter.SetSearchText("escape keeps query");
                var inputEvent = Activator.CreateInstance(AccessTools.TypeByName("UnityEngine.Event"));
                var eventType = AccessTools.Property(inputEvent.GetType(), "type");
                eventType.SetValue(inputEvent, Enum.Parse(eventType.PropertyType, "KeyDown"));
                AccessTools.Property(inputEvent.GetType(), "keyCode").SetValue(inputEvent, KeyCode.Escape);
                AccessTools.Method(inputType, "KeyPressed").Invoke(input, new[] { inputEvent });
                AccessTools.Method(inputType, "DeactivateInputField").Invoke(input, new object[] { false });
                Check(run, "mapfilter_search_escape_defocus", !MapFilter.SearchFocused &&
                    map.m_mode == Minimap.MapMode.Large && MapFilter.SearchText == "escape keeps query" && Minimap.InTextInput(),
                    "search Escape failed to defocus, preserve the query and gate input for the rest of the frame");
            }
            finally
            {
                patcher.UnpatchSelf();
                if (MapFilter.SearchFocused) MapFilter.DefocusSearch();
                MapFilter.SetSearchText("");
                focusedField.SetValue(map, originalFocused);
                timerField.SetValue(null, originalTimer);
            }
        }

        private static void CheckImeInput(FeatureRun run, Minimap map, Component input)
        {
            var type = input.GetType();
            var submit = (UnityEngine.Events.UnityEvent<string>)AccessTools.Property(type, "onSubmit").GetValue(input);
            var placeholder = (Behaviour)AccessTools.Property(type, "placeholder").GetValue(input);
            var compositionActive = AccessTools.Field(type, "m_IsCompositionActive");
            var updateLabel = AccessTools.Method(type, "ForceLabelUpdate");
            var patcher = new Harmony("automatics.smoke.mapfilter.ime." + run.RunId);
            try
            {
                // Drive TMP's composition getter without changing the operating system's IME state.
                _compositionInput = input;
                _compositionPreview = "どうぶつ";
                patcher.Patch(AccessTools.Property(type, "compositionString").GetGetMethod(true),
                    prefix: new HarmonyMethod(typeof(MapFilterFeatureSmoke), nameof(CompositionPreview)));
                patcher.Patch(AccessTools.Method(typeof(ZInput), "GetKeyDown", new[] { typeof(KeyCode), typeof(bool) }),
                    prefix: new HarmonyMethod(typeof(MapFilterFeatureSmoke), nameof(CompositionEnter)));
                MapFilter.SetSearchText("");
                updateLabel.Invoke(input, null);
                Check(run, "mapfilter_ime_composition_precondition", (bool)compositionActive.GetValue(input) &&
                    (int)AccessTools.Property(type, "compositionLength").GetValue(input) == _compositionPreview.Length,
                    "IME probe precondition failed: TMP did not enter composition through its input getter");
                MapFilter.UpdateInput();
                submit.Invoke(MapFilter.SearchText);
                Check(run, "mapfilter_ime_enter_keeps_focus", MapFilter.SearchFocused &&
                    MapFilter.SearchText == "" && !MapFilter.HasSearch && !placeholder.enabled,
                    "composition Enter defocused search, hid the preview behind the placeholder or searched uncommitted text");

                _compositionPreview = "";
                submit.Invoke(MapFilter.SearchText);
                Check(run, "mapfilter_ime_commit_transition_keeps_focus", MapFilter.SearchFocused &&
                    (bool)compositionActive.GetValue(input),
                    "Enter defocused before TMP refreshed its cached composition state");
                MapFilter.SetSearchText("動物");
                MapFilter.UpdateInput();
                Check(run, "mapfilter_ime_committed_text", MapFilter.SearchFocused &&
                    MapFilter.SearchText == "動物" && MapFilter.HasSearch && !placeholder.enabled,
                    "committed text was not searched, the confirmation Enter defocused, or the placeholder remained visible");
                placeholder.enabled = true;
                submit.Invoke(MapFilter.SearchText);
                Check(run, "mapfilter_ime_defocus_resync", !MapFilter.SearchFocused &&
                    map.m_mode == Minimap.MapMode.Large && MapFilter.SearchText == "動物" && !placeholder.enabled,
                    "normal submit did not defocus, preserve committed search or correct the placeholder");
                MapFilter.SetSearchText("");
                Check(run, "mapfilter_ime_clear_placeholder", MapFilter.SearchText == "" &&
                    !MapFilter.HasSearch && placeholder.enabled, "empty committed text did not restore the placeholder");
                AccessTools.Method(type, "Select").Invoke(input, null);
                AccessTools.Method(type, "ActivateInputFieldInternal").Invoke(input, null);
            }
            finally
            {
                patcher.UnpatchSelf();
                _compositionInput = null;
                _compositionPreview = null;
                MapFilter.SetSearchText("");
            }
        }

        private static bool CompositionPreview(object __instance, ref string __result)
        {
            if (!ReferenceEquals(__instance, _compositionInput)) return true;
            __result = _compositionPreview;
            return false;
        }

        private static bool CompositionEnter(KeyCode __0, ref bool __result)
        {
            __result = __0 == KeyCode.Return;
            return false;
        }

        private static bool ForceKeyDown(ref bool __result) { __result = true; return false; }
        private static bool CountPinAction() { _pinActions++; return false; }
        private static bool CountNavigationAction() { _navigationActions++; return false; }

        private static void CheckAllCategories(FeatureRun run, Minimap map, Vector3 origin)
        {
            foreach (var category in new[]
            {
                PinCategory.Animal, PinCategory.Monster, PinCategory.Flora, PinCategory.Mineral,
                PinCategory.Spawner, PinCategory.Dungeon, PinCategory.Spot, PinCategory.Portal,
                PinCategory.Vehicle, PinCategory.Other
            })
            {
                var name = "Automatics smoke category " + category;
                var pin = Map.AddPin(origin, name, false, new Target { name = name });
                var pinId = 100u + (uint)category;
                var id = new ZDOID(long.MinValue, pinId);
                try
                {
                    if (category == PinCategory.Vehicle)
                        AccessTools.Method(typeof(DynamicObjectMapping), "CacheVehiclePin")
                            .Invoke(null, new object[] { id, pin });
                    else if (category == PinCategory.Animal || category == PinCategory.Monster)
                    {
                        ((IDictionary)AccessTools.Field(typeof(DynamicObjectMapping), "PinDataCache").GetValue(null)).Add(id, pin);
                        ((IDictionary)AccessTools.Field(typeof(DynamicObjectMapping), "PinKeyCache").GetValue(null)).Add(pin, id);
                        ((IDictionary)AccessTools.Field(typeof(DynamicObjectMapping), "PinCategoryCache").GetValue(null)).Add(id, category);
                    }
                    else
                        Associate(pin, (PinKind)Enum.Parse(typeof(PinKind), category.ToString()), pinId);
                    Check(run, "mapfilter_classify_" + category,
                        PinCategories.TryClassify(pin, out var actual) && actual == category,
                        "live association precondition/classification failed for " + category);
                }
                finally { map.RemovePin(pin); }
            }
        }

        private static void Associate(Minimap.PinData pin, PinKind kind, uint id)
        {
            var method = AccessTools.Method(typeof(StaticObjectMapping), "CachePin", new[]
            {
                typeof(StaticObjectMapping.MapPinIdentify), typeof(Minimap.PinData), typeof(PinKind),
                typeof(string), typeof(string), typeof(PinSourceDomain)
            });
            method.Invoke(null, new object[]
            {
                new StaticObjectMapping.MapPinIdentify(new ZDOID(long.MinValue, id)), pin, kind,
                "smoke", "smoke", PinSourceDomain.Component
            });
        }

        private static void CheckModes(FeatureRun run, Minimap map, Vector3 center,
            List<Minimap.PinData> pins, string phase, Func<Minimap.PinData, bool> markerExpected)
        {
            foreach (var mode in new[] { Minimap.MapMode.Large, Minimap.MapMode.Small })
            {
                map.SetMapMode(mode);
                AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { center });
                Map.RefreshPins();
                var pinRoot = (Transform)AccessTools.Field(typeof(Minimap), mode == Minimap.MapMode.Large
                    ? "m_pinRootLarge" : "m_pinRootSmall").GetValue(map);
                foreach (var pin in pins)
                {
                    var marker = pin.m_uiElement && pin.m_uiElement.gameObject.activeInHierarchy &&
                        pin.m_uiElement.parent == pinRoot;
                    Check(run, $"mapfilter_{phase}_{mode}_{pins.IndexOf(pin)}_marker", marker == markerExpected(pin),
                        $"marker precondition/state failed for {pin.m_name}: expected={markerExpected(pin)}, actual={marker}");
                    if (!marker)
                    {
                        Check(run, $"mapfilter_{phase}_{mode}_{pins.IndexOf(pin)}_name_removed",
                            pin.m_NamePinData == null || !pin.m_NamePinData.PinNameGameObject ||
                            !pin.m_NamePinData.PinNameGameObject.activeInHierarchy,
                            "hidden marker retained a visible name element: " + pin.m_name);
                        continue;
                    }
                    var text = GetNameText(pin);
                    if (string.IsNullOrEmpty(pin.m_name)) continue;
                    Check(run, $"mapfilter_{phase}_{mode}_{pins.IndexOf(pin)}_text",
                        text && text.enabled &&
                        (mode != Minimap.MapMode.Large || pin.m_NamePinData.PinNameGameObject.activeInHierarchy),
                        $"name text precondition/state failed for {pin.m_name}: expected enabled=True, actual={(text ? text.enabled.ToString() : "missing")}");
                }
            }
            map.SetMapMode(Minimap.MapMode.Large);
            AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { center });
            Map.RefreshPins();
        }

        private static void CheckUnexplored(FeatureRun run, Minimap map, Vector3 origin,
            Minimap.PinData flora, Minimap.PinData mineral, Minimap.PinData manual,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings)
        {
            // The unexplored rule applies only with automatic mapping on. It is turned on for
            // this check alone and off again before the section yields a frame, so no mapping
            // pass runs with it.
            var automaticMapping = SmokeSetup.Field<bool>(typeof(MappingConfig), "_enableAutomaticMapping");
            var lifted = false;
            if (!MappingConfig.EnableAutomaticMapping)
            {
                if (automaticMapping == null || !SmokeSetup.Write(run, settings, automaticMapping, true))
                {
                    run.Skip("mapfilter_unexplored_combined", "enable_automatic_mapping is off and could not be turned on for the check");
                    return;
                }
                lifted = true;
            }
            var originalMineral = mineral.m_pos;
            var originalManual = manual.m_pos;
            try
            {
                var unexplored = new Vector3(20000f, origin.y, 20000f);
                if (!Map.IsExplored(flora.m_pos) || Map.IsExplored(unexplored))
                {
                    run.Fail("mapfilter_unexplored_combined", "exploration precondition failed: need an explored test pin and an unexplored point");
                    return;
                }
                Map.MovePin(mineral, unexplored);
                Map.MovePin(manual, unexplored + Vector3.right);
                Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList(new[] { "Flora" }));
                Write(run, settings, MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false);
                CheckModes(run, map, unexplored, new List<Minimap.PinData> { mineral, manual },
                    "unexplored_baseline", _ => true);
                Write(run, settings, MappingConfig.HideUnexploredAutomaticMappingPinsEntry, true);
                Check(run, "mapfilter_unexplored_combined", Map.ShouldHideAutomaticPin(flora) &&
                    Map.ShouldHideAutomaticPin(mineral) && !Map.ShouldHideAutomaticPin(manual),
                    "combined filter failed: category must hide explored Flora, exploration must hide Mineral, manual must stay visible");
                CheckModes(run, map, unexplored, new List<Minimap.PinData> { mineral, manual },
                    "unexplored_hidden", pin => pin == manual);
                Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, false);
                Check(run, "mapfilter_unexplored_independent", !Map.ShouldHideAutomaticPin(flora) &&
                    Map.ShouldHideAutomaticPin(mineral) && !Map.ShouldHideAutomaticPin(manual),
                    "enhancements off must restore explored Flora and keep the unexplored rule for Mineral");
                Write(run, settings, MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false);
                CheckModes(run, map, unexplored, new List<Minimap.PinData> { mineral, manual },
                    "unexplored_restored", _ => true);
            }
            finally
            {
                Map.MovePin(mineral, originalMineral);
                Map.MovePin(manual, originalManual);
                if (lifted) SmokeSetup.Write(run, settings, automaticMapping, false);
            }
        }

        private static void CheckConfigWritten(FeatureRun run, IEnumerable<ConfigEntryBase> entries)
        {
            foreach (var entry in entries)
            {
                var section = "";
                string value = null;
                foreach (var raw in File.ReadAllLines(entry.ConfigFile.ConfigFilePath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[")) section = line;
                    if (section != "[automatic_mapping]") continue;
                    var equals = line.IndexOf('=');
                    if (equals >= 0 && line.Substring(0, equals).Trim() == entry.Definition.Key)
                        value = line.Substring(equals + 1).Trim();
                }
                Check(run, "mapfilter_config_written_" + entry.Definition.Key,
                    value == TomlTypeConverter.ConvertToString(entry.BoxedValue, entry.SettingType),
                    "config persistence precondition/write failed: the live .cfg does not contain the control's value");
            }
        }

        private static void Write(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings, ConfigEntryBase entry, object value)
        {
            if (!SmokeSession.WriteSetting(run.RunId, settings[entry], value, out var refusal))
                throw new InvalidOperationException(entry.Definition.Key + " setting write precondition failed: " + refusal);
        }

        private static void Click(FeatureRun run, SmokeSession.SettingRestoration setting,
            MapFilter.FilterControl control, object expected)
        {
            if (!SmokeSession.InvokeSettingControl(run.RunId, setting, expected, control.InvokeClick, out var refusal))
                throw new InvalidOperationException("control click precondition failed: " + refusal);
        }

        private static void Check(FeatureRun run, string name, bool passed, string failure)
        {
            if (passed) run.Pass(name);
            else run.Fail(name, failure);
        }
    }

    internal static class PinTooltipFeatureSmoke
    {
        private static ZDOID _cachedId;
        private static Minimap.PinData _savedPortalPin;
        private static int _clientReads;
        private static int _sectorReads;

        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            var map = Minimap.instance;
            if (!run.IsCurrent || !player || !map || !ZNetScene.instance || !InventoryGui.instance ||
                !ZNet.instance || !ZNet.instance.IsServer() || ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("pintooltip_section", "requires a loaded single-player world and the inventory UI");
                yield break;
            }

            var objects = new List<GameObject>();
            var pins = new List<Minimap.PinData>();
            var settings = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            var originalMode = map.m_mode;
            var originalTarget = Navigation.CaptureTarget();
            var originalSearch = MapFilter.SearchText;
            var originalSearchPinned = MapFilter.SearchPinned;
            var originalFocus = MapFilter.SearchFocused;
            var originalPointer = ZInput.pointerPosition;
            var offsetField = AccessTools.Field(typeof(Minimap), "m_mapOffset");
            var originalOffset = offsetField.GetValue(map);
            var center = AccessTools.Method(typeof(Minimap), "GetViewCenterWorldPoint").Invoke(map, null);
            var cachedProbe = new Harmony("automatics.smoke.pintooltip." + run.RunId);
            var clientProbe = new Harmony("automatics.smoke.pintooltip.client." + run.RunId);
            var mergeRangeEntry = (ConfigEntry<int>)AccessTools.Field(typeof(MappingConfig), "_floraPinMergeRange").GetValue(null);
            Func<string, bool> handBack = _ =>
            {
                clientProbe.UnpatchSelf();
                cachedProbe.UnpatchSelf();
                _cachedId = ZDOID.None;
                _savedPortalPin = null;
                _clientReads = 0;
                _sectorReads = 0;
                PinTooltip.Hide();
                foreach (var pin in pins) if (Map.ContainsPin(pin)) map.RemovePin(pin);
                pins.Clear();
                foreach (var go in objects.ToArray()) Remove(go);
                objects.Clear();
                if (map) Navigation.RestoreTarget(originalTarget);
                SmokeSession.RestoreSettings(run.RunId);
                if (MappingModule.Running && MappingConfig.EnableMapEnhancements)
                {
                    map.SetMapMode(Minimap.MapMode.Large);
                    MapFilter.Update(map);
                    MapFilter.SetSearchText(originalSearch);
                    if (MapFilter.SearchPinned != originalSearchPinned) MapFilter.SearchToggle.InvokeClick();
                    if (originalFocus)
                    {
                        AccessTools.Method(MapFilter.SearchInput.GetType(), "Select").Invoke(MapFilter.SearchInput, null);
                        AccessTools.Method(MapFilter.SearchInput.GetType(), "ActivateInputFieldInternal").Invoke(MapFilter.SearchInput, null);
                    }
                }
                AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new[] { center });
                if (Cursor.lockState != CursorLockMode.Locked) ZInput.SetMousePosition(originalPointer);
                map.SetMapMode(originalMode);
                offsetField.SetValue(map, originalOffset);
                Map.RefreshPins();
                return true;
            };
            SmokeSession.TrackHandBack(handBack);
            try
            {
                foreach (var entry in new ConfigEntryBase[]
                {
                    MappingConfig.EnableMapEnhancementsEntry, MappingConfig.MapFilterHiddenCategoriesEntry,
                    MappingConfig.HideUnexploredAutomaticMappingPinsEntry, mergeRangeEntry
                })
                {
                    if (!SmokeSession.BeginSetting(run.RunId, entry, out var setting, out var refusal))
                    {
                        run.Fail("pintooltip_settings", "setting ownership precondition failed: " + refusal);
                        yield break;
                    }
                    settings.Add(entry, setting);
                }

                // Automatic Mapping on for the run when it is off; the settings restore turns it
                // off again.
                var module = new SmokeSetup.SetupResult();
                var enable = SmokeSetup.EnableModule(run.RunId, settings, MappingConfig.ModuleEntry,
                    () => MappingModule.Running, module);
                while (enable.MoveNext()) yield return enable.Current;
                if (!module.Ok)
                {
                    run.Skip("pintooltip_section", "Automatic Mapping is not running: " + module.Reason);
                    yield break;
                }
                if (module.Changed) run.Pass("pintooltip_setup", "Automatic Mapping " + module.Reason);

                Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, true);
                Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
                Write(run, settings, MappingConfig.HideUnexploredAutomaticMappingPinsEntry, false);
                Write(run, settings, mergeRangeEntry, 8);
                map.SetMapMode(Minimap.MapMode.Large);
                MapFilter.Update(map);
                if (MapFilter.SearchFocused) MapFilter.DefocusSearch();
                MapFilter.SetSearchText("");

                var origin = player.transform.position + Vector3.up * 15f;
                var creature = Spawn("Deer", origin, objects);
                if (!creature || !creature.GetComponent<Character>())
                {
                    run.Fail("pintooltip_creature_precondition", "spawned creature precondition failed: Deer has no valid Character/ZDO");
                    yield break;
                }
                var character = creature.GetComponent<Character>();
                var initialZdo = creature.GetComponent<ZNetView>().GetZDO();
                var initialCached = PinTooltipContent.ReadObject(ZNetScene.instance.GetPrefab("Deer"), initialZdo, false, ZNet.instance.GetTime());
                Check(run, "pintooltip_full_health_default", initialZdo.GetFloat(ZDOVars.s_health, -1f) < 0f &&
                    initialCached.Health == initialCached.MaxHealth,
                    "missing-health precondition/default failed: fresh creature must have no health field and cached health must default to max");
                character.SetLevel(2);
                character.SetHealth(character.GetMaxHealth() / 2f);
                var creaturePin = AddPin(map, pins, creature.transform.position, "Automatics smoke creature");
                AssociateDynamic(creaturePin, creature.GetComponent<ZNetView>().GetZDO().m_uid);
                var loaded = PinTooltipContent.Build(creaturePin);
                Check(run, "pintooltip_creature", loaded.Name == creaturePin.m_name &&
                    loaded.Text.Contains(Line("hp", character.GetHealth().ToString("0.#", CultureInfo.InvariantCulture),
                        character.GetMaxHealth().ToString("0.#", CultureInfo.InvariantCulture))) &&
                    loaded.Text.Contains(Line("level", 2)) && !loaded.Text.Contains(Line("outdated")),
                    "damaged level-2 creature content precondition/output failed: " + loaded.Text);
                Check(run, "pintooltip_horizontal_distance", loaded.Text.Contains(Line("distance",
                    Vector3.ProjectOnPlane(creaturePin.m_pos - player.transform.position, Vector3.up).magnitude.ToString("0", CultureInfo.InvariantCulture))),
                    "horizontal distance/name content failed for the elevated test object");

                _cachedId = creature.GetComponent<ZNetView>().GetZDO().m_uid;
                cachedProbe.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.FindInstance), new[] { typeof(ZDOID) }),
                    prefix: new HarmonyMethod(typeof(PinTooltipFeatureSmoke), nameof(HideTestInstance)));
                var cached = PinTooltipContent.Read(creaturePin, player.transform.position, ZNet.instance.GetTime());
                Check(run, "pintooltip_server_unloaded_creature", ZNet.instance.IsServer() &&
                    cached.Members.Single().State == PinTooltipContent.ObjectState.Current &&
                    PinTooltipContent.Build(creaturePin).Text == loaded.Text,
                    "server/unloaded ZDO precondition or content failed: authoritative HP/level must have no outdated note");
                StubClient(clientProbe);
                var clientCreature = PinTooltipContent.Read(creaturePin, player.transform.position, ZNet.instance.GetTime());
                Check(run, "pintooltip_cached_creature", _clientReads > 0 && ZNet.instance.IsServer() &&
                    clientCreature.Members.Single().State == PinTooltipContent.ObjectState.Cached &&
                    PinTooltipContent.Format(clientCreature, Translate).Text == loaded.Text + "\n" + Line("outdated"),
                    "client role probe/cached ZDO precondition or content failed: HP/level must carry the outdated note; probe reads=" + _clientReads);
                cachedProbe.UnpatchSelf();
                var readsBeforeLoaded = _clientReads;
                var restoredCreature = PinTooltipContent.Read(creaturePin, player.transform.position, ZNet.instance.GetTime());
                Check(run, "pintooltip_loaded_no_note", _clientReads == readsBeforeLoaded &&
                    restoredCreature.Members.Single().State == PinTooltipContent.ObjectState.Loaded &&
                    PinTooltipContent.Format(restoredCreature, Translate).Text == loaded.Text,
                    "restored live-instance precondition or content failed: loaded HP/level must have no outdated note");
                RestoreServer(run, clientProbe, "creature");

                var ready = Spawn("Pickable_Mushroom", origin + Vector3.right * 4f, objects);
                var picked = Spawn("Pickable_Mushroom", origin + Vector3.right * 8f, objects);
                if (!ready || !picked || !ready.GetComponent<Pickable>() || !picked.GetComponent<Pickable>() ||
                    picked.GetComponent<Pickable>().m_respawnTimeMinutes <= 0f)
                {
                    run.Fail("pintooltip_pickable_precondition", "ready/picked respawning pickable precondition failed");
                    yield break;
                }
                ready.GetComponent<Pickable>().SetPicked(false);
                picked.GetComponent<Pickable>().SetPicked(true);
                var readyId = ready.GetComponent<ZNetView>().GetZDO().m_uid;
                var pickedId = picked.GetComponent<ZNetView>().GetZDO().m_uid;
                var readyPin = AddPin(map, pins, ready.transform.position, "Automatics smoke ready");
                var pickedPin = AddPin(map, pins, picked.transform.position, "Automatics smoke picked");
                AssociateStatic(readyPin, readyId, PinKind.Flora);
                AssociateStatic(pickedPin, pickedId, PinKind.Flora);
                Check(run, "pintooltip_ready", PinTooltipContent.Build(readyPin).Text.Contains(Line("ready")),
                    "ready pickable precondition/output failed");
                var now = ZNet.instance.GetTime();
                var pickedSnapshot = PinTooltipContent.Read(pickedPin, player.transform.position, now);
                var member = pickedSnapshot.Members.Single();
                var expectedSeconds = picked.GetComponent<Pickable>().m_respawnTimeMinutes * 60d -
                    (now.Ticks - picked.GetComponent<ZNetView>().GetZDO().GetLong(ZDOVars.s_pickedTime)) / (double)TimeSpan.TicksPerSecond;
                Check(run, "pintooltip_respawn", !member.Ready && member.RespawnSeconds.HasValue &&
                    Math.Abs(member.RespawnSeconds.Value - expectedSeconds) < 0.001d &&
                    PinTooltipContent.Format(pickedSnapshot, Translate).Text.Contains(Line("respawn", Duration(expectedSeconds))),
                    "picked timestamp/respawn duration precondition or estimated countdown output failed");

                map.RemovePin(readyPin);
                map.RemovePin(pickedPin);
                var merged = AddPin(map, pins, origin + Vector3.right * 6f, "Automatics smoke mixed flora");
                AssociateStatic(merged, readyId, PinKind.Flora);
                AssociateStatic(merged, pickedId, PinKind.Flora);
                var mergedContent = PinTooltipContent.Build(merged);
                Check(run, "pintooltip_merged", mergedContent.Text.Contains(Line("ready_count", 1, 2)) &&
                    mergedContent.Text.Contains(Line("earliest", Duration(expectedSeconds))),
                    "merged two-member association precondition/count or earliest estimate failed: " + mergedContent.Text);
                _cachedId = pickedId;
                cachedProbe.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.FindInstance), new[] { typeof(ZDOID) }),
                    prefix: new HarmonyMethod(typeof(PinTooltipFeatureSmoke), nameof(HideTestInstance)));
                var cachedFlora = PinTooltipContent.Read(merged, player.transform.position, now);
                Check(run, "pintooltip_server_unloaded_flora", ZNet.instance.IsServer() &&
                    cachedFlora.Members.Any(x => x.State == PinTooltipContent.ObjectState.Current) &&
                    PinTooltipContent.Format(cachedFlora, Translate).Text == mergedContent.Text,
                    "server/unloaded flora precondition or content failed: authoritative members must retain count/estimate without the outdated note");
                StubClient(clientProbe);
                var clientFlora = PinTooltipContent.Read(merged, player.transform.position, now);
                Check(run, "pintooltip_cached_flora", _clientReads > 0 && ZNet.instance.IsServer() &&
                    clientFlora.Members.Any(x => x.State == PinTooltipContent.ObjectState.Cached) &&
                    PinTooltipContent.Format(clientFlora, Translate).Text == mergedContent.Text + "\n" + Line("outdated"),
                    "client role probe/cached flora precondition or content failed: mixed members must retain count/estimate with the outdated note; probe reads=" + _clientReads);
                RestoreServer(run, clientProbe, "flora");
                cachedProbe.UnpatchSelf();
                var later = PinTooltipContent.Read(merged, player.transform.position, now.AddMinutes(1));
                Check(run, "pintooltip_countdown", Math.Abs(later.Members[0].RespawnSeconds.GetValueOrDefault() +
                    later.Members[1].RespawnSeconds.GetValueOrDefault() - (expectedSeconds - 60d)) < 0.001d,
                    "countdown did not advance on the network-time base");

                var depositPrefab = ZNetScene.instance.GetPrefab("rock4_copper_frac");
                if (!depositPrefab || !depositPrefab.GetComponent<MineRock5>())
                {
                    run.Fail("pintooltip_deposit_precondition", "section prefab precondition failed: rock4_copper_frac has no MineRock5");
                    yield break;
                }
                var deposit = Spawn("rock4_copper_frac", origin + Vector3.forward * 8f, objects);
                if (!deposit || !deposit.GetComponent<MineRock5>())
                {
                    run.Fail("pintooltip_deposit_precondition", "spawned section deposit precondition failed: rock4_copper_frac has no MineRock5/valid ZDO");
                    yield break;
                }
                var rock = deposit.GetComponent<MineRock5>();
                var areas = (IList)AccessTools.Field(typeof(MineRock5), "m_hitAreas").GetValue(rock);
                if (areas == null || areas.Count < 2)
                {
                    run.Fail("pintooltip_deposit_precondition", "MineRock5 Awake/section precondition failed: need at least two initialized hit areas");
                    yield break;
                }
                AccessTools.Method(typeof(MineRock5), "RPC_SetAreaHealth").Invoke(rock, new object[] { 0L, 0, 0f });
                AccessTools.Method(typeof(MineRock5), "SaveHealth").Invoke(rock, null);
                var depositPin = AddPin(map, pins, deposit.transform.position, "Automatics smoke deposit");
                var depositId = deposit.GetComponent<ZNetView>().GetZDO().m_uid;
                AssociateStatic(depositPin, depositId, PinKind.Mineral);
                Check(run, "pintooltip_sections", PinTooltipContent.Build(depositPin).Text.Contains(Line("sections", areas.Count - 1, areas.Count)),
                    "broken section/package precondition/output failed");
                _cachedId = depositId;
                cachedProbe.Patch(AccessTools.Method(typeof(ZNetScene), nameof(ZNetScene.FindInstance), new[] { typeof(ZDOID) }),
                    prefix: new HarmonyMethod(typeof(PinTooltipFeatureSmoke), nameof(HideTestInstance)));
                var serverSections = PinTooltipContent.Build(depositPin);
                Check(run, "pintooltip_server_unloaded_sections", ZNet.instance.IsServer() &&
                    serverSections.Text.Contains(Line("sections", areas.Count - 1, areas.Count)) &&
                    !serverSections.Text.Contains(Line("outdated")),
                    "server/unloaded deposit precondition or content failed: authoritative sections must have no outdated note");
                StubClient(clientProbe);
                var clientSections = PinTooltipContent.Read(depositPin, player.transform.position, ZNet.instance.GetTime());
                Check(run, "pintooltip_cached_sections", _clientReads > 0 && ZNet.instance.IsServer() &&
                    clientSections.Members.Single().State == PinTooltipContent.ObjectState.Cached &&
                    PinTooltipContent.Format(clientSections, Translate).Text == serverSections.Text + "\n" + Line("outdated"),
                    "client role probe/cached deposit precondition or content failed: section details must carry the outdated note; probe reads=" + _clientReads);
                RestoreServer(run, clientProbe, "sections");
                cachedProbe.UnpatchSelf();

                var saved = Map.AddPin(origin + Vector3.left * 4f, "Automatics smoke saved", true,
                    new Target { name = "Automatics smoke saved" });
                pins.Add(saved);
                Check(run, "pintooltip_no_link", saved.m_save && Map.IsAutomaticPin(saved) &&
                    PinTooltipContent.Build(saved).Text.Contains(Line("no_link")),
                    "saved automatic pin without association precondition/reason failed");
                _savedPortalPin = Map.AddPin(origin + Vector3.back * 4f, "Automatics smoke saved portal", true,
                    new Target { name = "Automatics smoke saved portal" });
                pins.Add(_savedPortalPin);
                cachedProbe.Patch(AccessTools.Method(typeof(PinCategories), nameof(PinCategories.TryClassify)),
                    prefix: new HarmonyMethod(typeof(PinTooltipFeatureSmoke), nameof(ClassifySavedPortal)));
                var portalContent = PinTooltipContent.Build(_savedPortalPin);
                Check(run, "pintooltip_saved_portal", _savedPortalPin.m_save && Map.IsAutomaticPin(_savedPortalPin) &&
                    !StaticObjectMapping.OwnsPin(_savedPortalPin) && !DynamicObjectMapping.OwnsPin(_savedPortalPin) &&
                    PinCategories.TryClassify(_savedPortalPin, out var portalCategory) && portalCategory == PinCategory.Portal &&
                    portalContent.Name == _savedPortalPin.m_name && portalContent.Text.Split('\n').Length == 1,
                    "saved portal category/no-association precondition or content failed: only name and distance should appear");
                cachedProbe.UnpatchSelf();
                _savedPortalPin = null;
                var manual = map.AddPin(origin + Vector3.left * 8f, Minimap.PinType.Icon3, "Automatics smoke manual", false, false);
                pins.Add(manual);
                Check(run, "pintooltip_manual", PinTooltipContent.Build(manual).Text.Split('\n').Length == 1,
                    "manual pin should carry only its name and distance");
                var location = AddPin(map, pins, origin + Vector3.left * 12f, "Automatics smoke location");
                AssociateLocation(location);
                Check(run, "pintooltip_location", PinTooltipContent.Build(location).Text.Split('\n').Length == 1,
                    "location-domain pin should carry only its name and distance");

                AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { merged.m_pos });
                AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases").Invoke(null, null);
                Map.RefreshPins();
                if (!merged.m_uiElement || !merged.m_uiElement.gameObject.activeInHierarchy)
                {
                    run.Fail("pintooltip_ui_precondition", "test pin marker precondition failed: mixed flora marker is not visible on the large map");
                    yield break;
                }
                var point = merged.m_uiElement.position;
                CheckUi(run, map, merged, point, settings);
                CheckSavedObjects(run, map, pins, objects, ready, picked, deposit, merged, depositPin, cachedProbe);
                var removedId = creature.GetComponent<ZNetView>().GetZDO().m_uid;
                foreach (var pin in pins) if (Map.ContainsPin(pin)) map.RemovePin(pin);
                pins.Clear();
                foreach (var go in objects) Remove(go);
                objects.Clear();
                var hoverPin = map.AddPin(player.transform.position + Vector3.forward * 300f,
                    Minimap.PinType.Icon3, "Automatics smoke hover", false, false);
                pins.Add(hoverPin);
                var pointerSteps = CheckPointer(run, map, hoverPin, settings);
                while (pointerSteps.MoveNext()) yield return pointerSteps.Current;
                if (!run.IsCurrent) yield break;
                var removalDeadline = Time.unscaledTime + 3f;
                while (run.IsCurrent && ZDOMan.instance.GetZDO(removedId) != null && Time.unscaledTime < removalDeadline)
                    yield return null;
                if (!run.IsCurrent) yield break;
                var removedPin = AddPin(map, pins, origin, "Automatics smoke missing data");
                AssociateDynamic(removedPin, removedId);
                var removedContent = PinTooltipContent.Build(removedPin);
                Check(run, "pintooltip_server_removed", ZNet.instance.IsServer() && ZDOMan.instance.GetZDO(removedId) == null &&
                    removedContent.Name == removedPin.m_name && removedContent.Text.Split('\n').Length == 1,
                    "server/removed-object precondition or content failed: missing authoritative ZDO must leave only name and distance");
                StubClient(clientProbe);
                var missing = PinTooltipContent.Read(removedPin, player.transform.position, ZNet.instance.GetTime());
                Check(run, "pintooltip_not_loaded", _clientReads > 0 && ZNet.instance.IsServer() &&
                    ZDOMan.instance.GetZDO(removedId) == null && missing.Members.Single().State == PinTooltipContent.ObjectState.NoData &&
                    PinTooltipContent.Format(missing, Translate).Text == removedContent.Text + "\n" + Line("not_loaded"),
                    "client role probe/missing-data precondition or content failed: unavailable local data must carry the not-loaded reason; probe reads=" + _clientReads);
                RestoreServer(run, clientProbe, "missing_data");

            }
            finally
            {
                // Before the hand-back, whose own restore then finds nothing left; that one is
                // for a section that never gets here.
                SmokeSetup.ReportSettings(run, "pintooltip_settings_restored");
                if (handBack("pin tooltip smoke")) SmokeSession.ReleaseHandBack(handBack);
            }
        }

        private static void CheckSavedObjects(FeatureRun run, Minimap map, List<Minimap.PinData> pins,
            List<GameObject> objects, GameObject ready, GameObject picked, GameObject deposit, Minimap.PinData merged,
            Minimap.PinData depositPin, Harmony probe)
        {
            if (!ValheimObject.Flora.GetIdentify(Objects.GetName(ready.GetComponent<Pickable>()), out var floraId) ||
                !ValheimObject.Flora.GetName(floraId, out var floraLabel) ||
                !ValheimObject.Mineral.GetIdentify(Objects.GetName(deposit.GetComponent<MineRock5>()), out var mineralId) ||
                !ValheimObject.Mineral.GetName(mineralId, out var mineralLabel))
                throw new InvalidOperationException("saved tooltip registry labels are unavailable");
            map.RemovePin(merged);
            map.RemovePin(depositPin);
            Minimap.PinData Saved(string name, Vector3 position, PinKind kind, params ZDOID[] ids)
            {
                var pin = Map.AddPin(position, name, true, new Target { name = name });
                pins.Add(pin);
                foreach (var id in ids) AssociateStatic(pin, id, kind);
                StaticObjectMapping.OnRemovePin(pin);
                Check(run, "pintooltip_saved_unlinked_" + pins.Count,
                    pin.m_save && Map.IsAutomaticPin(pin) && !StaticObjectMapping.OwnsPin(pin),
                    "saved automatic pin retained its test association");
                return pin;
            }
            var readyId = ready.GetComponent<ZNetView>().GetZDO().m_uid;
            var pickedId = picked.GetComponent<ZNetView>().GetZDO().m_uid;
            var depositId = deposit.GetComponent<ZNetView>().GetZDO().m_uid;
            var single = Saved(floraLabel, ready.transform.position, PinKind.Flora, readyId);
            var singleObjects = PinTooltipContent.ResolveSavedObjects(single);
            Check(run, "pintooltip_saved_flora", singleObjects.Ids.SequenceEqual(new[] { readyId }) &&
                !singleObjects.MergedFlora && PinTooltipContent.Build(single, singleObjects).Text.Contains(Line("ready")),
                "saved flora did not resolve its nearest matching local object and readiness");
            var clusterName = StaticObjectMapping.ComputeFloraPinName(floraLabel, 2);
            var cluster = Map.AddPin((ready.transform.position + picked.transform.position) / 2f,
                clusterName, false, new Target { name = floraLabel });
            pins.Add(cluster);
            AssociateStatic(cluster, readyId, PinKind.Flora);
            AssociateStatic(cluster, pickedId, PinKind.Flora);
            Check(run, "pintooltip_click_save_precondition", !cluster.m_save && Map.IsAutomaticPin(cluster) &&
                StaticObjectMapping.OwnsPin(cluster), "merged flora pin was not an associated unsaved automatic pin");
            var savedByClick = StaticObjectMapping.SetSaveFlag(cluster);
            Check(run, "pintooltip_click_saved_unlinked", savedByClick && cluster.m_save && cluster.m_ownerID == 0 &&
                !Map.IsAutomaticPin(cluster) && !StaticObjectMapping.OwnsPin(cluster),
                "SetSaveFlag did not save and untrack the automatic merged flora pin");
            var clusterObjects = PinTooltipContent.ResolveSavedObjects(cluster);
            Check(run, "pintooltip_saved_merged_flora", clusterObjects.MergedFlora &&
                new HashSet<ZDOID>(clusterObjects.Ids).SetEquals(new[] { readyId, pickedId }) &&
                PinTooltipContent.Build(cluster, clusterObjects).Text.Contains(Line("ready_count", 1, 2)) &&
                PinTooltipContent.Build(cluster, clusterObjects).Text.Contains(Translate("@text_automatic_mapping_tooltip_earliest").Split('{')[0]),
                "saved merged flora did not match the formatter's base label, count and earliest estimate");
            var mineralPositionArgs = new object[] { deposit.GetComponent<MineRock5>(), Vector3.zero, 0f };
            if (!(bool)AccessTools.Method(typeof(StaticObjectMapping), "TryGetMineralPosition").Invoke(null, mineralPositionArgs))
                throw new InvalidOperationException("saved mineral mapping position is unavailable");
            var mineralPosition = (Vector3)mineralPositionArgs[1];
            var mineral = Saved(mineralLabel, mineralPosition, PinKind.Mineral, depositId);
            var mineralObjects = PinTooltipContent.ResolveSavedObjects(mineral);
            var areas = (IList)AccessTools.Field(typeof(MineRock5), "m_hitAreas").GetValue(deposit.GetComponent<MineRock5>());
            Check(run, "pintooltip_saved_mineral", mineralObjects.Ids.SequenceEqual(new[] { depositId }) &&
                PinTooltipContent.Build(mineral, mineralObjects).Text.Contains(Line("sections", areas.Count - 1, areas.Count)),
                "saved mineral at its mapped collider centre did not resolve its matching local object and remaining sections");
            var missing = map.AddPin(ready.transform.position + Vector3.up * 100f, Minimap.PinType.Icon3,
                Map.StoredPinName(floraLabel), true, false);
            pins.Add(missing);
            var missingObjects = PinTooltipContent.ResolveSavedObjects(missing);
            Check(run, "pintooltip_saved_not_found", missingObjects.Ids.Count == 0 &&
                PinTooltipContent.Build(missing, missingObjects).Text.Contains(Line("no_link")),
                "own saved flora-labelled pin without a matching nearby object lost its reason line");
            var manual = map.AddPin(single.m_pos, Minimap.PinType.Icon3, "Automatics smoke saved manual", true, false);
            pins.Add(manual);
            var manualObjects = PinTooltipContent.ResolveSavedObjects(manual);
            Check(run, "pintooltip_saved_manual", manualObjects.Ids.Count == 0 &&
                PinTooltipContent.Build(manual, manualObjects).Text.Split('\n').Length == 1,
                "saved manual pin with an unrelated label incorrectly acquired object details");
            map.RemovePin(manual);
            var labelledManual = map.AddPin(single.m_pos, Minimap.PinType.Icon3, single.m_name, true, false);
            pins.Add(labelledManual);
            var manualMatches = PinTooltipContent.ResolveSavedObjects(labelledManual);
            Check(run, "pintooltip_saved_manual_flora", !Map.IsAutomaticPin(labelledManual) &&
                manualMatches.Ids.SequenceEqual(new[] { readyId }) &&
                PinTooltipContent.Build(labelledManual, manualMatches).Text.Contains(Line("ready")),
                "own saved manual pin with a unique flora label did not resolve readiness");
            map.RemovePin(labelledManual);
            var localizedName = Localization.instance.Localize(Objects.GetName(ready.GetComponent<Pickable>()));
            var localizedManual = map.AddPin(single.m_pos, Minimap.PinType.Icon3, localizedName, true, false);
            pins.Add(localizedManual);
            var localizedMatches = PinTooltipContent.ResolveSavedObjects(localizedManual);
            Check(run, "pintooltip_saved_localized_flora", localizedName != single.m_name &&
                localizedName == Localization.instance.Localize(single.m_name) &&
                !Map.IsAutomaticPin(localizedManual) && !PinCategories.TryClassify(localizedManual, out _) &&
                localizedMatches.Ids.SequenceEqual(new[] { readyId }) &&
                PinTooltipContent.Build(localizedManual, localizedMatches).Text.Contains(Line("ready")),
                "own saved pin-key pin with a localized flora label did not resolve readiness");
            map.RemovePin(localizedManual);
            var shared = new Minimap.PinData { m_save = true, m_ownerID = 1L, m_name = single.m_name, m_pos = single.m_pos };
            var sharedMatches = PinTooltipContent.ResolveSavedObjects(shared);
            Check(run, "pintooltip_saved_shared", sharedMatches.Ids.Count == 0 &&
                PinTooltipContent.Build(shared, sharedMatches).Text.Split('\n').Length == 1,
                "shared flora-labelled pin incorrectly acquired details or a no-link reason");
            probe.Patch(AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects)),
                prefix: new HarmonyMethod(typeof(PinTooltipFeatureSmoke), nameof(CountSectorRead)));
            try
            {
                CheckSavedHover(run, map, single, "flora", Line("ready"));
                CheckSavedHover(run, map, cluster, "merged_flora", Line("ready_count", 1, 2));
                CheckSavedHover(run, map, mineral, "mineral", Line("sections", areas.Count - 1, areas.Count));
            }
            finally { probe.UnpatchSelf(); }

            // Keep the chain above existing flora so only these four objects can join it.
            var chain = new List<GameObject>();
            for (var i = 0; i < 4; i++)
            {
                var flora = Spawn("Pickable_Mushroom", ready.transform.position + Vector3.up * 50f +
                    Vector3.right * (i * 7f - 10.5f), objects);
                if (!flora || !flora.GetComponent<Pickable>())
                    throw new InvalidOperationException("saved flora chain fixture is unavailable");
                flora.GetComponent<Pickable>().SetPicked(false);
                chain.Add(flora);
            }
            var chainIds = chain.Select(flora => flora.GetComponent<ZNetView>().GetZDO().m_uid).ToArray();
            var chainPin = Saved(StaticObjectMapping.ComputeFloraPinName(floraLabel, 4),
                (chain[0].transform.position + chain[3].transform.position) / 2f, PinKind.Flora, chainIds);
            var chainObjects = PinTooltipContent.ResolveSavedObjects(chainPin);
            Check(run, "pintooltip_saved_flora_chain", chainObjects.MergedFlora &&
                new HashSet<ZDOID>(chainObjects.Ids).SetEquals(chainIds) &&
                PinTooltipContent.Build(chainPin, chainObjects).Text.Contains(Line("ready_count", 4, 4)),
                "saved x4 flora pin did not include the endpoints linked through neighbours 7 m apart at merge range 8");

            // Pin Looked-at Object Key on a member of a merged flora patch. The patch's network
            // forms a second or two after its pickables spawn, so it is built here; the saved pin
            // above stands at the patch centre and is taken away, so the key first meets a patch
            // without any pin and then one with an automatic pin.
            map.RemovePin(cluster);
            var player = Player.m_localPlayer;
            var readyNode = FloraNode.Find(readyId);
            var pickedNode = FloraNode.Find(pickedId);
            if (readyNode && pickedNode)
                AccessTools.Method(typeof(FloraNode), "NetworkConstruction").Invoke(readyNode, null);
            var network = readyNode ? readyNode.Network : null;
            if (network == null || !pickedNode || !ReferenceEquals(network, pickedNode.Network) || network.NodeCount != 2)
            {
                run.Skip("pin_key_creates_patch_pin", "the two test flora did not form one flora network");
                run.Skip("pin_key_patch_pin_not_duplicated", "no flora network was available");
                run.Skip("pin_key_saves_automatic_pin", "the two test flora did not form one flora network");
                run.Skip("pin_key_saved_pin_not_duplicated", "no flora network was available");
                run.Skip("nav_key_marks_patch", "no flora network was available");
                run.Skip("nav_key_repeat_clears", "no flora network was available");
                run.Skip("nav_key_targets_automatic_patch_pin", "no flora network was available");
                run.Skip("nav_key_targets_saved_patch_pin", "no flora network was available");
                return;
            }
            network.Update();
            // The camera ray first, then onto each flora from just beside it on every side: the
            // section's test deposit stands close enough to cover the flora from some directions.
            var rays = new List<KeyValuePair<Ray, ZDOID>>();
            var colliders = new[] { ready, picked }.Select(target => new KeyValuePair<Collider, ZDOID>(
                target.GetComponentsInChildren<Collider>().FirstOrDefault(x => x.enabled && x.gameObject.activeInHierarchy),
                target.GetComponent<ZNetView>().GetZDO().m_uid)).Where(x => x.Key).ToList();
            if (GameCamera.instance)
            {
                var camera = GameCamera.instance.transform.position;
                foreach (var target in colliders)
                    rays.Add(new KeyValuePair<Ray, ZDOID>(new Ray(camera, target.Key.bounds.center - camera), target.Value));
            }
            foreach (var direction in new[]
                         { Vector3.down, Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                foreach (var height in new[] { 1.5f, 3f })
                    foreach (var target in colliders)
                        rays.Add(new KeyValuePair<Ray, ZDOID>(
                            new Ray(target.Key.bounds.center - direction * height, direction), target.Value));
            var ray = default(Ray);
            var aimable = false;
            var rayNotes = new List<string>();
            var interactMask = AccessTools.FieldRefAccess<Player, int>("m_interactMask")(player);
            foreach (var candidate in rays)
            {
                if (ObjectPinning.TryResolveFromRay(player, candidate.Key, out var aimed) &&
                    aimed.UniqueId == candidate.Value)
                {
                    ray = candidate.Key;
                    aimable = true;
                    break;
                }
                var first = Physics.RaycastAll(candidate.Key, MappingConfig.PinObjectRange, interactMask)
                    .Where(x => !x.collider.transform.IsChildOf(player.transform))
                    .OrderBy(x => x.distance).FirstOrDefault();
                rayNotes.Add(first.collider
                    ? $"hit {first.collider.name}/{LayerMask.LayerToName(first.collider.gameObject.layer)}" +
                      $"{(first.collider.isTrigger ? "/trigger" : "")} root {first.collider.transform.root.name} " +
                      $"at {first.distance:0.0}m resolved {(aimed != null ? aimed.UniqueId.ToString() : "none")}"
                    : "hit nothing");
            }
            if (!aimable)
            {
                var targets = string.Join(", ", colliders.Select(x =>
                    $"{x.Key.name}/{LayerMask.LayerToName(x.Key.gameObject.layer)}{(x.Key.isTrigger ? "/trigger" : "")} " +
                    $"{x.Value}"));
                run.Skip("pin_key_creates_patch_pin", "no test ray resolved the test flora: targets [" + targets +
                    "]; rays [" + string.Join("; ", rayNotes) + "]");
                run.Skip("pin_key_patch_pin_not_duplicated", "no unobstructed flora ray was available");
                run.Skip("pin_key_saves_automatic_pin", "every test ray to the test flora is obstructed");
                run.Skip("pin_key_saved_pin_not_duplicated", "no unobstructed flora ray was available");
                run.Skip("nav_key_marks_patch", "no unobstructed flora ray was available");
                run.Skip("nav_key_repeat_clears", "no unobstructed flora ray was available");
                run.Skip("nav_key_targets_automatic_patch_pin", "no unobstructed flora ray was available");
                run.Skip("nav_key_targets_saved_patch_pin", "no unobstructed flora ray was available");
                return;
            }
            Minimap.PinData PressPinKey()
            {
                var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                try
                {
                    return ObjectPinning.TryPinFromRay(player, ray);
                }
                finally
                {
                    // Include a pin even if a message or an AddPin postfix throws after adding it.
                    pins.AddRange(Map.GetAllPins().Where(pin => !before.Contains(pin)));
                }
            }

            // Navigate to Looked-at Object Key on the same patch; the run restores the key and the
            // navigation target.
            var navBound = SmokeSession.BeginSetting(run.RunId, MappingConfig.NavigateToAimedObjectKeyEntry,
                    out var navSetting, out var navRefusal) &&
                SmokeSession.WriteSetting(run.RunId, navSetting, new KeyboardShortcut(KeyCode.F8), out navRefusal);
            // The key acts only with the large map closed: the player takes no input while it is
            // open, and CheckUi's search defocus blocks text input on the large map for the rest
            // of this frame. Each press returns the key's gate state for the failure messages.
            string PressNavKey()
            {
                var before = new HashSet<Minimap.PinData>(Map.GetAllPins());
                var mode = map.m_mode;
                try
                {
                    map.SetMapMode(Minimap.MapMode.Small);
                    var state = $"running={MappingModule.Running}, large map={map.m_mode == Minimap.MapMode.Large}, " +
                                $"text input={Minimap.InTextInput()}, " +
                                $"key bound={MappingConfig.NavigateToAimedObjectKey.MainKey != KeyCode.None}";
                    Navigation.TryNavigateFromRay(player, ray, true);
                    return state;
                }
                finally
                {
                    map.SetMapMode(mode);
                    pins.AddRange(Map.GetAllPins().Where(pin => !before.Contains(pin)));
                }
            }
            // What the last press left, against the pin it should target and the pin count before it.
            string NavResult(Minimap.PinData expected, int countBefore) =>
                "target=" + (Navigation.TargetPin == null ? "none"
                    : ReferenceEquals(Navigation.TargetPin, expected) ? "expected"
                    : ReferenceEquals(Navigation.TargetPin, Navigation.TemporaryMarker) ? "marker" : "other") +
                $", marker={(Navigation.TemporaryMarker == null ? "none" : "present")}, " +
                $"pin delta={Map.GetAllPins().Count - countBefore}";
            Navigation.ClearTarget();

            // Without an automatic pin, the key adds the pin the scan would give the patch.
            var existing = new HashSet<Minimap.PinData>(Map.GetAllPins());
            var count = existing.Count;
            var patchPin = PressPinKey();
            var patchObjects = patchPin != null ? PinTooltipContent.ResolveSavedObjects(patchPin) : null;
            Check(run, "pin_key_creates_patch_pin", patchPin != null && !existing.Contains(patchPin) &&
                Map.GetAllPins().Count == count + 1 && patchPin.m_save && !Map.IsAutomaticPin(patchPin) &&
                Vector3.Distance(patchPin.m_pos, network.Center) < 0.01f &&
                patchPin.m_name == Map.StoredPinName(StaticObjectMapping.ComputeFloraPinName(floraLabel, 2)) &&
                patchObjects.MergedFlora && new HashSet<ZDOID>(patchObjects.Ids).SetEquals(new[] { readyId, pickedId }),
                "the key on a flora patch without an automatic pin did not add one saved pin named and placed like its automatic pin");
            // A pin that was already on the map is not the test's to remove.
            if (patchPin == null || existing.Contains(patchPin))
                run.Skip("pin_key_patch_pin_not_duplicated", "the first press created no new pin");
            else
            {
                count = Map.GetAllPins().Count;
                Check(run, "pin_key_patch_pin_not_duplicated", PressPinKey() == null && Map.GetAllPins().Count == count,
                    "the key on a member of a flora patch with its saved patch pin added another pin");
                // The automatic-pin checks below need the patch centre free.
                map.RemovePin(patchPin);
            }

            // Navigation to a patch without a pin marks the patch centre, not the plant looked at.
            if (!navBound)
            {
                run.Skip("nav_key_marks_patch", "the navigation key could not be bound: " + navRefusal);
                run.Skip("nav_key_repeat_clears", "the navigation key could not be bound: " + navRefusal);
            }
            else
            {
                existing = new HashSet<Minimap.PinData>(Map.GetAllPins());
                var navState = PressNavKey();
                var marker = Navigation.TemporaryMarker;
                Check(run, "nav_key_marks_patch", marker != null && ReferenceEquals(Navigation.TargetPin, marker) &&
                    !existing.Contains(marker) && Map.GetAllPins().Count == existing.Count + 1 && !marker.m_save &&
                    !Map.IsAutomaticPin(marker) && Vector3.Distance(marker.m_pos, network.Center) < 0.01f &&
                    marker.m_name == Map.StoredPinName(StaticObjectMapping.ComputeFloraPinName(floraLabel, 2)),
                    "navigation to a flora patch without an automatic pin did not add one unsaved marker named and placed like the patch pin; " +
                    navState + ", " + NavResult(marker, existing.Count) + (marker == null ? "" :
                        $", saved={marker.m_save}, automatic={Map.IsAutomaticPin(marker)}, " +
                        $"distance to patch centre={Vector3.Distance(marker.m_pos, network.Center):0.00}m, " +
                        $"name={marker.m_name}, expected name=" +
                        Map.StoredPinName(StaticObjectMapping.ComputeFloraPinName(floraLabel, 2))));
                PressNavKey();
                Check(run, "nav_key_repeat_clears", Navigation.TargetPin == null && Navigation.TemporaryMarker == null &&
                    Map.GetAllPins().Count == existing.Count && (marker == null || !Map.GetAllPins().Contains(marker)),
                    "repeating navigation to the patch did not clear it and remove its marker");
            }

            var keyPin = Map.AddPin(network.Center, clusterName, false, new Target { name = floraLabel });
            pins.Add(keyPin);
            AssociateStatic(keyPin, readyId, PinKind.Flora);
            AssociateStatic(keyPin, pickedId, PinKind.Flora);
            var keyPinName = keyPin.m_name;
            var automatic = !keyPin.m_save && Map.IsAutomaticPin(keyPin) && StaticObjectMapping.OwnsPin(keyPin);
            if (!navBound)
                run.Skip("nav_key_targets_automatic_patch_pin", "the navigation key could not be bound: " + navRefusal);
            else
            {
                count = Map.GetAllPins().Count;
                var navState = PressNavKey();
                Check(run, "nav_key_targets_automatic_patch_pin", automatic && ReferenceEquals(Navigation.TargetPin, keyPin) &&
                    Navigation.TemporaryMarker == null && Map.GetAllPins().Count == count,
                    "navigation to a flora patch with an unsaved automatic pin did not target it without a marker; " +
                    navState + ", " + NavResult(keyPin, count) + $", automatic={automatic}, " +
                    $"pin hidden={Map.ShouldHideAutomaticPinIgnoringSearch(keyPin)}");
                Navigation.ClearTarget();
            }
            count = Map.GetAllPins().Count;
            var pressed = PressPinKey();
            var keyObjects = PinTooltipContent.ResolveSavedObjects(keyPin);
            Check(run, "pin_key_saves_automatic_pin", automatic && ReferenceEquals(pressed, keyPin) &&
                Map.GetAllPins().Count == count && keyPin.m_save && !Map.IsAutomaticPin(keyPin) &&
                !StaticObjectMapping.OwnsPin(keyPin) && keyPin.m_name == keyPinName && keyObjects.MergedFlora &&
                new HashSet<ZDOID>(keyObjects.Ids).SetEquals(new[] { readyId, pickedId }),
                "unsaved associated merged flora pin precondition or the key's in-place save failed");
            if (!keyPin.m_save)
            {
                run.Skip("pin_key_saved_pin_not_duplicated", "the first press did not save the automatic pin");
                run.Skip("nav_key_targets_saved_patch_pin", "the first press did not save the automatic pin");
                return;
            }
            count = Map.GetAllPins().Count;
            Check(run, "pin_key_saved_pin_not_duplicated", PressPinKey() == null && Map.GetAllPins().Count == count,
                "the key on a member of a saved flora patch pin added another pin");
            if (!navBound)
                run.Skip("nav_key_targets_saved_patch_pin", "the navigation key could not be bound: " + navRefusal);
            else
            {
                count = Map.GetAllPins().Count;
                var navState = PressNavKey();
                Check(run, "nav_key_targets_saved_patch_pin", ReferenceEquals(Navigation.TargetPin, keyPin) &&
                    Navigation.TemporaryMarker == null && Map.GetAllPins().Count == count,
                    "navigation to a flora patch with its saved pin did not target that pin without a marker; " +
                    navState + ", " + NavResult(keyPin, count) + $", saved={keyPin.m_save}, " +
                    $"pin hidden={Map.ShouldHideAutomaticPinIgnoringSearch(keyPin)}");
                Navigation.ClearTarget();
            }
        }

        private static void CountSectorRead() => _sectorReads++;

        private static void CheckSavedHover(FeatureRun run, Minimap map, Minimap.PinData pin, string kind, string detail)
        {
            map.SetMapMode(Minimap.MapMode.Large);
            AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { pin.m_pos });
            Map.RefreshPins();
            PinTooltip.Hide();
            _sectorReads = 0;
            PinTooltip.UpdateAt(map, pin.m_uiElement.position);
            var resolved = PinTooltip.ResolvedObjects;
            Check(run, "pintooltip_saved_" + kind + "_hover", PinTooltip.HoveredPin == pin && resolved != null &&
                resolved.Ids.Count > 0 && _sectorReads == 1 && PinTooltip.Content.Text.Contains(detail),
                "hover change did not resolve the saved pin and display its details");
            PinTooltip.Invalidate();
            PinTooltip.UpdateAt(map, pin.m_uiElement.position);
            Check(run, "pintooltip_saved_" + kind + "_cache", ReferenceEquals(resolved, PinTooltip.ResolvedObjects) &&
                _sectorReads == 1 && PinTooltip.Content.Text.Contains(detail),
                "unchanged hover scanned sectors again or lost its resolved details");
        }

        private static void CheckUi(FeatureRun run, Minimap map, Minimap.PinData pin, Vector3 point,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings)
        {
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_components", PinTooltip.Root && PinTooltip.HasTopicAndText,
                "inventory-element tooltip prefab precondition failed: Topic/Text text components were not both found after creation");
            if (!PinTooltip.Root || !PinTooltip.HasTopicAndText) return;
            var shown = PinTooltip.Root && PinTooltip.Root.activeInHierarchy &&
                ReferenceEquals(PinTooltip.HoveredPin, pin) && PinTooltip.Content.Text == PinTooltipContent.Build(pin).Text &&
                Text(PinTooltip.Root, "Topic") == PinTooltip.Content.Name && Text(PinTooltip.Root, "Text") == PinTooltip.Content.Text;
            Check(run, "pintooltip_ui_show", shown,
                "cursor/prefab precondition or tooltip show/content failed at the test marker's screen point");
            if (!shown) return;
            var original = PinTooltip.Content;
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_cached", ReferenceEquals(original, PinTooltip.Content),
                "unchanged hover rebuilt/localized content before its one-second refresh");
            PinTooltip.Invalidate();
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_refresh", PinTooltip.Content != null && !ReferenceEquals(original, PinTooltip.Content),
                "due refresh failed to rebuild countdown content");
            var rect = MapFilter.ScreenRect((RectTransform)PinTooltip.Root.transform.GetChild(0));
            Check(run, "pintooltip_ui_clamped", rect.xMin >= -1f && rect.yMin >= -1f &&
                rect.xMax <= Screen.width + 1f && rect.yMax <= Screen.height + 1f,
                "tooltip screen clamp failed: " + rect);
            PinTooltip.UpdateAt(map, new Vector3(-100000f, -100000f));
            Check(run, "pintooltip_ui_leave", Hidden(), "moving off every pin left the tooltip visible");
            PinTooltip.UpdateAt(map, point);
            Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, false);
            Check(run, "pintooltip_ui_option_immediate", Hidden(), "enhancements off did not hide the shown tooltip in the setting-change frame");
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_option_off", Hidden(), "enhancements off allowed tooltip at the test pin");
            Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, true);
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_option_on", PinTooltip.Root && PinTooltip.Root.activeInHierarchy,
                "enhancements on did not restore hover details");
            AccessTools.Method(MapFilter.SearchInput.GetType(), "Select").Invoke(MapFilter.SearchInput, null);
            AccessTools.Method(MapFilter.SearchInput.GetType(), "ActivateInputFieldInternal").Invoke(MapFilter.SearchInput, null);
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_search_focus", MapFilter.SearchFocused && Hidden(),
                "search focus precondition/hide failed");
            MapFilter.DefocusSearch();
            PinTooltip.UpdateAt(map, point);
            map.SetMapMode(Minimap.MapMode.Small);
            Check(run, "pintooltip_ui_close", Hidden(), "leaving the large map did not hide the tooltip immediately");
            PinTooltip.UpdateAt(map, point);
            Check(run, "pintooltip_ui_minimap", Hidden(), "minimap allowed tooltip rendering");
        }

        private static IEnumerator CheckPointer(FeatureRun run, Minimap map, Minimap.PinData pin,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings)
        {
            map.SetMapMode(Minimap.MapMode.Large);
            AccessTools.Field(typeof(Minimap), "m_mapOffset").SetValue(map, pin.m_pos - Player.m_localPlayer.transform.position);
            AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { pin.m_pos });
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases").Invoke(null, null);
            Map.RefreshPins();
            yield return null;
            if (!run.IsCurrent) yield break;
            if (Cursor.lockState == CursorLockMode.Locked || !pin.m_uiElement || !pin.m_uiElement.gameObject.activeInHierarchy)
            {
                run.Skip("pintooltip_real_pointer", "cursor/marker precondition unavailable: cursor is locked or test marker is not visible; real-hover check remains manual");
                yield break;
            }
            var point = pin.m_uiElement.position;
            ZInput.SetMousePosition(point);
            yield return null;
            yield return null;
            if (!run.IsCurrent) yield break;
            if (Vector2.Distance(ZInput.pointerPosition, point) > 2f)
            {
                run.Skip("pintooltip_real_pointer", "ZInput.SetMousePosition did not drive pointerPosition to the marker; real-hover check remains manual");
                yield break;
            }
            Check(run, "pintooltip_real_show", PinTooltip.Root && PinTooltip.Root.activeInHierarchy &&
                PinTooltip.HasTopicAndText &&
                ReferenceEquals(PinTooltip.HoveredPin, pin) && Text(PinTooltip.Root, "Topic") == pin.m_name &&
                Text(PinTooltip.Root, "Text") == PinTooltipContent.Build(pin).Text,
                "real cursor reached the marker but Minimap.Update did not show matching tooltip content");
            var off = point + new Vector3(150f, 150f);
            var world = (Vector3)AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint").Invoke(map, new object[] { off });
            var radius = (float)AccessTools.Field(typeof(Minimap), "m_removeRadius").GetValue(map) *
                ((float)AccessTools.Field(typeof(Minimap), "m_largeZoom").GetValue(map) * 2f);
            if (Map.GetClosestPin(world, radius) != null)
                run.Fail("pintooltip_real_leave", "empty hover point precondition failed: another visible pin is within the radius");
            else
            {
                ZInput.SetMousePosition(off);
                yield return null;
                yield return null;
                if (!run.IsCurrent) yield break;
                Check(run, "pintooltip_real_leave", Vector2.Distance(ZInput.pointerPosition, off) <= 2f && Hidden(),
                    "real cursor off the pins did not hide the tooltip, or pointer warp precondition failed");
            }
            ZInput.SetMousePosition(point);
            yield return null;
            yield return null;
            if (!run.IsCurrent) yield break;
            Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, false);
            yield return null;
            if (!run.IsCurrent) yield break;
            Check(run, "pintooltip_real_option_off", Hidden(), "real hover while enhancements are off showed a tooltip");
            Write(run, settings, MappingConfig.EnableMapEnhancementsEntry, true);
            yield return null;
            if (!run.IsCurrent) yield break;
            Check(run, "pintooltip_real_option_on", PinTooltip.Root && PinTooltip.Root.activeInHierarchy &&
                ReferenceEquals(PinTooltip.HoveredPin, pin), "real hover did not resume when enhancements were enabled");
            map.SetMapMode(Minimap.MapMode.Small);
            yield return null;
            if (!run.IsCurrent) yield break;
            Check(run, "pintooltip_real_close", Hidden(), "real update left a tooltip after closing the large map");
        }

        private static string Text(GameObject root, string name)
        {
            var type = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
            return (string)AccessTools.Property(type, "text").GetValue(Utils.FindChild(root.transform, name).GetComponent(type));
        }

        internal static bool ActivateExisting(Minimap map, out string precondition)
        {
            var pin = Navigation.TemporaryMarker;
            if (pin == null) { precondition = "navigation test marker is missing"; return false; }
            AccessTools.Method(typeof(Minimap), "CenterMap").Invoke(map, new object[] { pin.m_pos });
            AccessTools.Method(AccessTools.TypeByName("UnityEngine.Canvas"), "ForceUpdateCanvases").Invoke(null, null);
            Map.RefreshPins();
            if (!pin.m_uiElement || !pin.m_uiElement.gameObject.activeInHierarchy)
            { precondition = "navigation test marker has no visible large-map element"; return false; }
            PinTooltip.UpdateAt(map, pin.m_uiElement.position);
            precondition = "hover at navigation test marker screen point with both Topic/Text components";
            return PinTooltip.Root && PinTooltip.HasTopicAndText && PinTooltip.Root.activeInHierarchy && ReferenceEquals(PinTooltip.HoveredPin, pin);
        }

        private static bool Hidden() => (!PinTooltip.Root || !PinTooltip.Root.activeSelf) && PinTooltip.HoveredPin == null;

        private static void StubClient(Harmony probe)
        {
            _clientReads = 0;
            probe.Patch(AccessTools.Method(typeof(PinTooltipContent), nameof(PinTooltipContent.IsServer)),
                prefix: new HarmonyMethod(typeof(PinTooltipFeatureSmoke), nameof(ReportClient)));
        }

        private static bool ReportClient(ref bool __result)
        {
            _clientReads++;
            __result = false;
            return false;
        }

        private static void RestoreServer(FeatureRun run, Harmony probe, string section)
        {
            probe.UnpatchSelf();
            var reads = _clientReads;
            Check(run, "pintooltip_role_restored_" + section,
                PinTooltipContent.IsServer() && ZNet.instance.IsServer() && _clientReads == reads,
                "client role probe restoration failed: content reader must use the real server role after unpatching");
        }

        private static bool ClassifySavedPortal(Minimap.PinData pin, ref PinCategory category, ref bool __result)
        {
            if (!ReferenceEquals(pin, _savedPortalPin)) return true;
            category = PinCategory.Portal;
            __result = true;
            return false;
        }

        private static bool HideTestInstance(ZDOID id, ref GameObject __result)
        {
            if (id != _cachedId) return true;
            __result = null;
            return false;
        }

        private static GameObject Spawn(string name, Vector3 position, List<GameObject> created)
        {
            var prefab = ZNetScene.instance.GetPrefab(name);
            if (!prefab) return null;
            var go = UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            SmokeSession.TrackSpawn(go);
            created.Add(go);
            var view = go.GetComponent<ZNetView>();
            if (!view || !view.IsValid()) return null;
            view.GetZDO().Persistent = false;
            return go;
        }

        private static void Remove(GameObject go)
        {
            if (!go) return;
            go.SetActive(false);
            if (SmokeSession.DestroyTestObject(go, 0, "pin tooltip smoke")) SmokeSession.ReleaseSpawn(go);
        }

        private static Minimap.PinData AddPin(Minimap map, List<Minimap.PinData> pins, Vector3 pos, string name)
        {
            var pin = Map.AddPin(pos, name, false, new Target { name = name });
            pins.Add(pin);
            return pin;
        }

        private static void AssociateDynamic(Minimap.PinData pin, ZDOID id)
        {
            ((IDictionary)AccessTools.Field(typeof(DynamicObjectMapping), "PinDataCache").GetValue(null))[id] = pin;
            ((IDictionary)AccessTools.Field(typeof(DynamicObjectMapping), "PinKeyCache").GetValue(null))[pin] = id;
        }

        private static void AssociateStatic(Minimap.PinData pin, ZDOID id, PinKind kind)
        {
            CacheStatic(pin, new StaticObjectMapping.MapPinIdentify(id), kind, PinSourceDomain.Component);
        }

        private static void AssociateLocation(Minimap.PinData pin)
        {
            CacheStatic(pin, new StaticObjectMapping.MapPinIdentify(pin.m_pos), PinKind.Spot, PinSourceDomain.Location);
        }

        private static void CacheStatic(Minimap.PinData pin, StaticObjectMapping.MapPinIdentify key, PinKind kind, PinSourceDomain domain)
        {
            AccessTools.Method(typeof(StaticObjectMapping), "CachePin", new[]
            {
                typeof(StaticObjectMapping.MapPinIdentify), typeof(Minimap.PinData), typeof(PinKind),
                typeof(string), typeof(string), typeof(PinSourceDomain)
            }).Invoke(null, new object[] { key, pin, kind, "smoke", "smoke", domain });
        }

        private static string Translate(string key) => Automatics.L10N.Translate(key);
        private static string Line(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture,
            Translate("@text_automatic_mapping_tooltip_" + key), args);
        private static string Duration(double seconds) => Line("duration", (long)Math.Ceiling(seconds / 60d));

        private static void Write(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> settings,
            ConfigEntryBase entry, object value)
        {
            if (!SmokeSession.WriteSetting(run.RunId, settings[entry], value, out var refusal))
                throw new InvalidOperationException(entry.Definition.Key + " setting write precondition failed: " + refusal);
        }

        private static void Check(FeatureRun run, string name, bool passed, string failure)
        {
            if (passed) run.Pass(name);
            else run.Fail(name, failure);
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
