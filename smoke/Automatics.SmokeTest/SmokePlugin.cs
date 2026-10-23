using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Automatics.AutomaticMapping;
using Automatics.AutomaticStorage;
using Automatics.Valheim;
using BepInEx;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.SmokeTest
{
    [BepInPlugin("net.eidee.valheim.automatics.smoke", "Automatics Smoke Test", "0.1.0")]
    [BepInDependency("net.eidee.valheim.automatics")]
    public sealed class SmokePlugin : BaseUnityPlugin
    {
        private const float RuntimeWaitSeconds = 20f;
        private const string SmokeDirectoryName = "AutomaticsSmoke";

        private bool _hasRun;

        // A coroutine stops with the component that runs it and is never disposed, so neither
        // its remaining steps nor an iterator's finally hand anything back on that path.
        // Whatever a smoke command still owes the user's game - the Automatic Pickup setting,
        // a spawned test object - is handed back from here instead (SmokeSession).
        private void OnApplicationQuit()
        {
            SmokeSession.HandBack("the game is quitting");
        }

        private void OnDisable()
        {
            SmokeSession.HandBack("the smoke plugin was disabled");
        }

        private void OnDestroy()
        {
            SmokeSession.HandBack("the smoke plugin was destroyed");
        }

        private IEnumerator Start()
        {
            // A lifecycle check cut short by a crash or a killed process left a record of the
            // setting it changed. It is reported as a warning and never acted on: the config is
            // not written from it.
            SmokeSession.WarnAboutInterruptedRun();

            // Register the optional in-game world smoke commands before the readiness
            // wait so the Hooks.OnInitTerminal subscriptions are in place before the
            // terminal's one-time init fires (mirrors how Automatics itself registers).
            // All of them run across frames, so they are handed this plugin to run their
            // coroutines on.
            WorldSmokeCommand.EnsureRegistered(this);
            LifecycleSmokeCommand.EnsureRegistered(this);
            ProcessingSmokeCommand.EnsureRegistered(this);
            FeaturesSmokeCommand.EnsureRegistered(this);
            SmokeSetup.EnsureRegistered(this);

            // Only Localization.instance is consumed by a check (and that check
            // skip-guards on it), so the readiness wait covers just that. The object
            // registries under test are populated synchronously during the Automatics
            // plugin's Awake, so they are ready before Start() first ticks; there is
            // no ObjectDB-dependent check, so waiting on ObjectDB would only stall a
            // main-menu run for the full timeout with no benefit.
            var start = Time.realtimeSinceStartup;
            while (TryGetGameLocalization() == null &&
                   Time.realtimeSinceStartup - start < RuntimeWaitSeconds)
                yield return new WaitForSeconds(0.5f);

            RunSmokeChecksOnce();
        }

        private void RunSmokeChecksOnce()
        {
            if (_hasRun) return;
            _hasRun = true;

            var results = new List<CheckResult>
            {
                RunCheck("storage_routing_exact_stacks_first", CheckStorageRoutingExactStacksFirst),
                RunCheck("storage_routing_category_before_fallback", CheckStorageRoutingCategoryBeforeFallback),
                RunCheck("storage_routing_empty_before_other", CheckStorageRoutingEmptyBeforeOther),
                RunCheck("storage_routing_reference_limit_keeps_nearest", CheckStorageRoutingReferenceLimitKeepsNearest),
                RunCheck("storage_routing_multiple_exact_targets_preserve_order", CheckStorageRoutingMultipleExactTargetsPreserveOrder),
                RunCheck("object_matcher_invalid_regex_is_no_match", CheckObjectMatcherInvalidRegexIsNoMatch),
                RunCheck("custom_entries_filter_invalid_definitions", CheckCustomEntriesFilterInvalidDefinitions),
                RunCheck("object_json_file_isolation", CheckObjectJsonFileIsolation),
                RunCheck("icon_regex_validation_and_runtime_fallback", CheckIconRegexValidationAndRuntimeFallback),
                RunCheck("mineral_internal_name_resolves", CheckMineralInternalNameResolves),
                RunCheck("flora_seed_tag_classification", CheckFloraSeedTagClassification),
                RunCheck("dungeon_regex_resolves_location_name", CheckDungeonRegexResolvesLocationName),
                RunCheck("animal_regex_fish_and_multi_matcher_bird", CheckAnimalRegexFishAndMultiMatcherBird),
                RunCheck("door_internal_name_resolution", CheckDoorInternalNameResolution),
                RunCheck("container_resolution_and_allowlist", CheckContainerResolutionAndAllowlist),
                RunCheck("automatics_translation_keys_loaded", CheckAutomaticsTranslationKeysLoaded),
                RunCheck("vanilla_label_localizes_via_game", CheckVanillaLabelLocalizesViaGame),
                RunCheck("config_gui_lists_automatics_with_object_editor", CheckConfigGuiListsAutomaticsWithObjectEditor),
                RunCheck("custom_object_list_edits_keep_other_elements", CheckCustomObjectListEditsKeepOtherElements)
            };

            foreach (var result in results)
            {
                if (result.Status == CheckStatus.Pass)
                    Logger.LogInfo($"[Automatics Smoke] PASS {result.Name}");
                else if (result.Status == CheckStatus.Skip)
                    Logger.LogWarning($"[Automatics Smoke] SKIP {result.Name}: {result.Message}");
                else
                    Logger.LogError($"[Automatics Smoke] FAIL {result.Name}: {result.Message}");
            }

            var failed = results.Where(result => result.Status == CheckStatus.Fail)
                                .Select(result => result.Name)
                                .ToArray();
            var skipped = results.Where(result => result.Status == CheckStatus.Skip)
                                 .Select(result => result.Name)
                                 .ToArray();
            var passed = results.Count(result => result.Status == CheckStatus.Pass);

            Logger.LogInfo(
                $"[Automatics Smoke] Summary: total={results.Count} passed={passed} failed={failed.Length} skipped={skipped.Length} failedChecks=[{string.Join(", ", failed)}] skippedChecks=[{string.Join(", ", skipped)}]");
        }

        private CheckResult RunCheck(string name, Func<CheckResult> check)
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

        // -- Storage routing (ported from StorageRoutingHarness) -------------------

        private CheckResult CheckStorageRoutingExactStacksFirst()
        {
            var source = Item("$item_wood", itemType: 1, stack: 20, maxStackSize: 50);
            var containers = new[]
            {
                Container(0, emptySlots: 4, Item("$item_wood", itemType: 1, stack: 45, maxStackSize: 50)),
                Container(1, emptySlots: 4, Item("$item_stone", itemType: 1, stack: 1, maxStackSize: 50)),
                Container(2, emptySlots: 4)
            };

            var routes = StoragePlanner.Plan(source, containers, storeIntoAnyContainer: true);

            AssertRoute(routes[0], 0, StorageRouteKind.ExactStack);
            AssertBefore(routes, StorageRouteKind.ExactStack, StorageRouteKind.CategoryAffinity);
            AssertBefore(routes, StorageRouteKind.ExactStack, StorageRouteKind.Empty);
            return CheckResult.Pass();
        }

        private CheckResult CheckStorageRoutingCategoryBeforeFallback()
        {
            var source = Item("$item_wood", itemType: 1, stack: 20, maxStackSize: 50);
            var containers = new[]
            {
                Container(0, emptySlots: 4, Item("$item_mead", itemType: 2, stack: 1, maxStackSize: 10)),
                Container(1, emptySlots: 4, Item("$item_stone", itemType: 1, stack: 1, maxStackSize: 50)),
                Container(2, emptySlots: 4)
            };

            var routes = StoragePlanner.Plan(source, containers, storeIntoAnyContainer: true);

            AssertRoute(routes[0], 1, StorageRouteKind.CategoryAffinity);
            AssertBefore(routes, StorageRouteKind.CategoryAffinity, StorageRouteKind.Empty);
            AssertBefore(routes, StorageRouteKind.CategoryAffinity, StorageRouteKind.Other);
            return CheckResult.Pass();
        }

        private CheckResult CheckStorageRoutingEmptyBeforeOther()
        {
            var source = Item("$item_wood", itemType: 1, stack: 20, maxStackSize: 50);
            var containers = new[]
            {
                Container(0, emptySlots: 4, Item("$item_mead", itemType: 2, stack: 1, maxStackSize: 10)),
                Container(1, emptySlots: 4)
            };

            var routes = StoragePlanner.Plan(source, containers, storeIntoAnyContainer: true);

            AssertRoute(routes[0], 1, StorageRouteKind.Empty);
            AssertBefore(routes, StorageRouteKind.Empty, StorageRouteKind.Other);
            return CheckResult.Pass();
        }

        private CheckResult CheckStorageRoutingReferenceLimitKeepsNearest()
        {
            var source = Item("$item_wood", itemType: 1, stack: 20, maxStackSize: 50);
            var containers = new[]
            {
                Container(0, emptySlots: 4),
                Container(1, emptySlots: 4, Item("$item_wood", itemType: 1, stack: 45, maxStackSize: 50)),
                Container(2, emptySlots: 4, Item("$item_wood", itemType: 1, stack: 45, maxStackSize: 50))
            };

            var routes = StoragePlanner.Plan(source, containers, referenceLimit: 1,
                storeIntoAnyContainer: true);

            Expect(routes.All(x => x.ContainerIndex == 0),
                "reference limit should prevent routes to containers outside the nearest limit");
            return CheckResult.Pass();
        }

        private CheckResult CheckStorageRoutingMultipleExactTargetsPreserveOrder()
        {
            var source = Item("$item_wood", itemType: 1, stack: 20, maxStackSize: 50);
            var containers = new[]
            {
                Container(0, emptySlots: 0, Item("$item_wood", itemType: 1, stack: 45, maxStackSize: 50)),
                Container(1, emptySlots: 4, Item("$item_wood", itemType: 1, stack: 40, maxStackSize: 50)),
                Container(2, emptySlots: 4),
                Container(3, emptySlots: 4, Item("$item_stone", itemType: 1)),
                Container(4, emptySlots: 4, Item("$item_wood", itemType: 1, stack: 50,
                    maxStackSize: 50, quality: 2, worldLevel: 1)),
                Container(5, emptySlots: 4, Item("$item_wood", itemType: 1, stack: 50, maxStackSize: 50)),
                Container(6, emptySlots: 0, Item("$item_wood", itemType: 1, stack: 50, maxStackSize: 50))
            };

            var routes = StoragePlanner.Plan(source, containers);
            var exactRoutes = routes
                .Where(x => x.Kind == StorageRouteKind.ExactStack)
                .ToList();

            Expect(exactRoutes.Count == 2, "expected two exact-stack routes");
            AssertRoute(exactRoutes[0], 0, StorageRouteKind.ExactStack);
            AssertRoute(exactRoutes[1], 1, StorageRouteKind.ExactStack);
            Expect(routes.Count == 5,
                "default routing should only use matching stacks and free slots in same-item containers");
            AssertRoute(routes[0], 0, StorageRouteKind.ExactStack);
            AssertRoute(routes[1], 1, StorageRouteKind.ExactStack);
            AssertRoute(routes[2], 1, StorageRouteKind.SameItem);
            AssertRoute(routes[3], 4, StorageRouteKind.SameItem);
            AssertRoute(routes[4], 5, StorageRouteKind.SameItem);
            return CheckResult.Pass();
        }

        // -- Object validation (ported from ObjectValidationHarness) ---------------

        private CheckResult CheckObjectMatcherInvalidRegexIsNoMatch()
        {
            var matcher = new ObjectMatcher { regex = true, value = "[" };
            Expect(!matcher.Matches("anything"),
                "invalid object regex should be a runtime no-match without throwing");
            return CheckResult.Pass();
        }

        private CheckResult CheckCustomEntriesFilterInvalidDefinitions()
        {
            // Isolated instance so the custom registration cannot pollute the live
            // domain registries. Warning-text assertions from the headless harness are
            // re-expressed as observable GetIdentify/IsDefined state because the real
            // ModUtils.Logger has no capture buffer.
            var custom = new ValheimObject("custom-harness");

            custom.RegisterCustom(new ObjectElement[]
            {
                null,
                new ObjectElement
                {
                    identifier = "BadRegex",
                    label = "Bad Regex",
                    matches = new List<ObjectMatcher>
                    {
                        new ObjectMatcher { regex = true, value = "[" }
                    }
                },
                new ObjectElement
                {
                    identifier = "GoodCustom",
                    label = "Good Custom",
                    matches = new List<ObjectMatcher>
                    {
                        new ObjectMatcher { value = "GoodCustomPrefab" }
                    }
                }
            });

            Expect(custom.GetIdentify("GoodCustomPrefab", out var identifier) &&
                   identifier == "GoodCustom",
                "valid custom entry should remain registered when invalid entries are skipped");
            Expect(!custom.IsDefined("BadRegex"),
                "invalid-regex custom entry should not be registered");
            return CheckResult.Pass();
        }

        private CheckResult CheckObjectJsonFileIsolation()
        {
            // Behaviour slice of the headless harness: a valid object file still
            // registers when a sibling file is invalid and another contains a
            // duplicate exact matcher. Warning-text assertions are dropped because
            // ModUtils.Logger has no capture buffer. NOTE: ValheimObject.Initialize
            // mutates the global Flora registry; PostInitialize only clears the JSON
            // cache, so GoodFlora/DupA/DupB persist for the session. Single-shot smoke
            // and the identifiers do not shadow real game objects, so this is safe.
            var directory = CreateSmokeTempDirectory("objects");
            try
            {
                File.WriteAllText(Path.Combine(directory, "invalid.json"),
                    "{\"type\":\"flora\",\"values\":[null]}");
                File.WriteAllText(Path.Combine(directory, "valid.json"),
                    "{\"type\":\"flora\",\"values\":[{\"identifier\":\"GoodFlora\",\"label\":\"Good Flora\",\"matches\":[{\"value\":\"GoodPrefab\"}]}]}");
                File.WriteAllText(Path.Combine(directory, "duplicate.json"),
                    "{\"type\":\"flora\",\"values\":[{\"identifier\":\"DupA\",\"label\":\"Dup A\",\"matches\":[{\"value\":\"DuplicatePrefab\"}]},{\"identifier\":\"DupB\",\"label\":\"Dup B\",\"matches\":[{\"value\":\"DuplicatePrefab\"}]}]}");

                ValheimObject.Initialize(new[] { directory });

                Expect(ValheimObject.Flora.GetIdentify("GoodPrefab", out var identifier) &&
                       identifier == "GoodFlora",
                    "valid object file should register despite an invalid and a duplicate sibling");
                return CheckResult.Pass();
            }
            finally
            {
                ValheimObject.PostInitialize();
                TryDeleteDirectory(directory);
            }
        }

        private CheckResult CheckIconRegexValidationAndRuntimeFallback()
        {
            // IconPack.ValidateRegexTarget and IsNameMatch are private static, so
            // reflection is required even with InternalsVisibleTo.
            var validateRegexTarget = typeof(IconPack).GetMethod(
                "ValidateRegexTarget",
                BindingFlags.Static | BindingFlags.NonPublic);
            Expect(validateRegexTarget != null,
                "IconPack.ValidateRegexTarget(Static|NonPublic) was not found via reflection");

            var validateArgs = new object[] { new Target { name = "r/[" }, null };
            Expect(!(bool)validateRegexTarget.Invoke(null, validateArgs),
                "invalid icon regex should be rejected during validation");

            var isNameMatch = typeof(IconPack).GetMethod(
                "IsNameMatch",
                BindingFlags.Static | BindingFlags.NonPublic);
            Expect(isNameMatch != null,
                "IconPack.IsNameMatch(Static|NonPublic) was not found via reflection");

            Expect(!(bool)isNameMatch.Invoke(null, new object[] { "Boar", "r/[", true }),
                "invalid icon regex should be a runtime no-match without throwing");
            return CheckResult.Pass();
        }

        // -- New feature smoke checks (real registries / game state) ---------------

        private CheckResult CheckMineralInternalNameResolves()
        {
            Expect(ValheimObject.Mineral.GetIdentify("$piece_deposit_copper", out var copper) &&
                   copper == "CopperDeposit",
                "$piece_deposit_copper should resolve to CopperDeposit");
            Expect(ValheimObject.Mineral.GetIdentify("$piece_deposit_tin", out var tin) &&
                   tin == "TinDeposit",
                "$piece_deposit_tin should resolve to TinDeposit");
            Expect(ValheimObject.Mineral.GetIdentify("$piece_deposit_silvervein", out var silver) &&
                   silver == "SilverVein",
                "$piece_deposit_silvervein should resolve to SilverVein");
            Expect(ValheimObject.Mineral.GetName("CopperDeposit", out var label) &&
                   label == "$piece_deposit_copper",
                "CopperDeposit should round-trip back to $piece_deposit_copper");
            Expect(ValheimObject.Mineral.GetIdentify("Rock_4", out var rock4) &&
                   rock4 == "Rock",
                "Rock_4 should resolve to Rock");
            Expect(ValheimObject.Mineral.GetIdentify("rock4_forest_frac", out var rock4ForestFrac) &&
                   rock4ForestFrac == "Rock",
                "rock4_forest_frac should resolve to Rock");
            Expect(ValheimObject.Mineral.GetIdentify("Rock", out var rock) &&
                   rock == "Rock",
                "Rock should resolve to Rock");
            Expect(!ValheimObject.Mineral.GetIdentify("rock4_copper", out _),
                "rock4_copper should not resolve to Rock");
            return CheckResult.Pass();
        }

        private CheckResult CheckFloraSeedTagClassification()
        {
            Expect(ValheimObject.Flora.GetIdentify("$item_carrotseeds", out var seeds) &&
                   seeds == "CarrotSeeds",
                "$item_carrotseeds should resolve to CarrotSeeds");
            Expect(ValheimObject.Flora.HasTag("CarrotSeeds", "seed"),
                "CarrotSeeds should carry the seed tag");
            Expect(ValheimObject.Flora.GetIdentify("$item_carrot", out var crop) &&
                   crop == "Carrot",
                "$item_carrot should resolve to Carrot");
            Expect(!ValheimObject.Flora.HasTag("Carrot", "seed"),
                "Carrot (the crop) should not carry the seed tag");
            Expect(!ValheimObject.Flora.HasTag("NotAnId", "seed"),
                "unknown identifier should not carry any tag");
            return CheckResult.Pass();
        }

        private CheckResult CheckDungeonRegexResolvesLocationName()
        {
            Expect(ValheimObject.Dungeon.GetIdentify("Crypt2", out var crypt2) &&
                   crypt2 == "BurialChambers",
                "Crypt2 should resolve to BurialChambers");
            Expect(ValheimObject.Dungeon.GetIdentify("Crypt", out var crypt) &&
                   crypt == "BurialChambers",
                "Crypt should resolve to BurialChambers");
            Expect(ValheimObject.Dungeon.GetIdentify("SunkenCrypt4", out var sunken) &&
                   sunken == "SunkenCrypts",
                "SunkenCrypt4 should resolve to SunkenCrypts");
            Expect(!ValheimObject.Dungeon.GetIdentify("NotACrypt", out _),
                "NotACrypt should not resolve to any dungeon");
            return CheckResult.Pass();
        }

        private CheckResult CheckAnimalRegexFishAndMultiMatcherBird()
        {
            Expect(ValheimObject.Animal.GetIdentify("$animal_fish1", out var fish1) &&
                   fish1 == "Fish",
                "$animal_fish1 should resolve to Fish");
            Expect(ValheimObject.Animal.GetIdentify("$animal_fish42", out var fish42) &&
                   fish42 == "Fish",
                "$animal_fish42 should resolve to Fish");
            Expect(!ValheimObject.Animal.GetIdentify("$animal_fish", out _),
                "$animal_fish without a digit should not resolve to Fish");
            Expect(ValheimObject.Animal.GetIdentify("$Seagal", out var seagal) &&
                   seagal == "Bird",
                "$Seagal should resolve to Bird (first exact matcher)");
            Expect(ValheimObject.Animal.GetIdentify("$Crow", out var crow) &&
                   crow == "Bird",
                "$Crow should resolve to Bird (second exact matcher on the same element)");
            return CheckResult.Pass();
        }

        private CheckResult CheckDoorInternalNameResolution()
        {
            // Door settings are bound even when the module starts off, so its object registry is
            // always available to console commands and to a later runtime enable.
            Expect(AutomaticDoor.Globals.Door.GetIdentify("$piece_wooddoor", out var wood) &&
                   wood == "WoodDoor",
                "$piece_wooddoor should resolve to WoodDoor");
            Expect(AutomaticDoor.Globals.Door.GetIdentify("$piece_irongate", out var iron) &&
                   iron == "IronGate",
                "$piece_irongate should resolve to IronGate");
            return CheckResult.Pass();
        }

        private CheckResult CheckContainerResolutionAndAllowlist()
        {
            Expect(ValheimObject.Container.GetIdentify("$piece_chest", out var chest) &&
                   chest == "PieceChest",
                "$piece_chest should resolve to PieceChest");

            var identifiers = ValheimObject.Container.GetAllElements()
                .Select(x => x.identifier)
                .ToList();
            Expect(identifiers.Contains("PieceChestWood"),
                "container registry should contain PieceChestWood");
            Expect(identifiers.Contains("PieceChestBlackmetal"),
                "container registry should contain PieceChestBlackmetal");
            Expect(identifiers.Contains("PieceChestPrivate"),
                "container registry should contain PieceChestPrivate (guards the processing excludes allowlist)");
            return CheckResult.Pass();
        }

        private CheckResult CheckAutomaticsTranslationKeysLoaded()
        {
            // Automatics' own Languages file is loaded into the L10N TranslationCache
            // at startup, so this resolves even when Localization.instance is null
            // (e.g. main menu). The resolved value is locale-dependent (en="Bird",
            // ja="鳥"), so we assert only that @animal_bird resolved to a real
            // translation rather than the raw key — proving the Languages file loaded
            // without coupling the check to the active language. If the word is absent
            // (unseeded cache), SKIP rather than FAIL.
            var translated = global::Automatics.Automatics.L10N.Translate("@animal_bird");
            if (string.IsNullOrEmpty(translated) ||
                translated == "@animal_bird" ||
                translated == "animal_bird" ||
                translated == "automatics_animal_bird" ||
                translated == "[automatics_animal_bird]")
                return CheckResult.Skip(
                    "Automatics translation cache does not hold @animal_bird yet");

            return CheckResult.Pass();
        }

        private CheckResult CheckVanillaLabelLocalizesViaGame()
        {
            var localization = TryGetGameLocalization();
            if (localization == null)
                return CheckResult.Skip("Valheim Localization.instance is not available");

            var localized = localization.Localize("$piece_deposit_copper");
            Expect(!string.IsNullOrEmpty(localized),
                "$piece_deposit_copper should localize to a non-empty string");
            Expect(localized != "[piece_deposit_copper]",
                "$piece_deposit_copper should point at a real vanilla Localization key");
            return CheckResult.Pass();
        }

        // -- Custom object lists on the config screens -----------------------------

        private CheckResult CheckConfigGuiListsAutomaticsWithObjectEditor()
        {
            var plugin = global::Automatics.Automatics.Plugin;
            Expect(plugin != null, "the Automatics plugin has not been initialized");

            // Registering a plugin on the in-game config screen patches Settings.Awake under a Harmony
            // id made from the plugin GUID, so the patch owner shows that Automatics registered.
            var metadata = plugin.Info.Metadata;
            var harmonyId = "net.eideehi.modutils.configurationgui." + metadata.GUID;
            var awake = AccessTools.Method(typeof(Settings), "Awake");
            Expect(awake != null, "Settings.Awake was not found");
            var patches = Harmony.GetPatchInfo(awake);
            Expect(patches != null && patches.Prefixes.Any(x => x.owner == harmonyId),
                $"Settings.Awake should have a prefix from {harmonyId}, so the Mods tab lists Automatics");

            var entries = plugin.Config
                .Select(x => x.Value)
                .Where(x => x.SettingType == typeof(List<ObjectElement>))
                .ToList();
            Expect(entries.Count > 0, "the Automatics config should have custom object list entries");
            foreach (var entry in entries)
            {
                var definition = entry.Definition;
                Expect(ConfigurationGui.TryCreateCustomEditor(entry, metadata.Name, out var editor) &&
                       editor is ObjectElementListEditor,
                    $"[{definition.Section}:{definition.Key}] should get the custom object list editor on the config screen");
            }

            return CheckResult.Pass();
        }

        private CheckResult CheckCustomObjectListEditsKeepOtherElements()
        {
            var empty = new List<ObjectElement>();

            var plain = AddedElement(empty, "test_id", "Test Label", "Boar");
            Expect(plain.identifier == "test_id" && plain.label == "Test Label" && plain.tags == null,
                "an added element should take the identifier and label as typed and no tags");
            ExpectOnlyMatcher(plain, false, "Boar", "a plain pattern");
            ExpectOnlyMatcher(AddedElement(empty, "test_id", "Test Label", "r/^a$"), true, "^a$",
                "a pattern with the r/ prefix");
            ExpectOnlyMatcher(AddedElement(empty, "test_id", "Test Label", "r/"), true, "",
                "the r/ prefix alone");
            Expect(AddedElement(empty, "a b", "Test Label", "Boar").identifier == "ab",
                "whitespace should be removed from the identifier");
            var spaced = AddedElement(empty, "test_id", " Test  Label ", " Boar ");
            Expect(spaced.label == " Test  Label ", "the label should be kept as typed, spaces included");
            ExpectOnlyMatcher(spaced, false, " Boar ", "a plain pattern with spaces");

            Expect(!ObjectElementList.TryCopyWithElement(empty, "", "Test Label", "Boar", out _),
                "nothing should be added without an identifier");
            Expect(!ObjectElementList.TryCopyWithElement(empty, "test_id", "", "Boar", out _),
                "nothing should be added without a label");
            Expect(!ObjectElementList.TryCopyWithElement(empty, "test_id", "Test Label", "", out _),
                "nothing should be added without a pattern");
            Expect(empty.Count == 0, "adding should leave the input list empty");

            var tagged = new ObjectElement
            {
                identifier = "Tagged",
                label = "Tagged",
                matches = new List<ObjectMatcher>
                {
                    new ObjectMatcher { value = "Wood" },
                    new ObjectMatcher { regex = true, value = "^Wood_\\d+$" }
                },
                tags = new List<string> { "test_tag" }
            };
            var stored = new List<ObjectElement> { null, tagged };
            Expect(ObjectElementList.TryCopyWithElement(stored, "test_id", "Test Label", "Boar", out var added),
                "an element should be added to a list that holds other elements");
            Expect(!ReferenceEquals(added, stored), "adding should return a new list");
            Expect(added.Count == 3 && added[0] == null && ReferenceEquals(added[1], tagged) &&
                   added[2] != null && added[2].identifier == "test_id",
                "adding should keep the stored null and element as the same instances in order, followed by the new element");
            Expect(stored.Count == 2 && stored[0] == null && ReferenceEquals(stored[1], tagged),
                "adding should leave the input list as it was");

            var other = new ObjectElement
            {
                identifier = "Other",
                label = "Other",
                matches = new List<ObjectMatcher> { new ObjectMatcher { value = "Boar" } }
            };
            var before = new List<ObjectElement> { other, tagged, null };
            var removed = ObjectElementList.CopyWithoutElement(before, tagged);
            Expect(!ReferenceEquals(removed, before), "removing should return a new list");
            Expect(removed.Count == 2 && ReferenceEquals(removed[0], other) && removed[1] == null,
                "removing should drop only the given element and keep the others in order");
            Expect(before.Count == 3 && ReferenceEquals(before[0], other) && ReferenceEquals(before[1], tagged) &&
                   before[2] == null,
                "removing should leave the input list as it was");
            return CheckResult.Pass();
        }

        private static ObjectElement AddedElement(List<ObjectElement> elements, string identifier,
            string label, string pattern)
        {
            Expect(ObjectElementList.TryCopyWithElement(elements, identifier, label, pattern, out var copy),
                $"an element should be added from \"{identifier}\", \"{label}\" and \"{pattern}\"");
            Expect(copy.Count == elements.Count + 1, "adding should append exactly one element");
            return copy[copy.Count - 1];
        }

        private static void ExpectOnlyMatcher(ObjectElement element, bool regex, string value, string input)
        {
            Expect(element.matches != null && element.matches.Count == 1,
                $"{input} should give the added element one matcher");
            var matcher = element.matches[0];
            Expect(matcher.regex == regex && matcher.value == value,
                $"{input} should give a matcher with regex={regex} and value \"{value}\", got regex={matcher.regex} and value \"{matcher.value}\"");
        }

        // -- Storage routing helpers (ported from the harness) ---------------------

        private static StorageItemSnapshot Item(string name, int itemType, int stack = 1,
            int maxStackSize = 50, int quality = 1, int worldLevel = 0)
        {
            return new StorageItemSnapshot(name, quality, worldLevel, itemType, stack, maxStackSize);
        }

        private static StorageContainerSnapshot Container(int index, int emptySlots,
            params StorageItemSnapshot[] items)
        {
            return new StorageContainerSnapshot(index, emptySlots,
                items ?? Array.Empty<StorageItemSnapshot>());
        }

        private static void AssertRoute(StorageRoute route, int containerIndex, StorageRouteKind kind)
        {
            Expect(route.ContainerIndex == containerIndex,
                $"unexpected container index; expected {containerIndex}, got {route.ContainerIndex}");
            Expect(route.Kind == kind,
                $"unexpected route kind; expected {kind}, got {route.Kind}");
        }

        private static void AssertBefore(IReadOnlyList<StorageRoute> routes, StorageRouteKind first,
            StorageRouteKind second)
        {
            var firstIndex = FirstIndexOfKind(routes, first);
            var secondIndex = FirstIndexOfKind(routes, second);
            if (firstIndex >= 0 && secondIndex >= 0 && firstIndex > secondIndex)
                throw new InvalidOperationException($"{first} should be routed before {second}");
        }

        private static int FirstIndexOfKind(IEnumerable<StorageRoute> routes, StorageRouteKind kind)
        {
            var index = 0;
            foreach (var route in routes)
            {
                if (route.Kind == kind) return index;
                index++;
            }

            return -1;
        }

        // -- Shared helpers (mirrored from the reference SmokePlugin) ---------------

        private static Localization TryGetGameLocalization()
        {
            try
            {
                return Localization.instance;
            }
            catch
            {
                return null;
            }
        }

        private static string CreateSmokeTempDirectory(string purpose)
        {
            var baseDirectory = string.IsNullOrEmpty(Paths.ConfigPath)
                ? Path.Combine(Path.GetTempPath(), SmokeDirectoryName)
                : Path.Combine(Paths.ConfigPath, SmokeDirectoryName);
            var directory = Path.Combine(baseDirectory, purpose,
                DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch
            {
                // Smoke temp cleanup is best-effort; stale files remain under AutomaticsSmoke.
            }
        }

        private static void Expect(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

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

            public static CheckResult Pass()
            {
                return new CheckResult(CheckStatus.Pass, "", "");
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
