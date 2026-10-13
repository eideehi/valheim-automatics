using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using WoodcuttingArea = Automatics.AutomaticWoodcutting.AreaWoodcutting;
using WoodcuttingComponent = Automatics.AutomaticWoodcutting.AutomaticWoodcutting;
using WoodcuttingConfig = Automatics.AutomaticWoodcutting.Config;
using WoodcuttingModule = Automatics.AutomaticWoodcutting.Module;

namespace Automatics.SmokeTest
{
    // The `woodcuttingarea` section of automatics_smoke_features: area woodcutting through real axe
    // swings. Non-persistent trees of an early species are spawned around the spot in front of
    // the player: the swing's target, a tree a little beyond the swing's reach, and two trees
    // that are felled at once into a log and a stump each, one of them of a second species. More
    // trees stand for each exclusion: one above the axe's tool tier, one of the second species,
    // and one just beyond the area radius. The section turns Automatic Mining's passes and
    // enable_automatic_woodcutting off, so area woodcutting runs with the passes off, leaves only
    // the test species in allow_woodcutting_tree, sets the area radius, and caps the extra hits
    // below the number of targets that qualify. It then starts real attacks with
    // Humanoid.StartAttack:
    //   - the first swing fells its target, whose fresh log must not be chosen, and expands up to
    //     the cap;
    //   - the second, with the axe worn down to one and a half hits, expands until the axe breaks;
    //   - the third, with every exclusion lifted, reaches each target the first one left out;
    //   - a club swing and a hit of the module's own entry expand nothing;
    //   - last, a log, a tree and a stump stand alone in front of you in turn, and what a real
    //     swing at each raised is compared with what a hit of the module's entry on it raises.
    // An observer patch, removed afterwards, records the melee call, every damage call on the test
    // objects, the point of each swing's first direct hit on an allowed test object, and every
    // skill raise and stamina use of the player. It drops any hit of the player on another object
    // and any terrain operation a player hit spawns, and takes the log and stump a felling of a
    // test tree spawns as test objects. The session holds a hand-back that removes it if the run
    // is cut short. Every test object gets a health no hit reaches, except the first swing's
    // target, so only the setup and that swing fell anything and nothing drops. Afterwards the
    // added items are removed, the previously equipped items are equipped again, and the view
    // pitch, the skills, your stamina and the settings are put back.
    internal static class WoodcuttingAreaFeatureSmoke
    {
        private const string Label = "woodcutting area feature smoke";
        private const string SectionCheck = "woodcuttingarea_section";
        private const string RestoreCheck = "woodcutting_area_settings_restored";
        private const float StartTimeout = 1f;
        private const float SwingTimeout = 3f;
        private const float ViewPitch = 5f;
        // Lower, so a swing reaches a stump.
        private const float StumpViewPitch = 30f;
        private const float TriggerDistance = 1.6f;
        private const float SideOffset = 1.7f;
        private const float ClearRadius = 1.2f;
        private const int Cap = 2;

        // Standing trees of the earlier biomes for the second species, in order of preference.
        private static readonly string[] SecondTreePrefabs = { "FirTree", "Pinetree_01", "Birch1", "Birch2", "Oak1" };

        public static IEnumerator Run(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ObjectDB.instance ||
                !ZoneSystem.instance || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip(SectionCheck, "requires a loaded single-player world");
                yield break;
            }
            if (!WoodcuttingModule.Running)
            {
                run.Skip(SectionCheck, "Automatic Woodcutting is not running; enable the module first");
                yield break;
            }
            if (!WoodcuttingConfig.EnableAreaWoodcutting)
            {
                run.Skip(SectionCheck, "enable_area_woodcutting is off; turn it on and retry");
                yield break;
            }
            if (!ModuleRegistry.RuntimeToggleSupported)
            {
                run.Skip(SectionCheck,
                    "this Harmony build cannot remove patches at runtime, so the swing observer is not added");
                yield break;
            }
            if (player.InAttack() || player.IsSwimming() || !player.IsOnGround() || player.InPlaceMode() ||
                player.IsAttached() || player.IsDead())
            {
                run.Skip(SectionCheck,
                    "stand on open level ground, out of build mode, not attacking, swimming or attached");
                yield break;
            }

            var fixture = Fixture.Plan(player, out var planSkip);
            if (fixture == null)
            {
                run.Skip(SectionCheck, planSkip);
                yield break;
            }
            var blocker = fixture.FindBlocker(player);
            if (blocker != null)
            {
                run.Skip(SectionCheck,
                    $"{blocker} is near the spots in front of you where the test trees go, and a swing could reach it; face open ground and retry");
                yield break;
            }
            var nearby = new List<WoodcuttingArea.Target>();
            WoodcuttingComponent.CollectAreaTargets(fixture.TriggerSpot, fixture.LiftedRadius + 2f, nearby);
            if (nearby.Count > 0)
            {
                run.Skip(SectionCheck,
                    $"an allowed tree, log or stump of your world is within {fixture.LiftedRadius + 2f:0.#} m of the spot in front of you, and area woodcutting could hit it; move away from trees and retry");
                yield break;
            }
            if (player.GetInventory().GetEmptySlots() < 2)
            {
                run.Skip(SectionCheck, "needs two free inventory slots for the test axe and club");
                yield break;
            }

            var held = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            var spawned = new List<GameObject>();
            var state = new WoodcuttingFeatureSmoke.PlayerState(player, WoodcuttingFeatureSmoke.RaisableSkills());
            Func<string, bool> handBack = _ => state.Restore();
            SmokeSession.TrackHandBack(handBack);
            try
            {
                if (!PrepareSettings(run, held, fixture, out var refusal))
                {
                    run.Skip(SectionCheck, refusal);
                    yield break;
                }

                if (!fixture.Spawn(spawned, out var spawnSkip))
                {
                    run.Skip(SectionCheck, spawnSkip);
                    yield break;
                }

                if (!SwingObserver.Attach(player, spawned, spawned, out var problem))
                {
                    run.Skip(SectionCheck, problem);
                    yield break;
                }

                if (!fixture.FellSetupTrees(spawned, out var fellSkip))
                {
                    run.Skip(SectionCheck, fellSkip);
                    yield break;
                }

                // A stump gets its component in its Start, and a log takes damage 0.2 s after it
                // spawns.
                var ready = Time.time + WoodcuttingFeatureSmoke.LogDelay;
                for (var frame = 0; frame < WoodcuttingFeatureSmoke.StartFrames || Time.time < ready; frame++)
                    yield return null;
                Physics.SyncTransforms();
                if (!fixture.Registered(out var registerSkip))
                {
                    run.Skip(SectionCheck, registerSkip);
                    yield break;
                }

                var axe = state.Add(fixture.AxeName);
                if (axe == null || !player.EquipItem(axe, false))
                {
                    run.Skip(SectionCheck, $"could not add and equip a {fixture.AxeName}");
                    yield break;
                }
                yield return null;

                var swings = new List<SwingResult>();

                // The first swing: capped, with the exclusions in place, felling its target. Until
                // the third swing, a direct hit on the second species does not open an expansion.
                SwingObserver.TriggerAllowed = go => !fixture.SecondSpeciesObjects.Contains(go);
                SetHealth(fixture.Trigger, 1f);
                var first = new SwingResult { Name = "first" };
                var steps = Swing(player, axe, fixture.Facing, ViewPitch, state, first, spawned);
                while (steps.MoveNext()) yield return steps.Current;
                swings.Add(first);
                fixture.CollectFresh(first);
                CheckFirstSwing(run, first, fixture, axe);

                // The second swing: the axe wears out during the expansion.
                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;
                var second = new SwingResult { Name = "second" };
                var shared = axe.m_shared;
                var drain = shared.m_useDurability ? shared.m_useDurabilityDrain * Game.m_durabilityRate : 0f;
                if (drain <= 0f || !Write(run, held, WoodcuttingConfig.AreaWoodcuttingMaxTargetsEntry, 32))
                {
                    run.Skip("woodcutting_area_stops_when_axe_breaks",
                        drain <= 0f ? "the test axe does not wear" : "area_woodcutting_max_targets could not be raised for the check");
                }
                else
                {
                    axe.m_durability = drain * 1.5f;
                    steps = Swing(player, axe, fixture.Facing, ViewPitch, state, second, spawned);
                    while (steps.MoveNext()) yield return steps.Current;
                    swings.Add(second);
                    axe.m_durability = axe.GetMaxDurability();
                    if (!player.IsItemEquiped(axe)) player.EquipItem(axe, false);
                    CheckAxeBreaks(run, second, fixture, drain);
                }

                // The third swing: every exclusion of the first lifted.
                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;
                var third = new SwingResult { Name = "third" };
                if (!fixture.LiftExclusions(run, held, out var liftSkip))
                {
                    run.Skip("woodcutting_area_excluded_hittable", liftSkip);
                }
                else
                {
                    SwingObserver.TriggerAllowed = go => true;
                    steps = Swing(player, axe, fixture.Facing, ViewPitch, state, third, spawned);
                    while (steps.MoveNext()) yield return steps.Current;
                    swings.Add(third);
                    CheckExcludedHittable(run, first, second, third, fixture);
                }
                CheckKindsExpanded(run, swings);
                CheckTriggerPoints(run, swings);

                CheckNotFromEntry(run, player, fixture);

                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;
                var other = new SwingResult { Name = "club" };
                var club = state.Add(WoodcuttingFeatureSmoke.OtherWeaponPrefab);
                if (club == null || !player.EquipItem(club, false))
                {
                    run.Skip("woodcutting_area_not_from_other_weapons",
                        $"could not add and equip a {WoodcuttingFeatureSmoke.OtherWeaponPrefab}");
                }
                else
                {
                    yield return null;
                    steps = Swing(player, club, fixture.Facing, ViewPitch, state, other, spawned);
                    while (steps.MoveNext()) yield return steps.Current;
                    CheckNoExpansion(run, "woodcutting_area_not_from_other_weapons", other);
                }

                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;
                player.EquipItem(axe, false);
                yield return null;
                steps = SkillRule(run, player, axe, fixture, state, spawned);
                while (steps.MoveNext()) yield return steps.Current;

                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;

                run.Pass("woodcutting_area_world_guarded",
                    $"blocked {SwingObserver.BlockedHits} hit(s) on objects other than the test objects and {SwingObserver.BlockedTerrainOps} terrain operation(s)");
            }
            finally
            {
                try
                {
                    if (!SwingObserver.Detach(out var failure))
                        run.Fail("woodcutting_area_observer_removed", failure);
                }
                finally
                {
                    try
                    {
                        if (state.Restore()) SmokeSession.ReleaseHandBack(handBack);
                        else
                            run.Fail("woodcutting_area_player_restored",
                                "the previously equipped items could not all be equipped again; it is retried at the next hand-back");
                    }
                    finally
                    {
                        try
                        {
                            ReportSettings(run);
                        }
                        finally
                        {
                            foreach (var go in spawned) WoodcuttingFeatureSmoke.RemoveTestObject(go);
                        }
                    }
                }
            }
        }

        // Automatic Mining's passes off, as in the woodcutting section; enable_automatic_woodcutting
        // off, so no pass hits the test objects with the test axe
        // and the swings show area woodcutting working with the passes off; the area radius and
        // the cap of the first swing; and allow_woodcutting_tree with the test species alone, so
        // no other species keeps the second species' log or stump allowed.
        private static bool PrepareSettings(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Fixture fixture, out string refusal)
        {
            refusal = null;
            var allowed = new StringList(new[] { fixture.Species });
            if (!WoodcuttingFeatureSmoke.PauseAutomaticMining(run, held))
                refusal = "enable_automatic_mining could not be turned off for the run, and Automatic Mining could wear a pickaxe of yours while the section runs";
            else if (!Write(run, held, WoodcuttingConfig.EnableAutomaticWoodcuttingEntry, false) ||
                WoodcuttingConfig.EnableAutomaticWoodcutting)
                refusal = "enable_automatic_woodcutting could not be turned off for the run, and a pass would hit the test trees";
            else if (!Write(run, held, WoodcuttingConfig.AreaWoodcuttingRadiusEntry, fixture.Radius))
                refusal = $"area_woodcutting_radius could not be set to {fixture.Radius:0.#} for the run";
            else if (!Write(run, held, WoodcuttingConfig.AreaWoodcuttingMaxTargetsEntry, Cap))
                refusal = $"area_woodcutting_max_targets could not be set to {Cap} for the run";
            else if (!Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, allowed))
                refusal = "allow_woodcutting_tree could not be written for the run";
            return refusal == null;
        }

        private static bool Write(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
            ConfigEntryBase entry, object value)
        {
            return WoodcuttingFeatureSmoke.Write(run, held, entry, value);
        }

        private static void ReportSettings(FeatureRun run)
        {
            var results = SmokeSession.RestoreSettings(run.RunId);
            if (results.Count == 0) return;

            var detail = string.Join("; ", results.Select(x => x.Value));
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                run.Fail(RestoreCheck, detail);
            else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                      x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                run.Skip(RestoreCheck, "restoration skipped: " + detail);
            else
                run.Pass(RestoreCheck, detail);
        }

        // -- Swings ---------------------------------------------------------------------------

        private static IEnumerator Swing(Player player, ItemDrop.ItemData weapon, Vector3 facing, float pitch,
            WoodcuttingFeatureSmoke.PlayerState state, SwingResult result, List<GameObject> spawned)
        {
            SwingObserver.Reset();
            result.DurabilityBefore = weapon.m_durability;
            result.ExpandedBefore = WoodcuttingArea.ExpandedHitCount;
            result.SpawnedBefore = spawned.Count;
            // A swing needs stamina to start; your own is put back at the end.
            if (state.CanSetStamina) state.SetStamina(player.GetMaxStamina(), 0f);

            var started = false;
            var deadline = Time.time + StartTimeout;
            while (true)
            {
                player.SetLookDir(facing);
                state.LookDown(pitch);
                if (player.StartAttack(null, false))
                {
                    started = true;
                    break;
                }
                if (Time.time >= deadline) break;
                yield return null;
            }
            if (!started)
            {
                result.Problem =
                    $"the {weapon.m_shared.m_name} attack did not start within {StartTimeout}s (stamina, an action in progress or the weapon refused it)";
                yield break;
            }

            deadline = Time.time + SwingTimeout;
            while (SwingObserver.MeleeCalls == 0 && Time.time < deadline)
                yield return null;
            if (SwingObserver.MeleeCalls == 0)
            {
                result.Problem = $"no melee hit was triggered within {SwingTimeout}s of the attack start";
                yield break;
            }

            yield return null;
            yield return null;
            result.Capture(weapon, spawned);
        }

        private static IEnumerator WaitForAttackEnd(Player player)
        {
            var deadline = Time.time + SwingTimeout;
            while (player && player.InAttack() && Time.time < deadline)
                yield return null;
        }

        // -- Checks ---------------------------------------------------------------------------

        private static void CheckFirstSwing(FeatureRun run, SwingResult swing, Fixture fixture, ItemDrop.ItemData axe)
        {
            var names = new[]
            {
                "woodcutting_area_expands", "woodcutting_area_cap_and_radius", "woodcutting_area_damage_applied",
                "woodcutting_area_durability_per_target", "woodcutting_area_skill_once",
                "woodcutting_area_stamina_once", "woodcutting_area_with_passes_off",
                "woodcutting_area_fresh_log_skipped", "woodcutting_area_tool_tier_skipped",
                "woodcutting_area_species_skipped", "woodcutting_area_outside_radius_untouched"
            };
            var direct = swing.Records.Where(x => !x.Extra).ToList();
            if (!swing.Completed || direct.Count == 0)
            {
                var reason = swing.Completed
                    ? "the swing hit none of the test objects; face open level ground and retry"
                    : swing.Problem;
                foreach (var name in names) run.Skip(name, reason);
                return;
            }

            var extras = swing.Records.Where(x => x.Extra).ToList();
            // Distances are measured from the swing's first direct hit on an allowed test object,
            // as the observer saw it, not from the point area woodcutting reports.
            var centre = swing.ObservedTrigger;
            const string noCentre = "no direct hit on an allowed test object was seen, so there is no point to measure from";
            if (swing.Expanded >= 1 && extras.Count == swing.Expanded && swing.BlockedExtras == 0)
                run.Pass(names[0], $"{direct.Count} test object(s) hit by the swing, {swing.Expanded} extra");
            else
                run.Fail(names[0],
                    $"expected at least one extra hit on the test objects: {swing.Expanded} counted, {extras.Count} damage call(s) observed, {swing.BlockedExtras} on other objects");

            // The targets the cap leaves room for: those that qualify and the swing did not hit.
            var directObjects = new HashSet<GameObject>(direct.Select(x => x.Target ? x.Target.gameObject : null));
            var open = fixture.Positives.Where(x => x && !directObjects.Contains(x)).ToList();
            var extraObjects = extras.Select(x => x.Target ? x.Target.gameObject : null).ToList();
            var far = centre.HasValue
                ? extras.Where(x => Vector3.Distance(x.Point, centre.Value) > fixture.Radius + 0.01f).ToList()
                : new List<DamageRecord>();
            var repeated = extraObjects.Count - extraObjects.Distinct().Count() +
                           extraObjects.Count(directObjects.Contains);
            var stray = extraObjects.Count(x => !open.Contains(x));
            if (!centre.HasValue)
                run.Skip(names[1], noCentre);
            else if (open.Count <= Cap)
                run.Skip(names[1],
                    $"the swing hit {fixture.Positives.Count - open.Count} of the {fixture.Positives.Count} targets the cap is set below, so the cap could not be reached");
            else if (swing.Expanded == Cap && far.Count == 0 && repeated == 0 && stray == 0)
                run.Pass(names[1],
                    $"{swing.Expanded} extra hit(s), exactly the cap of {Cap} with {open.Count} targets qualifying, all within {fixture.Radius:0.##} m, none hit twice");
            else
                run.Fail(names[1],
                    $"extra={swing.Expanded} cap={Cap} qualifying={open.Count}, beyond {fixture.Radius:0.##} m={far.Count}, hit twice={repeated}, outside the qualifying targets={stray}");
            fixture.CapExcluded.Clear();
            fixture.CapExcluded.AddRange(open.Where(x => !extraObjects.Contains(x)));

            var undamaged = extras.Count(x => !(WoodcuttingFeatureSmoke.ReadHealth(x.Target) < WoodcuttingFeatureSmoke.SeededHealth));
            if (extras.Count > 0 && undamaged == 0)
                run.Pass(names[2], $"all {extras.Count} extra target(s) lost health");
            else if (extras.Count == 0)
                run.Skip(names[2], "no extra hit to inspect");
            else
                run.Fail(names[2], $"{undamaged} of {extras.Count} extra target(s) kept their health");

            var shared = axe.m_shared;
            var drain = shared.m_useDurability ? shared.m_useDurabilityDrain * Game.m_durabilityRate : 0f;
            var expected = drain * (1 + swing.Expanded);
            var delta = swing.DurabilityBefore - swing.DurabilityAfter;
            if (Mathf.Abs(delta - expected) <= 0.001f)
                run.Pass(names[3], $"durability -{delta:0.###} = {drain:0.###} x (1 + {swing.Expanded})");
            else
                run.Fail(names[3], $"durability -{delta:0.###}, expected {drain:0.###} x (1 + {swing.Expanded}) = {expected:0.###}");

            var bySkill = swing.Raises.GroupBy(x => x.Skill).ToList();
            if (bySkill.Count == 0)
                run.Skip(names[4], "the swing raised no skill, so there is no gain to count");
            else if (bySkill.All(x => x.Count() == 1))
                run.Pass(names[4], $"{string.Join(", ", bySkill.Select(x => x.Key.ToString()).ToArray())} raised once for the swing");
            else
                run.Fail(names[4],
                    $"skills raised during the swing: {string.Join(", ", bySkill.Select(x => $"{x.Key} x{x.Count()}").ToArray())}");

            if (swing.StaminaUses == 1)
                run.Pass(names[5], "stamina was used once for the swing");
            else
                run.Fail(names[5], $"stamina was used {swing.StaminaUses} time(s) for one swing");

            if (!WoodcuttingConfig.EnableAutomaticWoodcutting && swing.Expanded >= 1)
                run.Pass(names[6], "with enable_automatic_woodcutting off, the swing still expanded");
            else
                run.Fail(names[6],
                    $"enable_automatic_woodcutting is {(WoodcuttingConfig.EnableAutomaticWoodcutting ? "on" : "off")} and the swing expanded {swing.Expanded} time(s)");

            if (!fixture.FreshLog)
                run.Skip(names[7], "the swing did not fell its target into a fresh log");
            else
                CheckUntouched(run, names[7], swing, new[] { fixture.FreshLog }, "the log the swing's own felling spawned");

            CheckUntouched(run, names[8], swing, new[] { fixture.TierTree },
                "the tree above the axe's tool tier");
            CheckUntouched(run, names[9], swing, fixture.SecondSpeciesObjects,
                "the tree, log and stump of the species taken out of allow_woodcutting_tree");

            if (!centre.HasValue)
            {
                run.Skip(names[10], noCentre);
                return;
            }

            // Any collider of the tree counts here, so a doubtful placement skips rather than passes.
            var colliders = new HashSet<Collider>(fixture.FarTree.GetComponentsInChildren<Collider>());
            var reached = Physics.OverlapSphere(centre.Value, fixture.Radius, ~0, QueryTriggerInteraction.Collide)
                .Any(colliders.Contains);
            if (reached)
                run.Skip(names[10], $"the tree placed beyond the radius lies within {fixture.Radius:0.##} m of the hit; retry facing another way");
            else
                CheckUntouched(run, names[10], swing, new[] { fixture.FarTree },
                    $"the tree beyond {fixture.Radius:0.##} m of the hit, as the physics engine's own sphere overlap confirms");
        }

        // The given test objects took no hit in the swing and kept their health.
        private static void CheckUntouched(FeatureRun run, string name, SwingResult swing,
            IEnumerable<GameObject> objects, string what)
        {
            var list = objects.ToList();
            if (list.Count == 0 || list.Any(x => !x))
            {
                run.Skip(name, $"{what} could not be prepared");
                return;
            }

            var hits = swing.Records.Count(x => x.Target && list.Contains(x.Target.gameObject));
            var damaged = list.Count(x => !(WoodcuttingFeatureSmoke.ReadHealth(TargetOf(x)) >= WoodcuttingFeatureSmoke.SeededHealth));
            if (hits == 0 && damaged == 0)
                run.Pass(name, $"{what} took no hit and kept {(list.Count == 1 ? "its" : "their")} health");
            else
                run.Fail(name, $"{what}: {hits} hit(s), {damaged} lost health");
        }

        // With the axe worn down to one and a half hits, the swing's own hit leaves half a hit,
        // which lets exactly one extra target through before the axe has none left.
        private static void CheckAxeBreaks(FeatureRun run, SwingResult swing, Fixture fixture, float drain)
        {
            const string name = "woodcutting_area_stops_when_axe_breaks";
            if (!swing.Completed || swing.Records.All(x => x.Extra))
            {
                run.Skip(name, swing.Completed ? "the swing hit none of the test objects" : swing.Problem);
                return;
            }

            var direct = new HashSet<GameObject>(swing.Records.Where(x => !x.Extra && x.Target).Select(x => x.Target.gameObject));
            var open = fixture.Positives.Count(x => x && !direct.Contains(x));
            if (open < 2)
            {
                run.Skip(name, "fewer than two targets qualified, so the axe could not be seen to stop the expansion");
                return;
            }

            if (swing.Expanded == 1 && swing.DurabilityAfter <= 0f)
                run.Pass(name,
                    $"with durability for one and a half hits, the swing expanded once of {open} qualifying targets and left the axe at {swing.DurabilityAfter:0.###}");
            else
                run.Fail(name,
                    $"durability {swing.DurabilityBefore:0.###} ({drain:0.###} a hit): {swing.Expanded} extra hit(s) of {open} qualifying, durability after {swing.DurabilityAfter:0.###}");
        }

        // Each target the first swing left out, through the cap, its tool tier, its species or the
        // radius, takes a hit of the third swing once its exclusion is lifted, and the log the
        // first swing's felling spawned takes one in a later swing.
        private static void CheckExcludedHittable(FeatureRun run, SwingResult first, SwingResult second,
            SwingResult third, Fixture fixture)
        {
            const string name = "woodcutting_area_excluded_hittable";
            if (!third.Completed || third.Records.All(x => x.Extra))
            {
                run.Skip(name, third.Completed ? "the swing hit none of the test objects" : third.Problem);
                return;
            }

            // Only the colliders area woodcutting measures count here, so the far tree is expected
            // only when one of them lies inside the raised radius.
            var colliders = new HashSet<Collider>(fixture.FarTree.GetComponentsInChildren<Collider>()
                .Where(x => x.enabled && !x.isTrigger));
            var farInside = third.ObservedTrigger.HasValue &&
                            Physics.OverlapSphere(third.ObservedTrigger.Value, fixture.LiftedRadius, ~0,
                                    QueryTriggerInteraction.Ignore)
                                .Any(colliders.Contains);

            var expected = new List<(string What, GameObject Target)>();
            foreach (var go in fixture.CapExcluded) expected.Add(("a target the cap left out", go));
            expected.Add(("the tree above the axe's tool tier", fixture.TierTree));
            foreach (var go in fixture.SecondSpeciesObjects) expected.Add(("an object of the second species", go));
            if (farInside) expected.Add(("the tree beyond the first radius", fixture.FarTree));

            var hit = new HashSet<GameObject>(third.Records.Where(x => x.Target).Select(x => x.Target.gameObject));
            var missed = expected.Where(x => !x.Target || !hit.Contains(x.Target)).Select(x => x.What).ToList();
            var freshHit = !fixture.FreshLog || new[] { second, third }.Any(x =>
                x.Completed && x.Records.Any(y => y.Target && y.Target.gameObject == fixture.FreshLog));
            if (!freshHit) missed.Add("the log the first swing's felling spawned");

            if (missed.Count == 0)
                run.Pass(name,
                    $"once lifted, {expected.Count} excluded target(s) took a hit of the third swing{(farInside ? "" : " (the far tree stayed outside the raised radius and is not counted)")}{(fixture.FreshLog ? ", and the fresh log took one of a later swing" : "")}");
            else
                run.Fail(name, $"not hit once its exclusion was lifted: {string.Join(", ", missed.ToArray())}");
        }

        // Across the axe swings, area woodcutting's extra hits reached a standing tree, a log and a
        // stump.
        private static void CheckKindsExpanded(FeatureRun run, List<SwingResult> swings)
        {
            const string name = "woodcutting_area_trees_logs_stumps";
            var extras = swings.Where(x => x.Completed).SelectMany(x => x.Records).Where(x => x.Extra && x.Target).ToList();
            if (extras.Count == 0)
            {
                run.Skip(name, "no swing added an extra hit");
                return;
            }

            var trees = extras.Count(x => x.Target is TreeBase);
            var logs = extras.Count(x => x.Target is TreeLog);
            var stumps = extras.Count(x => x.Target is Destructible);
            if (trees > 0 && logs > 0 && stumps > 0)
                run.Pass(name, $"extra hits landed on {trees} tree(s), {logs} log(s) and {stumps} stump(s)");
            else
                run.Fail(name, $"extra hits landed on {trees} tree(s), {logs} log(s) and {stumps} stump(s); each kind should take one");
        }

        // Each axe swing that saw a direct hit on an allowed test object centred its expansion on
        // that hit's point, and no swing expanded without one.
        private static void CheckTriggerPoints(FeatureRun run, List<SwingResult> swings)
        {
            const string name = "woodcutting_area_trigger_point";
            var compared = 0;
            var problems = new List<string>();
            foreach (var swing in swings.Where(x => x.Completed))
            {
                if (!swing.ObservedTrigger.HasValue)
                {
                    if (swing.Expanded > 0)
                        problems.Add($"the {swing.Name} swing expanded, but no direct hit on an allowed test object was seen");
                    continue;
                }

                compared++;
                var gap = Vector3.Distance(swing.ObservedTrigger.Value, swing.Trigger);
                if (gap > 0.001f)
                    problems.Add($"the {swing.Name} swing centred its expansion {gap:0.###} m from its first direct hit on an allowed test object");
            }

            if (problems.Count > 0)
                run.Fail(name, string.Join("; ", problems.ToArray()));
            else if (compared == 0)
                run.Skip(name, "no swing hit an allowed test object itself");
            else
                run.Pass(name, $"in {compared} swing(s), the expansion was centred on the first direct hit on an allowed test object, as the observer saw it");
        }

        // A hit of the module's own entry on a test object in front of you expands nothing.
        private static void CheckNotFromEntry(FeatureRun run, Player player, Fixture fixture)
        {
            const string name = "woodcutting_area_not_from_automatic_woodcutting";
            SwingObserver.Reset();
            var expanded = WoodcuttingArea.ExpandedHitCount;
            WoodcuttingComponent chopped = null;
            foreach (var go in fixture.InFront())
            {
                var component = go ? go.GetComponent<WoodcuttingComponent>() : null;
                if (!component || !component.Chop(player)) continue;
                chopped = component;
                break;
            }

            if (!chopped)
            {
                run.Skip(name, "the module's entry hit none of the test objects in front of you");
                return;
            }

            var extras = SwingObserver.Records.Count(x => x.Extra);
            if (WoodcuttingArea.ExpandedHitCount == expanded && extras == 0 && !WoodcuttingArea.SwingActive)
                run.Pass(name, "a hit of the module's own entry on a test object expanded nothing");
            else
                run.Fail(name, $"{WoodcuttingArea.ExpandedHitCount - expanded} extra hit(s) counted, {extras} observed");
        }

        // The skills a hit of the module's entry raises on a log, a tree and a stump, each against
        // what a real axe swing raised when the only test object it hit itself was that same
        // target. Each target stands alone at the spot in front of you; a swing's extra hits raise
        // nothing, so they do not change what it raised.
        private static IEnumerator SkillRule(FeatureRun run, Player player, ItemDrop.ItemData axe, Fixture fixture,
            WoodcuttingFeatureSmoke.PlayerState state, List<GameObject> spawned)
        {
            var names = new[]
            {
                "woodcutting_area_skill_rule_log", "woodcutting_area_skill_rule_tree",
                "woodcutting_area_skill_rule_stump"
            };
            if (player.RightItem != axe)
            {
                foreach (var name in names) run.Skip(name, "the test axe could not be equipped again");
                yield break;
            }

            // The log the first swing's felling spawned, alone once its stump is gone.
            WoodcuttingFeatureSmoke.RemoveTestObject(fixture.FreshStump);
            IEnumerator steps;
            if (!fixture.FreshLog)
            {
                run.Skip(names[0], "the first swing did not fell its target into a log");
            }
            else
            {
                steps = CompareSkill(run, names[0], player, axe, fixture, state, spawned, fixture.FreshLog, ViewPitch);
                while (steps.MoveNext()) yield return steps.Current;
                WoodcuttingFeatureSmoke.RemoveTestObject(fixture.FreshLog);
            }

            var tree = fixture.SpawnFront(WoodcuttingFeatureSmoke.TreePrefab, spawned, out var skip);
            if (!tree)
            {
                run.Skip(names[1], skip);
            }
            else
            {
                steps = CompareSkill(run, names[1], player, axe, fixture, state, spawned, tree, ViewPitch);
                while (steps.MoveNext()) yield return steps.Current;
                WoodcuttingFeatureSmoke.RemoveTestObject(tree);
            }

            var stubPrefab = ZNetScene.instance.GetPrefab(WoodcuttingFeatureSmoke.TreePrefab)
                .GetComponent<TreeBase>().m_stubPrefab;
            var stump = fixture.SpawnFront(stubPrefab.name, spawned, out skip);
            if (!stump)
            {
                run.Skip(names[2], skip);
                yield break;
            }

            // The stump gets the module's component in its Start.
            for (var frame = 0; frame < WoodcuttingFeatureSmoke.StartFrames && !stump.GetComponent<WoodcuttingComponent>(); frame++)
                yield return null;
            steps = CompareSkill(run, names[2], player, axe, fixture, state, spawned, stump, StumpViewPitch);
            while (steps.MoveNext()) yield return steps.Current;
            WoodcuttingFeatureSmoke.RemoveTestObject(stump);
        }

        private static IEnumerator CompareSkill(FeatureRun run, string name, Player player, ItemDrop.ItemData axe,
            Fixture fixture, WoodcuttingFeatureSmoke.PlayerState state, List<GameObject> spawned, GameObject target,
            float pitch)
        {
            var component = target ? target.GetComponent<WoodcuttingComponent>() : null;
            if (!component)
            {
                run.Skip(name, "the target carries no component of the module");
                yield break;
            }

            var steps = WaitForAttackEnd(player);
            while (steps.MoveNext()) yield return steps.Current;
            Physics.SyncTransforms();
            axe.m_durability = axe.GetMaxDurability();
            var swing = new SwingResult { Name = name };
            steps = Swing(player, axe, fixture.Facing, pitch, state, swing, spawned);
            while (steps.MoveNext()) yield return steps.Current;
            if (!swing.Completed)
            {
                run.Skip(name, swing.Problem);
                yield break;
            }

            var direct = swing.Records.Where(x => !x.Extra).ToList();
            if (direct.Count == 0 || direct.Any(x => !x.Target || x.Target.gameObject != target) ||
                swing.BlockedDirect > 0)
            {
                run.Skip(name,
                    $"the swing did not hit the target alone ({direct.Count} hit(s) on test objects, {swing.BlockedDirect} on other objects); the spot in front of you could not be arranged");
                yield break;
            }

            SwingObserver.Reset();
            if (!component.Chop(player))
            {
                run.Skip(name, "the module's entry could not reach the target from your eyes");
                yield break;
            }

            var module = Describe(SwingObserver.Raises);
            var game = Describe(swing.Raises);
            var kind = target.GetComponent<TreeBase>() ? "tree" : target.GetComponent<TreeLog>() ? "log" : "stump";
            var type = component.Target.GetDestructibleType();
            if (module == game)
                run.Pass(name, $"on a {kind} (type {type}), the module's entry raised [{module}], as a real swing at it did");
            else
                run.Fail(name, $"on a {kind} (type {type}), the module's entry raised [{module}], a real swing at it [{game}]");
        }

        private static void CheckNoExpansion(FeatureRun run, string name, SwingResult swing)
        {
            if (!swing.Completed)
            {
                run.Skip(name, swing.Problem);
                return;
            }
            var direct = swing.Records.Count(x => !x.Extra);
            if (direct == 0)
            {
                run.Skip(name, "the hit reached none of the test objects; face open level ground and retry");
                return;
            }

            var extras = swing.Records.Count(x => x.Extra);
            if (swing.Expanded == 0 && extras == 0 && !WoodcuttingArea.SwingActive)
                run.Pass(name, $"{direct} hit(s) on the test objects, no extra hit");
            else
                run.Fail(name, $"{swing.Expanded} extra hit(s) counted, {extras} observed");
        }

        private static string Describe(IEnumerable<(Skills.SkillType Skill, float Value)> raises)
        {
            return string.Join(", ", raises.OrderBy(x => x.Skill).Select(x => $"{x.Skill} by {x.Value:0.###}").ToArray());
        }

        // -- Test objects ---------------------------------------------------------------------

        private static Component TargetOf(GameObject go)
        {
            return go ? go.GetComponent<IDestructible>() as Component : null;
        }

        private static void SetHealth(GameObject go, float health)
        {
            if (Objects.GetZNetView(go.transform, out var nview) && nview != null && nview.IsValid())
                nview.GetZDO().Set(ZDOVars.s_health, health);
        }

        // Where the test objects go, which of them qualify, and how the exclusions are lifted.
        private sealed class Fixture
        {
            public Vector3 Facing;
            public Vector3 TriggerSpot;
            public float Radius;
            public float LiftedRadius;
            public string Species;
            public string SecondSpecies;
            public string AxeName;
            public int AxeTier;

            public GameObject Trigger;
            public GameObject Near;
            public GameObject Felled;
            public GameObject TierTree;
            public GameObject SecondFelled;
            public GameObject SecondTree;
            public GameObject FarTree;
            public GameObject FreshLog;
            public GameObject FreshStump;

            public readonly List<GameObject> Positives = new List<GameObject>();
            public readonly List<GameObject> SecondSpeciesObjects = new List<GameObject>();
            public readonly List<GameObject> CapExcluded = new List<GameObject>();

            private string _secondPrefab;
            private Vector3 _origin;
            private Vector3 _right;
            private float _ring;
            private int _treeTier;

            // Picks the species, the axe and the spots, all from what the loaded world has.
            public static Fixture Plan(Player player, out string skip)
            {
                skip = null;
                var fixture = new Fixture();
                var species = WoodcuttingFeatureSmoke.SpeciesOf(WoodcuttingFeatureSmoke.TreePrefab);
                var tree = ZNetScene.instance.GetPrefab(WoodcuttingFeatureSmoke.TreePrefab)?.GetComponent<TreeBase>();
                if (!tree || species.Count != 1)
                {
                    skip = $"the {WoodcuttingFeatureSmoke.TreePrefab} prefab is not available or does not resolve to one species";
                    return null;
                }
                fixture.Species = species.First();

                foreach (var name in SecondTreePrefabs)
                {
                    var second = ZNetScene.instance.GetPrefab(name)?.GetComponent<TreeBase>();
                    if (!second || !second.m_logPrefab || !second.m_stubPrefab) continue;
                    var of = WoodcuttingFeatureSmoke.SpeciesOf(name);
                    if (of.Count != 1 || of.Contains(fixture.Species)) continue;
                    // Its log and stump must not be reached from the test species as well, or
                    // they stay allowed through it.
                    if (WoodcuttingFeatureSmoke.SpeciesOf(second.m_logPrefab.name).Contains(fixture.Species) ||
                        WoodcuttingFeatureSmoke.SpeciesOf(second.m_stubPrefab.name).Contains(fixture.Species))
                        continue;
                    if (WoodcuttingFeatureSmoke.PickAxe(Mathf.Max(tree.m_minToolTier, second.m_minToolTier), out _) == null)
                        continue;
                    fixture._secondPrefab = name;
                    fixture.SecondSpecies = of.First();
                    break;
                }
                if (fixture._secondPrefab == null)
                {
                    skip = $"no second tree species is available among {string.Join(", ", SecondTreePrefabs)}";
                    return null;
                }
                if (!tree.m_logPrefab || !tree.m_stubPrefab)
                {
                    skip = $"the {WoodcuttingFeatureSmoke.TreePrefab} prefab has no log or no stump";
                    return null;
                }

                var secondTier = ZNetScene.instance.GetPrefab(fixture._secondPrefab).GetComponent<TreeBase>().m_minToolTier;
                fixture._treeTier = tree.m_minToolTier;
                fixture.AxeName = WoodcuttingFeatureSmoke.PickAxe(Mathf.Max(tree.m_minToolTier, secondTier), out var axe);
                fixture.AxeTier = axe.m_toolTier;

                var facing = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                if (facing.sqrMagnitude < 0.5f) facing = Vector3.forward;
                fixture.Facing = facing;
                fixture._right = Vector3.Cross(Vector3.up, facing);
                fixture._origin = player.transform.position;
                var attack = axe.m_attack;
                // Beyond the swing's own reach, so only area woodcutting hits the targets there.
                fixture._ring = Mathf.Max(attack.m_attackRange + attack.m_attackRayWidth + 1.6f, 3.4f);
                fixture.Radius = Mathf.Min(fixture._ring + 1.5f, 7f);
                fixture.LiftedRadius = Mathf.Min(fixture.Radius + 3f, 10f);
                fixture.TriggerSpot = fixture._origin + facing * TriggerDistance;
                return fixture;
            }

            private IEnumerable<(string Prefab, Vector3 Spot)> Spots()
            {
                var f = Facing;
                var r = _right;
                var o = _origin;
                yield return (WoodcuttingFeatureSmoke.TreePrefab, o + f * TriggerDistance);
                yield return (WoodcuttingFeatureSmoke.TreePrefab, o + f * _ring);
                yield return (WoodcuttingFeatureSmoke.TreePrefab, o + f * (_ring - 0.3f) + r * SideOffset);
                yield return (WoodcuttingFeatureSmoke.TreePrefab, o + f * (_ring - 0.3f) - r * SideOffset);
                yield return (_secondPrefab, o + f * (_ring + 1.3f) + r * (SideOffset - 0.3f));
                yield return (_secondPrefab, o + f * (_ring + 1.3f) - r * (SideOffset - 0.3f));
                // Sideways, a little beyond the radius from the swing's target, and within the
                // raised radius of the third swing.
                yield return (WoodcuttingFeatureSmoke.TreePrefab,
                    o + f * TriggerDistance - r * Mathf.Min(Radius + 1.8f, LiftedRadius - 0.8f));
            }

            // Anything near the spots or in the swing's reach: a building piece or a destructible.
            // Returns its name, or null.
            public string FindBlocker(Player player)
            {
                var areas = Spots().Select(x => (x.Spot, ClearRadius))
                    .Concat(new[] { (_origin + Facing * (TriggerDistance * 0.5f), TriggerDistance + ClearRadius) });
                foreach (var (center, radius) in areas)
                foreach (var collider in Physics.OverlapSphere(center + Vector3.up, radius, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (!collider || collider.transform.IsChildOf(player.transform)) continue;

                    var piece = collider.GetComponentInParent<Piece>();
                    if (piece) return $"'{piece.m_name}' ({Objects.GetPrefabName(piece.gameObject)})";
                    var destructible = collider.GetComponentInParent<IDestructible>() as Component;
                    if (destructible)
                        return $"'{Objects.GetName(destructible)}' ({Objects.GetPrefabName(destructible.gameObject)})";
                }
                return null;
            }

            public bool Spawn(List<GameObject> spawned, out string skip)
            {
                skip = null;
                var objects = new List<GameObject>();
                foreach (var (prefab, spot) in Spots())
                {
                    var position = spot;
                    position.y = ZoneSystem.instance.GetGroundHeight(position);
                    var go = WoodcuttingFeatureSmoke.SpawnTestObject(prefab, position, Quaternion.identity, out skip);
                    if (!go) return false;
                    spawned.Add(go);
                    objects.Add(go);

                    var tree = go.GetComponent<TreeBase>();
                    tree.m_spawnOnDamage = null;
                    tree.m_dropWhenDestroyed = new DropTable();
                    if (!WoodcuttingFeatureSmoke.SeedHealth(tree))
                    {
                        skip = $"the spawned {prefab} has no health to seed";
                        return false;
                    }
                }

                Trigger = objects[0];
                Near = objects[1];
                Felled = objects[2];
                TierTree = objects[3];
                SecondFelled = objects[4];
                SecondTree = objects[5];
                FarTree = objects[6];
                TierTree.GetComponent<TreeBase>().m_minToolTier = AxeTier + 1;
                Physics.SyncTransforms();
                return true;
            }

            // The two trees that become a log and a stump each are felled at once, by a hit of
            // their own, not the player's.
            public bool FellSetupTrees(List<GameObject> spawned, out string skip)
            {
                skip = null;
                foreach (var (tree, positives) in new[] { (Felled, true), (SecondFelled, false) })
                {
                    var first = spawned.Count;
                    var component = tree.GetComponent<TreeBase>();
                    SetHealth(tree, 1f);
                    var hit = new HitData
                    {
                        m_toolTier = 100,
                        m_itemWorldLevel = (byte)Game.m_worldLevel,
                        m_point = tree.transform.position + Vector3.up,
                        m_dir = Facing
                    };
                    hit.m_damage.m_chop = 1000f;
                    component.Damage(hit);

                    var created = spawned.Skip(first).Where(x => x).ToList();
                    var log = created.FirstOrDefault(x => x.GetComponent<TreeLog>() &&
                                                          Objects.GetPrefabName(x) == component.m_logPrefab.name);
                    var stump = created.FirstOrDefault(x => x.GetComponent<Destructible>() &&
                                                            Objects.GetPrefabName(x) == component.m_stubPrefab.name);
                    if (tree.activeInHierarchy || !log || !stump)
                    {
                        skip = $"a setup tree was not felled into a log and a stump ({Objects.GetPrefabName(tree)})";
                        return false;
                    }

                    if (positives)
                    {
                        Positives.Add(log);
                        Positives.Add(stump);
                    }
                    else
                    {
                        SecondSpeciesObjects.Add(log);
                        SecondSpeciesObjects.Add(stump);
                    }
                }

                Positives.Add(Near);
                SecondSpeciesObjects.Add(SecondTree);
                return true;
            }

            // Every test object that should carry the module's component does, of the running
            // generation.
            public bool Registered(out string skip)
            {
                skip = null;
                var expected = Positives.Concat(SecondSpeciesObjects)
                    .Concat(new[] { Trigger, TierTree, FarTree }).ToList();
                var missing = expected.Count(x =>
                {
                    var component = x ? x.GetComponent<WoodcuttingComponent>() : null;
                    return !component || component.ScopeGeneration != WoodcuttingModule.Generation || !component.IsStanding();
                });
                if (missing == 0) return true;

                skip = $"{missing} of the {expected.Count} test trees, logs and stumps carry no component of the running generation or cannot take a hit yet";
                return false;
            }

            // The log and the stump the first swing's felling spawned, if any.
            public void CollectFresh(SwingResult swing)
            {
                var tree = ZNetScene.instance.GetPrefab(WoodcuttingFeatureSmoke.TreePrefab).GetComponent<TreeBase>();
                FreshLog = swing.Spawned.FirstOrDefault(x => x && x.GetComponent<TreeLog>() &&
                                                             Objects.GetPrefabName(x) == tree.m_logPrefab.name);
                FreshStump = swing.Spawned.FirstOrDefault(x => x && x.GetComponent<Destructible>() &&
                                                               Objects.GetPrefabName(x) == tree.m_stubPrefab.name);
            }

            // A test tree or stump at the spot in front of you, where the first swing's target
            // stood, at a health no hit reaches.
            public GameObject SpawnFront(string prefab, List<GameObject> spawned, out string skip)
            {
                var position = TriggerSpot;
                position.y = ZoneSystem.instance.GetGroundHeight(position);
                var go = WoodcuttingFeatureSmoke.SpawnTestObject(prefab, position, Quaternion.identity, out skip);
                if (!go) return null;
                spawned.Add(go);
                SwingObserver.Add(go);

                var tree = go.GetComponent<TreeBase>();
                if (tree)
                {
                    tree.m_spawnOnDamage = null;
                    tree.m_dropWhenDestroyed = new DropTable();
                }
                if (WoodcuttingFeatureSmoke.SeedHealth(TargetOf(go)))
                {
                    Physics.SyncTransforms();
                    return go;
                }

                skip = $"the spawned {prefab} has no health to seed";
                return null;
            }

            // The test objects in front of you, nearest first, for a hit of the module's entry.
            public IEnumerable<GameObject> InFront()
            {
                return new[] { FreshLog }.Concat(Positives).Concat(new[] { Trigger })
                    .Where(x => x && x.activeInHierarchy)
                    .OrderBy(x => Vector3.Distance(_origin, x.transform.position));
            }

            public bool LiftExclusions(FeatureRun run,
                Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, out string skip)
            {
                skip = null;
                if (TierTree) TierTree.GetComponent<TreeBase>().m_minToolTier = _treeTier;
                var allowed = new StringList(new[] { Species, SecondSpecies });
                if (!Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, allowed))
                    skip = $"allow_woodcutting_tree could not be given back the second species";
                else if (!Write(run, held, WoodcuttingConfig.AreaWoodcuttingRadiusEntry, LiftedRadius))
                    skip = $"area_woodcutting_radius could not be raised to {LiftedRadius:0.#}";
                else if (!Write(run, held, WoodcuttingConfig.AreaWoodcuttingMaxTargetsEntry, 32))
                    skip = "area_woodcutting_max_targets could not be raised";
                return skip == null;
            }
        }

        // -- Observation ----------------------------------------------------------------------

        private struct DamageRecord
        {
            public Component Target;
            public Collider Collider;
            public Vector3 Point;
            public DestructibleType Type;
            public bool Extra;
        }

        private sealed class SwingResult
        {
            public string Name;
            public bool Completed;
            public string Problem = "the swing did not run";
            public float DurabilityBefore;
            public float DurabilityAfter;
            public int ExpandedBefore;
            public int Expanded;
            public int SpawnedBefore;
            public int StaminaUses;
            public int BlockedExtras;
            public Vector3 Trigger;
            public Vector3? ObservedTrigger;
            public int BlockedDirect;
            public List<DamageRecord> Records = new List<DamageRecord>();
            public List<(Skills.SkillType Skill, float Value)> Raises = new List<(Skills.SkillType, float)>();
            public List<GameObject> Spawned = new List<GameObject>();

            public void Capture(ItemDrop.ItemData weapon, List<GameObject> spawned)
            {
                Completed = true;
                DurabilityAfter = weapon.m_durability;
                Expanded = WoodcuttingArea.ExpandedHitCount - ExpandedBefore;
                StaminaUses = SwingObserver.StaminaUses;
                BlockedExtras = SwingObserver.BlockedExtras;
                Trigger = WoodcuttingArea.LastTriggerPoint;
                ObservedTrigger = SwingObserver.ObservedTrigger;
                BlockedDirect = SwingObserver.BlockedDirect;
                Records = SwingObserver.Records.ToList();
                Raises = SwingObserver.Raises.ToList();
                Spawned = spawned.Skip(SpawnedBefore).ToList();
            }
        }

        // Patches added for the section only and removed at its end. Besides recording, they guard
        // the world while the section owns the player's attacks: a hit by the player on anything
        // but the test objects is dropped, and so is a terrain operation a player hit would spawn.
        // The attack itself, and every hit on the test objects, runs as in the game. The log and
        // stump a felling of a test tree spawns become test objects in the frame they spawn.
        private static class SwingObserver
        {
            private const string HarmonyId = "net.eidee.automatics.smoke.features.woodcuttingarea";

            private static Harmony _harmony;
            private static Player _player;
            private static TreeBase _felling;
            private static List<GameObject> _spawned;
            private static bool _inMelee;
            private static readonly HashSet<GameObject> TestObjects = new HashSet<GameObject>();

            // Registered with the session before any patch goes on, so a run cut short still
            // takes the patches off; released once they are off.
            private static readonly Func<string, bool> DetachHandBack = reason => Detach(out _);

            // Which test objects a direct hit may open an expansion on, as the section has set up
            // the allow list.
            public static Func<GameObject, bool> TriggerAllowed;

            // The point of the swing's first direct hit on an allowed test object.
            public static Vector3? ObservedTrigger { get; private set; }
            public static int BlockedDirect { get; private set; }

            public static readonly List<DamageRecord> Records = new List<DamageRecord>();
            public static readonly List<(Skills.SkillType Skill, float Value)> Raises =
                new List<(Skills.SkillType, float)>();
            public static int MeleeCalls { get; private set; }
            public static int StaminaUses { get; private set; }
            public static int BlockedExtras { get; private set; }
            public static int BlockedHits { get; private set; }
            public static int BlockedTerrainOps { get; private set; }

            public static bool Attach(Player player, IEnumerable<GameObject> testObjects, List<GameObject> spawned,
                out string problem)
            {
                problem = null;
                var melee = AccessTools.Method(typeof(Attack), "DoMeleeAttack");
                var raise = AccessTools.Method(typeof(Player), nameof(Player.RaiseSkill),
                    new[] { typeof(Skills.SkillType), typeof(float) });
                var stamina = AccessTools.Method(typeof(Player), nameof(Player.UseStamina), new[] { typeof(float) });
                var damages = new[]
                {
                    AccessTools.Method(typeof(TreeBase), nameof(TreeBase.Damage)),
                    AccessTools.Method(typeof(TreeLog), nameof(TreeLog.Damage)),
                    AccessTools.Method(typeof(Destructible), nameof(Destructible.Damage))
                };
                var terrain = AccessTools.Method(typeof(Attack), nameof(Attack.SpawnOnHitTerrain));
                var spawnLog = AccessTools.Method(typeof(TreeBase), "SpawnLog");
                var awakes = new[]
                {
                    AccessTools.Method(typeof(TreeLog), "Awake"),
                    AccessTools.Method(typeof(Destructible), "Awake")
                };
                var guarded = GuardedDamageMethods();
                if (melee == null || raise == null || stamina == null || terrain == null || spawnLog == null ||
                    damages.Any(x => x == null) || awakes.Any(x => x == null) || guarded.Count == 0)
                {
                    problem = "Attack.DoMeleeAttack, Attack.SpawnOnHitTerrain, Player.RaiseSkill, Player.UseStamina, TreeBase.SpawnLog, a Damage method or an Awake method was not found";
                    return false;
                }
                if (_harmony != null && !Detach(out var stale))
                {
                    problem = $"an earlier swing observer could not be removed ({stale})";
                    return false;
                }

                SmokeSession.TrackHandBack(DetachHandBack);
                _player = player;
                _spawned = spawned;
                TestObjects.Clear();
                foreach (var go in testObjects)
                    if (go) TestObjects.Add(go);
                Reset();
                BlockedHits = 0;
                BlockedTerrainOps = 0;
                _harmony = new Harmony(HarmonyId);
                try
                {
                    // The guards go on first, so no hit is recorded as landing that they drop.
                    _harmony.Patch(terrain, new HarmonyMethod(typeof(SwingObserver), nameof(TerrainGuard)));
                    foreach (var method in guarded)
                        _harmony.Patch(method, new HarmonyMethod(typeof(SwingObserver), nameof(DamageGuard)));
                    _harmony.Patch(melee, new HarmonyMethod(typeof(SwingObserver), nameof(MeleePrefix)),
                        new HarmonyMethod(typeof(SwingObserver), nameof(MeleePostfix)),
                        finalizer: new HarmonyMethod(typeof(SwingObserver), nameof(MeleeFinalizer)));
                    _harmony.Patch(raise, postfix: new HarmonyMethod(typeof(SwingObserver), nameof(RaisePostfix)));
                    _harmony.Patch(stamina, postfix: new HarmonyMethod(typeof(SwingObserver), nameof(StaminaPostfix)));
                    foreach (var damage in damages)
                        _harmony.Patch(damage, new HarmonyMethod(typeof(SwingObserver), nameof(DamagePrefix)));
                    _harmony.Patch(spawnLog, new HarmonyMethod(typeof(SwingObserver), nameof(SpawnLogPrefix)),
                        finalizer: new HarmonyMethod(typeof(SwingObserver), nameof(SpawnLogFinalizer)));
                    foreach (var awake in awakes)
                        _harmony.Patch(awake, postfix: new HarmonyMethod(typeof(SwingObserver), nameof(AwakePostfix)));
                }
                catch (Exception e)
                {
                    problem = $"the swing observer could not be added ({e.GetType().Name}: {e.Message})";
                    Detach(out _);
                    return false;
                }
                return true;
            }

            // Every implementation of IDestructible.Damage in the game assembly, each declared once.
            private static List<MethodInfo> GuardedDamageMethods()
            {
                var methods = new List<MethodInfo>();
                foreach (var type in AccessTools.GetTypesFromAssembly(typeof(IDestructible).Assembly))
                {
                    if (type == null || type.IsInterface || !typeof(IDestructible).IsAssignableFrom(type))
                        continue;
                    InterfaceMapping map;
                    try
                    {
                        map = type.GetInterfaceMap(typeof(IDestructible));
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        var method = map.TargetMethods[i];
                        if (map.InterfaceMethods[i].Name != nameof(IDestructible.Damage) ||
                            method.DeclaringType != type || method.IsAbstract || methods.Contains(method))
                            continue;
                        methods.Add(method);
                    }
                }
                return methods;
            }

            public static bool Detach(out string failure)
            {
                failure = null;
                _player = null;
                _felling = null;
                _spawned = null;
                _inMelee = false;
                TriggerAllowed = null;
                TestObjects.Clear();
                if (_harmony != null)
                {
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
                }

                SmokeSession.ReleaseHandBack(DetachHandBack);
                return true;
            }

            public static void Add(GameObject go)
            {
                if (go) TestObjects.Add(go);
            }

            // Apart from Detach, so the call is only compiled where a patch was made.
            private static void Unpatch(Harmony harmony)
            {
                harmony.UnpatchSelf();
            }

            public static void Reset()
            {
                Records.Clear();
                Raises.Clear();
                MeleeCalls = 0;
                StaminaUses = 0;
                BlockedExtras = 0;
                BlockedDirect = 0;
                ObservedTrigger = null;
            }

            private static bool TerrainGuard(Character character)
            {
                if (!_player || character != _player) return true;
                BlockedTerrainOps++;
                return false;
            }

            private static bool DamageGuard(object __instance, object[] __args)
            {
                if (!_player || __args == null || __args.Length == 0 || !(__args[0] is HitData hit) ||
                    hit.m_attacker != _player.GetZDOID())
                    return true;
                if (__instance is Component component && component && TestObjects.Contains(component.gameObject))
                    return true;
                BlockedHits++;
                if (WoodcuttingArea.Expanding) BlockedExtras++;
                else if (_inMelee) BlockedDirect++;
                return false;
            }

            private static void MeleePrefix(Humanoid ___m_character)
            {
                _inMelee = _player && ___m_character == _player;
            }

            private static void MeleePostfix(Humanoid ___m_character)
            {
                if (_player && ___m_character == _player) MeleeCalls++;
            }

            private static void MeleeFinalizer()
            {
                _inMelee = false;
            }

            private static void RaisePostfix(Player __instance, Skills.SkillType skill, float value)
            {
                if (_player && __instance == _player) Raises.Add((skill, value));
            }

            // Every call counts: the world's stamina rate can make the amount 0.
            private static void StaminaPostfix(Player __instance)
            {
                if (_player && __instance == _player) StaminaUses++;
            }

            private static void DamagePrefix(Component __instance, HitData hit)
            {
                if (!_player || hit == null || !__instance || !TestObjects.Contains(__instance.gameObject)) return;
                Records.Add(new DamageRecord
                {
                    Target = __instance,
                    Collider = hit.m_hitCollider,
                    Point = hit.m_point,
                    Type = ((IDestructible)__instance).GetDestructibleType(),
                    Extra = WoodcuttingArea.Expanding
                });

                if (_inMelee && !WoodcuttingArea.Expanding && !ObservedTrigger.HasValue &&
                    __instance.GetComponent<WoodcuttingComponent>() &&
                    (TriggerAllowed == null || TriggerAllowed(__instance.gameObject)))
                    ObservedTrigger = hit.m_point;
            }

            private static void SpawnLogPrefix(TreeBase __instance)
            {
                if (_player && __instance && TestObjects.Contains(__instance.gameObject)) _felling = __instance;
            }

            private static void SpawnLogFinalizer()
            {
                _felling = null;
            }

            private static void AwakePostfix(Component __instance)
            {
                if (!_player || !_felling || !__instance || TestObjects.Contains(__instance.gameObject)) return;

                var go = __instance.gameObject;
                TestObjects.Add(go);
                _spawned?.Add(go);
                WoodcuttingFeatureSmoke.AdoptFelled(go);
            }
        }
    }
}
