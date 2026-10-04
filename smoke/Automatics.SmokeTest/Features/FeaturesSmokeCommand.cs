using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("mining", MiningFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("planting", PlantingFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("farming", FarmingFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("guide", GuideFeatureSmoke.Run),
            new KeyValuePair<string, Func<FeatureRun, IEnumerator>>("supply", SupplyFeatureSmoke.Run)
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

    // Runs synchronously after opening the map so the scans cannot retire the synthetic
    // associations. Only this section's pins are removed, including its saved test pins.
    internal static class MapFilterFeatureSmoke
    {
        internal static IEnumerator Run(FeatureRun run)
        {
            var map = Minimap.instance;
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !map || !player || Game.m_noMap || !MappingModule.Running)
            {
                run.Skip("mapfilter_section", "requires a loaded world, an available map and Automatic Mapping running");
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
                    "control creation precondition failed: expected ten categories, search field and Pin search under m_largeRoot");
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
                    "module enable precondition/reset failed: no input, non-empty text or Pin search on");
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
            MapFilter.SetSearchText("  SMOKE FLORA  ");
            Check(run, "mapfilter_search_value_changed", MapFilter.SearchText == "  SMOKE FLORA  " && MapFilter.HasSearch,
                "the cloned input's value-changed callback did not record the search");
            CheckModes(run, map, origin, pins, "search_large", pin => map.m_mode == Minimap.MapMode.Small ||
                pin == flora || pin == manual || pin == shared);
            MapFilter.SearchToggle.InvokeClick();
            Check(run, "mapfilter_search_toggle_callback", MapFilter.SearchPinned && MapFilter.SearchToggle.Shown &&
                MapFilter.SearchToggle.Tint == Color.white, "Pin search's callback did not turn on its session value and tint");
            CheckModes(run, map, origin, pins, "search_pinned", pin => pin == flora || pin == manual || pin == shared);

            var originalName = flora.m_name;
            flora.m_name = "Automatics smoke renamed";
            CheckModes(run, map, origin, pins, "search_renamed_pin", pin => pin == manual || pin == shared);
            flora.m_name = originalName;
            CheckModes(run, map, origin, pins, "search_name_restored", pin => pin == flora || pin == manual || pin == shared);

            AccessTools.Method(typeof(Navigation), "SetTarget").Invoke(null, new object[] { mineral });
            CheckModes(run, map, origin, pins, "search_navigation_kept",
                pin => pin == flora || pin == mineral || pin == manual || pin == shared);
            for (var i = 0; i < 3; i++)
            {
                AccessTools.Field(typeof(Navigation), "_nextTargetPinValidationTime").SetValue(null, 0f);
                AccessTools.Method(typeof(Navigation), "ValidateTargetPin").Invoke(null, null);
            }
            Check(run, "mapfilter_search_navigation_survives", Navigation.TargetPin == mineral,
                "search cleared the non-matching navigation target during validation");
            Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList(new[] { "Flora" }));
            CheckModes(run, map, origin, pins, "search_category_combined",
                pin => pin == mineral || pin == manual || pin == shared);
            Write(run, settings, MappingConfig.MapFilterHiddenCategoriesEntry, new StringList());
            Navigation.ClearTarget();
            MapFilter.SetSearchText("  " + labels[saved].ToUpperInvariant() + "  ");
            Check(run, "mapfilter_search_localized_precondition", labels[saved].Length != 0,
                "the saved registry pin has no displayed localized label");
            CheckModes(run, map, origin, pins, "search_localized_case_insensitive",
                pin => pin == manual || pin == shared || labels[pin].Contains(labels[saved]));
            MapFilter.SetSearchText("nothing matches this smoke query");
            CheckModes(run, map, origin, pins, "search_nonmatching", pin => pin == manual || pin == shared);
            MapFilter.SetSearchText("   ");
            CheckModes(run, map, origin, pins, "search_whitespace_restores", _ => true);
            MapFilter.SetSearchText("");
            MapFilter.SearchToggle.InvokeClick();
            Check(run, "mapfilter_search_toggle_off", !MapFilter.SearchPinned && MapFilter.SearchToggle.Tint == Color.gray,
                "Pin search's callback did not restore its off value and tint");
            CheckModes(run, map, origin, pins, "search_clear_restores", _ => true);
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
                patcher.Patch(AccessTools.Method(typeof(KeyboardShortcut), "IsDown"),
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
            if (!MappingConfig.EnableAutomaticMapping)
            {
                run.Skip("mapfilter_unexplored_combined", "enable_automatic_mapping precondition is off");
                return;
            }
            var unexplored = new Vector3(20000f, origin.y, 20000f);
            if (!Map.IsExplored(flora.m_pos) || Map.IsExplored(unexplored))
            {
                run.Fail("mapfilter_unexplored_combined", "exploration precondition failed: need an explored test pin and an unexplored point");
                return;
            }
            var originalMineral = mineral.m_pos;
            var originalManual = manual.m_pos;
            try
            {
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
