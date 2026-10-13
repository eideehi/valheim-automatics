using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using WoodcuttingComponent = Automatics.AutomaticWoodcutting.AutomaticWoodcutting;
using WoodcuttingConfig = Automatics.AutomaticWoodcutting.Config;
using WoodcuttingModule = Automatics.AutomaticWoodcutting.Module;

namespace Automatics.SmokeTest
{
    // The `woodcutting` section of automatics_smoke_features: Automatic Woodcutting's hit on one
    // tree it spawns. Before anything is equipped or spawned, woodcutting_interval is set to 0, so
    // no periodic pass runs while the section holds a test axe. A non-persistent tree is spawned in
    // front of the player with a health no hit reaches, so it never falls, and a test axe is added
    // and equipped. The module's own per-target entry then hits the tree once, and the hit data,
    // the axe's wear and the skill gain are checked. The same entry has to refuse, with nothing
    // spent, with no weapon, another weapon, a worn-out axe, a tree above the axe's tier, a wall
    // between the eyes and the tree, and the tree out of range. The interval hook runs no pass at
    // interval 0 and one pass at a positive interval, and one pass is run the way the key runs
    // it; both passes need the test tree to be the nearest tree or log.
    // An observer patch, removed at the end, records every damage call on the test tree and every
    // skill raise of the player, and drops every damage call on any other tree or log, whoever
    // makes it.
    // The whole section runs in one frame. Afterwards the added items are removed, the previously
    // equipped items are equipped again, the skills a hit can raise and the settings are put back,
    // and the test objects are removed.
    internal static class WoodcuttingFeatureSmoke
    {
        private const string Label = "woodcutting feature smoke";
        private const string SectionCheck = "woodcutting_section";
        private const string RestoreCheck = "woodcutting_settings_restored";
        private const float SeededHealth = 100000f;
        private const float TreeDistance = 2f;
        private const float ClearMargin = 1.5f;
        private const float OutOfRangeMargin = 0.25f;
        private const float PassInterval = 1f;

        private const string TreePrefab = "Beech1";
        private const string WallPrefab = "woodwall";
        private const string OtherWeaponPrefab = "Club";

        // Axes of the earlier biomes, lowest tier first; the first one whose tool tier reaches the
        // test tree is used. The names are checked against ObjectDB at run time.
        private static readonly string[] AxePrefabs = { "AxeStone", "AxeFlint", "AxeBronze", "AxeIron" };

        // The layers a melee swing hits besides characters, as vanilla Attack masks them. Wider
        // than the module's own, so a test tree on a layer the module does not look at fails the
        // hit check instead of skipping the section.
        private static readonly Lazy<int> SwingMask = new Lazy<int>(() =>
            LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid"));

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
            if (!ModuleRegistry.RuntimeToggleSupported)
            {
                run.Skip(SectionCheck,
                    "this Harmony build cannot remove patches at runtime, so the hit observer is not added");
                yield break;
            }

            var held = new Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration>();
            PlayerState state = null;
            GameObject treeObject = null;
            try
            {
                // First of all, before anything is equipped or spawned and before a frame passes:
                // a periodic pass would hit trees of your world with the test axe.
                if (WoodcuttingConfig.WoodcuttingInterval > 0f)
                    Write(run, held, WoodcuttingConfig.WoodcuttingIntervalEntry, 0f);
                if (WoodcuttingConfig.WoodcuttingInterval > 0f)
                {
                    run.Skip(SectionCheck,
                        "woodcutting_interval could not be set to 0 for the run, and a periodic pass would hit trees with the test axe");
                    yield break;
                }

                if (player.InAttack() || player.IsSwimming() || !player.IsOnGround() ||
                    player.InPlaceMode() || player.IsAttached() || player.IsDead())
                {
                    run.Skip(SectionCheck,
                        "stand on open level ground, out of build mode, not attacking, swimming or attached");
                    yield break;
                }

                var facing = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
                if (facing.sqrMagnitude < 0.5f) facing = Vector3.forward;
                var blocker = FindBlocker(player, facing);
                if (blocker != null)
                {
                    run.Skip(SectionCheck,
                        $"{blocker} is near the spot in front of you where the test tree goes; face open ground and retry");
                    yield break;
                }

                var treePrefab = ZNetScene.instance.GetPrefab(TreePrefab);
                var treeTemplate = treePrefab ? treePrefab.GetComponent<TreeBase>() : null;
                if (!treeTemplate)
                {
                    run.Skip(SectionCheck, $"the {TreePrefab} prefab is not available");
                    yield break;
                }

                var axeName = PickAxe(treeTemplate.m_minToolTier, out var axeShared);
                if (axeName == null)
                {
                    run.Skip(SectionCheck, $"no axe prefab reaches tool tier {treeTemplate.m_minToolTier}");
                    yield break;
                }

                var range = WoodcuttingConfig.WoodcuttingRange > 0
                    ? WoodcuttingConfig.WoodcuttingRange
                    : axeShared.m_attack.m_attackRange;
                if (range < TreeDistance)
                {
                    run.Skip(SectionCheck,
                        $"the woodcutting range ({range:0.##} m) does not reach the test tree {TreeDistance:0.#} m in front of you; set woodcutting_range to 3 and retry");
                    yield break;
                }

                if (player.GetInventory().GetEmptySlots() < 2)
                {
                    run.Skip(SectionCheck, "needs two free inventory slots for the test axe and club");
                    yield break;
                }

                var spot = player.transform.position + facing * TreeDistance;
                spot.y = ZoneSystem.instance.GetGroundHeight(spot);
                treeObject = SpawnTestObject(TreePrefab, spot, Quaternion.identity, out var spawnSkip);
                var tree = treeObject ? treeObject.GetComponent<TreeBase>() : null;
                if (!tree || !SeedHealth(tree))
                {
                    run.Skip(SectionCheck, spawnSkip ?? $"the spawned {TreePrefab} has no health to seed");
                    yield break;
                }
                // So a hit spawns nothing besides its effects.
                tree.m_spawnOnDamage = null;
                Physics.SyncTransforms();

                var component = treeObject.GetComponent<WoodcuttingComponent>();
                if (!component || component.ScopeGeneration != WoodcuttingModule.Generation)
                {
                    run.Fail("woodcutting_tree_registered",
                        $"the spawned {TreePrefab} carries {(component ? $"a component of generation {component.ScopeGeneration}" : "no component")}, not one of the running generation {WoodcuttingModule.Generation}");
                    yield break;
                }
                run.Pass("woodcutting_tree_registered",
                    $"the tree spawned while the module runs carries a component of generation {component.ScopeGeneration}");

                var obstacle = FindObstacle(player, tree);
                if (obstacle != null)
                {
                    run.Skip(SectionCheck, obstacle);
                    yield break;
                }

                state = new PlayerState(player, axeShared);
                var axe = state.Add(axeName);
                if (axe == null || !player.EquipItem(axe, false) || player.RightItem != axe)
                {
                    run.Skip(SectionCheck, $"could not add and equip a {axeName}");
                    yield break;
                }

                if (!HitObserver.Attach(player, treeObject, out var problem))
                {
                    run.Skip(SectionCheck, problem);
                    yield break;
                }

                var hit = CheckHit(run, player, component, tree, axe);

                CheckRefusals(run, player, component, tree, axe, state, hit);
                CheckIntervalZero(run, player, tree, axe);
                CheckIntervalPass(run, held, player, component, tree, axe);
                CheckKeyPass(run, player, component, tree, axe);
                CheckOutOfRange(run, held, player, component, tree, axe, hit);

                run.Pass("woodcutting_world_guarded",
                    $"dropped {HitObserver.BlockedHits} damage call(s) on trees and logs other than the test tree");
            }
            finally
            {
                try
                {
                    if (!HitObserver.Detach(out var failure))
                        run.Fail("woodcutting_observer_removed", failure);
                }
                finally
                {
                    try
                    {
                        if (state != null && !state.Restore())
                            run.Fail("woodcutting_player_restored",
                                "the previously equipped items could not all be equipped again");
                    }
                    finally
                    {
                        try
                        {
                            if (held.Count > 0) ReportRestore(run);
                        }
                        finally
                        {
                            RemoveTestObject(treeObject);
                        }
                    }
                }
            }
        }

        // -- Checks ---------------------------------------------------------------------------

        // One hit through the module's per-target entry, with the test axe in the right hand.
        // Returns the hit's record, or null when there was none.
        private static HitRecord CheckHit(FeatureRun run, Player player, WoodcuttingComponent component,
            TreeBase tree, ItemDrop.ItemData axe)
        {
            var names = new[]
            {
                "woodcutting_hit_damage", "woodcutting_hit_data", "woodcutting_hit_durability",
                "woodcutting_hit_skill_once"
            };

            var shared = axe.m_shared;
            var attack = shared.m_attack;
            var expectedSkill = attack.m_specialHitSkill != Skills.SkillType.None &&
                                (tree.GetDestructibleType() & attack.m_specialHitType) != DestructibleType.None
                ? attack.m_specialHitSkill
                : shared.m_skillType;

            var durability = axe.m_durability;
            HitObserver.Reset();
            var chopped = component.Chop(player);
            var health = ReadHealth(tree);
            var records = HitObserver.Records.ToList();

            if (!chopped || records.Count != 1 || records[0].Target != tree || !(health < SeededHealth))
            {
                run.Fail(names[0],
                    $"expected one hit on the test tree that takes health: entry returned {chopped}, {records.Count} damage call(s), health {health:0.##} of {SeededHealth:0}");
                foreach (var name in names.Skip(1)) run.Skip(name, "there was no single hit to inspect");
                return null;
            }

            var record = records[0];
            var eye = player.m_eye.position;
            run.Pass(names[0],
                $"one hit took {SeededHealth - health:0.##} health, {Vector3.Distance(eye, record.Point):0.##} m from your eyes and {record.Point.y - eye.y:0.##} m from eye height");

            var problems = new List<string>();
            if (record.Attacker != player.GetZDOID()) problems.Add("the attacker is not you");
            if (record.HitType != HitData.HitType.PlayerHit) problems.Add($"hit type {record.HitType}");
            if (record.PushForce != 0f) problems.Add($"push force {record.PushForce:0.###}");
            var toTree = Vector3.ProjectOnPlane(tree.transform.position - player.transform.position, Vector3.up)
                .normalized;
            if (Mathf.Abs(record.Dir.y) > 0.0001f || Mathf.Abs(record.Dir.magnitude - 1f) > 0.001f ||
                Vector3.Dot(record.Dir, toTree) < 0.9f)
                problems.Add($"direction {record.Dir} is not level and from you toward the tree {toTree}");
            if (record.Skill != expectedSkill) problems.Add($"skill {record.Skill}, expected {expectedSkill}");
            if (problems.Count == 0)
                run.Pass(names[1],
                    $"attacker you, {record.HitType}, push force 0, level direction toward the tree, skill {record.Skill}");
            else
                run.Fail(names[1], string.Join("; ", problems));

            var drain = shared.m_useDurability ? shared.m_useDurabilityDrain * Game.m_durabilityRate : 0f;
            var delta = durability - axe.m_durability;
            if (Mathf.Abs(delta - drain) <= 0.001f)
                run.Pass(names[2], $"durability -{delta:0.###} = {shared.m_useDurabilityDrain:0.###} x {Game.m_durabilityRate:0.##}");
            else
                run.Fail(names[2], $"durability -{delta:0.###}, expected {drain:0.###}");

            var raises = HitObserver.Raises.ToList();
            var amount = attack.m_raiseSkillAmount;
            if (raises.Count == 1 && raises[0].Skill == expectedSkill && Mathf.Abs(raises[0].Value - amount) <= 0.0001f)
                run.Pass(names[3], $"{expectedSkill} raised once by {raises[0].Value:0.##}, the attack's raise amount");
            else
                run.Fail(names[3],
                    $"expected one raise of {expectedSkill} by the attack's raise amount {amount:0.##}, got [{string.Join(", ", raises.Select(x => $"{x.Skill} by {x.Value:0.##}"))}]");

            return record;
        }

        // The entry refuses, and spends nothing, in each of these conditions. Each one is set up,
        // checked and taken back before the next.
        private static void CheckRefusals(FeatureRun run, Player player, WoodcuttingComponent component,
            TreeBase tree, ItemDrop.ItemData axe, PlayerState state, HitRecord hit)
        {
            player.UnequipItem(axe, false);
            if (player.RightItem != null)
                run.Skip("woodcutting_refused_no_weapon", "the right hand still holds an item after the axe was unequipped");
            else
                CheckRefused(run, "woodcutting_refused_no_weapon", "nothing in the right hand", player,
                    component, tree, axe);

            var club = state.Add(OtherWeaponPrefab);
            if (club == null || !player.EquipItem(club, false) || player.RightItem != club)
                run.Skip("woodcutting_refused_other_weapon", $"could not add and equip a {OtherWeaponPrefab}");
            else
                CheckRefused(run, "woodcutting_refused_other_weapon", $"a {club.m_shared.m_skillType} weapon",
                    player, component, tree, axe);

            if (!player.EquipItem(axe, false) || player.RightItem != axe)
            {
                foreach (var name in new[]
                         {
                             "woodcutting_refused_no_durability", "woodcutting_refused_tool_tier",
                             "woodcutting_refused_blocked"
                         })
                    run.Skip(name, "the test axe could not be equipped again");
                return;
            }

            if (!axe.m_shared.m_useDurability)
            {
                run.Skip("woodcutting_refused_no_durability", "the test axe does not use durability");
            }
            else
            {
                var durability = axe.m_durability;
                axe.m_durability = 0f;
                try
                {
                    CheckRefused(run, "woodcutting_refused_no_durability", "an axe with no durability left",
                        player, component, tree, axe);
                }
                finally
                {
                    axe.m_durability = durability;
                }
            }

            var minToolTier = tree.m_minToolTier;
            tree.m_minToolTier = axe.m_shared.m_toolTier + 1;
            try
            {
                CheckRefused(run, "woodcutting_refused_tool_tier",
                    $"a tree of tool tier {tree.m_minToolTier} for an axe of tier {axe.m_shared.m_toolTier}",
                    player, component, tree, axe);
            }
            finally
            {
                tree.m_minToolTier = minToolTier;
            }

            CheckBlocked(run, player, component, tree, axe, hit);
        }

        // A wall spawned across the line from the eyes to where the hit landed, and removed again
        // in the same frame.
        private static void CheckBlocked(FeatureRun run, Player player, WoodcuttingComponent component,
            TreeBase tree, ItemDrop.ItemData axe, HitRecord hit)
        {
            const string name = "woodcutting_refused_blocked";
            if (hit == null)
            {
                run.Skip(name, "the hit check found no hit point to block");
                return;
            }

            var eye = player.m_eye.position;
            var wall = SpawnWall(eye, hit.Point, Vector3.ProjectOnPlane(hit.Point - eye, Vector3.up),
                out var skip);
            if (!wall)
            {
                run.Skip(name, skip);
                return;
            }

            try
            {
                CheckRefused(run, name, "a wall between your eyes and the tree", player, component, tree, axe);
            }
            finally
            {
                RemoveTestObject(wall);
            }
        }

        // With the interval at 0, the call FixedUpdate makes runs no pass, however long it says
        // has passed.
        private static void CheckIntervalZero(FeatureRun run, Player player, TreeBase tree, ItemDrop.ItemData axe)
        {
            const string name = "woodcutting_interval_zero_no_pass";
            var method = IntervalHook();
            if (method == null)
            {
                run.Skip(name, "the module's interval hook was not found");
                return;
            }
            if (player.RightItem != axe)
            {
                run.Skip(name, "the test axe is not in your right hand");
                return;
            }

            var health = ReadHealth(tree);
            var durability = axe.m_durability;
            var blocked = HitObserver.BlockedHits;
            HitObserver.Reset();
            method.Invoke(null, new object[] { player, 10f });

            var calls = HitObserver.Records.Count + HitObserver.BlockedHits - blocked;
            if (calls == 0 && ReadHealth(tree).Equals(health) && axe.m_durability.Equals(durability))
                run.Pass(name,
                    $"woodcutting_interval {WoodcuttingConfig.WoodcuttingInterval:0.##}: a 10 s step of the interval hook hit nothing");
            else
                run.Fail(name, $"the interval hook at interval 0 made {calls} hit(s)");
        }

        // The other side of the check above, through the same hook: at a positive interval, a
        // step longer than the interval runs one pass, which hits the test tree once. The interval
        // is written back to 0 straight after the call, before anything else runs.
        private static void CheckIntervalPass(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            WoodcuttingComponent component, TreeBase tree, ItemDrop.ItemData axe)
        {
            const string name = "woodcutting_interval_pass";
            var method = IntervalHook();
            if (method == null)
            {
                run.Skip(name, "the module's interval hook was not found");
                return;
            }
            if (player.RightItem != axe)
            {
                run.Skip(name, "the test axe is not in your right hand");
                return;
            }

            var nearer = FindNearerTarget(player, component, tree);
            if (nearer != null)
            {
                run.Skip(name, nearer);
                return;
            }

            var health = ReadHealth(tree);
            var blocked = HitObserver.BlockedHits;
            HitObserver.Reset();
            if (!Write(run, held, WoodcuttingConfig.WoodcuttingIntervalEntry, PassInterval))
            {
                run.Skip(name, $"woodcutting_interval could not be set to {PassInterval:0.##} for the check");
                return;
            }

            var step = PassInterval + 1f;
            try
            {
                method.Invoke(null, new object[] { player, step });
            }
            finally
            {
                Write(run, held, WoodcuttingConfig.WoodcuttingIntervalEntry, 0f);
            }

            var reset = WoodcuttingConfig.WoodcuttingInterval <= 0f;
            var records = HitObserver.Records.ToList();
            var others = HitObserver.BlockedHits - blocked;
            var after = ReadHealth(tree);
            if (records.Count == 1 && records[0].Target == tree && after < health && others == 0 && reset)
                run.Pass(name,
                    $"at woodcutting_interval {PassInterval:0.##}, a {step:0.##} s step of the interval hook ran one pass that hit the test tree once; the interval is back at 0");
            else
                run.Fail(name,
                    $"expected one hit on the test tree: {records.Count} hit(s) on it, {others} on other trees or logs, health {health:0.##} -> {after:0.##}" +
                    (reset ? "" : $"; woodcutting_interval could not be written back to 0 and reads {WoodcuttingConfig.WoodcuttingInterval:0.##}"));
        }

        // One pass the way the key runs it, at interval 0. The test tree has to be the nearest
        // candidate, so the pass cannot go to a tree or log of your world.
        private static void CheckKeyPass(FeatureRun run, Player player, WoodcuttingComponent component,
            TreeBase tree, ItemDrop.ItemData axe)
        {
            const string name = "woodcutting_key_pass";
            if (player.RightItem != axe)
            {
                run.Skip(name, "the test axe is not in your right hand");
                return;
            }

            var nearer = FindNearerTarget(player, component, tree);
            if (nearer != null)
            {
                run.Skip(name, nearer);
                return;
            }

            var health = ReadHealth(tree);
            var blocked = HitObserver.BlockedHits;
            HitObserver.Reset();
            WoodcuttingModule.RunKeyPass(player);

            var records = HitObserver.Records.ToList();
            var others = HitObserver.BlockedHits - blocked;
            var after = ReadHealth(tree);
            if (records.Count == 1 && records[0].Target == tree && after < health && others == 0)
                run.Pass(name,
                    $"at woodcutting_interval {WoodcuttingConfig.WoodcuttingInterval:0.##}, one key pass hit the test tree once");
            else
                run.Fail(name,
                    $"expected one hit on the test tree: {records.Count} hit(s) on it, {others} on other trees or logs, health {health:0.##} -> {after:0.##}");
        }

        // woodcutting_range is set below the distance from the eyes to where the hit landed, and
        // left there for the session to put back.
        private static void CheckOutOfRange(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            WoodcuttingComponent component, TreeBase tree, ItemDrop.ItemData axe, HitRecord hit)
        {
            const string name = "woodcutting_refused_out_of_range";
            if (hit == null)
            {
                run.Skip(name, "the hit check found no hit distance to stay under");
                return;
            }
            if (player.RightItem != axe)
            {
                run.Skip(name, "the test axe is not in your right hand");
                return;
            }

            var distance = Vector3.Distance(player.m_eye.position, hit.Point);
            var range = Mathf.FloorToInt(distance - OutOfRangeMargin);
            if (range < 1)
            {
                run.Skip(name, $"the hit landed {distance:0.##} m from your eyes, too close for a range of 1 m");
                return;
            }
            if (!Write(run, held, WoodcuttingConfig.WoodcuttingRangeEntry, range) ||
                WoodcuttingConfig.WoodcuttingRange != range)
            {
                run.Skip(name, $"woodcutting_range could not be set to {range} for the check");
                return;
            }

            CheckRefused(run, name, $"woodcutting_range {range} m for a hit {distance:0.##} m away", player,
                component, tree, axe);
        }

        private static void CheckRefused(FeatureRun run, string name, string condition, Player player,
            WoodcuttingComponent component, TreeBase tree, ItemDrop.ItemData axe)
        {
            var health = ReadHealth(tree);
            var durability = axe.m_durability;
            var blocked = HitObserver.BlockedHits;
            HitObserver.Reset();
            var chopped = component.Chop(player);

            var problems = new List<string>();
            if (chopped) problems.Add("the entry reported a hit");
            var calls = HitObserver.Records.Count + HitObserver.BlockedHits - blocked;
            if (calls > 0) problems.Add($"{calls} damage call(s)");
            if (!ReadHealth(tree).Equals(health)) problems.Add("the tree lost health");
            if (!axe.m_durability.Equals(durability)) problems.Add("the axe lost durability");
            if (HitObserver.Raises.Count > 0) problems.Add($"{HitObserver.Raises.Count} skill raise(s)");

            if (problems.Count == 0)
                run.Pass(name, $"{condition}: no hit, nothing spent");
            else
                run.Fail(name, $"{condition}: {string.Join(", ", problems)}");
        }

        // The module's interval hook, the call FixedUpdate makes; null when it is not found.
        private static System.Reflection.MethodInfo IntervalHook()
        {
            return AccessTools.Method(typeof(WoodcuttingModule), "OnPlayerFixedUpdate",
                new[] { typeof(Player), typeof(float) });
        }

        // A pass goes to the nearest candidate first, so it can hit the test tree only when the
        // test tree is strictly the nearest. Returns why it is not, or null.
        private static string FindNearerTarget(Player player, WoodcuttingComponent component, TreeBase tree)
        {
            var origin = player.transform.position;
            var testDistance = Vector3.Distance(origin, tree.transform.position);
            var nearer = UnityEngine.Object.FindObjectsByType<WoodcuttingComponent>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(x => x && x != component && Objects.GetZNetView(x, out var nview) && nview.IsValid())
                .Select(x => Vector3.Distance(origin, x.transform.position))
                .Where(x => x <= testDistance)
                .ToList();
            return nearer.Count > 0
                ? $"a tree or log of your world is {nearer.Min():0.#} m from you, not farther than the test tree ({testDistance:0.#} m), so the pass could go to it; move away from trees and retry"
                : null;
        }

        private static void ReportRestore(FeatureRun run)
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

        // Takes the setting on with the session - which records the change before it is made and
        // refuses it when the record cannot be written - and writes it. Returns whether it was
        // written; a refusal is logged.
        private static bool Write(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
            ConfigEntryBase entry, object value)
        {
            string refusal;
            if (!held.TryGetValue(entry, out var setting))
            {
                if (!SmokeSession.BeginSetting(run.RunId, entry, out setting, out refusal))
                {
                    FeaturesSmokeCommand.Warn(
                        $"[Automatics Smoke] {entry.Definition.Section}/{entry.Definition.Key} was not changed for the run: {refusal}");
                    return false;
                }

                held[entry] = setting;
            }

            if (SmokeSession.WriteSetting(run.RunId, setting, value, out refusal)) return true;

            FeaturesSmokeCommand.Warn(
                $"[Automatics Smoke] {entry.Definition.Section}/{entry.Definition.Key} was not changed for the run: {refusal}");
            return false;
        }

        // -- Test objects ---------------------------------------------------------------------

        // Anything near the spot where the test tree goes: a building piece, another tree or log,
        // a creature or any other destructible. Returns its name, or null.
        private static string FindBlocker(Player player, Vector3 facing)
        {
            var center = player.transform.position + facing * (TreeDistance * 0.5f);
            var radius = TreeDistance * 0.5f + ClearMargin;
            foreach (var collider in Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Ignore))
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

        // Whether a ray from the eyes toward the middle of one of the test tree's colliders, at eye
        // height, first meets the test tree. Returns why not, or null. Asked of the physics engine
        // directly, so a refused hit can be told apart from something of your world in the way.
        private static string FindObstacle(Player player, TreeBase tree)
        {
            var eye = player.m_eye.position;
            string obstacle = null;
            foreach (var collider in tree.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;

                var bounds = collider.bounds;
                var aim = new Vector3(bounds.center.x, Mathf.Clamp(eye.y, bounds.min.y, bounds.max.y),
                    bounds.center.z) - eye;
                if (!Physics.Raycast(eye, aim.normalized, out var hit, aim.magnitude + 1f, SwingMask.Value,
                        QueryTriggerInteraction.Ignore))
                    continue;

                var first = Projectile.FindHitObject(hit.collider);
                if (first == tree.gameObject) return null;
                obstacle = obstacle ?? Objects.GetPrefabName(first);
            }

            return obstacle != null
                ? $"{obstacle} stands between your eyes and the test tree; face open ground and retry"
                : "no part of the test tree was found at your eye height; stand on level ground and retry";
        }

        private static string PickAxe(int minToolTier, out ItemDrop.ItemData.SharedData shared)
        {
            foreach (var name in AxePrefabs)
            {
                var prefab = ObjectDB.instance.GetItemPrefab(name);
                var itemDrop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (itemDrop == null) continue;

                shared = itemDrop.m_itemData.m_shared;
                if (shared.m_skillType == Skills.SkillType.Axes && shared.m_toolTier >= minToolTier)
                    return name;
            }

            shared = null;
            return null;
        }

        // Spawns the prefab non-persistent and records it with the session, so a run cut short
        // still has it removed. Returns null with the reason when it could not be set up.
        private static GameObject SpawnTestObject(string prefabName, Vector3 position, Quaternion rotation,
            out string skip)
        {
            skip = null;
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (!prefab)
            {
                skip = $"the {prefabName} prefab is not available";
                return null;
            }

            var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
            SmokeSession.TrackSpawn(go);
            if (!Objects.GetZNetView(go.transform, out var nview) || nview == null || !nview.IsValid() ||
                nview.GetZDO() == null)
            {
                skip = $"the spawned {prefabName} has no ZDO";
                RemoveTestObject(go);
                return null;
            }
            if (!nview.IsOwner()) nview.ClaimOwnership();
            nview.GetZDO().Persistent = false;
            return go;
        }

        // The wall is moved so its colliders centre on the middle of the line from the eyes to the
        // hit point, and it is kept only when that line now meets the wall first.
        private static GameObject SpawnWall(Vector3 eye, Vector3 point, Vector3 facing, out string skip)
        {
            var middle = (eye + point) * 0.5f;
            var rotation = facing.sqrMagnitude > 0.001f ? Quaternion.LookRotation(facing) : Quaternion.identity;
            var wall = SpawnTestObject(WallPrefab, middle, rotation, out skip);
            if (!wall) return null;

            Physics.SyncTransforms();
            var colliders = wall.GetComponentsInChildren<Collider>().Where(x => x.enabled && !x.isTrigger).ToList();
            if (colliders.Count == 0)
            {
                skip = "the test wall has no collider";
                RemoveTestObject(wall);
                return null;
            }

            var bounds = colliders[0].bounds;
            foreach (var collider in colliders.Skip(1)) bounds.Encapsulate(collider.bounds);
            wall.transform.position += middle - bounds.center;
            Physics.SyncTransforms();

            var line = point - eye;
            if (Physics.Raycast(eye, line.normalized, out var hit, line.magnitude, SwingMask.Value,
                    QueryTriggerInteraction.Ignore) && hit.collider.transform.IsChildOf(wall.transform))
                return wall;

            skip = "the test wall did not end up between your eyes and the tree";
            RemoveTestObject(wall);
            return null;
        }

        // Deactivated first: ZNetScene destroys the GameObject only at the end of the frame, and
        // its colliders must not block the rays of the checks that follow.
        private static void RemoveTestObject(GameObject go)
        {
            if (!go) return;
            go.SetActive(false);
            Physics.SyncTransforms();
            if (SmokeSession.DestroyTestObject(go, 0, Label)) SmokeSession.ReleaseSpawn(go);
        }

        private static bool SeedHealth(TreeBase tree)
        {
            if (!Objects.GetZNetView(tree, out var nview) || nview == null || !nview.IsValid()) return false;
            nview.GetZDO().Set(ZDOVars.s_health, SeededHealth);
            return true;
        }

        // The stored health of the tree, NaN when it cannot be read.
        private static float ReadHealth(TreeBase tree)
        {
            if (!tree || !Objects.GetZNetView(tree, out var nview) || nview == null || !nview.IsValid())
                return float.NaN;
            return nview.GetZDO().GetFloat(ZDOVars.s_health, tree.m_health);
        }

        // -- Player state ---------------------------------------------------------------------

        // What the section changes on the player, and how it is put back. Restore is safe to call
        // again: each part is put back once.
        private sealed class PlayerState
        {
            private readonly Player _player;
            private readonly List<ItemDrop.ItemData> _equipped;
            private readonly List<ItemDrop.ItemData> _added = new List<ItemDrop.ItemData>();
            private readonly Dictionary<Skills.SkillType, (bool Had, float Level, float Accumulator)> _skills =
                new Dictionary<Skills.SkillType, (bool, float, float)>();
            private bool _skillsRestored;

            // The skills a hit with the axe can raise: its own and its attack's special-hit skill.
            public PlayerState(Player player, ItemDrop.ItemData.SharedData axe)
            {
                _player = player;
                _equipped = player.GetInventory().GetEquippedItems().ToList();

                foreach (var type in new[] { axe.m_skillType, axe.m_attack.m_specialHitSkill })
                {
                    if (type == Skills.SkillType.None || _skills.ContainsKey(type)) continue;
                    var skill = FindSkill(type);
                    _skills[type] = (skill != null, skill?.m_level ?? 0f, skill?.m_accumulator ?? 0f);
                }
            }

            public ItemDrop.ItemData Add(string prefabName)
            {
                var item = _player.GetInventory().AddItem(prefabName, 1, 1, 0, 0L, "", false);
                if (item != null) _added.Add(item);
                return item;
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

                if (!_skillsRestored)
                {
                    foreach (var pair in _skills)
                    {
                        var skill = FindSkill(pair.Key);
                        if (skill == null) continue;
                        skill.m_level = pair.Value.Had ? pair.Value.Level : 0f;
                        skill.m_accumulator = pair.Value.Had ? pair.Value.Accumulator : 0f;
                    }
                    _skillsRestored = true;
                }

                return done;
            }

            private Skills.Skill FindSkill(Skills.SkillType type)
            {
                return _player.GetSkills()?.GetSkillList()
                    .FirstOrDefault(x => x.m_info != null && x.m_info.m_skill == type);
            }
        }

        // -- Observation ----------------------------------------------------------------------

        private sealed class HitRecord
        {
            public Component Target;
            public ZDOID Attacker;
            public HitData.HitType HitType;
            public float PushForce;
            public Vector3 Dir;
            public Vector3 Point;
            public Skills.SkillType Skill;
        }

        // Patches added for the section only and removed at its end. While attached, they record
        // every damage call on the test tree and every skill raise of the player, and drop every
        // damage call on any other tree or log, whoever makes it, so no check can damage one of
        // your world. A call on the test tree is recorded whatever its attacker, so a malformed
        // hit fails the field checks instead of passing unseen.
        private static class HitObserver
        {
            private const string HarmonyId = "net.eidee.automatics.smoke.features.woodcutting";

            private static Harmony _harmony;
            private static bool _attached;
            private static Player _player;
            private static GameObject _tree;

            public static readonly List<HitRecord> Records = new List<HitRecord>();
            public static readonly List<(Skills.SkillType Skill, float Value)> Raises =
                new List<(Skills.SkillType, float)>();
            public static int BlockedHits { get; private set; }

            public static bool Attach(Player player, GameObject tree, out string problem)
            {
                problem = null;
                var raise = AccessTools.Method(typeof(Player), nameof(Player.RaiseSkill),
                    new[] { typeof(Skills.SkillType), typeof(float) });
                var damages = new[]
                {
                    AccessTools.Method(typeof(TreeBase), nameof(TreeBase.Damage)),
                    AccessTools.Method(typeof(TreeLog), nameof(TreeLog.Damage))
                };
                if (raise == null || damages.Any(x => x == null))
                {
                    problem = "Player.RaiseSkill, TreeBase.Damage or TreeLog.Damage was not found";
                    return false;
                }
                if (_harmony != null && !Detach(out var stale))
                {
                    problem = $"an earlier hit observer could not be removed ({stale})";
                    return false;
                }

                _player = player;
                _tree = tree;
                _attached = true;
                Reset();
                BlockedHits = 0;
                _harmony = new Harmony(HarmonyId);
                try
                {
                    foreach (var damage in damages)
                        _harmony.Patch(damage, new HarmonyMethod(typeof(HitObserver), nameof(DamagePrefix)));
                    _harmony.Patch(raise, postfix: new HarmonyMethod(typeof(HitObserver), nameof(RaisePostfix)));
                }
                catch (Exception e)
                {
                    problem = $"the hit observer could not be added ({e.GetType().Name}: {e.Message})";
                    Detach(out _);
                    return false;
                }
                return true;
            }

            public static bool Detach(out string failure)
            {
                failure = null;
                _attached = false;
                _player = null;
                _tree = null;
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
                Raises.Clear();
            }

            private static bool DamagePrefix(Component __instance, HitData hit)
            {
                if (!_attached) return true;
                if (!__instance || !_tree || __instance.gameObject != _tree)
                {
                    BlockedHits++;
                    return false;
                }

                Records.Add(hit == null
                    ? new HitRecord { Target = __instance, Attacker = ZDOID.None }
                    : new HitRecord
                    {
                        Target = __instance,
                        Attacker = hit.m_attacker,
                        HitType = hit.m_hitType,
                        PushForce = hit.m_pushForce,
                        Dir = hit.m_dir,
                        Point = hit.m_point,
                        Skill = hit.m_skill
                    });
                return true;
            }

            private static void RaisePostfix(Player __instance, Skills.SkillType skill, float value)
            {
                if (_player && __instance == _player) Raises.Add((skill, value));
            }
        }
    }
}
