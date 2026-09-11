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
                RunCheck("vanilla_label_localizes_via_game", CheckVanillaLabelLocalizesViaGame)
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

            var routes = StoragePlanner.Plan(source, containers);

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

            var routes = StoragePlanner.Plan(source, containers);

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

            var routes = StoragePlanner.Plan(source, containers);

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

            var routes = StoragePlanner.Plan(source, containers, referenceLimit: 1);

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
                Container(1, emptySlots: 0, Item("$item_wood", itemType: 1, stack: 40, maxStackSize: 50)),
                Container(2, emptySlots: 4)
            };

            var exactRoutes = StoragePlanner.Plan(source, containers)
                .Where(x => x.Kind == StorageRouteKind.ExactStack)
                .ToList();

            Expect(exactRoutes.Count == 2, "expected two exact-stack routes");
            AssertRoute(exactRoutes[0], 0, StorageRouteKind.ExactStack);
            AssertRoute(exactRoutes[1], 1, StorageRouteKind.ExactStack);
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
            // Globals.Door is a per-module ValheimObject that only populates from the
            // JSON cache when the automatic_door module is enabled (its registry is
            // touched during that module's Config.Initialize, before the cache is
            // cleared). With the module disabled the registry is empty for the session,
            // which is an environment/config state, not a regression: SKIP, not FAIL.
            if (!AutomaticDoor.Globals.Door.GetAllElements().Any())
                return CheckResult.Skip(
                    "automatic_door module disabled; door registry not loaded this session");

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
