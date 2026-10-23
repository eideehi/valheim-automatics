using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using PickupComponent = Automatics.AutomaticPickup.AutomaticPickup;
using PickupConfig = Automatics.AutomaticPickup.Config;
using PickupModule = Automatics.AutomaticPickup.Module;

namespace Automatics.SmokeTest
{
    // The `pickup` section of automatics_smoke_features: the Pickup All Nearby key is bound to
    // Left Shift + E by default, the hover line shows it, periodic pickup runs while it is
    // bound, and a key pass picks up only the objects named like the one it was made on, also
    // when the same pass picks up for the interval and takes the hovered object first.
    //
    // The section sets up what the world checks need, through SmokeSetup: Automatic Pickup is
    // turned on for the run when it is off; an unset Pickup All Nearby key is bound to its
    // default, Left Shift + E; automatic_pickup_range is set to 3.5 m for the two Wood pass
    // checks and to 6 m for the rest; a periodic interval above 0 is set to 0 from the start, so
    // only the checks' own requests make passes, and the last check sets it back to the user's
    // value, or to its default, 0.5 s, when that is 0. The settings go through SmokeSession: the
    // change is recorded before it is made, and put back afterwards unless the user changed the
    // setting meanwhile. Before any drop is spawned, the player is moved to clear ground when an
    // item drop, a pickable or a pickable item lies within reach of a pass; and items are put
    // aside when the inventory has no room for what the checks pick up.
    // The player and the items go back when the section ends.
    //
    // The key pass is requested through AutomaticPickup.RequestKeyPickup, the call Update makes
    // when the key goes down, with the object the player would be hovering; the pass itself is
    // the component's own coroutine. Its test drops have m_autoPickup cleared, so neither vanilla
    // auto-pickup nor the periodic pass can take them: only the key pass can. The overlap check
    // also makes the request FixedUpdate makes, AutomaticPickup.RequestIntervalPickup, in the
    // same frame. The interval check leaves periodic pickup to the component's own FixedUpdate
    // timer, with a drop placed beyond vanilla's auto-pickup reach and inside Automatic Pickup's.
    //
    // The two checks on drops vanilla does not allow yet spawn the drop and make the request in
    // the same frame, so the first pass sees a drop that vanilla still refuses (it refuses a
    // drop for 0.5 s after it spawns).
    //
    // Item safety as in the world command: picked-up test items are removed from the inventory
    // only when the count of that item grew by exactly what the check spawned; otherwise they
    // stay and the log says so.
    internal static class PickupFeatureSmoke
    {
        private const string WoodPrefab = "Wood";
        private const string StonePrefab = "Stone";

        private const string DefaultCheck = "pickup_key_default_is_left_shift_e";
        private const string SetupCheck = "pickup_setup";
        private const string HoverCheck = "pickup_hover_text_shows_key";
        private const string KeyPassCheck = "pickup_key_pass_same_name_only";
        private const string OverlapCheck = "pickup_key_and_interval_in_one_pass";
        private const string IntervalCheck = "pickup_interval_runs_with_key_bound";
        private const string WaitsCheck = "pickup_interval_pass_waits_for_pickable_drop";
        private const string KeyNotYetPickableCheck = "pickup_key_takes_not_yet_pickable_drop";
        private const string FishCheck = "pickup_key_leaves_fish_in_water";
        private const string FishGuardCheck = "pickup_fish_guard_removed";
        private const string RestoreCheck = "pickup_settings_restored";

        private static readonly string[] WorldChecks =
        {
            HoverCheck, KeyPassCheck, OverlapCheck, WaitsCheck, KeyNotYetPickableCheck, FishCheck, IntervalCheck
        };

        private const string FishPrefab = "Fish1";

        // automatic_pickup_range for the run. The overlap check places its drops 2.5 m away
        // (vanilla's 2 m reach and half a metre) and needs a metre to spare. The smaller range
        // for the two Wood pass checks keeps their passes, which take every Wood drop or every
        // allowed drop in range, off as much of the surroundings as possible. The other checks
        // place their drops up to 3 m away and get 3 m to spare: a drop spawned 0.3 m up can
        // roll more than a metre when it lands, and one that rolls out of range can be taken by
        // no pass.
        private const float PassRange = 3.5f;
        private const float SectionRange = 6f;

        // How far beyond the range the site is kept free of item drops: a drop of the user's can
        // roll or be thrown into the range while the section runs.
        private const float DropMargin = 2f;

        // Bounds on the waits, not estimates of them: vanilla refuses a drop for 0.5 s after it
        // spawns, and a pass the component starts finishes within a few frames.
        private const float PickableTimeoutSeconds = 5f;
        private const float KeyPassTimeoutSeconds = 3f;

        // How long a drop the key pass must leave is watched after the pass took the others.
        private const float SettleSeconds = 0.5f;

        // The key pass is ignored within 0.2 s of a vanilla interaction; the check waits that out.
        private const float HoverInteractCooldown = 0.25f;

        public static IEnumerator Run(FeatureRun run)
        {
            CheckDefaultKey(run);

            var problem = WorldProblem(out var player, out var wood, out var stone);
            if (problem != null)
            {
                SkipAll(run, problem, WorldChecks);
                yield break;
            }

            var held = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            SmokeSetup.InventoryStash stash = null;
            try
            {
                // The hover line and periodic pickup with the key bound need a key. An unset key
                // is bound to its default for the run and put back afterwards.
                if (PickupConfig.PickupAllNearbyKey.MainKey == KeyCode.None)
                    SmokeSetup.Write(run, held, PickupConfig.PickupAllNearbyKeyEntry,
                        new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift));

                // Every check makes its own passes until the last one, so periodic pickup is off
                // from the start: a pass the timer made could take the drop a check watches, or
                // items of the user's that would blur the counts. It is set before the module is
                // turned on, so no timer pass runs at all.
                var userInterval = PickupConfig.AutomaticPickupInterval;
                if (userInterval > 0f)
                    SmokeSetup.Write(run, held, PickupConfig.AutomaticPickupIntervalEntry, 0f);
                SmokeSetup.Write(run, held, PickupConfig.AutomaticPickupRangeEntry, PassRange);

                var module = new SmokeSetup.SetupResult();
                var steps = SmokeSetup.EnableModule(run.RunId, held, PickupConfig.ModuleEntry,
                    () => PickupModule.Running, module);
                while (steps.MoveNext()) yield return steps.Current;
                var component = player ? player.GetComponent<PickupComponent>() : null;
                if (!module.Ok || !component)
                {
                    SkipAll(run, !module.Ok
                        ? "Automatic Pickup is not running: " + module.Reason
                        : "the local player carries no AutomaticPickup component", WorldChecks);
                    yield break;
                }

                // Before any test drop is spawned: one spawned earlier would be left behind.
                var site = new SmokeSetup.SetupResult();
                steps = SmokeSetup.Relocate(run.RunId, ClearOfOwnItems, site);
                while (steps.MoveNext()) yield return steps.Current;
                if (!run.IsCurrent || !player)
                {
                    SkipAll(run, "the run was abandoned or the local player went away during the setup", WorldChecks);
                    yield break;
                }

                // Room for the most any one check picks up: two Wood, or two fish.
                var weight = 2f * Mathf.Max(UnitWeight(wood), UnitWeight(ZNetScene.instance.GetPrefab(FishPrefab)));
                stash = SmokeSetup.Stash(player, 2, weight + 0.1f);

                var setup = $"Automatic Pickup {module.Reason}; {site.Reason}; {stash.Report}";
                if (site.Ok)
                    run.Pass(SetupCheck, setup);
                else
                    run.Skip(SetupCheck,
                        $"no site clear of other drops was found, so the checks run where you stand and may be inconclusive: {setup}");

                var checks = Checks(run, held, player, component, wood, stone, userInterval);
                while (checks.MoveNext()) yield return checks.Current;

                if (site.Changed)
                {
                    var back = new SmokeSetup.SetupResult();
                    steps = SmokeSetup.ReturnToStart(run.RunId, back);
                    while (steps.MoveNext()) yield return steps.Current;
                    if (!back.Ok)
                        FeaturesSmokeCommand.Warn(
                            $"[Automatics Smoke] pickup: the player was not moved back to the starting spot yet ({back.Reason}); the run's hand-back tries again when the command ends");
                }
            }
            finally
            {
                stash?.Restore();
                if (held.Count > 0) SmokeSetup.ReportSettings(run, RestoreCheck);
            }
        }

        private static IEnumerator Checks(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
            Player player, PickupComponent component, GameObject wood, GameObject stone, float userInterval)
        {
            var keyPass = FeaturesSmokeCommand.Contain(
                CheckWoodPass(run, KeyPassCheck, false, player, component, wood, stone), run, KeyPassCheck);
            while (keyPass.MoveNext()) yield return keyPass.Current;

            yield return null;
            if (!run.IsCurrent)
            {
                SkipAbandoned(run, OverlapCheck, WaitsCheck, KeyNotYetPickableCheck, FishCheck, IntervalCheck);
                yield break;
            }

            var overlap = FeaturesSmokeCommand.Contain(
                CheckWoodPass(run, OverlapCheck, true, player, component, wood, stone), run, OverlapCheck);
            while (overlap.MoveNext()) yield return overlap.Current;

            yield return null;
            if (!run.IsCurrent)
            {
                SkipAbandoned(run, WaitsCheck, KeyNotYetPickableCheck, FishCheck, IntervalCheck);
                yield break;
            }

            // The rest place their drops up to 3 m away and keep 3 m to spare for a roll.
            SmokeSetup.Write(run, held, PickupConfig.AutomaticPickupRangeEntry, SectionRange);

            var waits = FeaturesSmokeCommand.Contain(
                CheckIntervalPassWaits(run, player, component, wood), run, WaitsCheck);
            while (waits.MoveNext()) yield return waits.Current;

            yield return null;
            if (!run.IsCurrent)
            {
                SkipAbandoned(run, KeyNotYetPickableCheck, FishCheck, IntervalCheck);
                yield break;
            }

            var keyNotYetPickable = FeaturesSmokeCommand.Contain(
                CheckKeyTakesNotYetPickable(run, player, component, wood), run, KeyNotYetPickableCheck);
            while (keyNotYetPickable.MoveNext()) yield return keyNotYetPickable.Current;

            yield return null;
            if (!run.IsCurrent)
            {
                SkipAbandoned(run, FishCheck, IntervalCheck);
                yield break;
            }

            var fish = FeaturesSmokeCommand.Contain(
                CheckFishInWater(run, player, component), run, FishCheck);
            while (fish.MoveNext()) yield return fish.Current;

            yield return null;
            if (!run.IsCurrent)
            {
                SkipAbandoned(run, IntervalCheck);
                yield break;
            }

            // Periodic pickup is back on for this last check, at the user's interval or, when
            // that is 0, at its default, 0.5 s: while it is on, Automatic Pickup also picks
            // up your own items nearby.
            if (PickupConfig.AutomaticPickupInterval <= 0f)
                SmokeSetup.Write(run, held, PickupConfig.AutomaticPickupIntervalEntry,
                    userInterval > 0f ? userInterval : 0.5f);

            var interval = FeaturesSmokeCommand.Contain(
                CheckIntervalPickup(run, player, wood), run, IntervalCheck);
            while (interval.MoveNext()) yield return interval.Current;
        }

        // A site where the checks can tell their test items from the user's: nothing within reach
        // of a pass that a pass could collect - no item drop, and no pickable or pickable item,
        // which the interval's part of a pass harvests before it looks at drops. A harvest could
        // add Wood or Stone to the counts, or fill the slots the checks need for their own items.
        // Only drops move, so only they are looked for beyond the range.
        private static string ClearOfOwnItems(Vector3 site)
        {
            var drop = SmokeSetup.Clearance(SectionRange + DropMargin, SmokeSetup.SiteKinds.Drops)(site);
            if (drop != null) return drop;

            // Trigger colliders count here: an interactable's collider may be one.
            const float radius = SectionRange;
            foreach (var collider in Physics.OverlapSphere(site, radius, ~0, QueryTriggerInteraction.Collide))
            {
                if (!collider) continue;
                var pickable = collider.GetComponentInParent<Pickable>();
                if (pickable) return $"pickable '{Objects.GetName(pickable)}' within {radius:0} m";
                var pickableItem = collider.GetComponentInParent<PickableItem>();
                if (pickableItem) return $"pickable item '{Objects.GetPrefabName(pickableItem.gameObject)}' within {radius:0} m";
            }

            return null;
        }

        private static float UnitWeight(GameObject prefab)
        {
            var item = prefab ? PickableHarvester.CreatePickupItemData(prefab, 1) : null;
            return item != null ? item.GetWeight(1) : 0f;
        }

        private static void SkipAll(FeatureRun run, string reason, IEnumerable<string> checks)
        {
            foreach (var check in checks)
                run.Skip(check, reason);
        }

        // The default the entry was bound with, and how BepInEx writes it into the file. It needs
        // no world: the setting is bound whether the module is on or off.
        private static void CheckDefaultKey(FeatureRun run)
        {
            var entry = PickupConfig.PickupAllNearbyKeyEntry;
            if (entry == null)
            {
                run.Fail(DefaultCheck, "the pickup_all_nearby_key entry is not bound");
                return;
            }

            var expected = new KeyboardShortcut(KeyCode.E, KeyCode.LeftShift);
            var serialized = TomlTypeConverter.ConvertToString(entry.DefaultValue, typeof(KeyboardShortcut));
            if (!(entry.DefaultValue is KeyboardShortcut actual) || !actual.Equals(expected) ||
                serialized != "E + LeftShift")
            {
                run.Fail(DefaultCheck,
                    $"the default of pickup_all_nearby_key is '{serialized}', not 'E + LeftShift'");
                return;
            }

            run.Pass(DefaultCheck, $"default 'E + LeftShift'; the current value is '{entry.GetSerializedValue()}'");
        }

        private static string WorldProblem(out Player player, out GameObject wood, out GameObject stone)
        {
            player = null;
            wood = null;
            stone = null;

            if (ZNetScene.instance == null) return "ZNetScene.instance is null; run it in a loaded world";
            player = Player.m_localPlayer;
            if (player == null) return "Player.m_localPlayer is null";
            if (player.GetInventory() == null) return "the player inventory is null";
            if (ObjectDB.instance == null || ObjectDB.instance.m_items.Count == 0) return "ObjectDB is not ready";

            wood = ZNetScene.instance.GetPrefab(WoodPrefab);
            stone = ZNetScene.instance.GetPrefab(StonePrefab);
            if (wood == null || wood.GetComponent<ItemDrop>() == null) return "the Wood prefab was not found";
            if (stone == null || stone.GetComponent<ItemDrop>() == null) return "the Stone prefab was not found";
            return null;
        }

        // Two Wood drops and one Stone drop a metre apart, all with m_autoPickup cleared. A key
        // pass requested on the first Wood drop has to take both Wood drops and leave the Stone.
        //
        // Without overlap they lie 1.5 m to the right of the player, and the hover line is read
        // from the first Wood drop. With overlap they lie to the left, a metre beyond vanilla's
        // auto-pickup reach, and in one frame the first Wood drop is given m_autoPickup and both
        // an interval and a key request are made, as FixedUpdate and Update make them. The
        // interval's part of that pass takes the hovered drop itself, and the key's part still
        // has to take the other Wood drop by its name.
        private static IEnumerator CheckWoodPass(FeatureRun run, string check, bool overlap,
            Player player, PickupComponent component, GameObject woodPrefab, GameObject stonePrefab)
        {
            var transform = player.transform;
            var range = PickupConfig.AutomaticPickupRange;
            Vector3 origin;
            var spacing = 1f;
            if (overlap)
            {
                // Half a metre beyond vanilla's reach and 0.6 m apart, so every drop stays well
                // inside Automatic Pickup's range even after it has fallen or rolled a little.
                var distance = player.m_autoPickupRange + 0.5f;
                spacing = 0.6f;
                if (range < distance + 1f)
                {
                    run.Skip(check,
                        $"automatic_pickup_range is {range:0.#} m, too short to place the drops beyond vanilla auto-pickup ({player.m_autoPickupRange:0.#} m); set it to {distance + 1f:0.#} m or more to include this check");
                    yield break;
                }

                origin = transform.position + Vector3.up * 0.3f - transform.right * distance;
            }
            else
            {
                if (range < 2.5f)
                {
                    var reason =
                        $"automatic_pickup_range is {range:0.#} m and the test drops lie up to 1.9 m away; set it to 2.5 m or more to include this check";
                    run.Skip(HoverCheck, reason);
                    run.Skip(check, reason);
                    yield break;
                }

                origin = transform.position + Vector3.up * 0.3f + transform.right * 1.5f;
            }

            var inventory = player.GetInventory();
            var woodSample = PickableHarvester.CreatePickupItemData(woodPrefab, 1);
            var stoneSample = PickableHarvester.CreatePickupItemData(stonePrefab, 1);

            ItemDrop first = null;
            ItemDrop second = null;
            ItemDrop other = null;
            var woodMayBeLeft = false;
            var stoneMayBeLeft = false;
            try
            {
                first = FeaturesSmokeCommand.SpawnTestDrop(woodPrefab, origin, false);
                second = FeaturesSmokeCommand.SpawnTestDrop(woodPrefab, origin + transform.forward * spacing, false);
                other = FeaturesSmokeCommand.SpawnTestDrop(stonePrefab, origin - transform.forward * spacing, false);
                if (first == null || second == null || other == null)
                {
                    if (!overlap) run.Skip(HoverCheck, "a test drop could not be set up");
                    run.Skip(check, "a test drop could not be set up");
                    yield break;
                }

                if (!overlap) CheckHoverText(run, first);

                var paused = Game.IsPaused();
                var started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player &&
                       !(first.CanPickup() && second.CanPickup() && other.CanPickup()) &&
                       !AnyConsumed(first, second, other) &&
                       Time.realtimeSinceStartup - started < PickableTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                if (!run.IsCurrent || !player)
                {
                    run.Skip(check, "inconclusive: the run was abandoned or the player went away before the pass");
                    yield break;
                }

                if (AnyConsumed(first, second, other))
                {
                    run.Fail(check,
                        "a test drop disappeared before the pass; its m_autoPickup was false, so neither vanilla auto-pickup nor periodic pickup should have taken it");
                    yield break;
                }

                if (!(first.CanPickup() && second.CanPickup() && other.CanPickup()))
                {
                    if (paused)
                        run.Skip(check,
                            $"inconclusive: the game was paused while the check waited up to {PickableTimeoutSeconds:0.#}s for the drops to become pickable");
                    else
                        run.Fail(check,
                            $"the drops did not become pickable within {PickableTimeoutSeconds:0.#}s");
                    yield break;
                }

                if (!PickableHarvester.CanAddItem(player, first.m_itemData, 2))
                {
                    run.Skip(check, "the inventory cannot take two Wood (space or weight); make room to include this check");
                    yield break;
                }

                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player &&
                       Time.time - Reflections.GetField<float>(player, "m_lastHoverInteractTime") < HoverInteractCooldown &&
                       Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                    yield return null;

                // The drops fall and can roll after they spawn. One that has left Automatic
                // Pickup's range could not be taken by any pass, so the check cannot tell anything.
                var distances = $"first {Distance(player, first):0.00} m, second {Distance(player, second):0.00} m, stone {Distance(player, other):0.00} m, range {range:0.#} m";
                if (Distance(player, first) > range - 0.1f || Distance(player, second) > range - 0.1f)
                {
                    run.Skip(check, $"inconclusive: a Wood drop rolled out of automatic_pickup_range before the pass ({distances})");
                    yield break;
                }

                var woodBefore = PickableHarvester.CountExactItems(inventory, woodSample);
                var stoneBefore = PickableHarvester.CountExactItems(inventory, stoneSample);
                woodMayBeLeft = true;
                stoneMayBeLeft = true;

                // One frame, no yield in between: the pass that follows sees both requests.
                if (overlap)
                {
                    first.m_autoPickup = true;
                    component.RequestIntervalPickup();
                }

                component.RequestKeyPickup(first.gameObject);

                paused = Game.IsPaused();
                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player &&
                       !(FeaturesSmokeCommand.IsConsumed(first) && FeaturesSmokeCommand.IsConsumed(second)) &&
                       Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && Time.realtimeSinceStartup - started < SettleSeconds)
                    yield return null;

                if (!player)
                {
                    run.Skip(check, "inconclusive: the local player went away during the pass");
                    yield break;
                }

                var woodDelta = PickableHarvester.CountExactItems(inventory, woodSample) - woodBefore;
                var stoneDelta = PickableHarvester.CountExactItems(inventory, stoneSample) - stoneBefore;
                if (woodDelta <= 0) woodMayBeLeft = false;
                if (stoneDelta <= 0) stoneMayBeLeft = false;

                var firstTaken = FeaturesSmokeCommand.IsConsumed(first);
                var secondTaken = FeaturesSmokeCommand.IsConsumed(second);
                var otherTaken = FeaturesSmokeCommand.IsConsumed(other);

                // Proven to be the test drops' items only when the counts moved by exactly them.
                if (woodDelta == 2 && firstTaken && secondTaken &&
                    FeaturesSmokeCommand.RemoveExactItems(inventory, woodSample, 2) == 2)
                    woodMayBeLeft = false;
                if (stoneDelta == 1 && otherTaken &&
                    FeaturesSmokeCommand.RemoveExactItems(inventory, stoneSample, 1) == 1)
                    stoneMayBeLeft = false;
                var notes = woodMayBeLeft || stoneMayBeLeft
                    ? "; the picked-up test items could not be told apart from your own and were left in the inventory"
                    : "";

                if (!firstTaken || !secondTaken)
                {
                    if (paused && run.IsCurrent)
                        run.Skip(check,
                            $"inconclusive: the game was paused during the pass (first Wood taken={firstTaken}, second={secondTaken})");
                    else
                        run.Fail(check,
                            $"the pass did not take both Wood drops (first={firstTaken}, second={secondTaken}, woodDelta={woodDelta}; at the request {distances}){notes}");
                    yield break;
                }

                if (otherTaken)
                {
                    run.Fail(check,
                        $"the pass made on a Wood drop also took the Stone drop (stoneDelta={stoneDelta}){notes}");
                    yield break;
                }

                if (woodDelta < 2)
                {
                    run.Fail(check,
                        $"both Wood drops disappeared but the Wood in the inventory grew by {woodDelta}, not 2{notes}");
                    yield break;
                }

                // The test Stone drop stayed, so Stone that came or went was not the test's: a pass
                // with an interval part takes every allowed drop in range, Stone of your own too.
                if (stoneDelta != 0)
                {
                    run.Skip(check,
                        $"inconclusive: the test Stone drop stayed, but the Stone in the inventory changed by {stoneDelta}, so Stone other than the test drop moved, such as Stone of your own nearby that the pass picked up; run it away from other Stone drops{notes}");
                    yield break;
                }

                if (woodDelta > 2)
                {
                    run.Skip(check,
                        $"inconclusive: the Wood in the inventory grew by {woodDelta}, more than the 2 test drops, so Wood of your own nearby was picked up too; run it away from other Wood drops{notes}");
                    yield break;
                }

                run.Pass(check, overlap
                    ? "one pass took the hovered Wood drop with the interval and the other Wood drop by its name, and left the Stone drop (woodDelta=2)"
                    : "both Wood drops were picked up and the Stone drop was left (woodDelta=2)");
            }
            finally
            {
                if (woodMayBeLeft || stoneMayBeLeft)
                    FeaturesSmokeCommand.Warn(
                        $"[Automatics Smoke] {check}: test Wood or Stone the pass picked up could not be attributed to the test drops, so it was left in the inventory");

                // A drop still there is destroyed only while it holds the one item the check gave
                // it; one that grew has absorbed real items and is left in the world.
                FeaturesSmokeCommand.RemoveTestDrop(first, check);
                FeaturesSmokeCommand.RemoveTestDrop(second, check);
                FeaturesSmokeCommand.RemoveTestDrop(other, check);
            }
        }

        // The line the hover text patch adds: the key as BepInEx prints it, and the message with
        // the item's name.
        private static void CheckHoverText(FeatureRun run, ItemDrop drop)
        {
            var key = PickupConfig.PickupAllNearbyKey;
            if (key.MainKey == KeyCode.None)
            {
                run.Skip(HoverCheck,
                    "pickup_all_nearby_key is not set, so no hover line is added; set a key to include this check");
                return;
            }

            var message = global::Automatics.Automatics.L10N.Localize(
                "@message_automatic_pickup_pickup_all_nearby", drop.m_itemData.m_shared.m_name);
            if (string.IsNullOrEmpty(message) || message.Contains("message_automatic_pickup"))
            {
                run.Fail(HoverCheck, $"the hover message is not translated: '{message}'");
                return;
            }

            var line = $"[<color=yellow><b>{key}</b></color>] {message}";
            var text = drop.GetHoverText();
            if (text == null || !text.Contains(line))
            {
                run.Fail(HoverCheck, $"the hover text lacks '{line}': '{text}'");
                return;
            }

            run.Pass(HoverCheck, $"'{key}' with '{message}'");
        }

        // A Wood drop that allows auto-pickup, placed a metre beyond vanilla's auto-pickup reach,
        // has to be picked up by the component's own timer while the key is bound.
        private static IEnumerator CheckIntervalPickup(FeatureRun run, Player player, GameObject woodPrefab)
        {
            if (!player)
            {
                run.Skip(IntervalCheck, "the local player went away");
                yield break;
            }

            if (PickupConfig.PickupAllNearbyKey.MainKey == KeyCode.None)
            {
                run.Skip(IntervalCheck,
                    "pickup_all_nearby_key is not set; this check is about periodic pickup with the key bound, so set a key to include it");
                yield break;
            }

            var interval = PickupConfig.AutomaticPickupInterval;
            if (interval <= 0f)
            {
                run.Skip(IntervalCheck,
                    "automatic_pickup_interval is 0, so periodic pickup is off; set it above 0 to include this check");
                yield break;
            }

            var distance = player.m_autoPickupRange + 1f;
            var range = PickupConfig.AutomaticPickupRange;
            if (distance > range - 0.5f)
            {
                run.Skip(IntervalCheck,
                    $"automatic_pickup_range is {range:0.#} m, too short to place a drop beyond vanilla auto-pickup ({player.m_autoPickupRange:0.#} m); set it to {distance + 0.5f:0.#} m or more to include this check");
                yield break;
            }

            var inventory = player.GetInventory();
            var sample = PickableHarvester.CreatePickupItemData(woodPrefab, 1);
            var transform = player.transform;
            var position = transform.position + Vector3.up * 0.3f - transform.right * distance;

            ItemDrop drop = null;
            var woodMayBeLeft = false;
            try
            {
                drop = FeaturesSmokeCommand.SpawnTestDrop(woodPrefab, position, true);
                if (drop == null)
                {
                    run.Skip(IntervalCheck, "the test drop could not be set up");
                    yield break;
                }

                if (!PickableHarvester.CanAddItem(player, drop.m_itemData))
                {
                    run.Skip(IntervalCheck, "the inventory cannot take one Wood (space or weight); make room to include this check");
                    yield break;
                }

                var before = PickableHarvester.CountExactItems(inventory, sample);
                woodMayBeLeft = true;

                var timeout = interval + PickableTimeoutSeconds + 1f;
                var paused = Game.IsPaused();
                var started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(drop) &&
                       Time.realtimeSinceStartup - started < timeout)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                if (!player)
                {
                    run.Skip(IntervalCheck, "inconclusive: the local player went away while the check waited");
                    yield break;
                }

                var delta = PickableHarvester.CountExactItems(inventory, sample) - before;
                if (delta <= 0) woodMayBeLeft = false;

                if (!FeaturesSmokeCommand.IsConsumed(drop))
                {
                    if (!run.IsCurrent || paused)
                        run.Skip(IntervalCheck,
                            "inconclusive: the run was abandoned or the game was paused while the check waited");
                    else
                        run.Fail(IntervalCheck,
                            $"a Wood drop {distance:0.#} m away was not picked up within {timeout:0.#}s at interval {interval:0.##}s with pickup_all_nearby_key '{PickupConfig.PickupAllNearbyKey}'");
                    yield break;
                }

                if (delta <= 0)
                {
                    run.Skip(IntervalCheck,
                        "inconclusive: the drop was taken but no Wood reached the inventory, so something other than Automatic Pickup took it");
                    yield break;
                }

                var note = "";
                if (delta == 1 && FeaturesSmokeCommand.RemoveExactItems(inventory, sample, 1) == 1)
                    woodMayBeLeft = false;
                else
                    note = $"; the Wood count grew by {delta}, so the test Wood could not be told apart from your own and was left in the inventory";

                run.Pass(IntervalCheck,
                    $"a Wood drop {distance:0.#} m away was picked up at interval {interval:0.##}s with pickup_all_nearby_key '{PickupConfig.PickupAllNearbyKey}'{note}");
            }
            finally
            {
                if (woodMayBeLeft)
                    FeaturesSmokeCommand.Warn(
                        $"[Automatics Smoke] {IntervalCheck}: the Wood periodic pickup took could not be attributed to the test drop, so it was left in the inventory");
                FeaturesSmokeCommand.RemoveTestDrop(drop, IntervalCheck);
            }
        }

        private static void SkipAbandoned(FeatureRun run, params string[] checks)
        {
            foreach (var check in checks)
                run.Skip(check, "the run was abandoned before this check");
        }

        // A Wood drop that allows auto-pickup, a metre beyond vanilla's auto-pickup reach, is
        // asked for in the frame it spawns, when vanilla still refuses it. The pass the interval
        // asks for must leave it alone - not start vanilla's deferred pickup, which would take
        // it as soon as vanilla allows - and a later pass must take it.
        private static IEnumerator CheckIntervalPassWaits(FeatureRun run, Player player,
            PickupComponent component, GameObject woodPrefab)
        {
            if (!player)
            {
                run.Skip(WaitsCheck, "the local player went away");
                yield break;
            }

            if (PickupConfig.AutomaticPickupInterval > 0f)
            {
                run.Skip(WaitsCheck,
                    "automatic_pickup_interval is above 0 and could not be set to 0 for the run, so the timer could make a pass of its own; set it to 0 to include this check");
                yield break;
            }

            var distance = player.m_autoPickupRange + 1f;
            var range = PickupConfig.AutomaticPickupRange;
            if (distance > range - 0.5f)
            {
                run.Skip(WaitsCheck,
                    $"automatic_pickup_range is {range:0.#} m, too short to place a drop beyond vanilla auto-pickup ({player.m_autoPickupRange:0.#} m); set it to {distance + 0.5f:0.#} m or more to include this check");
                yield break;
            }

            var inventory = player.GetInventory();
            var sample = PickableHarvester.CreatePickupItemData(woodPrefab, 1);
            var transform = player.transform;
            var position = transform.position + Vector3.up * 0.3f - transform.right * distance;
            // The check waits seconds for vanilla to allow the drop. Placed at your height where the
            // ground there is higher, it would start inside the terrain, fall through it and be
            // metres under it by the later pass, so it starts just above the ground there.
            position.y = ZoneSystem.instance.GetGroundHeight(position) + 0.3f;

            ItemDrop drop = null;
            var woodMayBeLeft = false;
            try
            {
                drop = FeaturesSmokeCommand.SpawnTestDrop(woodPrefab, position, true);
                if (drop == null)
                {
                    run.Skip(WaitsCheck, "the test drop could not be set up");
                    yield break;
                }

                // The check waits seconds for vanilla to allow the drop. On sloping ground it would
                // roll meanwhile, out of automatic_pickup_range or into vanilla's own reach, so it
                // only falls.
                var body = drop.GetComponent<Rigidbody>();
                if (body)
                    body.constraints |= RigidbodyConstraints.FreezePositionX | RigidbodyConstraints.FreezePositionZ |
                                        RigidbodyConstraints.FreezeRotation;

                if (drop.CanPickup())
                {
                    run.Skip(WaitsCheck, "inconclusive: vanilla already allowed the drop in the frame it spawned");
                    yield break;
                }

                if (!PickableHarvester.CanAddItem(player, drop.m_itemData))
                {
                    run.Skip(WaitsCheck, "the inventory cannot take one Wood (space or weight); make room to include this check");
                    yield break;
                }

                var before = PickableHarvester.CountExactItems(inventory, sample);
                woodMayBeLeft = true;

                // The pass takes the request within a tenth of a second and reaches the drops
                // two frames after it starts; three more frames make sure it has been through.
                component.RequestIntervalPickup();
                var paused = Game.IsPaused();
                var started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(drop) &&
                       Reflections.GetField<bool>(component, "_active") &&
                       Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                for (var frame = 0; frame < 3 && run.IsCurrent && player; frame++)
                    yield return null;

                if (!run.IsCurrent || !player)
                {
                    run.Skip(WaitsCheck, "inconclusive: the run was abandoned or the player went away during the first pass");
                    yield break;
                }

                if (FeaturesSmokeCommand.IsConsumed(drop))
                {
                    run.Fail(WaitsCheck,
                        "the pass took the drop before vanilla allowed it to be picked up");
                    yield break;
                }

                if (drop.CanPickup())
                {
                    run.Skip(WaitsCheck,
                        "inconclusive: the first pass ran after vanilla had already allowed the drop (a slow frame or a pause)");
                    yield break;
                }

                // No pass is requested while vanilla starts allowing the drop and for half a
                // second after it: a deferred pickup started by the first pass would take it now.
                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(drop) &&
                       !drop.CanPickup() &&
                       Time.realtimeSinceStartup - started < PickableTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(drop) &&
                       Time.realtimeSinceStartup - started < SettleSeconds)
                    yield return null;

                if (!run.IsCurrent || !player)
                {
                    run.Skip(WaitsCheck, "inconclusive: the run was abandoned or the player went away while the check waited");
                    yield break;
                }

                if (FeaturesSmokeCommand.IsConsumed(drop))
                {
                    run.Fail(WaitsCheck,
                        "the first pass, made while vanilla still refused the drop, left it to be picked up without a later pass: it started vanilla's deferred pickup");
                    yield break;
                }

                if (!drop.CanPickup())
                {
                    if (paused)
                        run.Skip(WaitsCheck,
                            $"inconclusive: the game was paused while the check waited up to {PickableTimeoutSeconds:0.#}s for the drop to become pickable");
                    else
                        run.Fail(WaitsCheck, $"the drop did not become pickable within {PickableTimeoutSeconds:0.#}s");
                    yield break;
                }

                // The drop falls while the check waits. One that has left Automatic Pickup's range
                // could not be taken by any pass, so the check cannot tell anything.
                range = PickupConfig.AutomaticPickupRange;
                var where = $"the drop {Distance(player, drop):0.00} m away, {player.transform.position.y - drop.transform.position.y:0.00} m below you, range {range:0.#} m";
                if (Distance(player, drop) > range - 0.1f)
                {
                    run.Skip(WaitsCheck, $"inconclusive: the drop left automatic_pickup_range before the later pass ({where})");
                    yield break;
                }

                component.RequestIntervalPickup();
                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(drop) &&
                       Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                if (!player)
                {
                    run.Skip(WaitsCheck, "inconclusive: the local player went away during the second pass");
                    yield break;
                }

                var delta = PickableHarvester.CountExactItems(inventory, sample) - before;
                if (delta <= 0) woodMayBeLeft = false;

                if (!FeaturesSmokeCommand.IsConsumed(drop))
                {
                    if (!run.IsCurrent || paused)
                        run.Skip(WaitsCheck,
                            "inconclusive: the run was abandoned or the game was paused while the check waited");
                    else
                        run.Fail(WaitsCheck,
                            $"a later pass did not pick up the drop within {KeyPassTimeoutSeconds:0.#}s of vanilla allowing it (at the request {where})");
                    yield break;
                }

                if (delta <= 0)
                {
                    run.Skip(WaitsCheck,
                        "inconclusive: the drop was taken but no Wood reached the inventory, so something other than Automatic Pickup took it");
                    yield break;
                }

                var note = "";
                if (delta == 1 && FeaturesSmokeCommand.RemoveExactItems(inventory, sample, 1) == 1)
                    woodMayBeLeft = false;
                else
                    note = $"; the Wood count grew by {delta}, so the test Wood could not be told apart from your own and was left in the inventory";

                run.Pass(WaitsCheck,
                    $"a pass made while vanilla refused the drop left it in the world, and a later pass picked it up{note}");
            }
            finally
            {
                // Only a drop that was taken can have left Wood; an exit before the later pass leaves
                // the drop in the world.
                if (woodMayBeLeft && FeaturesSmokeCommand.IsConsumed(drop))
                    FeaturesSmokeCommand.Warn(
                        $"[Automatics Smoke] {WaitsCheck}: the Wood the later pass took could not be attributed to the test drop, so it was left in the inventory");
                FeaturesSmokeCommand.RemoveTestDrop(drop, WaitsCheck);
            }
        }

        // A key pass is requested in the frame a Wood drop spawns, when vanilla still refuses it,
        // with periodic pickup off. The drop's m_autoPickup is cleared, so only the key pass can
        // take it: it has to start vanilla's deferred pickup, which takes the drop once vanilla
        // allows it.
        private static IEnumerator CheckKeyTakesNotYetPickable(FeatureRun run, Player player,
            PickupComponent component, GameObject woodPrefab)
        {
            if (!player)
            {
                run.Skip(KeyNotYetPickableCheck, "the local player went away");
                yield break;
            }

            if (PickupConfig.AutomaticPickupInterval > 0f)
            {
                run.Skip(KeyNotYetPickableCheck,
                    "automatic_pickup_interval is above 0 and could not be set to 0 for the run; set it to 0 to include this check");
                yield break;
            }

            if (PickupConfig.AutomaticPickupRange < 2f)
            {
                run.Skip(KeyNotYetPickableCheck,
                    $"automatic_pickup_range is {PickupConfig.AutomaticPickupRange:0.#} m and the test drop lies 1.5 m away; set it to 2 m or more to include this check");
                yield break;
            }

            var inventory = player.GetInventory();
            var sample = PickableHarvester.CreatePickupItemData(woodPrefab, 1);
            var transform = player.transform;
            var position = transform.position + Vector3.up * 0.3f + transform.right * 1.5f;

            // The key pass is ignored within 0.2 s of a vanilla interaction.
            var started = Time.realtimeSinceStartup;
            while (run.IsCurrent && player &&
                   Time.time - Reflections.GetField<float>(player, "m_lastHoverInteractTime") < HoverInteractCooldown &&
                   Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                yield return null;

            if (!run.IsCurrent || !player)
            {
                run.Skip(KeyNotYetPickableCheck, "inconclusive: the run was abandoned or the player went away before the pass");
                yield break;
            }

            ItemDrop drop = null;
            var woodMayBeLeft = false;
            try
            {
                drop = FeaturesSmokeCommand.SpawnTestDrop(woodPrefab, position, false);
                if (drop == null)
                {
                    run.Skip(KeyNotYetPickableCheck, "the test drop could not be set up");
                    yield break;
                }

                if (drop.CanPickup())
                {
                    run.Skip(KeyNotYetPickableCheck, "inconclusive: vanilla already allowed the drop in the frame it spawned");
                    yield break;
                }

                if (!PickableHarvester.CanAddItem(player, drop.m_itemData))
                {
                    run.Skip(KeyNotYetPickableCheck, "the inventory cannot take one Wood (space or weight); make room to include this check");
                    yield break;
                }

                var before = PickableHarvester.CountExactItems(inventory, sample);
                woodMayBeLeft = true;

                component.RequestKeyPickup(drop.gameObject);

                var paused = Game.IsPaused();
                started = Time.realtimeSinceStartup;
                var timeout = PickableTimeoutSeconds + KeyPassTimeoutSeconds;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(drop) &&
                       Time.realtimeSinceStartup - started < timeout)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                if (!player)
                {
                    run.Skip(KeyNotYetPickableCheck, "inconclusive: the local player went away while the check waited");
                    yield break;
                }

                var delta = PickableHarvester.CountExactItems(inventory, sample) - before;
                if (delta <= 0) woodMayBeLeft = false;

                if (!FeaturesSmokeCommand.IsConsumed(drop))
                {
                    if (!run.IsCurrent || paused)
                        run.Skip(KeyNotYetPickableCheck,
                            "inconclusive: the run was abandoned or the game was paused while the check waited");
                    else
                        run.Fail(KeyNotYetPickableCheck,
                            $"a key pass made while vanilla refused the drop did not collect it within {timeout:0.#}s, with periodic pickup off");
                    yield break;
                }

                if (delta <= 0)
                {
                    run.Skip(KeyNotYetPickableCheck,
                        "inconclusive: the drop was taken but no Wood reached the inventory, so something other than Automatic Pickup took it");
                    yield break;
                }

                var note = "";
                if (delta == 1 && FeaturesSmokeCommand.RemoveExactItems(inventory, sample, 1) == 1)
                    woodMayBeLeft = false;
                else
                    note = $"; the Wood count grew by {delta}, so the test Wood could not be told apart from your own and was left in the inventory";

                run.Pass(KeyNotYetPickableCheck,
                    $"a key pass made while vanilla refused the drop collected it once vanilla allowed it, with periodic pickup off{note}");
            }
            finally
            {
                if (woodMayBeLeft)
                    FeaturesSmokeCommand.Warn(
                        $"[Automatics Smoke] {KeyNotYetPickableCheck}: the Wood the key pass took could not be attributed to the test drop, so it was left in the inventory");
                FeaturesSmokeCommand.RemoveTestDrop(drop, KeyNotYetPickableCheck);
            }
        }

        // Two fish drops of one kind lie a few steps from the player, one in water and one out of
        // it. Vanilla lets a fish be picked up only out of water, so a key pass requested on the
        // fish in water makes no request, the hover line is not added to it, and a key pass
        // requested on the fish out of water takes that fish and leaves the one in water.
        private static IEnumerator CheckFishInWater(FeatureRun run, Player player, PickupComponent component)
        {
            if (!player)
            {
                run.Skip(FishCheck, "the local player went away");
                yield break;
            }

            var range = PickupConfig.AutomaticPickupRange;
            if (range < 2.5f)
            {
                run.Skip(FishCheck,
                    $"automatic_pickup_range is {range:0.#} m and the test drops lie up to 1.9 m away; set it to 2.5 m or more to include this check");
                yield break;
            }

            var prefab = ZNetScene.instance.GetPrefab(FishPrefab);
            if (prefab == null || prefab.GetComponent<ItemDrop>() == null || prefab.GetComponent<Fish>() == null)
            {
                run.Skip(FishCheck, $"the {FishPrefab} prefab was not found or is not a fish drop");
                yield break;
            }

            var inventory = player.GetInventory();
            var sample = PickableHarvester.CreatePickupItemData(prefab, 1);
            var transform = player.transform;
            var origin = transform.position + Vector3.up * 0.3f + transform.right * 1.5f;

            ItemDrop swimming = null;
            ItemDrop landed = null;
            var fishMayBeLeft = false;
            var guarded = false;
            Func<string, bool> handBack = _ => WaterGuard.Detach(out _);
            SmokeSession.TrackHandBack(handBack);
            try
            {
                swimming = FeaturesSmokeCommand.SpawnTestDrop(prefab, origin, false);
                landed = FeaturesSmokeCommand.SpawnTestDrop(prefab, origin + transform.forward * 0.6f, false);
                if (swimming == null || landed == null)
                {
                    run.Skip(FishCheck, "a test drop could not be set up");
                    yield break;
                }

                var swimmingFish = swimming.GetComponent<Fish>();
                var landedFish = landed.GetComponent<Fish>();
                if (swimmingFish == null || landedFish == null)
                {
                    run.Skip(FishCheck, $"the spawned {FishPrefab} carries no Fish component");
                    yield break;
                }

                // In the frame they spawn, before any water volume has written into them: from
                // here on the game's water-level writes into the two fish are dropped, so the
                // levels below hold also by the water. Without runtime unpatching the fish go
                // unguarded, and FishFixtureProblem below still tells a broken fixture apart.
                if (ModuleRegistry.RuntimeToggleSupported)
                {
                    if (!WaterGuard.Attach(swimmingFish, landedFish, out var problem))
                    {
                        run.Skip(FishCheck, problem);
                        yield break;
                    }

                    guarded = true;
                }

                // The water level Fish keeps for itself, set above the fish: what the game's water
                // volume writes into it when the fish is in water.
                var level = swimming.transform.position.y + 10f;
                Reflections.SetField(swimmingFish, "m_inWater", level);
                if (swimmingFish.IsOutOfWater() || !landedFish.IsOutOfWater())
                {
                    run.Skip(FishCheck, "inconclusive: the test fish could not be put in and out of water");
                    yield break;
                }

                var key = PickupConfig.PickupAllNearbyKey;
                if (key.MainKey != KeyCode.None)
                {
                    var line = $"[<color=yellow><b>{key}</b></color>]";
                    if (swimming.GetHoverText().Contains(line))
                    {
                        run.Fail(FishCheck, "the hover text of a fish in water shows the Pickup All Nearby line");
                        yield break;
                    }

                    if (!landed.GetHoverText().Contains(line))
                    {
                        run.Fail(FishCheck, "the hover text of a fish out of water lacks the Pickup All Nearby line");
                        yield break;
                    }
                }

                var paused = Game.IsPaused();
                var started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player &&
                       !(swimming.CanPickup() && landed.CanPickup()) &&
                       !AnyConsumed(swimming, landed) &&
                       Time.realtimeSinceStartup - started < PickableTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                }

                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player &&
                       Time.time - Reflections.GetField<float>(player, "m_lastHoverInteractTime") < HoverInteractCooldown &&
                       Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                    yield return null;

                if (!run.IsCurrent || !player)
                {
                    run.Skip(FishCheck, "inconclusive: the run was abandoned or the player went away before the pass");
                    yield break;
                }

                if (AnyConsumed(swimming, landed))
                {
                    run.Fail(FishCheck,
                        "a test fish disappeared before the pass; its m_autoPickup was false, so neither vanilla auto-pickup nor periodic pickup should have taken it");
                    yield break;
                }

                if (!(swimming.CanPickup() && landed.CanPickup()))
                {
                    if (paused)
                        run.Skip(FishCheck,
                            $"inconclusive: the game was paused while the check waited up to {PickableTimeoutSeconds:0.#}s for the fish to become pickable");
                    else
                        run.Fail(FishCheck, $"the fish did not become pickable within {PickableTimeoutSeconds:0.#}s");
                    yield break;
                }

                if (!PickableHarvester.CanAddItem(player, landed.m_itemData, 2))
                {
                    run.Skip(FishCheck, "the inventory cannot take two fish (space or weight); make room to include this check");
                    yield break;
                }

                var distances = $"swimming {Distance(player, swimming):0.00} m, landed {Distance(player, landed):0.00} m, range {range:0.#} m";
                if (Distance(player, swimming) > range - 0.1f || Distance(player, landed) > range - 0.1f)
                {
                    run.Skip(FishCheck, $"inconclusive: a fish moved out of automatic_pickup_range before the pass ({distances})");
                    yield break;
                }

                var fixture = FishFixtureProblem(swimming, level, landed);
                if (fixture != null)
                {
                    run.Skip(FishCheck, $"inconclusive: before the pass, {fixture}; {FixtureHint(guarded)}");
                    yield break;
                }

                var before = PickableHarvester.CountExactItems(inventory, sample);
                fishMayBeLeft = true;

                // On the fish in water: no request, so a pass that took both fish of this name
                // would show here. The fixture is read on every frame until a fish is taken.
                component.RequestKeyPickup(swimming.gameObject);
                string broken = null;
                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && Time.realtimeSinceStartup - started < SettleSeconds + 0.3f &&
                       !AnyConsumed(swimming, landed))
                {
                    yield return null;
                    if (broken == null) broken = FishFixtureProblem(swimming, level, landed);
                }

                if (!player)
                {
                    run.Skip(FishCheck, "inconclusive: the local player went away during the pass");
                    yield break;
                }

                var delta = PickableHarvester.CountExactItems(inventory, sample) - before;
                if (delta <= 0) fishMayBeLeft = false;
                if (AnyConsumed(swimming, landed) || delta != 0)
                {
                    var taken =
                        $"swimming taken={FeaturesSmokeCommand.IsConsumed(swimming)}, landed taken={FeaturesSmokeCommand.IsConsumed(landed)}, fishDelta={delta}";
                    if (broken != null)
                        run.Skip(FishCheck,
                            $"inconclusive: before a fish was taken, {broken}; {FixtureHint(guarded)} ({taken})");
                    else
                        run.Fail(FishCheck, $"a key pass requested on a fish in water took fish ({taken})");
                    yield break;
                }

                fixture = FishFixtureProblem(swimming, level, landed);
                if (fixture != null)
                {
                    run.Skip(FishCheck, $"inconclusive: before the second pass, {fixture}; {FixtureHint(guarded)}");
                    yield break;
                }

                // On the fish out of water: that fish is taken, the one in water stays.
                fishMayBeLeft = true;
                component.RequestKeyPickup(landed.gameObject);
                // Read only while the fish in water is still there: a fixture that breaks after it
                // was taken cannot explain that it was taken.
                broken = null;
                paused = Game.IsPaused();
                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && !FeaturesSmokeCommand.IsConsumed(landed) &&
                       Time.realtimeSinceStartup - started < KeyPassTimeoutSeconds)
                {
                    yield return null;
                    if (Game.IsPaused()) paused = true;
                    if (broken == null && !FeaturesSmokeCommand.IsConsumed(swimming))
                        broken = FishFixtureProblem(swimming, level, landed);
                }

                started = Time.realtimeSinceStartup;
                while (run.IsCurrent && player && Time.realtimeSinceStartup - started < SettleSeconds)
                {
                    yield return null;
                    if (broken == null && !FeaturesSmokeCommand.IsConsumed(swimming))
                        broken = FishFixtureProblem(swimming, level, landed);
                }

                if (!player)
                {
                    run.Skip(FishCheck, "inconclusive: the local player went away during the pass");
                    yield break;
                }

                delta = PickableHarvester.CountExactItems(inventory, sample) - before;
                if (delta <= 0) fishMayBeLeft = false;
                var landedTaken = FeaturesSmokeCommand.IsConsumed(landed);
                var swimmingTaken = FeaturesSmokeCommand.IsConsumed(swimming);
                if (delta == 1 && landedTaken && !swimmingTaken &&
                    FeaturesSmokeCommand.RemoveExactItems(inventory, sample, 1) == 1)
                    fishMayBeLeft = false;
                var notes = fishMayBeLeft
                    ? "; the picked-up test fish could not be told apart from your own and was left in the inventory"
                    : "";

                if ((!landedTaken || swimmingTaken) && broken != null)
                {
                    run.Skip(FishCheck,
                        $"inconclusive: during the second pass, {broken}; {FixtureHint(guarded)} (landed taken={landedTaken}, swimming taken={swimmingTaken}){notes}");
                    yield break;
                }

                if (!landedTaken)
                {
                    if (paused && run.IsCurrent)
                        run.Skip(FishCheck, "inconclusive: the game was paused during the pass");
                    else
                        run.Fail(FishCheck,
                            $"a key pass requested on a fish out of water did not take it within {KeyPassTimeoutSeconds:0.#}s{notes}");
                    yield break;
                }

                if (swimmingTaken)
                {
                    run.Fail(FishCheck,
                        $"a key pass requested on a fish out of water also took the fish in water{notes}");
                    yield break;
                }

                if (delta != 1)
                {
                    run.Skip(FishCheck,
                        $"inconclusive: the fish in the inventory grew by {delta}, not 1, so a fish of your own was nearby{notes}");
                    yield break;
                }

                run.Pass(FishCheck,
                    $"a fish in water got no key pass and no hover line; a key pass on a fish out of water took it and left the one in water{notes}");
            }
            finally
            {
                if (WaterGuard.Detach(out var failure)) SmokeSession.ReleaseHandBack(handBack);
                else run.Fail(FishGuardCheck, $"the patch that keeps the test fish's water levels could not be removed ({failure}); it is retried at the next hand-back");

                if (fishMayBeLeft)
                    FeaturesSmokeCommand.Warn(
                        $"[Automatics Smoke] {FishCheck}: the fish the pass took could not be attributed to the test drops, so it was left in the inventory");
                FeaturesSmokeCommand.RemoveTestDrop(swimming, FishCheck);
                FeaturesSmokeCommand.RemoveTestDrop(landed, FishCheck);
            }
        }

        private static string FixtureHint(bool guarded)
        {
            return guarded
                ? "the patch that drops the game's water-level writes into the two test fish was in place, so something else changed them"
                : "this Harmony build cannot remove patches at runtime, so the game's water-level writes into the test fish were not blocked: a water volume around a fish writes its own level into it on every update, so run this check away from water";
        }

        // While a check holds its two test fish, the game's water-level writes into them are
        // dropped. Every write to Fish.m_inWater goes through Fish.SetLiquidLevel, which a water
        // volume calls on every update for a fish inside its trigger and when the fish leaves it
        // (WaterVolume.UpdateFloaters, OnTriggerExit, OnDestroy), and a liquid surface likewise;
        // without the patch, near water the level the check wrote would not hold. The patch also
        // keeps the water volume off the fish, so a fish standing in real water gets no buoyancy
        // and may sink there (unverified in game; the section moves to dry ground first). Other
        // fish are untouched, and the patch is removed when the check ends, or at the run's
        // hand-back when it is cut short.
        private static class WaterGuard
        {
            private const string HarmonyId = "net.eidee.automatics.smoke.features.pickup.fish";

            private static readonly HashSet<Fish> Guarded = new HashSet<Fish>();
            private static Harmony _harmony;

            public static bool Attach(Fish first, Fish second, out string problem)
            {
                problem = null;
                var method = AccessTools.Method(typeof(Fish), nameof(Fish.SetLiquidLevel));
                if (method == null)
                {
                    problem = "Fish.SetLiquidLevel was not found, so the test fish's water levels cannot be kept";
                    return false;
                }

                if (_harmony != null && !Detach(out var stale))
                {
                    problem = $"an earlier fish water patch could not be removed ({stale})";
                    return false;
                }

                Guarded.Add(first);
                Guarded.Add(second);
                _harmony = new Harmony(HarmonyId);
                try
                {
                    _harmony.Patch(method, new HarmonyMethod(typeof(WaterGuard), nameof(KeepLevel)));
                }
                catch (Exception e)
                {
                    problem = $"the fish water patch could not be added ({e.GetType().Name}: {e.Message})";
                    Detach(out _);
                    return false;
                }

                return true;
            }

            // A patch that cannot be removed stays guarding nothing.
            public static bool Detach(out string failure)
            {
                failure = null;
                Guarded.Clear();
                if (_harmony == null) return true;

                try
                {
                    Unpatch(_harmony);
                }
                catch (Exception e)
                {
                    failure = $"{e.GetType().Name}: {e.Message}";
                    return false;
                }

                _harmony = null;
                return true;
            }

            // Apart from Detach, so the call is only compiled where a patch was made.
            private static void Unpatch(Harmony harmony)
            {
                harmony.UnpatchSelf();
            }

            private static bool KeepLevel(Fish __instance)
            {
                return !Guarded.Contains(__instance);
            }
        }

        // Why the test fish are no longer one in water and one out of it, or null while they are.
        // A fish already taken is not read. A water volume whose trigger holds a fish writes the
        // real water level into it on every update (WaterVolume.UpdateFloaters), so without the
        // water patch the level the check wrote does not hold near water.
        private static string FishFixtureProblem(ItemDrop swimming, float level, ItemDrop landed)
        {
            if (!FeaturesSmokeCommand.IsConsumed(swimming))
            {
                var fish = swimming.GetComponent<Fish>();
                var read = Reflections.GetField<float>(fish, "m_inWater");
                var volume = Reflections.GetField<WaterVolume>(fish, "m_waterVolume") != null;
                if (read != level || volume || fish.IsOutOfWater())
                    return $"the fish set in water reads a water level of {read:0.00} m, not the {level:0.00} m written into it, " +
                           $"has {(volume ? "a" : "no")} water volume attached and is {(fish.IsOutOfWater() ? "out of" : "in")} water " +
                           $"(fish at {swimming.transform.position.y:0.00} m)";
            }

            if (!FeaturesSmokeCommand.IsConsumed(landed))
            {
                var fish = landed.GetComponent<Fish>();
                if (!fish.IsOutOfWater())
                    return $"the fish set out of water is in water (water level {Reflections.GetField<float>(fish, "m_inWater"):0.00} m, fish at {landed.transform.position.y:0.00} m)";
            }

            return null;
        }

        private static float Distance(Player player, ItemDrop drop)
        {
            return drop ? Vector3.Distance(player.transform.position, drop.transform.position) : -1f;
        }

        private static bool AnyConsumed(params ItemDrop[] drops)
        {
            foreach (var drop in drops)
                if (FeaturesSmokeCommand.IsConsumed(drop))
                    return true;
            return false;
        }
    }
}
