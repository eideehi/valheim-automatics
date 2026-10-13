using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using MiningConfig = Automatics.AutomaticMining.Config;
using WoodcuttingComponent = Automatics.AutomaticWoodcutting.AutomaticWoodcutting;
using WoodcuttingConfig = Automatics.AutomaticWoodcutting.Config;
using WoodcuttingModule = Automatics.AutomaticWoodcutting.Module;
using WoodcuttingSpecies = Automatics.AutomaticWoodcutting.TreeSpecies;
using WoodcuttingTrees = Automatics.AutomaticWoodcutting.Globals;

namespace Automatics.SmokeTest
{
    // The `woodcutting` section of automatics_smoke_features: Automatic Woodcutting's hits on a tree
    // it spawns, and on the log and stump that felling the tree leaves. Before anything is
    // equipped or spawned, woodcutting_interval is set to 0, so no periodic pass runs while the
    // section holds a test axe. It then checks the tree data against the prefabs of the loaded
    // world, against species it works out from the prefabs itself, and writes the settings the
    // checks assume where yours differ: Automatic Mining's passes off, woodcutting passes on, an
    // axe needed in the right hand, and the test tree's species allowed. A
    // non-persistent tree is spawned in front of the player with a health no hit reaches, so it
    // does not fall until the section fells it, and a test axe is added and equipped. The module's
    // own per-target entry then hits the tree once, and the hit data, the axe's wear and the skill
    // gain are checked. The same entry has to refuse, with nothing spent, with no weapon, another
    // weapon, a worn-out axe, a tree above the axe's tier, a wall between the eyes and the tree,
    // and the tree out of range. The interval hook runs no pass at interval 0 and one pass at a
    // positive interval, and one pass is run the way the key runs it; those passes need the test
    // tree to be the nearest tree, log or stump. Then come the tree's species, the passes switch,
    // and an axe taken from the inventory. Last, the tree is felled: the log it spawns is
    // refused until the game lets it take damage and hit after that, and its stump gets the
    // component from the real Destructible.Start hook and is hit by a pass, while another
    // Destructible spawned next to you gets none.
    // An observer patch, removed at the end, records every damage call on the test objects and
    // every skill raise of the player, drops every damage call on any other tree, log or stump,
    // whoever makes it, and takes the log and stump a felling of a test tree spawns as test
    // objects: recorded with the session, non-persistent, at a health no hit reaches, and the log
    // held where it spawned instead of falling. The session holds a hand-back that removes the
    // observer if the run is cut short.
    // Afterwards the added items are removed, the previously equipped items are equipped again, the
    // skills a hit can raise, the durability of your own axes and the
    // settings are put back, and the test objects are removed. A hand-back registered with the
    // session does the same when the run is cut short.
    internal static class WoodcuttingFeatureSmoke
    {
        private const string Label = "woodcutting feature smoke";
        private const string SectionCheck = "woodcutting_section";
        private const string RestoreCheck = "woodcutting_settings_restored";
        internal const float SeededHealth = 100000f;
        private const float TreeDistance = 2f;
        private const float ClearMargin = 1.5f;
        private const float OutOfRangeMargin = 0.25f;
        private const float PassInterval = 1f;
        // A log ignores damage for 0.2 s after it spawns; the section waits a little longer.
        internal const float LogDelay = 0.3f;
        internal const int StartFrames = 10;

        internal const string TreePrefab = "Beech1";
        private const string WallPrefab = "woodwall";
        internal const string OtherWeaponPrefab = "Club";

        // Axes of the earlier biomes, lowest tier first; the first one whose tool tier reaches the
        // test tree is used. The names are checked against ObjectDB at run time.
        internal static readonly string[] AxePrefabs = { "AxeStone", "AxeFlint", "AxeBronze", "AxeIron" };

        // Destructibles of the earlier biomes that are not the stump of a tree, for the check that
        // only stumps get the component; the first one available is used.
        private static readonly string[] OtherDestructiblePrefabs = { "stubbe", "Bush01", "Bush02_en" };

        // Tree identifiers that predate the Mistlands update; a check names only these on screen.
        internal static readonly HashSet<string> EarlyTrees = new HashSet<string>(new[]
        {
            "Beech", "Birch", "Oak", "Fir", "Pine", "AncientTree"
        }, StringComparer.Ordinal);

        internal static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Woodcutting");

        internal static readonly FieldInfo StaminaField = AccessTools.Field(typeof(Player), "m_stamina");
        internal static readonly FieldInfo RegenTimerField = AccessTools.Field(typeof(Player), "m_staminaRegenTimer");

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
            var spawned = new List<GameObject>();
            PlayerState state = null;
            Func<string, bool> handBack = null;
            GameObject treeObject = null;
            GameObject otherObject = null;
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

                CheckSpeciesData(run, held);

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

                var species = SpeciesOf(TreePrefab);
                if (species.Count != 1)
                {
                    run.Skip(SectionCheck,
                        $"the {TreePrefab} prefab resolves to {species.Count} tree species instead of one");
                    yield break;
                }
                var treeSpecies = species.First();

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

                var slots = 1 + AxePrefabs.Length;
                if (player.GetInventory().GetEmptySlots() < slots)
                {
                    run.Skip(SectionCheck, $"needs {slots} free inventory slots for the test axes and club");
                    yield break;
                }

                if (!PrepareSettings(run, held, treeSpecies, out var refusal))
                {
                    run.Skip(SectionCheck, refusal);
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
                // So a hit spawns nothing besides its effects, and the felling at the end drops
                // nothing but its log and stump.
                tree.m_spawnOnDamage = null;
                tree.m_dropWhenDestroyed = new DropTable();
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

                state = new PlayerState(player, RaisableSkills());
                handBack = _ => state.Restore();
                SmokeSession.TrackHandBack(handBack);
                var axe = state.Add(axeName);
                if (axe == null || !player.EquipItem(axe, false) || player.RightItem != axe)
                {
                    run.Skip(SectionCheck, $"could not add and equip a {axeName}");
                    yield break;
                }

                if (!HitObserver.Attach(player, treeObject, spawned, out var problem))
                {
                    run.Skip(SectionCheck, problem);
                    yield break;
                }

                var hit = CheckHit(run, player, component, tree, axe);

                CheckRefusals(run, player, component, tree, axe, state, hit);
                CheckIntervalZero(run, player, tree, axe);
                CheckIntervalPass(run, held, player, tree, axe);
                CheckKeyPass(run, player, tree, axe);
                CheckOutOfRange(run, held, player, component, tree, axe, hit);
                CheckTreeSpecies(run, held, player, component, tree, axe, treeSpecies);
                CheckSwitchOff(run, held, player, tree, axe);
                CheckInventoryAxe(run, held, player, tree, axe, state);

                // Behind you, so it is in the way of nothing.
                var otherSpot = player.transform.position - facing * TreeDistance;
                otherSpot.y = ZoneSystem.instance.GetGroundHeight(otherSpot);
                otherObject = SpawnOtherDestructible(otherSpot, out var otherSkip);

                var steps = Fell(run, held, player, component, tree, axe, treeSpecies, spawned);
                while (steps.MoveNext()) yield return steps.Current;

                CheckOtherDestructible(run, otherObject, otherSkip);

                run.Pass("woodcutting_world_guarded",
                    $"dropped {HitObserver.BlockedHits} damage call(s) on trees, logs and stumps other than the test objects");
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
                        if (state != null)
                        {
                            if (state.Restore()) SmokeSession.ReleaseHandBack(handBack);
                            else
                                run.Fail("woodcutting_player_restored",
                                    "the previously equipped items could not all be equipped again; it is retried at the next hand-back");
                        }
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
                            RemoveTestObject(otherObject);
                            foreach (var go in spawned) RemoveTestObject(go);
                        }
                    }
                }
            }
        }

        // -- Species data -----------------------------------------------------------------------

        // Over the prefabs of the loaded world. Every built-in tree identifier matches at least one
        // standing tree prefab. The species each log and stump prefab should resolve to is worked
        // out here from the prefabs alone, apart from the module: every standing tree prefab passes
        // its own species down its log, that log's smaller logs in turn, and its stump. The module
        // has to resolve each of them to exactly that set, each standing tree to its own species,
        // and no log prefab may be left without one. Each prefab that trees of more than one
        // species lead to is then tried with each of those species alone in allow_woodcutting_tree,
        // which has to allow it, and with none of them, which must not; the list is written back
        // straight after. The screen gives counts and the species that predate the Mistlands
        // update; every name goes to the log file.
        private static void CheckSpeciesData(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held)
        {
            var prefabs = ZNetScene.instance.m_prefabs.Where(x => x).ToList();
            var trees = prefabs.Where(x => x.GetComponent<TreeBase>()).ToList();
            var logs = prefabs.Where(x => x.GetComponent<TreeLog>()).ToList();

            const string matchName = "woodcutting_species_identifiers_match";
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tree in trees)
                if (WoodcuttingTrees.Tree.GetIdentify(tree.name, out var identifier))
                    matched.Add(identifier);
            var builtIn = WoodcuttingTrees.Tree.GetElements().Select(x => x.identifier).ToList();
            var unmatched = builtIn.Where(x => !matched.Contains(x)).ToList();
            foreach (var identifier in unmatched)
                Log.LogWarning($"{matchName}: {identifier} matches no standing tree prefab");
            if (builtIn.Count > 0 && unmatched.Count == 0)
                run.Pass(matchName, $"each of the {builtIn.Count} built-in tree identifier(s) matches a standing tree prefab");
            else
                run.Fail(matchName, builtIn.Count == 0
                    ? "there is no built-in tree identifier"
                    : NameEarly(unmatched, "built-in identifier(s) match no standing tree prefab"));

            var walked = WalkTreePrefabs(trees);

            const string coverageName = "woodcutting_species_coverage";
            var names = trees.Concat(logs).Select(x => x.name).Concat(walked.Keys)
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var unresolved = 0;
            var mismatched = 0;
            foreach (var name in names)
            {
                var expected = ExpectedSpecies(name, walked);
                var resolved = SpeciesOf(name);
                if (expected.Count == 0)
                {
                    unresolved++;
                    Log.LogWarning($"{coverageName}: no standing tree prefab leads to {name}, and it matches no identifier");
                }
                else if (!resolved.SetEquals(expected))
                {
                    mismatched++;
                    Log.LogWarning(
                        $"{coverageName}: {name} resolves to [{string.Join(", ", resolved.OrderBy(x => x).ToArray())}], the prefabs lead to [{string.Join(", ", expected.OrderBy(x => x).ToArray())}]");
                }
            }
            if (trees.Count > 0 && unresolved == 0 && mismatched == 0)
                run.Pass(coverageName,
                    $"all {trees.Count} standing tree prefab(s) and the {names.Count - trees.Count} log and stump prefab(s) resolve to the species the prefabs lead to");
            else
                run.Fail(coverageName, trees.Count == 0
                    ? "the loaded world has no standing tree prefab"
                    : $"{unresolved} prefab(s) lead to no species and {mismatched} resolve to other species than the prefabs lead to (named in the log file)");

            CheckSharedSpecies(run, held, walked);
        }

        // Each log and stump prefab with the species of the standing trees whose prefabs lead to
        // it, worked out from the prefabs: the tree's log, that log's smaller logs in turn, and the
        // tree's stump.
        private static Dictionary<string, HashSet<string>> WalkTreePrefabs(IEnumerable<GameObject> trees)
        {
            var walked = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var prefab in trees)
            {
                if (!WoodcuttingTrees.Tree.GetIdentify(prefab.name, out var species)) continue;

                var tree = prefab.GetComponent<TreeBase>();
                var reached = new List<GameObject>();
                if (tree.m_stubPrefab) reached.Add(tree.m_stubPrefab);
                var seen = new HashSet<GameObject>();
                for (var log = tree.m_logPrefab; log && seen.Add(log);)
                {
                    reached.Add(log);
                    var treeLog = log.GetComponent<TreeLog>();
                    log = treeLog ? treeLog.m_subLogPrefab : null;
                }

                foreach (var go in reached)
                {
                    if (!walked.TryGetValue(go.name, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        walked.Add(go.name, set);
                    }
                    set.Add(species);
                }
            }
            return walked;
        }

        // A prefab that matches an identifier itself has that species, whatever leads to it.
        private static HashSet<string> ExpectedSpecies(string name, Dictionary<string, HashSet<string>> walked)
        {
            if (WoodcuttingTrees.Tree.GetIdentify(name, out var own))
                return new HashSet<string>(StringComparer.Ordinal) { own };
            return walked.TryGetValue(name, out var set)
                ? set
                : new HashSet<string>(StringComparer.Ordinal);
        }

        private static void CheckSharedSpecies(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
            Dictionary<string, HashSet<string>> walked)
        {
            const string name = "woodcutting_species_shared_allowed";
            var shared = walked.Where(x => !WoodcuttingTrees.Tree.GetIdentify(x.Key, out _) && x.Value.Count > 1)
                .OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            if (shared.Count == 0)
            {
                run.Pass(name, $"each of the {walked.Count} log and stump prefab(s) is reached from one species");
                return;
            }

            var all = WoodcuttingTrees.Tree.GetAllElements().Select(x => x.identifier).ToList();
            var original = WoodcuttingConfig.AllowWoodcuttingTree;
            var refused = new List<string>();
            var leaked = new List<string>();
            string skip = null;
            try
            {
                foreach (var pair in shared)
                {
                    Log.LogInfo($"{name}: {pair.Key} is reached from {string.Join(", ", pair.Value.OrderBy(x => x).ToArray())}");
                    foreach (var species in pair.Value)
                    {
                        if (!Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, new StringList(new[] { species })))
                        {
                            skip = "allow_woodcutting_tree could not be written for the check";
                            return;
                        }
                        if (WoodcuttingSpecies.IsAllowed(pair.Key)) continue;
                        refused.Add(species);
                        Log.LogWarning($"{name}: {pair.Key} is not allowed with {species} alone in allow_woodcutting_tree");
                    }

                    if (!Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry,
                            new StringList(all.Where(x => !pair.Value.Contains(x)))))
                    {
                        skip = "allow_woodcutting_tree could not be written for the check";
                        return;
                    }
                    if (!WoodcuttingSpecies.IsAllowed(pair.Key)) continue;
                    leaked.Add(pair.Key);
                    Log.LogWarning($"{name}: {pair.Key} is allowed with none of its species in allow_woodcutting_tree");
                }
            }
            finally
            {
                Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, original);
                if (skip != null) run.Skip(name, skip);
            }

            var tried = shared.Sum(x => x.Value.Count);
            if (refused.Count == 0 && leaked.Count == 0)
                run.Pass(name,
                    $"{shared.Count} log or stump prefab(s) are reached from more than one species (named in the log file); each was allowed by each of its {tried} species alone and not without them");
            else
            {
                var parts = new List<string>();
                if (refused.Count > 0) parts.Add(NameEarly(refused.Distinct(), "species alone did not allow a prefab they lead to"));
                if (leaked.Count > 0) parts.Add($"{leaked.Count} prefab(s) stayed allowed without any of their species");
                run.Fail(name, string.Join("; ", parts.ToArray()) + " (named in the log file)");
            }
        }

        // The species identifiers of a prefab, as the module resolves them.
        internal static HashSet<string> SpeciesOf(string prefabName)
        {
            var species = new HashSet<string>(StringComparer.Ordinal);
            WoodcuttingSpecies.GetSpecies(prefabName, species);
            return species;
        }

        // Names the early identifiers and counts the later ones.
        internal static string NameEarly(IEnumerable<string> identifiers, string what)
        {
            var list = identifiers.ToList();
            var early = list.Where(EarlyTrees.Contains).ToList();
            var later = list.Count - early.Count;
            var parts = new List<string>();
            if (early.Count > 0) parts.Add(string.Join(", ", early.ToArray()));
            if (later > 0) parts.Add($"{later} later one(s) named in the log file");
            return $"{list.Count} {what}: {string.Join("; ", parts.ToArray())}";
        }

        // -- Settings ---------------------------------------------------------------------------

        // The settings the checks below assume, written for the section where yours differ: passes
        // on, an axe needed in the right hand, and the test tree's species
        // allowed. The checks that need another value write it and write this one back.
        private static bool PrepareSettings(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, string species, out string refusal)
        {
            refusal = null;
            if (!PauseAutomaticMining(run, held))
                refusal = "enable_automatic_mining could not be turned off for the run, and Automatic Mining could wear a pickaxe of yours while the section runs";
            else if (!WoodcuttingConfig.EnableAutomaticWoodcutting &&
                !Write(run, held, WoodcuttingConfig.EnableAutomaticWoodcuttingEntry, true))
                refusal = "enable_automatic_woodcutting could not be turned on for the run";
            else if (!WoodcuttingConfig.NeedToEquipAxe &&
                     !Write(run, held, WoodcuttingConfig.NeedToEquipAxeEntry, true))
                refusal = "need_to_equip_axe could not be turned on for the run, and a pass would use any axe of your inventory";
            else if (!WoodcuttingConfig.AllowWoodcuttingTree.Contains(species) &&
                     !Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry,
                         new StringList(WoodcuttingConfig.AllowWoodcuttingTree.Concat(new[] { species }))))
                refusal = $"allow_woodcutting_tree could not be given {species} for the run";
            return refusal == null;
        }

        // Automatic Mining's own timer could wear a pickaxe of yours and raise your Pickaxes skill
        // while the section runs across frames. Its passes are turned off for the section through
        // the session, as the mining section does for itself, and put back with the other
        // settings. Returns false when they run and could not be turned off.
        internal static bool PauseAutomaticMining(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held)
        {
            if (!MiningConfig.EnableAutomaticMining || MiningConfig.MiningInterval <= 0f) return true;
            return Write(run, held, MiningConfig.EnableAutomaticMiningEntry, false) &&
                   !MiningConfig.EnableAutomaticMining;
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
            var expectedSkill = HitSkill(axe, tree);

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
            if (record.ItemWorldLevel != (byte)axe.m_worldLevel)
                problems.Add($"item world level {record.ItemWorldLevel}, expected {(byte)axe.m_worldLevel}");
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

            CheckRaise(run, names[3], axe, tree, "the tree");
            return record;
        }

        // The skill raises of the last hit against what a swing raises on the target: its
        // skill once, by the attack's raise amount, on a target of the attack's special-hit or
        // skill-hit type, and nothing on any other target.
        private static void CheckRaise(FeatureRun run, string name, ItemDrop.ItemData axe, IDestructible target,
            string what)
        {
            var attack = axe.m_shared.m_attack;
            var skill = HitSkill(axe, target);
            var expected = RaisesSkill(axe, target);
            var raises = HitObserver.Raises.ToList();
            var amount = attack.m_raiseSkillAmount;
            var observed = $"[{string.Join(", ", raises.Select(x => $"{x.Skill} by {x.Value:0.##}"))}]";
            if (!expected)
            {
                if (raises.Count == 0)
                    run.Pass(name, $"{what} is of neither the special-hit nor the skill-hit type of the attack, and no skill was raised");
                else
                    run.Fail(name, $"{what} raises no skill in a swing, but the hit raised {observed}");
                return;
            }

            if (raises.Count == 1 && raises[0].Skill == skill && Mathf.Abs(raises[0].Value - amount) <= 0.0001f)
                run.Pass(name, $"{skill} raised once by {raises[0].Value:0.##}, the attack's raise amount, on {what}");
            else
                run.Fail(name,
                    $"expected one raise of {skill} by the attack's raise amount {amount:0.##} on {what}, got {observed}");
        }

        // The skill a swing's hit on the target carries: the special-hit skill on a target of the
        // attack's special-hit type, else the weapon's own.
        internal static Skills.SkillType HitSkill(ItemDrop.ItemData axe, IDestructible target)
        {
            var shared = axe.m_shared;
            var attack = shared.m_attack;
            return attack.m_specialHitSkill != Skills.SkillType.None &&
                   (target.GetDestructibleType() & attack.m_specialHitType) != DestructibleType.None
                ? attack.m_specialHitSkill
                : shared.m_skillType;
        }

        private static bool RaisesSkill(ItemDrop.ItemData axe, IDestructible target)
        {
            var attack = axe.m_shared.m_attack;
            var type = target.GetDestructibleType();
            return attack.m_specialHitSkill != Skills.SkillType.None &&
                   (type & attack.m_specialHitType) != DestructibleType.None ||
                   (type & attack.m_skillHitType) != DestructibleType.None;
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
            TreeBase tree, ItemDrop.ItemData axe)
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

            var nearer = FindNearerTarget(player, tree);
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
                    $"expected one hit on the test tree: {records.Count} hit(s) on it, {others} on other trees, logs or stumps, health {health:0.##} -> {after:0.##}" +
                    (reset ? "" : $"; woodcutting_interval could not be written back to 0 and reads {WoodcuttingConfig.WoodcuttingInterval:0.##}"));
        }

        // One pass the way the key runs it, at interval 0. The test tree has to be the nearest
        // candidate, so the pass cannot go to a tree, log or stump of your world.
        private static void CheckKeyPass(FeatureRun run, Player player, TreeBase tree, ItemDrop.ItemData axe)
        {
            const string name = "woodcutting_key_pass";
            if (player.RightItem != axe)
            {
                run.Skip(name, "the test axe is not in your right hand");
                return;
            }

            var nearer = FindNearerTarget(player, tree);
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
                    $"expected one hit on the test tree: {records.Count} hit(s) on it, {others} on other trees, logs or stumps, health {health:0.##} -> {after:0.##}");
        }

        // woodcutting_range is set below the distance from the eyes to where the hit landed, and
        // written back straight after the check.
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

            var previous = WoodcuttingConfig.WoodcuttingRange;
            if (!Write(run, held, WoodcuttingConfig.WoodcuttingRangeEntry, range) ||
                WoodcuttingConfig.WoodcuttingRange != range)
            {
                run.Skip(name, $"woodcutting_range could not be set to {range} for the check");
                return;
            }

            try
            {
                CheckRefused(run, name, $"woodcutting_range {range} m for a hit {distance:0.##} m away", player,
                    component, tree, axe);
            }
            finally
            {
                Write(run, held, WoodcuttingConfig.WoodcuttingRangeEntry, previous);
            }
        }

        // The test tree resolves to its own species, and with that species taken out of
        // allow_woodcutting_tree a pass leaves it alone. The list is written back straight after.
        private static void CheckTreeSpecies(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            WoodcuttingComponent component, TreeBase tree, ItemDrop.ItemData axe, string species)
        {
            const string name = "woodcutting_species_tree";
            var of = SpeciesOf(Objects.GetPrefabName(tree.gameObject));
            if (of.Count == 1 && of.Contains(species) && component.IsAllowed())
                run.Pass(name, $"the test tree resolves to {species}, which allow_woodcutting_tree allows");
            else
                run.Fail(name,
                    $"the test tree resolves to {of.Count} species ({(of.Contains(species) ? species : "not " + species)}) and is {(component.IsAllowed() ? "" : "not ")}allowed");

            const string removedName = "woodcutting_species_removed_tree";
            if (player.RightItem != axe)
            {
                run.Skip(removedName, "the test axe is not in your right hand");
                return;
            }
            var nearer = FindNearerTarget(player, tree);
            if (nearer != null)
            {
                run.Skip(removedName, nearer);
                return;
            }

            var allowed = WoodcuttingConfig.AllowWoodcuttingTree;
            if (!Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry,
                    new StringList(allowed.Where(x => x != species))))
            {
                run.Skip(removedName, $"allow_woodcutting_tree could not be written without {species}");
                return;
            }

            bool stillAllowed;
            var health = ReadHealth(tree);
            HitObserver.Reset();
            try
            {
                stillAllowed = component.IsAllowed();
                WoodcuttingModule.RunKeyPass(player);
            }
            finally
            {
                Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, allowed);
            }

            var hits = HitObserver.Records.Count(x => x.Target == tree);
            if (!stillAllowed && hits == 0 && ReadHealth(tree).Equals(health))
                run.Pass(removedName, $"without {species} in allow_woodcutting_tree, a key pass left the test tree alone; the list is written back");
            else
                run.Fail(removedName,
                    $"without {species}: the tree is {(stillAllowed ? "" : "not ")}allowed and took {hits} hit(s)");
        }

        // With enable_automatic_woodcutting off, the interval hook runs no pass even at a positive
        // interval and a step longer than it, and takes up no timer. The key's path has the same
        // guard but cannot be pressed from here. Both settings are written back straight after.
        private static void CheckSwitchOff(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            TreeBase tree, ItemDrop.ItemData axe)
        {
            const string name = "woodcutting_switch_off_no_pass";
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
            var nearer = FindNearerTarget(player, tree);
            if (nearer != null)
            {
                run.Skip(name, nearer);
                return;
            }

            var health = ReadHealth(tree);
            var blocked = HitObserver.BlockedHits;
            var timers = WoodcuttingModule.WoodcuttingTimerCount;
            HitObserver.Reset();
            var step = PassInterval + 1f;
            var off = false;
            try
            {
                if (!Write(run, held, WoodcuttingConfig.EnableAutomaticWoodcuttingEntry, false) ||
                    !Write(run, held, WoodcuttingConfig.WoodcuttingIntervalEntry, PassInterval))
                {
                    run.Skip(name, "enable_automatic_woodcutting or woodcutting_interval could not be written for the check");
                    return;
                }
                off = !WoodcuttingConfig.EnableAutomaticWoodcutting;
                method.Invoke(null, new object[] { player, step });
            }
            finally
            {
                Write(run, held, WoodcuttingConfig.WoodcuttingIntervalEntry, 0f);
                Write(run, held, WoodcuttingConfig.EnableAutomaticWoodcuttingEntry, true);
            }

            var calls = HitObserver.Records.Count + HitObserver.BlockedHits - blocked;
            var after = WoodcuttingModule.WoodcuttingTimerCount;
            if (off && calls == 0 && ReadHealth(tree).Equals(health) && after == timers)
                run.Pass(name,
                    $"with enable_automatic_woodcutting off, a {step:0.##} s step of the interval hook at woodcutting_interval {PassInterval:0.##} hit nothing and took up no timer");
            else
                run.Fail(name,
                    $"with the switch {(off ? "off" : "still on")}: {calls} hit(s), timers {timers} -> {after}");
        }

        // With need_to_equip_axe off and nothing in the right hand, a pass uses the lowest-tier
        // axe of the inventory whose tool tier reaches the tree, the first such one on a tie, and
        // wears that axe; with it on, the same pass does nothing. The test tree's tier is raised so
        // the lowest test axe does not reach it when there are three tiers to choose from. Your own
        // axes count like the test axes; their durability is put back at the end.
        private static void CheckInventoryAxe(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            TreeBase tree, ItemDrop.ItemData axe, PlayerState state)
        {
            const string usedName = "woodcutting_inventory_axe_used";
            const string equipName = "woodcutting_inventory_axe_needs_equip";
            var tiers = AxePrefabs
                .Select(x => (Name: x, Shared: ObjectDB.instance.GetItemPrefab(x)?.GetComponent<ItemDrop>()?.m_itemData.m_shared))
                .Where(x => x.Shared != null && x.Shared.m_skillType == Skills.SkillType.Axes)
                .GroupBy(x => x.Shared.m_toolTier)
                .OrderBy(x => x.Key)
                .Select(x => x.First())
                .ToList();
            string skip = null;
            if (tiers.Count < 2) skip = "fewer than two axe tiers are available";
            else if (player.RightItem != axe) skip = "the test axe is not in your right hand";
            else skip = FindNearerTarget(player, tree);
            if (skip != null)
            {
                run.Skip(usedName, skip);
                run.Skip(equipName, skip);
                return;
            }

            var targetTier = tiers.Count >= 3 ? tiers[1].Shared.m_toolTier : tiers[0].Shared.m_toolTier;
            foreach (var tier in tiers)
                if (tier.Name != Objects.GetPrefabName(axe.m_dropPrefab))
                    state.Add(tier.Name);

            var inventory = player.GetInventory();
            var minToolTier = tree.m_minToolTier;
            player.UnequipItem(axe, false);
            tree.m_minToolTier = targetTier;
            try
            {
                if (player.RightItem != null)
                {
                    run.Skip(usedName, "the right hand still holds an item after the axe was unequipped");
                    run.Skip(equipName, "the right hand still holds an item after the axe was unequipped");
                    return;
                }
                if (!Write(run, held, WoodcuttingConfig.NeedToEquipAxeEntry, false))
                {
                    run.Skip(usedName, "need_to_equip_axe could not be turned off for the check");
                    run.Skip(equipName, "need_to_equip_axe could not be turned off for the check");
                    return;
                }

                // Worked out here from the inventory, apart from the module.
                var axes = inventory.GetAllItems().Where(x => x.m_shared.m_skillType == Skills.SkillType.Axes).ToList();
                var expected = axes
                    .Where(x => (!x.m_shared.m_useDurability || x.m_durability > 0f) && x.m_shared.m_toolTier >= targetTier)
                    .OrderBy(x => x.m_shared.m_toolTier)
                    .FirstOrDefault();
                var before = axes.ToDictionary(x => x, x => x.m_durability);
                HitObserver.Reset();
                WoodcuttingModule.RunKeyPass(player);

                var records = HitObserver.Records.Where(x => x.Target == tree).ToList();
                var worn = axes.Where(x => !x.m_durability.Equals(before[x])).ToList();
                var drain = expected == null || !expected.m_shared.m_useDurability
                    ? 0f
                    : expected.m_shared.m_useDurabilityDrain * Game.m_durabilityRate;
                var problems = new List<string>();
                if (expected == null) problems.Add($"no axe of the inventory reaches tier {targetTier}");
                if (records.Count != 1) problems.Add($"{records.Count} hit(s) on the test tree");
                else if (expected != null &&
                         (records[0].ToolTier != expected.m_shared.m_toolTier ||
                          records[0].ItemWorldLevel != (byte)expected.m_worldLevel))
                    problems.Add($"the hit carries tool tier {records[0].ToolTier} and world level {records[0].ItemWorldLevel}, not those of the tier {expected.m_shared.m_toolTier} axe");
                if (expected != null && drain > 0f &&
                    (worn.Count != 1 || worn[0] != expected || Mathf.Abs(before[expected] - expected.m_durability - drain) > 0.001f))
                    problems.Add($"{worn.Count} axe(s) lost durability, expected only the tier {expected.m_shared.m_toolTier} axe by {drain:0.###}");
                if (problems.Count == 0)
                    run.Pass(usedName,
                        $"with nothing in your right hand and a tree of tier {targetTier}, a key pass hit it with the tier {expected.m_shared.m_toolTier} axe of the inventory ({tiers.Count} tiers held) and wore only that axe");
                else
                    run.Fail(usedName, string.Join("; ", problems));

                if (!Write(run, held, WoodcuttingConfig.NeedToEquipAxeEntry, true))
                {
                    run.Skip(equipName, "need_to_equip_axe could not be turned back on for the check");
                    return;
                }
                var health = ReadHealth(tree);
                before = axes.ToDictionary(x => x, x => x.m_durability);
                HitObserver.Reset();
                WoodcuttingModule.RunKeyPass(player);
                var calls = HitObserver.Records.Count(x => x.Target == tree);
                var changed = axes.Count(x => !x.m_durability.Equals(before[x]));
                if (calls == 0 && changed == 0 && ReadHealth(tree).Equals(health))
                    run.Pass(equipName, "with need_to_equip_axe on and nothing in your right hand, a key pass hit nothing and wore no axe");
                else
                    run.Fail(equipName, $"with need_to_equip_axe on and nothing in your right hand: {calls} hit(s), {changed} axe(s) worn");
            }
            finally
            {
                tree.m_minToolTier = minToolTier;
                Write(run, held, WoodcuttingConfig.NeedToEquipAxeEntry, true);
                if (inventory.ContainsItem(axe) && !player.IsItemEquiped(axe)) player.EquipItem(axe, false);
            }
        }

        // -- Felling ---------------------------------------------------------------------------

        // The test tree is felled by one hit of the entry. Its log is refused by the entry and by
        // a pass while the game still ignores damage on it, and its stump gets the component from
        // the real Destructible.Start hook a frame later. Once the log can take damage, both
        // resolve to the tree's species, a pass leaves both alone while that species is not
        // allowed, the entry hits the log, and, with the log removed, a pass hits the stump.
        private static IEnumerator Fell(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, Player player,
            WoodcuttingComponent component, TreeBase tree, ItemDrop.ItemData axe, string species,
            List<GameObject> spawned)
        {
            var names = new[]
            {
                "woodcutting_felling_spawned", "woodcutting_fresh_log_refused", "woodcutting_stump_attached",
                "woodcutting_species_log_stump", "woodcutting_species_removed_log_stump",
                "woodcutting_log_hit_after_delay", "woodcutting_stump_pass_hit"
            };
            var logPrefab = tree.m_logPrefab;
            var stubPrefab = tree.m_stubPrefab;
            string skip = null;
            if (player.RightItem != axe) skip = "the test axe is not in your right hand";
            else if (!logPrefab || !stubPrefab) skip = $"the {TreePrefab} prefab has no log or no stump";
            else if (!Objects.GetZNetView(tree, out var nview) || nview == null || !nview.IsValid())
                skip = "the test tree is gone";
            else nview.GetZDO().Set(ZDOVars.s_health, 1f);
            if (skip != null)
            {
                foreach (var name in names) run.Skip(name, skip);
                yield break;
            }

            var first = spawned.Count;
            HitObserver.Reset();
            var felled = component.Chop(player);
            var fellTime = Time.time;
            var created = spawned.Skip(first).Where(x => x).ToList();
            var log = created.Select(x => x.GetComponent<TreeLog>())
                .FirstOrDefault(x => x && Objects.GetPrefabName(x.gameObject) == logPrefab.name);
            var stump = created.Select(x => x.GetComponent<Destructible>())
                .FirstOrDefault(x => x && Objects.GetPrefabName(x.gameObject) == stubPrefab.name);
            var gone = !tree || !tree.gameObject.activeInHierarchy;
            if (!felled || !gone || !log || !stump)
            {
                run.Fail(names[0],
                    $"one hit at 1 health: entry returned {felled}, the tree is {(gone ? "gone" : "still standing")}, log {(log ? "spawned" : "missing")}, stump {(stump ? "spawned" : "missing")}");
                foreach (var name in names.Skip(1)) run.Skip(name, "the test tree was not felled into a log and a stump");
                yield break;
            }
            run.Pass(names[0], "one hit felled the test tree into a log and a stump, both taken as test objects");
            Physics.SyncTransforms();

            CheckFreshLog(run, names[1], player, log, axe);

            for (var frame = 0; frame < StartFrames && !stump.GetComponent<WoodcuttingComponent>(); frame++)
                yield return null;
            var stumpComponent = stump ? stump.GetComponent<WoodcuttingComponent>() : null;
            if (stumpComponent && stumpComponent.ScopeGeneration == WoodcuttingModule.Generation &&
                stumpComponent.Owner == stump)
                run.Pass(names[2], $"the stump carries a component of generation {stumpComponent.ScopeGeneration} after its Start");
            else
                run.Fail(names[2],
                    $"the stump carries {(stumpComponent ? $"a component of generation {stumpComponent.ScopeGeneration}" : "no component")} {StartFrames} frames after it spawned");

            while (Time.time < fellTime + LogDelay) yield return null;
            Physics.SyncTransforms();
            var logComponent = log ? log.GetComponent<WoodcuttingComponent>() : null;
            if (!logComponent || !stumpComponent)
            {
                foreach (var name in names.Skip(3)) run.Skip(name, "the log or the stump has no component");
                yield break;
            }

            var logSpecies = SpeciesOf(logPrefab.name);
            var stumpSpecies = SpeciesOf(stubPrefab.name);
            if (logSpecies.Contains(species) && stumpSpecies.Contains(species) && logComponent.IsAllowed() &&
                stumpComponent.IsAllowed())
                run.Pass(names[3],
                    $"the log and the stump resolve to {species}, the felled tree's species, and are allowed (the log to {logSpecies.Count}, the stump to {stumpSpecies.Count} species)");
            else
                run.Fail(names[3],
                    $"the log resolves {(logSpecies.Contains(species) ? "" : "not ")}to {species} and is {(logComponent.IsAllowed() ? "" : "not ")}allowed; the stump resolves {(stumpSpecies.Contains(species) ? "" : "not ")}to {species} and is {(stumpComponent.IsAllowed() ? "" : "not ")}allowed");

            CheckSpeciesRemovedLogStump(run, held, names[4], player, log, stump, logComponent, stumpComponent,
                species, logSpecies, stumpSpecies);
            CheckLogHit(run, names[5], player, log, logComponent, axe);

            RemoveTestObject(log.gameObject);
            CheckStumpPass(run, names[6], player, stump, axe);
        }

        // The log the felling just spawned: the entry refuses it and a pass leaves it alone while
        // the game still ignores damage on it.
        private static void CheckFreshLog(FeatureRun run, string name, Player player, TreeLog log,
            ItemDrop.ItemData axe)
        {
            var component = log.GetComponent<WoodcuttingComponent>();
            if (!component || component.ScopeGeneration != WoodcuttingModule.Generation)
            {
                run.Fail(name,
                    $"the fresh log carries {(component ? $"a component of generation {component.ScopeGeneration}" : "no component")}, not one of the running generation {WoodcuttingModule.Generation}");
                return;
            }

            var health = ReadHealth(log);
            var durability = axe.m_durability;
            HitObserver.Reset();
            var chopped = component.Chop(player);
            var worn = !axe.m_durability.Equals(durability);
            WoodcuttingModule.RunKeyPass(player);

            var problems = new List<string>();
            if (chopped) problems.Add("the entry reported a hit");
            if (worn) problems.Add("the entry wore the axe");
            var hits = HitObserver.Records.Count(x => x.Target == log);
            if (hits > 0) problems.Add($"{hits} damage call(s) on it");
            if (!ReadHealth(log).Equals(health)) problems.Add("it lost health");
            if (problems.Count == 0)
                run.Pass(name, "within its spawn delay, the fresh log was refused by the entry and left alone by a key pass");
            else
                run.Fail(name, $"within its spawn delay: {string.Join(", ", problems)}");
        }

        // With the felled tree's species taken out of allow_woodcutting_tree, its log and stump
        // are not allowed unless another allowed species also leads to them, and a pass leaves
        // the ones that are not alone. The list is written back straight after.
        private static void CheckSpeciesRemovedLogStump(FeatureRun run,
            Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held, string name, Player player,
            TreeLog log, Destructible stump, WoodcuttingComponent logComponent, WoodcuttingComponent stumpComponent,
            string species, HashSet<string> logSpecies, HashSet<string> stumpSpecies)
        {
            var nearer = FindNearerTarget(player, stump, log);
            if (nearer != null)
            {
                run.Skip(name, nearer);
                return;
            }

            var allowed = WoodcuttingConfig.AllowWoodcuttingTree;
            var without = new StringList(allowed.Where(x => x != species));
            var logExpected = logSpecies.Any(x => x != species && without.Contains(x));
            var stumpExpected = stumpSpecies.Any(x => x != species && without.Contains(x));
            if (logExpected && stumpExpected)
            {
                run.Skip(name, "another allowed species leads to both the log and the stump");
                return;
            }
            if (!Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, without))
            {
                run.Skip(name, $"allow_woodcutting_tree could not be written without {species}");
                return;
            }

            bool logAllowed, stumpAllowed;
            HitObserver.Reset();
            try
            {
                logAllowed = logComponent.IsAllowed();
                stumpAllowed = stumpComponent.IsAllowed();
                WoodcuttingModule.RunKeyPass(player);
            }
            finally
            {
                Write(run, held, WoodcuttingConfig.AllowWoodcuttingTreeEntry, allowed);
            }

            var logHits = HitObserver.Records.Count(x => x.Target == log);
            var stumpHits = HitObserver.Records.Count(x => x.Target == stump);
            var problems = new List<string>();
            if (logAllowed != logExpected) problems.Add($"the log is {(logAllowed ? "" : "not ")}allowed");
            if (stumpAllowed != stumpExpected) problems.Add($"the stump is {(stumpAllowed ? "" : "not ")}allowed");
            if (!logExpected && logHits > 0) problems.Add($"the log took {logHits} hit(s)");
            if (!stumpExpected && stumpHits > 0) problems.Add($"the stump took {stumpHits} hit(s)");
            if (problems.Count == 0)
                run.Pass(name,
                    $"without {species} in allow_woodcutting_tree, the log is {(logAllowed ? "still allowed through another species" : "not allowed")} and the stump {(stumpAllowed ? "still allowed through another species" : "not allowed")}, and a key pass left the ones not allowed alone; the list is written back");
            else
                run.Fail(name, $"without {species}: {string.Join("; ", problems)}");
        }

        // Past its spawn delay, the entry hits the log once.
        private static void CheckLogHit(FeatureRun run, string name, Player player, TreeLog log,
            WoodcuttingComponent component, ItemDrop.ItemData axe)
        {
            var health = ReadHealth(log);
            HitObserver.Reset();
            var chopped = component.Chop(player);
            var records = HitObserver.Records.Where(x => x.Target == log).ToList();
            var after = ReadHealth(log);
            if (chopped && records.Count == 1 && after < health)
            {
                run.Pass(name, $"past its spawn delay, the entry hit the log once and took {health - after:0.##} health");
                CheckRaise(run, "woodcutting_log_skill", axe, log, "the log");
                return;
            }

            run.Skip("woodcutting_log_skill", "there was no hit on the log to inspect");
            var obstacle = chopped ? null : FindObstacle(player, log);
            if (obstacle != null)
                run.Skip(name, obstacle.Replace("test tree", "test log"));
            else
                run.Fail(name, $"entry returned {chopped}, {records.Count} damage call(s) on the log, health {health:0.##} -> {after:0.##}");
        }

        // With the log removed, a key pass hits the stump: the stump has to be the nearest
        // candidate.
        private static void CheckStumpPass(FeatureRun run, string name, Player player, Destructible stump,
            ItemDrop.ItemData axe)
        {
            var nearer = FindNearerTarget(player, stump);
            if (nearer != null)
            {
                run.Skip(name, nearer);
                run.Skip("woodcutting_stump_skill", nearer);
                return;
            }

            var health = ReadHealth(stump);
            var blocked = HitObserver.BlockedHits;
            HitObserver.Reset();
            WoodcuttingModule.RunKeyPass(player);
            var records = HitObserver.Records.Where(x => x.Target == stump).ToList();
            var others = HitObserver.BlockedHits - blocked;
            var after = ReadHealth(stump);
            if (records.Count == 1 && after < health && others == 0)
            {
                run.Pass(name, $"a key pass hit the stump once and took {health - after:0.##} health");
                CheckRaise(run, "woodcutting_stump_skill", axe, stump, "the stump");
                return;
            }

            run.Skip("woodcutting_stump_skill", "there was no hit on the stump to inspect");
            var obstacle = records.Count == 0 && others == 0 ? FindObstacle(player, stump) : null;
            if (obstacle != null)
                run.Skip(name, obstacle.Replace("test tree", "test stump"));
            else
                run.Fail(name, $"{records.Count} hit(s) on the stump, {others} on other trees, logs or stumps, health {health:0.##} -> {after:0.##}");
        }

        // A Destructible that is not the stump of a tree gets no component from the same hook.
        private static void CheckOtherDestructible(FeatureRun run, GameObject other, string skip)
        {
            const string name = "woodcutting_other_destructible_not_attached";
            if (!other)
            {
                run.Skip(name, skip ?? "the other destructible is gone");
                return;
            }

            // Destructible clears its first-frame flag in Start, the call the module's hook follows.
            var firstFrame = AccessTools.Field(typeof(Destructible), "m_firstFrame");
            var destructible = other.GetComponent<Destructible>();
            if (firstFrame == null || (bool)firstFrame.GetValue(destructible))
            {
                run.Skip(name, $"the Start of {Objects.GetPrefabName(other)} could not be seen to have run");
                return;
            }

            var component = other.GetComponent<WoodcuttingComponent>();
            if (!component)
                run.Pass(name, $"{Objects.GetPrefabName(other)}, a Destructible that is not a stump, got no component");
            else
                run.Fail(name, $"{Objects.GetPrefabName(other)}, a Destructible that is not a stump, carries a component");
        }

        private static void CheckRefused(FeatureRun run, string name, string condition, Player player,
            WoodcuttingComponent component, TreeBase tree, ItemDrop.ItemData axe)
        {
            var health = ReadHealth(tree);
            var durability = axe.m_durability;
            var stamina = ReadStamina(player);
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
            if (!ReadStamina(player).Equals(stamina)) problems.Add("stamina was used");

            if (problems.Count == 0)
                run.Pass(name, $"{condition}: no hit, nothing spent");
            else
                run.Fail(name, $"{condition}: {string.Join(", ", problems)}");
        }

        // The module's interval hook, the call FixedUpdate makes; null when it is not found.
        private static MethodInfo IntervalHook()
        {
            return AccessTools.Method(typeof(WoodcuttingModule), "OnPlayerFixedUpdate",
                new[] { typeof(Player), typeof(float) });
        }

        // A pass goes to the nearest candidate first, so it can hit a test object only when that
        // object is strictly the nearest that is not a test object. Returns why it is not, or
        // null. Further test objects passed in count as the target's distance as well.
        private static string FindNearerTarget(Player player, Component target, params Component[] alongside)
        {
            var origin = player.transform.position;
            var testDistance = new[] { target }.Concat(alongside).Where(x => x)
                .Min(x => Vector3.Distance(origin, x.transform.position));
            var nearer = UnityEngine.Object.FindObjectsByType<WoodcuttingComponent>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(x => x && !HitObserver.IsTestObject(x.gameObject) &&
                            Objects.GetZNetView(x, out var nview) && nview.IsValid())
                .Select(x => Vector3.Distance(origin, x.transform.position))
                .Where(x => x <= testDistance)
                .ToList();
            return nearer.Count > 0
                ? $"a tree, log or stump of your world is {nearer.Min():0.#} m from you, not farther than the test object ({testDistance:0.#} m), so the pass could go to it; move away from trees and retry"
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
        internal static bool Write(FeatureRun run, Dictionary<ConfigEntryBase, SmokeSession.SettingRestoration> held,
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

        // Anything near the spot where the test tree goes: a building piece, another tree, log or
        // stump, a creature or any other destructible. Returns its name, or null.
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

        // Whether a ray from the eyes toward the middle of one of the test object's colliders, at
        // eye height, first meets the test object. Returns why not, or null. Asked of the physics
        // engine directly, so a refused hit can be told apart from something of your world in the
        // way.
        private static string FindObstacle(Player player, Component target)
        {
            var eye = player.m_eye.position;
            string obstacle = null;
            foreach (var collider in target.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;

                var bounds = collider.bounds;
                var aim = new Vector3(bounds.center.x, Mathf.Clamp(eye.y, bounds.min.y, bounds.max.y),
                    bounds.center.z) - eye;
                if (!Physics.Raycast(eye, aim.normalized, out var hit, aim.magnitude + 1f, SwingMask.Value,
                        QueryTriggerInteraction.Ignore))
                    continue;

                var first = Projectile.FindHitObject(hit.collider);
                if (first == target.gameObject) return null;
                obstacle = obstacle ?? Objects.GetPrefabName(first);
            }

            return obstacle != null
                ? $"{obstacle} stands between your eyes and the test tree; face open ground and retry"
                : "no part of the test tree was found at your eye height; stand on level ground and retry";
        }

        internal static string PickAxe(int minToolTier, out ItemDrop.ItemData.SharedData shared)
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

        // Every skill a check can raise: those of the test axes and their special hits, and the
        // club's.
        internal static IEnumerable<Skills.SkillType> RaisableSkills()
        {
            foreach (var name in AxePrefabs.Concat(new[] { OtherWeaponPrefab }))
            {
                var prefab = ObjectDB.instance.GetItemPrefab(name);
                var itemDrop = prefab ? prefab.GetComponent<ItemDrop>() : null;
                if (itemDrop == null) continue;

                var shared = itemDrop.m_itemData.m_shared;
                yield return shared.m_skillType;
                yield return shared.m_attack.m_specialHitSkill;
            }
        }

        // The first non-stump Destructible prefab available, spawned non-persistent. Returns null
        // with the reason when there is none.
        private static GameObject SpawnOtherDestructible(Vector3 position, out string skip)
        {
            skip = "none of the other destructible prefabs is available (" + string.Join(", ", OtherDestructiblePrefabs) + ")";
            foreach (var name in OtherDestructiblePrefabs)
            {
                var prefab = ZNetScene.instance.GetPrefab(name);
                if (!prefab || !prefab.GetComponent<Destructible>() || !prefab.GetComponent<ZNetView>() ||
                    prefab.GetComponent<TreeBase>() || prefab.GetComponent<TreeLog>() ||
                    WoodcuttingSpecies.IsStub(name.GetStableHashCode()))
                    continue;

                return SpawnTestObject(name, position, Quaternion.identity, out skip);
            }
            return null;
        }

        // Spawns the prefab non-persistent and records it with the session, so a run cut short
        // still has it removed. Returns null with the reason when it could not be set up.
        internal static GameObject SpawnTestObject(string prefabName, Vector3 position, Quaternion rotation,
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

        // A log or stump that felling a test tree spawned, taken as a test object in the frame it
        // spawns: recorded with the session, non-persistent and at a health no hit reaches. A log
        // stays where it spawned instead of falling onto you, the test objects or your buildings.
        internal static void AdoptFelled(GameObject go)
        {
            SmokeSession.TrackSpawn(go);
            if (Objects.GetZNetView(go.transform, out var nview) && nview != null && nview.IsValid() &&
                nview.GetZDO() != null)
            {
                if (!nview.IsOwner()) nview.ClaimOwnership();
                nview.GetZDO().Persistent = false;
                nview.GetZDO().Set(ZDOVars.s_health, SeededHealth);
            }

            var body = go.GetComponent<TreeLog>() ? go.GetComponent<Rigidbody>() : null;
            if (body) body.isKinematic = true;
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
        internal static void RemoveTestObject(GameObject go)
        {
            if (!go) return;
            go.SetActive(false);
            Physics.SyncTransforms();
            if (SmokeSession.DestroyTestObject(go, 0, Label)) SmokeSession.ReleaseSpawn(go);
        }

        internal static bool SeedHealth(Component target)
        {
            if (!Objects.GetZNetView(target, out var nview) || nview == null || !nview.IsValid()) return false;
            nview.GetZDO().Set(ZDOVars.s_health, SeededHealth);
            return true;
        }

        // The stored health of a tree, log or stump, NaN when it cannot be read.
        internal static float ReadHealth(Component target)
        {
            if (!target || !Objects.GetZNetView(target, out var nview) || nview == null || !nview.IsValid())
                return float.NaN;
            var fallback = target is TreeBase tree ? tree.m_health : SeededHealth;
            return nview.GetZDO().GetFloat(ZDOVars.s_health, fallback);
        }

        private static float ReadStamina(Player player)
        {
            return StaminaField != null ? (float)StaminaField.GetValue(player) : 0f;
        }

        // -- Player state ---------------------------------------------------------------------

        // What a woodcutting section changes on the player, and how it is put back; the area
        // section uses it too. Restore is safe to call again: each part is put back once.
        internal sealed class PlayerState
        {
            private readonly Player _player;
            private readonly List<ItemDrop.ItemData> _equipped;
            private readonly List<ItemDrop.ItemData> _added = new List<ItemDrop.ItemData>();
            private readonly Dictionary<Skills.SkillType, (bool Had, float Level, float Accumulator)> _skills =
                new Dictionary<Skills.SkillType, (bool, float, float)>();
            private readonly Dictionary<ItemDrop.ItemData, float> _durability;
            private readonly float _stamina;
            private readonly float _regenTimer;
            private readonly FieldInfo _pitchField;
            private readonly float _pitch;
            private bool _pitchChanged;
            private bool _skillsRestored;
            private bool _durabilityRestored;
            private bool _staminaChanged;

            public PlayerState(Player player, IEnumerable<Skills.SkillType> skills)
            {
                _player = player;
                _equipped = player.GetInventory().GetEquippedItems().ToList();
                // A pass may use one of your own axes; its durability is put back.
                _durability = player.GetInventory().GetAllItems()
                    .Where(x => x.m_shared.m_skillType == Skills.SkillType.Axes)
                    .ToDictionary(x => x, x => x.m_durability);

                foreach (var type in skills)
                {
                    if (type == Skills.SkillType.None || _skills.ContainsKey(type)) continue;
                    var skill = FindSkill(type);
                    _skills[type] = (skill != null, skill?.m_level ?? 0f, skill?.m_accumulator ?? 0f);
                }

                if (CanSetStamina)
                {
                    _stamina = (float)StaminaField.GetValue(player);
                    _regenTimer = (float)RegenTimerField.GetValue(player);
                }

                _pitchField = AccessTools.Field(typeof(Player), "m_lookPitch");
                if (_pitchField != null && _pitchField.FieldType == typeof(float))
                    _pitch = (float)_pitchField.GetValue(player);
                else
                    _pitchField = null;
            }

            public bool CanSetStamina => StaminaField != null && RegenTimerField != null;

            public void SetStamina(float stamina, float regenTimer)
            {
                _staminaChanged = true;
                StaminaField.SetValue(_player, stamina);
                RegenTimerField.SetValue(_player, regenTimer);
            }

            public void RestoreStamina()
            {
                if (!_staminaChanged || !_player) return;
                StaminaField.SetValue(_player, _stamina);
                RegenTimerField.SetValue(_player, _regenTimer);
                _staminaChanged = false;
            }

            public void LookDown(float pitch)
            {
                if (_pitchField == null) return;
                _pitchField.SetValue(_player, pitch);
                _pitchChanged = true;
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

                if (!_durabilityRestored)
                {
                    foreach (var pair in _durability)
                        if (inventory.ContainsItem(pair.Key))
                            pair.Key.m_durability = pair.Value;
                    _durabilityRestored = true;
                }

                if (_pitchChanged)
                {
                    _pitchField.SetValue(_player, _pitch);
                    _pitchChanged = false;
                }

                RestoreStamina();

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
            public byte ItemWorldLevel;
            public short ToolTier;
        }

        // Patches added for the section only and removed at its end. While attached, they record
        // every damage call on the test objects and every skill raise of the player, and drop
        // every damage call on any other tree, log or stump, whoever makes it, so no check can
        // damage one of your world; a Destructible that is not a stump is left alone. A call on a
        // test object is recorded whatever its attacker, so a malformed hit fails the field checks
        // instead of passing unseen. The log and stump a felling of a test tree spawns become test
        // objects in the frame they spawn.
        private static class HitObserver
        {
            private const string HarmonyId = "net.eidee.automatics.smoke.features.woodcutting";

            private static Harmony _harmony;
            private static bool _attached;
            private static Player _player;
            private static TreeBase _felling;

            // Registered with the session before any patch goes on, so a run cut short still
            // takes the patches off; released once they are off.
            private static readonly Func<string, bool> DetachHandBack = reason => Detach(out _);
            private static List<GameObject> _spawned;
            private static readonly HashSet<GameObject> TestObjects = new HashSet<GameObject>();

            public static readonly List<HitRecord> Records = new List<HitRecord>();
            public static readonly List<(Skills.SkillType Skill, float Value)> Raises =
                new List<(Skills.SkillType, float)>();
            public static int BlockedHits { get; private set; }

            public static bool IsTestObject(GameObject go)
            {
                return go && TestObjects.Contains(go);
            }

            public static bool Attach(Player player, GameObject tree, List<GameObject> spawned, out string problem)
            {
                problem = null;
                var raise = AccessTools.Method(typeof(Player), nameof(Player.RaiseSkill),
                    new[] { typeof(Skills.SkillType), typeof(float) });
                var damages = new[]
                {
                    AccessTools.Method(typeof(TreeBase), nameof(TreeBase.Damage)),
                    AccessTools.Method(typeof(TreeLog), nameof(TreeLog.Damage)),
                    AccessTools.Method(typeof(Destructible), nameof(Destructible.Damage))
                };
                var spawnLog = AccessTools.Method(typeof(TreeBase), "SpawnLog");
                var awakes = new[]
                {
                    AccessTools.Method(typeof(TreeLog), "Awake"),
                    AccessTools.Method(typeof(Destructible), "Awake")
                };
                if (raise == null || spawnLog == null || damages.Any(x => x == null) || awakes.Any(x => x == null))
                {
                    problem = "Player.RaiseSkill, TreeBase.SpawnLog, a Damage method or an Awake method was not found";
                    return false;
                }
                if (_harmony != null && !Detach(out var stale))
                {
                    problem = $"an earlier hit observer could not be removed ({stale})";
                    return false;
                }

                SmokeSession.TrackHandBack(DetachHandBack);
                _player = player;
                _spawned = spawned;
                TestObjects.Clear();
                TestObjects.Add(tree);
                _attached = true;
                Reset();
                BlockedHits = 0;
                _harmony = new Harmony(HarmonyId);
                try
                {
                    foreach (var damage in damages)
                        _harmony.Patch(damage, new HarmonyMethod(typeof(HitObserver), nameof(DamagePrefix)));
                    _harmony.Patch(raise, postfix: new HarmonyMethod(typeof(HitObserver), nameof(RaisePostfix)));
                    _harmony.Patch(spawnLog, new HarmonyMethod(typeof(HitObserver), nameof(SpawnLogPrefix)),
                        finalizer: new HarmonyMethod(typeof(HitObserver), nameof(SpawnLogFinalizer)));
                    foreach (var awake in awakes)
                        _harmony.Patch(awake, postfix: new HarmonyMethod(typeof(HitObserver), nameof(AwakePostfix)));
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
                _felling = null;
                _spawned = null;
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
                if (!__instance || !TestObjects.Contains(__instance.gameObject))
                {
                    if (__instance is Destructible destructible && !IsStump(destructible)) return true;
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
                        Skill = hit.m_skill,
                        ItemWorldLevel = hit.m_itemWorldLevel,
                        ToolTier = hit.m_toolTier
                    });
                return true;
            }

            private static bool IsStump(Destructible destructible)
            {
                var zdo = Objects.GetZNetView(destructible, out var nview) && nview != null ? nview.GetZDO() : null;
                return zdo != null && WoodcuttingSpecies.IsStub(zdo.GetPrefab());
            }

            private static void RaisePostfix(Player __instance, Skills.SkillType skill, float value)
            {
                if (_player && __instance == _player) Raises.Add((skill, value));
            }

            private static void SpawnLogPrefix(TreeBase __instance)
            {
                if (_attached && __instance && TestObjects.Contains(__instance.gameObject)) _felling = __instance;
            }

            private static void SpawnLogFinalizer()
            {
                _felling = null;
            }

            private static void AwakePostfix(Component __instance)
            {
                if (!_attached || !_felling || !__instance || TestObjects.Contains(__instance.gameObject)) return;

                var go = __instance.gameObject;
                TestObjects.Add(go);
                _spawned?.Add(go);
                AdoptFelled(go);
            }
        }
    }
}
