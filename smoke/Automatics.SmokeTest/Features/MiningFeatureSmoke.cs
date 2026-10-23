using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using MiningArea = Automatics.AutomaticMining.AreaMining;
using MiningComponent = Automatics.AutomaticMining.AutomaticMining;
using MiningConfig = Automatics.AutomaticMining.Config;
using MiningModule = Automatics.AutomaticMining.Module;

namespace Automatics.SmokeTest
{
    // Area mining through real swings: two deposits are spawned in front of the player and a
    // third just beyond the radius, a spawned pickaxe is equipped, and Humanoid.StartAttack starts
    // the attack its animation event carries into Attack.DoMeleeAttack. An observer patch, removed
    // afterwards, records the melee call, every rock damage call and every Pickaxes skill raise,
    // and drops any player hit on another object and any terrain operation a player hit spawns.
    // Before anything is spawned, the section sets itself up: it turns the module and area mining
    // on, restricts allow_mining_mineral to the first test kind with no deposit of your world in
    // reach, turns you to the first of eight headings with no building piece or other
    // destructible within reach, moves you to clear ground nearby when no kind or no heading
    // will do, and puts items aside for two free inventory slots; you are taken back to where the
    // command started at the end of the section.
    // The deposits get a health far above any hit, so no section breaks and nothing drops. Automatic
    // Repair is turned off for the section, because it puts a worn item back to full durability once
    // a second and would hide the pickaxe's wear between the frames a swing spans. Afterwards the added items are removed, the
    // previously equipped items are equipped again, and the view pitch and the Pickaxes skill are
    // put back.
    internal static class MiningFeatureSmoke
    {
        private const string Label = "mining feature smoke";
        private const float SeededHealth = 100000f;
        private const float StartTimeout = 1f;
        private const float SwingTimeout = 3f;
        private const float ViewPitch = 15f;
        private const float RockDistance = 1.8f;
        private const float ReachAhead = 1.5f;
        private const float ReachRadius = 4f;
        private const float OutsideMargin = 0.3f;

        // Deposits of the earlier biomes, smallest first; the first one with no deposit of its kind
        // of your world in reach is used. The names are checked against ZNetScene at run time.
        private static readonly string[] RockPrefabs =
            { "MineRock_Tin", "MineRock_Obsidian", "rock4_copper", "mudpile" };

        private static readonly string[] PickaxePrefabs = { "PickaxeIron", "PickaxeBronze", "PickaxeAntler" };
        private const string OtherWeaponPrefab = "Club";

        public static IEnumerator Run(FeatureRun run)
        {
            return WoodcuttingFeatureSmoke.ReturnAfter(run, "mining_section", RunSection(run));
        }

        private static IEnumerator RunSection(FeatureRun run)
        {
            var player = Player.m_localPlayer;
            if (!run.IsCurrent || !player || !ZNetScene.instance || !ObjectDB.instance ||
                !ZoneSystem.instance || !ZNet.instance || !ZNet.instance.IsServer() ||
                ZNet.instance.GetPeerConnections() > 0)
            {
                run.Skip("mining_section", "requires a loaded single-player world");
                yield break;
            }
            if (!ModuleRegistry.RuntimeToggleSupported)
            {
                run.Skip("mining_section",
                    "this Harmony build cannot remove patches at runtime, so the swing observer is not added");
                yield break;
            }
            if (player.InAttack() || player.IsSwimming() || !player.IsOnGround() || player.InPlaceMode() ||
                player.IsAttached() || player.IsDead())
            {
                run.Skip("mining_section",
                    "stand on open level ground, out of build mode, not attacking, swimming or attached");
                yield break;
            }

            var held = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            var state = new PlayerState(player);
            var rocks = new List<GameObject>();
            SmokeSetup.InventoryStash stash = null;
            Func<string, bool> handBack = _ => state.Restore();
            SmokeSession.TrackHandBack(handBack);
            try
            {
                // Before the module can start: its timer would mine with a pickaxe of yours.
                if (!TryPauseAutomaticMining(run, held))
                    yield break;

                var setup = new List<string>();
                var module = new SmokeSetup.SetupResult();
                var steps = SmokeSetup.EnableModule(run.RunId, held, MiningConfig.ModuleEntry,
                    () => MiningModule.Running, module);
                while (steps.MoveNext()) yield return steps.Current;
                if (!module.Ok)
                {
                    run.Skip("mining_section", $"Automatic Mining is not running: {module.Reason}");
                    yield break;
                }
                if (module.Changed) setup.Add("Automatic Mining turned on for the section");

                if (!MiningConfig.EnableAreaMining)
                {
                    var areaEntry = SmokeSetup.Field<bool>(typeof(MiningConfig), "_enableAreaMining");
                    if (areaEntry == null || !Write(run, held, areaEntry, true) || !MiningConfig.EnableAreaMining)
                    {
                        run.Skip("mining_section", "enable_area_mining is off and could not be turned on for the run");
                        yield break;
                    }
                    setup.Add("enable_area_mining turned on for the section");
                }

                if (!TryPauseAutomaticRepair(run, held))
                    yield break;

                var kinds = RockKinds(out var kindSkip);
                if (kinds.Count == 0)
                {
                    run.Skip("mining_section", kindSkip);
                    yield break;
                }

                var radius = MiningConfig.AreaMiningRadius;
                var reach = radius + RockDistance + 4f;
                var start = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                if (start.sqrMagnitude < 0.5f) start = Vector3.forward;
                var kind = PickKind(run, held, player, kinds, reach, out var refused, out var kindProblem);
                if (refused)
                {
                    run.Skip("mining_section", kindProblem);
                    yield break;
                }
                var heading = kind != null ? FindHeading(player, player.transform.position, start, out kindProblem) : null;
                if (heading == null)
                {
                    var minerals = WorldMinerals();
                    var move = new SmokeSetup.SetupResult();
                    steps = WoodcuttingFeatureSmoke.Settle(run, ReachAhead + ReachRadius,
                        p => SiteProblem(player, p, start, kinds, minerals, reach), kindProblem, move);
                    while (steps.MoveNext()) yield return steps.Current;
                    if (!move.Ok)
                    {
                        run.Skip("mining_section", move.Reason);
                        yield break;
                    }
                    if (move.Changed) setup.Add(move.Reason);

                    kind = PickKind(run, held, player, kinds, reach, out _, out kindProblem);
                    heading = kind != null ? FindHeading(player, player.transform.position, start, out kindProblem) : null;
                    if (heading == null)
                    {
                        run.Skip("mining_section", kindProblem);
                        yield break;
                    }
                }
                setup.Add($"allow_mining_mineral set to {kind.Identifier} alone for the section");

                var rockPrefab = kind.Prefab;
                var pickaxeName = PickPickaxe(kind.MinToolTier);
                var facing = heading.Value;
                setup.Add(WoodcuttingFeatureSmoke.Turn(start, facing));
                steps = WoodcuttingFeatureSmoke.Face(player, facing);
                while (steps.MoveNext()) yield return steps.Current;

                stash = SmokeSetup.Stash(player, 2);
                if (player.GetInventory().GetEmptySlots() < 2)
                {
                    run.Skip("mining_section",
                        $"needs two free inventory slots for the test pickaxe and club, and putting your unequipped items aside did not free them ({stash.Report})");
                    yield break;
                }
                setup.Add(stash.Report);
                run.Pass("mining_setup", string.Join("; ", setup));

                var right = Vector3.Cross(Vector3.up, facing);
                var first = player.transform.position + facing * RockDistance;
                var spacing = Mathf.Clamp(radius * 0.5f, 0.8f, 1.5f);
                foreach (var position in new[] { first, first + right * spacing })
                {
                    var go = SpawnRock(rockPrefab, position, facing);
                    if (go) rocks.Add(go);
                }
                if (rocks.Count != 2)
                {
                    run.Skip("mining_section", $"could not prepare two non-persistent {rockPrefab.name}");
                    yield break;
                }

                // A third deposit on the other side, its bounds a little beyond the radius from
                // the first one's, so a hit on the first two cannot reach it.
                yield return null;
                Physics.SyncTransforms();
                var width = HitColliders(rocks[0]).Select(x => x.bounds)
                    .Select(x => Mathf.Abs(x.extents.x * right.x) + Mathf.Abs(x.extents.z * right.z))
                    .DefaultIfEmpty(1f).Max();
                var outside = SpawnRock(rockPrefab, first - right * (2f * width + radius + OutsideMargin), facing);
                if (outside) rocks.Add(outside);

                // Destructible ignores damage in its first frame, and MineRock collects its hit
                // areas in Start.
                yield return null;
                yield return null;
                Physics.SyncTransforms();
                if (!rocks.All(x => x && x.GetComponent<MiningComponent>() && SeedHealth(x)))
                {
                    run.Skip("mining_section",
                        $"the spawned {rockPrefab.name} were not registered by Automatic Mining or have no health to seed");
                    yield break;
                }

                var pickaxe = state.Add(pickaxeName);
                if (pickaxe == null || !player.EquipItem(pickaxe, false))
                {
                    run.Skip("mining_section", $"could not add and equip a {pickaxeName}");
                    yield break;
                }
                yield return null;

                if (!SwingObserver.Attach(player, rocks, out var problem))
                {
                    run.Skip("mining_section", problem);
                    yield break;
                }

                var swing = new SwingResult();
                steps = Swing(player, pickaxe, facing, state, swing);
                while (steps.MoveNext()) yield return steps.Current;
                CheckPickaxeSwing(run, swing, rocks, pickaxe, radius);
                CheckOutsideRock(run, swing, rocks, outside, radius);

                var automatic = new SwingResult();
                steps = AutomaticMiningHit(player, pickaxe, rocks[0], automatic);
                while (steps.MoveNext()) yield return steps.Current;
                CheckNoExpansion(run, "mining_area_not_from_automatic_mining", automatic, rocks);

                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;
                var other = new SwingResult();
                var club = state.Add(OtherWeaponPrefab);
                if (club == null || !player.EquipItem(club, false))
                {
                    run.Skip("mining_area_not_from_other_weapons", $"could not add and equip a {OtherWeaponPrefab}");
                }
                else
                {
                    yield return null;
                    steps = Swing(player, club, facing, state, other);
                    while (steps.MoveNext()) yield return steps.Current;
                    CheckNoExpansion(run, "mining_area_not_from_other_weapons", other, rocks);
                }

                steps = WaitForAttackEnd(player);
                while (steps.MoveNext()) yield return steps.Current;

                run.Pass("mining_world_guarded",
                    $"blocked {SwingObserver.BlockedHits} hit(s) on objects other than the test rocks and {SwingObserver.BlockedTerrainOps} terrain operation(s)");
            }
            finally
            {
                try
                {
                    if (!SwingObserver.Detach(out var failure))
                        run.Fail("mining_observer_removed", failure);
                }
                finally
                {
                    try
                    {
                        if (state.Restore()) SmokeSession.ReleaseHandBack(handBack);
                        else run.Fail("mining_player_restored", "the previously equipped items could not all be equipped again; it is retried at the next hand-back");
                    }
                    finally
                    {
                        try
                        {
                            // After the test items are gone, so the slots they took are free.
                            stash?.Restore();
                            ReportSettings(run);
                        }
                        finally
                        {
                            foreach (var go in rocks)
                                if (go && SmokeSession.DestroyTestObject(go, 0, Label))
                                    SmokeSession.ReleaseSpawn(go);
                        }
                    }
                }
            }
        }

        // Automatic Mining's own timer would mine the test deposits with the test pickaxe while
        // the swings run. It is turned off for the section, through the session.
        private static bool TryPauseAutomaticMining(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held)
        {
            if (WoodcuttingFeatureSmoke.PauseAutomaticMining(run, held)) return true;

            run.Skip("mining_section",
                "enable_automatic_mining could not be turned off for the run, and its timer would mine the test rocks");
            return false;
        }

        // Automatic Repair would put the test pickaxe back to full durability between the frames a
        // swing spans, and so hide its wear. The module is turned off for the section, through the
        // session, and put back at its end.
        private static bool TryPauseAutomaticRepair(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held)
        {
            if (WoodcuttingFeatureSmoke.PauseAutomaticRepair(run, held)) return true;

            run.Skip("mining_section",
                "Automatic Repair could not be turned off for the run, and its repair tick would restore the pickaxe's durability between frames");
            return false;
        }

        private static void ReportSettings(FeatureRun run)
        {
            SmokeSetup.ReportSettings(run, "mining_settings_restored");
        }

        private static bool Write(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
            ConfigEntryBase entry, object value)
        {
            return SmokeSetup.Write(run, held, entry, value);
        }

        // -- The test kind ----------------------------------------------------------------------

        // A deposit prefab the section can spawn: its mineral identifier and the tool tier it needs.
        private sealed class RockKind
        {
            public GameObject Prefab;
            public string Identifier;
            public int MinToolTier;
        }

        // The deposit prefabs of RockPrefabs the loaded world has, each with its identifier and a
        // pickaxe prefab that reaches its tier, in order.
        private static List<RockKind> RockKinds(out string skip)
        {
            skip = "none of the test deposits is available with a mineral identifier and a pickaxe that reaches it (" +
                   string.Join(", ", RockPrefabs) + ")";
            var kinds = new List<RockKind>();
            foreach (var name in RockPrefabs)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                var component = prefab ? RockComponent(prefab) : null;
                var identifier = Identify(component);
                if (identifier == null) continue;

                int tier;
                switch (component)
                {
                    case MineRock rock: tier = rock.m_minToolTier; break;
                    case MineRock5 rock5: tier = rock5.m_minToolTier; break;
                    case Destructible destructible: tier = destructible.m_minToolTier; break;
                    default: continue;
                }
                if (PickPickaxe(tier) == null) continue;
                kinds.Add(new RockKind { Prefab = prefab, Identifier = identifier, MinToolTier = tier });
            }
            return kinds;
        }

        // The mineral identifier allow_mining_mineral knows the deposit by, as Automatic Mining
        // resolves it, or null.
        private static string Identify(Component component)
        {
            return component && ValheimObject.Mineral.GetIdentify(Objects.GetName(component), out var identifier)
                ? identifier
                : null;
        }

        // The first kind no deposit of your world within reach of the spot you stand on would let
        // area mining hit: allow_mining_mineral is written to each kind alone in turn, and area
        // mining's own collection answers. The list stays at the kind returned; for none, it holds
        // the last kind tried, and problem says why. refused is true when the list could not be
        // written at all.
        private static RockKind PickKind(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
            Player player, List<RockKind> kinds, float reach, out bool refused, out string problem)
        {
            refused = false;
            var entry = SmokeSetup.Field<StringList>(typeof(MiningConfig), "_allowMiningMineral");
            var nearby = new List<MiningArea.Target>();
            foreach (var kind in kinds)
            {
                if (entry == null || !Write(run, held, entry, new StringList(new[] { kind.Identifier })) ||
                    MiningConfig.AllowMiningMinerals.Count != 1 || !MiningConfig.AllowMiningMinerals.Contains(kind.Identifier))
                {
                    refused = true;
                    problem = $"allow_mining_mineral could not be set to {kind.Identifier} alone for the run";
                    return null;
                }

                nearby.Clear();
                MiningComponent.CollectAreaTargets(player, player.transform.position, reach, nearby);
                if (nearby.Count == 0)
                {
                    problem = null;
                    return kind;
                }
            }

            problem =
                $"a deposit of your world of each test kind ({string.Join(", ", kinds.Select(x => x.Identifier))}) is within {reach:0.#} m, and area mining could hit it";
            return null;
        }

        // The deposits of your world that Automatic Mining knows, by identifier, with their colliders.
        private static List<(string Identifier, Collider[] Colliders)> WorldMinerals()
        {
            return UnityEngine.Object.FindObjectsByType<MiningComponent>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(x => x)
                .Select(x => (Identify(RockComponent(x.gameObject)), x.GetComponentsInChildren<Collider>()))
                .Where(x => x.Item1 != null)
                .ToList();
        }

        // Whether a deposit of the kind has a collider whose bounds come within radius of center:
        // never nearer than area mining's own test finds it, so no deposit it would find is missed.
        private static bool MineralNear(List<(string Identifier, Collider[] Colliders)> minerals, string identifier,
            Vector3 center, float radius)
        {
            return minerals.Any(x => x.Identifier == identifier &&
                                     x.Colliders.Any(c => c && c.bounds.SqrDistance(center) <= radius * radius));
        }

        // -- Swings ---------------------------------------------------------------------------

        private static IEnumerator Swing(Player player, ItemDrop.ItemData weapon, Vector3 facing,
            PlayerState state, SwingResult result)
        {
            SwingObserver.Reset();
            result.DurabilityBefore = weapon.m_durability;
            result.ExpandedBefore = MiningArea.ExpandedHitCount;

            var started = false;
            var deadline = Time.time + StartTimeout;
            while (true)
            {
                player.SetLookDir(facing);
                state.LookDown(ViewPitch);
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

            // Rock damage runs through an RPC to the owner, this peer.
            yield return null;
            yield return null;
            result.Capture(weapon);
        }

        // The hit path Automatic Mining's hooks take, outside any swing.
        private static IEnumerator AutomaticMiningHit(Player player, ItemDrop.ItemData pickaxe, GameObject rock,
            SwingResult result)
        {
            SwingObserver.Reset();
            result.DurabilityBefore = pickaxe.m_durability;
            result.ExpandedBefore = MiningArea.ExpandedHitCount;

            var method = AccessTools.Method(typeof(MiningComponent), "Mining",
                new[] { typeof(Player), typeof(ItemDrop.ItemData), typeof(float), typeof(IDestructible), typeof(Collider[]) });
            var target = rock.GetComponent<IDestructible>();
            var collider = HitColliders(rock).FirstOrDefault();
            if (method == null || target == null || !collider)
            {
                result.Problem = "Automatic Mining's hit path or a hit collider of the test rock was not found";
                yield break;
            }

            var mined = (bool)method.Invoke(null, new object[] { player, pickaxe, 10f, target, new[] { collider } });
            if (!mined)
            {
                result.Problem = "Automatic Mining's hit path found no clear line from your eyes to the test rock";
                yield break;
            }

            yield return null;
            yield return null;
            result.Capture(pickaxe);
        }

        private static IEnumerator WaitForAttackEnd(Player player)
        {
            var deadline = Time.time + SwingTimeout;
            while (player && player.InAttack() && Time.time < deadline)
                yield return null;
        }

        // -- Checks ---------------------------------------------------------------------------

        private static void CheckPickaxeSwing(FeatureRun run, SwingResult swing, List<GameObject> rocks,
            ItemDrop.ItemData pickaxe, float radius)
        {
            var names = new[]
            {
                "mining_area_expands", "mining_area_cap_and_radius", "mining_area_damage_applied",
                "mining_area_durability_per_target", "mining_area_skill_once"
            };
            var direct = swing.Records.Where(x => !x.Extra && IsTestRock(x, rocks)).ToList();
            if (!swing.Completed || direct.Count == 0)
            {
                var reason = swing.Completed
                    ? "the swing hit none of the test rocks; face open level ground and retry"
                    : swing.Problem;
                foreach (var name in names) run.Skip(name, reason);
                return;
            }

            var extras = swing.Records.Where(x => x.Extra).ToList();
            if (swing.Expanded >= 1 && extras.Count == swing.Expanded)
                run.Pass(names[0], $"{direct.Count} section(s) hit by the swing, {swing.Expanded} extra");
            else
                run.Fail(names[0],
                    $"expected at least one extra hit on the test rocks: {swing.Expanded} counted, {extras.Count} damage call(s) observed");

            var cap = MiningConfig.AreaMiningMaxTargets;
            var far = extras.Where(x => Vector3.Distance(x.Point, swing.Trigger) > radius + 0.01f).ToList();
            var directSections = new HashSet<Collider>(direct.Where(x => x.Collider).Select(x => x.Collider));
            var repeated = extras.Count(x => IsSection(x) && directSections.Contains(x.Collider)) +
                           (extras.Count - extras.Select(x => IsSection(x) ? (object)x.Collider : x.Target).Distinct().Count());
            var foreign = extras.Count(x => !IsTestRock(x, rocks));
            if (swing.Expanded <= cap && far.Count == 0 && repeated == 0 && foreign == 0)
                run.Pass(names[1], $"{swing.Expanded} extra of at most {cap}, all within {radius:0.##} m, none hit twice");
            else
                run.Fail(names[1],
                    $"extra={swing.Expanded} cap={cap}, beyond {radius:0.##} m={far.Count}, hit twice={repeated}, outside the test rocks={foreign}");

            var undamaged = extras.Count(x => !(ReadHealth(x.Target, x.Collider) < SeededHealth));
            if (extras.Count > 0 && undamaged == 0)
                run.Pass(names[2], $"all {extras.Count} extra target(s) lost health");
            else if (extras.Count == 0)
                run.Skip(names[2], "no extra hit to inspect");
            else
                run.Fail(names[2], $"{undamaged} of {extras.Count} extra target(s) kept their health");

            var shared = pickaxe.m_shared;
            var drain = shared.m_useDurability ? shared.m_useDurabilityDrain * Game.m_durabilityRate : 0f;
            var expected = drain * (1 + swing.Expanded);
            var delta = swing.DurabilityBefore - swing.DurabilityAfter;
            if (Mathf.Abs(delta - expected) <= 0.001f)
                run.Pass(names[3], $"durability -{delta:0.###} = {drain:0.###} x (1 + {swing.Expanded})");
            else
                run.Fail(names[3], $"durability -{delta:0.###}, expected {drain:0.###} x (1 + {swing.Expanded}) = {expected:0.###}");

            if (swing.RaiseCalls == 1)
                run.Pass(names[4]);
            else
                run.Fail(names[4], $"the Pickaxes skill was raised {swing.RaiseCalls} time(s) for one swing");
        }

        private static void CheckNoExpansion(FeatureRun run, string name, SwingResult swing, List<GameObject> rocks)
        {
            if (!swing.Completed)
            {
                run.Skip(name, swing.Problem);
                return;
            }
            var direct = swing.Records.Count(x => !x.Extra && IsTestRock(x, rocks));
            if (direct == 0)
            {
                run.Skip(name, "the hit reached none of the test rocks; face open level ground and retry");
                return;
            }

            var extras = swing.Records.Count(x => x.Extra);
            if (swing.Expanded == 0 && extras == 0 && !MiningArea.SwingActive)
                run.Pass(name, $"{direct} hit(s) on the test rocks, no extra hit");
            else
                run.Fail(name, $"{swing.Expanded} extra hit(s) counted, {extras} observed");
        }

        // The rock placed just beyond the radius takes no extra hit. Whether it really lies beyond
        // is decided by the physics engine's own sphere overlap at the swing's first mineral hit,
        // not by the geometry area mining uses.
        private static void CheckOutsideRock(FeatureRun run, SwingResult swing, List<GameObject> rocks,
            GameObject outside, float radius)
        {
            const string name = "mining_area_outside_radius_untouched";
            if (!outside)
            {
                run.Skip(name, "the rock beyond the radius could not be spawned");
                return;
            }
            if (!swing.Completed || !swing.Records.Any(x => !x.Extra && IsTestRock(x, rocks)))
            {
                run.Skip(name, swing.Completed ? "the swing hit none of the test rocks" : swing.Problem);
                return;
            }

            var colliders = new HashSet<Collider>(HitColliders(outside));
            var reached = Physics.OverlapSphere(swing.Trigger, radius, ~0, QueryTriggerInteraction.Collide)
                .Any(colliders.Contains);
            if (reached)
            {
                run.Skip(name, $"the rock placed beyond the radius lies within {radius:0.##} m of the hit; retry facing another way");
                return;
            }

            var component = RockComponent(outside);
            var damaged = colliders.Count(x => ReadHealth(component, x) < SeededHealth) +
                          swing.Records.Count(x => x.Extra && x.Target && x.Target.gameObject == outside);
            if (damaged == 0)
                run.Pass(name, $"the rock beyond {radius:0.##} m kept its health");
            else
                run.Fail(name, $"the rock beyond {radius:0.##} m of the hit took {damaged} hit(s) or lost health");
        }

        private static bool IsTestRock(DamageRecord record, List<GameObject> rocks)
        {
            return record.Target && rocks.Contains(record.Target.gameObject);
        }

        private static bool IsSection(DamageRecord record)
        {
            return record.Target is MineRock || record.Target is MineRock5;
        }

        // -- Test objects ---------------------------------------------------------------------

        // The first heading with nothing a swing could damage within reach of a player standing at
        // origin, or null and why not.
        private static Vector3? FindHeading(Player player, Vector3 origin, Vector3 facing, out string problem)
        {
            var heading = WoodcuttingFeatureSmoke.FindHeading(facing, h => FindBlocker(player, origin, h),
                out var blocker);
            problem = heading == null
                ? $"{blocker} is within the swing's reach in every heading, and a swing could damage it"
                : null;
            return heading;
        }

        // The site test of a move: a clear heading at the site, and a test kind with no deposit of
        // your world near it. allow_mining_mineral is not written for each site; a deposit counts
        // when its colliders' bounds come within reach, wider than area mining's own test, which
        // the section runs again where it settles.
        private static string SiteProblem(Player player, Vector3 site, Vector3 facing, List<RockKind> kinds,
            List<(string Identifier, Collider[] Colliders)> minerals, float reach)
        {
            if (FindHeading(player, site, facing, out var problem) == null) return problem;
            return kinds.Any(x => !MineralNear(minerals, x.Identifier, site, reach))
                ? null
                : $"a deposit of each test kind within {reach:0.#} m";
        }

        // Anything a swing could damage besides the test rocks: a building piece or another
        // destructible within reach in front of a player standing at origin. Returns its name, or
        // null.
        private static string FindBlocker(Player player, Vector3 origin, Vector3 facing)
        {
            var center = origin + facing * ReachAhead;
            foreach (var collider in Physics.OverlapSphere(center, ReachRadius, ~0, QueryTriggerInteraction.Ignore))
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

        private static string PickPickaxe(int minToolTier)
        {
            foreach (var name in PickaxePrefabs)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(name);
                var itemDrop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (itemDrop == null) continue;

                var shared = itemDrop.m_itemData.m_shared;
                if (shared.m_skillType == Skills.SkillType.Pickaxes && shared.m_toolTier >= minToolTier)
                    return name;
            }
            return null;
        }

        private static Component RockComponent(GameObject go)
        {
            return (Component)go.GetComponent<MineRock5>() ?? (Component)go.GetComponent<MineRock>() ??
                   go.GetComponent<Destructible>();
        }

        private static GameObject SpawnRock(GameObject prefab, Vector3 position, Vector3 facing)
        {
            position.y = ZoneSystem.instance.GetGroundHeight(position);
            var go = UnityEngine.Object.Instantiate(prefab, position, Quaternion.LookRotation(-facing));
            SmokeSession.TrackSpawn(go);
            if (!Objects.GetZNetView(go.transform, out var nview) || nview == null || !nview.IsValid() ||
                nview.GetZDO() == null)
            {
                if (SmokeSession.DestroyTestObject(go, 0, Label)) SmokeSession.ReleaseSpawn(go);
                return null;
            }
            if (!nview.IsOwner()) nview.ClaimOwnership();
            nview.GetZDO().Persistent = false;
            return go;
        }

        private static IEnumerable<Collider> HitColliders(GameObject rock)
        {
            IEnumerable<Collider> colliders;
            switch (RockComponent(rock))
            {
                case MineRock mineRock:
                    colliders = AccessTools.Field(typeof(MineRock), "m_hitAreas").GetValue(mineRock) as Collider[] ??
                                Array.Empty<Collider>();
                    break;
                case Component component when component:
                    colliders = component.GetComponentsInChildren<Collider>();
                    break;
                default:
                    colliders = Array.Empty<Collider>();
                    break;
            }
            return colliders.Where(x => x && x.enabled && x.gameObject.activeInHierarchy);
        }

        // Every section at a health no hit reaches, so none breaks and drops its ore.
        private static bool SeedHealth(GameObject rock)
        {
            if (!Objects.GetZNetView(rock.transform, out var nview) || nview == null || !nview.IsValid())
                return false;
            var zdo = nview.GetZDO();
            switch (RockComponent(rock))
            {
                case MineRock mineRock:
                {
                    if (!(AccessTools.Field(typeof(MineRock), "m_hitAreas").GetValue(mineRock) is Collider[] areas) ||
                        areas.Length == 0)
                        return false;
                    for (var i = 0; i < areas.Length; i++) zdo.Set("Health" + i, SeededHealth);
                    return true;
                }
                case MineRock5 mineRock5:
                {
                    if (!(AccessTools.Field(typeof(MineRock5), "m_hitAreas").GetValue(mineRock5) is IList areas) ||
                        areas.Count == 0)
                        return false;
                    var package = new ZPackage();
                    package.Write(areas.Count);
                    for (var i = 0; i < areas.Count; i++) package.Write(SeededHealth);
                    zdo.Set(ZDOVars.s_health, Convert.ToBase64String(package.GetArray()));
                    return true;
                }
                case Destructible _:
                    zdo.Set(ZDOVars.s_health, SeededHealth);
                    return true;
                default:
                    return false;
            }
        }

        // The stored health of the section or object, NaN when it cannot be read.
        private static float ReadHealth(Component target, Collider collider)
        {
            if (!target || !Objects.GetZNetView(target, out var nview) || nview == null || !nview.IsValid())
                return float.NaN;
            var zdo = nview.GetZDO();
            switch (target)
            {
                case MineRock mineRock:
                {
                    var index = AreaIndex(typeof(MineRock), mineRock, collider);
                    return index < 0 ? float.NaN : zdo.GetFloat("Health" + index, SeededHealth);
                }
                case MineRock5 mineRock5:
                {
                    var index = AreaIndex(typeof(MineRock5), mineRock5, collider);
                    var text = zdo.GetString(ZDOVars.s_health);
                    if (index < 0 || string.IsNullOrEmpty(text)) return float.NaN;
                    var package = new ZPackage(Convert.FromBase64String(text));
                    var count = package.ReadInt();
                    var health = float.NaN;
                    for (var i = 0; i < count; i++)
                    {
                        var value = package.ReadSingle();
                        if (i == index) health = value;
                    }
                    return health;
                }
                case Destructible _:
                    return zdo.GetFloat(ZDOVars.s_health, SeededHealth);
                default:
                    return float.NaN;
            }
        }

        private static int AreaIndex(Type type, Component rock, Collider collider)
        {
            var method = AccessTools.Method(type, "GetAreaIndex", new[] { typeof(Collider) });
            return method == null || !collider ? -1 : (int)method.Invoke(rock, new object[] { collider });
        }

        // -- Player state ---------------------------------------------------------------------

        // What the section changes on the player, and how it is put back. Restore is safe to call
        // again: each part is put back once.
        private sealed class PlayerState
        {
            private readonly Player _player;
            private readonly List<ItemDrop.ItemData> _equipped;
            private readonly List<ItemDrop.ItemData> _added = new List<ItemDrop.ItemData>();
            private readonly System.Reflection.FieldInfo _pitchField;
            private readonly float _pitch;
            private readonly bool _hadSkill;
            private readonly float _skillLevel;
            private readonly float _skillAccumulator;
            private bool _pitchChanged;
            private bool _skillRestored;

            public PlayerState(Player player)
            {
                _player = player;
                _equipped = player.GetInventory().GetEquippedItems().ToList();
                _pitchField = AccessTools.Field(typeof(Player), "m_lookPitch");
                if (_pitchField != null && _pitchField.FieldType == typeof(float))
                    _pitch = (float)_pitchField.GetValue(player);
                else
                    _pitchField = null;

                var skill = PickaxeSkill();
                _hadSkill = skill != null;
                _skillLevel = skill?.m_level ?? 0f;
                _skillAccumulator = skill?.m_accumulator ?? 0f;
            }

            public ItemDrop.ItemData Add(string prefabName)
            {
                var item = _player.GetInventory().AddItem(prefabName, 1, 1, 0, 0L, "", false);
                if (item != null) _added.Add(item);
                return item;
            }

            public void LookDown(float pitch)
            {
                if (_pitchField == null) return;
                _pitchField.SetValue(_player, pitch);
                _pitchChanged = true;
            }

            public bool Restore()
            {
                // After a logout the player object is gone; there is nothing left to restore.
                if (!_player) return true;

                var inventory = _player.GetInventory();
                foreach (var item in _added)
                {
                    if (_player.IsItemEquiped(item)) _player.UnequipItem(item, false);
                    if (inventory.ContainsItem(item)) inventory.RemoveItem(item);
                }

                var done = true;
                foreach (var item in _equipped)
                    if (inventory.ContainsItem(item) && !_player.IsItemEquiped(item) &&
                        !_player.EquipItem(item, false))
                        done = false;

                if (_pitchChanged)
                {
                    _pitchField.SetValue(_player, _pitch);
                    _pitchChanged = false;
                }

                if (!_skillRestored)
                {
                    var skill = PickaxeSkill();
                    if (skill != null)
                    {
                        skill.m_level = _hadSkill ? _skillLevel : 0f;
                        skill.m_accumulator = _hadSkill ? _skillAccumulator : 0f;
                    }
                    _skillRestored = true;
                }

                return done;
            }

            private Skills.Skill PickaxeSkill()
            {
                return _player.GetSkills()?.GetSkillList()
                    .FirstOrDefault(x => x.m_info != null && x.m_info.m_skill == Skills.SkillType.Pickaxes);
            }
        }

        // -- Observation ----------------------------------------------------------------------

        private struct DamageRecord
        {
            public Component Target;
            public Collider Collider;
            public Vector3 Point;
            public bool Extra;
        }

        private sealed class SwingResult
        {
            public bool Completed;
            public string Problem = "the swing did not run";
            public float DurabilityBefore;
            public float DurabilityAfter;
            public int ExpandedBefore;
            public int Expanded;
            public int RaiseCalls;
            public Vector3 Trigger;
            public List<DamageRecord> Records = new List<DamageRecord>();

            public void Capture(ItemDrop.ItemData weapon)
            {
                Completed = true;
                DurabilityAfter = weapon.m_durability;
                Expanded = MiningArea.ExpandedHitCount - ExpandedBefore;
                RaiseCalls = SwingObserver.RaiseCalls;
                Trigger = MiningArea.LastTriggerPoint;
                Records = SwingObserver.Records.ToList();
            }
        }

        // Patches added for the section only and removed at its end. Besides recording, they guard
        // the world while the section owns the player's attacks: a hit by the player on anything
        // but the test rocks is dropped, and so is a terrain operation a player hit would spawn
        // (a pickaxe digs into the ground it strikes). The attack itself, and every hit on the
        // test rocks, runs as in the game.
        private static class SwingObserver
        {
            private const string HarmonyId = "net.eidee.automatics.smoke.features.mining";

            private static Harmony _harmony;
            private static Player _player;
            private static readonly HashSet<GameObject> TestObjects = new HashSet<GameObject>();

            public static readonly List<DamageRecord> Records = new List<DamageRecord>();
            public static int MeleeCalls { get; private set; }
            public static int RaiseCalls { get; private set; }
            public static int BlockedHits { get; private set; }
            public static int BlockedTerrainOps { get; private set; }

            public static bool Attach(Player player, IEnumerable<GameObject> testObjects, out string problem)
            {
                problem = null;
                var melee = AccessTools.Method(typeof(Attack), "DoMeleeAttack");
                var raise = AccessTools.Method(typeof(Player), nameof(Player.RaiseSkill),
                    new[] { typeof(Skills.SkillType), typeof(float) });
                var damages = new[]
                {
                    AccessTools.Method(typeof(MineRock5), nameof(MineRock5.Damage)),
                    AccessTools.Method(typeof(MineRock), nameof(MineRock.Damage)),
                    AccessTools.Method(typeof(Destructible), nameof(Destructible.Damage))
                };
                var terrain = AccessTools.Method(typeof(Attack), nameof(Attack.SpawnOnHitTerrain));
                var guarded = GuardedDamageMethods();
                if (melee == null || raise == null || terrain == null || damages.Any(x => x == null) ||
                    guarded.Count == 0)
                {
                    problem = "Attack.DoMeleeAttack, Attack.SpawnOnHitTerrain, Player.RaiseSkill or a Damage method was not found";
                    return false;
                }
                if (_harmony != null && !Detach(out var stale))
                {
                    problem = $"an earlier swing observer could not be removed ({stale})";
                    return false;
                }

                _player = player;
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
                    _harmony.Patch(melee, postfix: new HarmonyMethod(typeof(SwingObserver), nameof(MeleePostfix)));
                    _harmony.Patch(raise, postfix: new HarmonyMethod(typeof(SwingObserver), nameof(RaisePostfix)));
                    foreach (var damage in damages)
                        _harmony.Patch(damage, new HarmonyMethod(typeof(SwingObserver), nameof(DamagePrefix)));
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
            private static List<System.Reflection.MethodInfo> GuardedDamageMethods()
            {
                var methods = new List<System.Reflection.MethodInfo>();
                foreach (var type in AccessTools.GetTypesFromAssembly(typeof(IDestructible).Assembly))
                {
                    if (type == null || type.IsInterface || !typeof(IDestructible).IsAssignableFrom(type))
                        continue;
                    System.Reflection.InterfaceMapping map;
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
                TestObjects.Clear();
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

            public static void Reset()
            {
                Records.Clear();
                MeleeCalls = 0;
                RaiseCalls = 0;
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
                return false;
            }

            private static void MeleePostfix(Humanoid ___m_character)
            {
                if (_player && ___m_character == _player) MeleeCalls++;
            }

            private static void RaisePostfix(Player __instance, Skills.SkillType skill)
            {
                if (_player && __instance == _player && skill == Skills.SkillType.Pickaxes) RaiseCalls++;
            }

            private static void DamagePrefix(Component __instance, HitData hit)
            {
                if (!_player || hit == null) return;
                Records.Add(new DamageRecord
                {
                    Target = __instance,
                    Collider = hit.m_hitCollider,
                    Point = hit.m_point,
                    Extra = MiningArea.Expanding
                });
            }
        }
    }
}
