using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using CookingStationProcess = Automatics.AutomaticProcessing.CookingStationProcess;
using Process = Automatics.AutomaticProcessing.Process;
using ProcessingConfig = Automatics.AutomaticProcessing.Config;
using ProcessingLogics = Automatics.AutomaticProcessing.Logics;

namespace Automatics.SmokeTest
{
    // The skill subcommand of automatics_smoke_processing.
    //
    // Automatic Processing gives the player who built a cooking station the skill and the bonus
    // food the game gives for putting food in (0.4) and taking cooked food out (0.6 and a bonus
    // roll) by hand, and only in that player's own game. The check places a chest and a stone
    // oven of its own and sets who built the oven through the piece's creator field: nobody, a
    // player id that is not yours, then you. For each, in the frame of the spawn, it drives the
    // oven's Craft entry from the chest, turns the food the oven took into its cooked product as
    // the oven's own update would, and drives the Store entry for that slot. A Harmony prefix
    // on Player.RaiseSkill records every raise of the oven's skill meanwhile, with the value it
    // was called with (the method itself scales it by status effects). The bonus roll is made
    // certain by raising the game's InventoryGui.m_craftBonusChance (or impossible by setting
    // it to 0), with your skill level lifted to at least 1 before the first call so the roll
    // has a skill factor for every builder; once, the chest is filled first so that it has room
    // for exactly one more item.
    //
    // Your skill level and progress, the bonus chance and the patch are put back however the
    // run ends, through a hand-back registered with the session before any of them changes. The
    // bonus food and the filler the check added are taken out of its chest again right away, so
    // the chest and the oven go through the rpc subcommand's hand-back with their ledger
    // balanced. No setting is changed, and no frame passes while the test objects stand, so the
    // oven's own update never runs.
    internal static partial class ProcessingSmokeCommand
    {
        private static IEnumerator RunSkill(Terminal context, int runId, string runner)
        {
            return RunBench(context, runner, () => new SkillRun(runId, runner,
                line => Emit(context, LogLevel.Info, line),
                line => Emit(context, LogLevel.Warning, line)));
        }

        private sealed class SkillRun : BenchRun
        {
            private const string Setup = "processing_skill_setup";
            private const string NoBuilder = "processing_skill_no_builder";
            private const string OtherBuilder = "processing_skill_other_builder";
            private const string Builder = "processing_skill_builder";
            private const string BuilderBonus = "processing_skill_builder_bonus";
            private const string BonusNoRoom = "processing_skill_builder_bonus_no_room";
            private const string Restored = "processing_skill_state_restored";

            private const string HarmonyId = "net.eidee.valheim.automatics.smoke.processing-skill";

            // The raises of CookingStation.OnInteract for food put in and cooked food taken out.
            private const float CraftRaise = 0.4f;
            private const float StoreRaise = 0.6f;

            // One Craft and one Store per check, each on one material.
            private const int Cycles = 5;

            // Times a skill factor of at least 0.01, far above any roll.
            private const float CertainBonusChance = 1000f;

            // CookingStation.Status.Done; the enum is private.
            private const int CookedStatus = 1;

            private static readonly string[] Order =
            {
                NoBuilder, OtherBuilder, Builder, BuilderBonus, BonusNoRoom, Restored
            };

            private static readonly FieldInfo CreatorField = AccessTools.Field(typeof(Piece), "m_creator");

            private static readonly MethodInfo RaiseMethod = AccessTools.Method(typeof(Player),
                nameof(Player.RaiseSkill), new[] { typeof(Skills.SkillType), typeof(float) });

            // Skills.GetSkill adds the skill's entry when the character has none yet.
            private static readonly MethodInfo GetSkillMethod = AccessTools.Method(typeof(Skills),
                "GetSkill", new[] { typeof(Skills.SkillType) });

            private static readonly List<KeyValuePair<Skills.SkillType, float>> Raises =
                new List<KeyValuePair<Skills.SkillType, float>>();

            private static Player _watched;

            private readonly Dictionary<string, CheckResult> _checks =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly OvenBench _oven;
            private string _notReached = "not reached: an earlier step stopped the run";

            // What the run changes outside its own objects, and what it was before.
            private Func<string, bool> _stateHandBack;
            private Harmony _harmony;
            private Skills.SkillType _skill;
            private bool _hadSkill;
            private float _level;
            private float _accumulator;
            private bool _skillTouched;
            private float _bonusChance;
            private bool _bonusTouched;

            public SkillRun(int runId, string runner, Action<string> info, Action<string> warn)
                : this(runId, runner, info, warn, new OvenBench(Setup))
            {
            }

            private SkillRun(int runId, string runner, Action<string> info, Action<string> warn,
                OvenBench oven) : base(runId, runner, info, warn, new List<StationBench> { oven })
            {
                _oven = oven;
            }

            public override IReadOnlyList<CheckResult> Results
            {
                get
                {
                    // A run stopped before the first check reports why under the setup check.
                    if (_checks.Count == 0)
                        return new[]
                        {
                            _oven.Result ?? CheckResult.Fail(Setup,
                                "the run ended before this check reached a result")
                        };

                    return Order.Select(name => _checks.TryGetValue(name, out var result)
                        ? result
                        : CheckResult.Skip(name, _notReached)).ToList();
                }
            }

            public override IEnumerator Execute()
            {
                _info(
                    $"[Automatics Smoke] {_runner} started: it places a chest and a stone oven ahead of you, drives the oven's Craft and Store entries from the chest with nobody, another player and you as the oven's builder, and removes them; your cooking skill and the game's bonus chance are put back, and no setting is changed");

                var player = Player.m_localPlayer;
                if (!CheckEnvironment(player)) yield break;
                if (!FindChestPrefab(out var chestPrefab)) yield break;

                ResolveStations();
                var rawName = _oven.ObjectName.Substring(1);
                if (Active.Count > 0 && !ProcessingLogics.IsAllowProcessing(_oven.ObjectName, Process.Store))
                    SkipRemaining($"allow_processing_by_{rawName} does not include Store");
                if (Active.Count > 0 && ProcessingConfig.StoreOnlyIfProductExists(_oven.ObjectName))
                    SkipRemaining(
                        $"{rawName}_store_only_if_product_exists is on, so the oven stores only into a chest that already holds its product");
                ChooseItems();
                if (Active.Count == 0) yield break;
                if (!_oven.Chosen.To)
                {
                    SkipRemaining("the chosen conversion of the stone oven has no product");
                    yield break;
                }

                var playerId = player.GetPlayerID();
                if (playerId == 0L)
                {
                    SkipRemaining("your character has no player id yet");
                    yield break;
                }

                if (CreatorField == null || RaiseMethod == null || GetSkillMethod == null)
                {
                    SkipRemaining("Piece.m_creator, Player.RaiseSkill(SkillType, float) or Skills.GetSkill(SkillType) was not found in this game build");
                    yield break;
                }

                if (!PlanLayout(player, out var chestPosition, out var distance)) yield break;
                if (!CheckSurroundings(chestPosition, distance)) yield break;
                if (!PlaceBenches(chestPrefab, chestPosition, distance)) yield break;

                // Everything from here to the end happens in the frame of the spawn.
                if (!CheckSources()) yield break;

                var station = _oven.Station;
                var piece = station ? station.GetComponentInParent<Piece>() : null;
                if (!piece)
                {
                    SkipRemaining("the test stone oven has no Piece");
                    yield break;
                }

                if (station.m_skill == Skills.SkillType.None)
                {
                    SkipRemaining("the stone oven raises no skill in this game build (m_skill is None)");
                    yield break;
                }

                var wanted = ProcessingConfig.MaterialCountOfSuppressProcessing(_oven.ObjectName) + Cycles;
                _oven.Seeded = SeedChest(_benches.ChestInventory, _oven.MaterialPrefab, _oven.MaterialName,
                    wanted);
                if (_oven.Seeded < wanted)
                {
                    SkipRemaining(
                        $"only {_oven.Seeded} of the {wanted} {DisplayName(_oven.MaterialName)} fit into the test chest");
                    yield break;
                }

                if (!Watch(player, station)) yield break;

                var other = playerId == long.MaxValue ? playerId - 1 : playerId + 1;
                CheckNobody(NoBuilder, "no builder", Cycle(station, piece, 0L, CertainBonusChance));
                CheckNobody(OtherBuilder, "another player as the builder",
                    Cycle(station, piece, other, CertainBonusChance));
                CheckBuilder(station, piece, playerId);
                CheckRestored(player);
            }

            public override void Stopped(Exception e)
            {
                var message = $"the run stopped on {e.GetType().Name}: {e.Message}";
                var running = Order.FirstOrDefault(x => !_checks.ContainsKey(x));
                if (_checks.Count == 0) _oven.Fail(message);
                else if (running != null) _checks[running] = CheckResult.Fail(running, message);
                _notReached = "not reached: " + message;
            }

            public override void HandBack(string reason)
            {
                base.HandBack(reason);
                if (_stateHandBack != null && PutBack(reason))
                {
                    SmokeSession.ReleaseHandBack(_stateHandBack);
                    _stateHandBack = null;
                }
            }

            // Records the skill and the bonus chance, registers their hand-back, and only then
            // patches Player.RaiseSkill.
            private bool Watch(Player player, CookingStation station)
            {
                if (!InventoryGui.instance)
                {
                    SkipRemaining("the inventory screen (InventoryGui) is not loaded");
                    return false;
                }

                _skill = station.m_skill;
                var skill = FindSkill(player, _skill);
                _hadSkill = skill != null;
                _level = skill?.m_level ?? 0f;
                _accumulator = skill?.m_accumulator ?? 0f;
                _bonusChance = InventoryGui.instance.m_craftBonusChance;
                _skillTouched = true;

                _stateHandBack = PutBack;
                SmokeSession.TrackHandBack(_stateHandBack);

                // The bonus roll needs a skill factor above 0 for every builder, so that a
                // bonus given to the wrong player would show; the level is put back with the
                // rest.
                skill = (Skills.Skill)GetSkillMethod.Invoke(player.GetSkills(), new object[] { _skill });
                if (skill.m_level < 1f) skill.m_level = 1f;
                var factor = player.GetSkillFactor(_skill);
                if (factor * CertainBonusChance < 1f)
                {
                    SkipRemaining(
                        $"the bonus roll cannot be made certain: your {_skill} skill factor is {factor:0.###} even at level {skill.m_level:0.##}");
                    return false;
                }

                _harmony = new Harmony(HarmonyId);
                _watched = player;
                _harmony.Patch(RaiseMethod, new HarmonyMethod(typeof(SkillRun), nameof(RaisePrefix)));
                return true;
            }

            private static void RaisePrefix(Player __instance, Skills.SkillType skill, float value)
            {
                if (_watched && __instance == _watched) Raises.Add(new KeyValuePair<Skills.SkillType, float>(skill, value));
            }

            private List<float> TakeRaises()
            {
                var values = Raises.Where(x => x.Key == _skill).Select(x => x.Value).ToList();
                Raises.Clear();
                return values;
            }

            private sealed class CycleOutcome
            {
                public string Problem;
                public List<float> CraftRaises = new List<float>();
                public List<float> StoreRaises = new List<float>();
                public bool Stored;
                public bool SlotCleared;
                public int Delivered;
            }

            // One Craft and one Store with the given builder and bonus chance. With
            // roomForOne, the chest is filled with the product first so that it has room for
            // exactly one more. What the chest got beyond the one cooked item - bonus food and
            // filler - is taken out again before this returns.
            private CycleOutcome Cycle(CookingStation station, Piece piece, long builder, float bonusChance,
                bool roomForOne = false)
            {
                var outcome = new CycleOutcome();
                var chest = _benches.ChestInventory;
                var zdo = _oven.NView.GetZDO();
                var materialPrefabName = _oven.Chosen.From.gameObject.name;
                var product = _oven.Chosen.To;
                var productName = _oven.ProductName;
                var conversion = station.m_conversion.FirstOrDefault(x =>
                    x != null && x.m_from && x.m_from.gameObject.name == materialPrefabName);
                if (conversion == null)
                {
                    outcome.Problem = $"the test stone oven has no conversion from {DisplayName(_oven.MaterialName)}";
                    return outcome;
                }

                CreatorField.SetValue(piece, builder);

                _oven.Prepare();
                Raises.Clear();
                CookingStationProcess.Craft(station, _oven.NView);
                outcome.CraftRaises = TakeRaises();

                var slot = -1;
                for (var i = 0; i < station.m_slots.Length && slot < 0; i++)
                    if (zdo.GetString("slot" + i) == materialPrefabName)
                        slot = i;
                if (slot < 0)
                {
                    outcome.Problem =
                        $"the Craft entry put no {DisplayName(_oven.MaterialName)} into the oven ({_oven.Diagnose()})";
                    return outcome;
                }

                // What the oven's own update writes once the food is cooked.
                zdo.Set("slot" + slot, product.gameObject.name);
                zdo.Set("slotstatus" + slot, CookedStatus);

                var start = chest.CountItems(productName, -1, false);
                try
                {
                    if (roomForOne && !FillLeavingOne(chest, product, productName, out outcome.Problem))
                        return outcome;

                    var before = chest.CountItems(productName, -1, false);
                    InventoryGui.instance.m_craftBonusChance = bonusChance;
                    _bonusTouched = true;
                    try
                    {
                        outcome.Stored = CookingStationProcess.Store(station, _oven.NView, slot, conversion);
                    }
                    finally
                    {
                        InventoryGui.instance.m_craftBonusChance = _bonusChance;
                    }

                    outcome.StoreRaises = TakeRaises();
                    outcome.Delivered = chest.CountItems(productName, -1, false) - before;
                    outcome.SlotCleared = string.IsNullOrEmpty(zdo.GetString("slot" + slot));
                    return outcome;
                }
                finally
                {
                    // Whatever happened above, the chest keeps only the cooked item that left the
                    // slot; the filler and any bonus go, so the ledger balances. A shortfall is
                    // left as it is for the ledger to report.
                    var kept = string.IsNullOrEmpty(zdo.GetString("slot" + slot)) ? 1 : 0;
                    var surplus = chest.CountItems(productName, -1, false) - start - kept;
                    if (surplus > 0) chest.RemoveItem(productName, surplus);
                }
            }

            // Fills the chest with the product, then takes one out of a stack of at least two, so
            // the chest has room for exactly one more and no empty slot. What it adds is taken
            // out again by the caller.
            private static bool FillLeavingOne(Inventory chest, ItemDrop product, string productName,
                out string problem)
            {
                problem = null;
                var maxStack = product.m_itemData.m_shared.m_maxStackSize;
                var added = 0;
                for (var call = 0; call < 1000; call++)
                {
                    var count = chest.CountItems(productName, -1, false);
                    chest.AddItem(product.gameObject, Mathf.Max(1, maxStack));
                    var delta = chest.CountItems(productName, -1, false) - count;
                    if (delta <= 0) break;
                    added += delta;
                }

                var stack = chest.GetAllItems()
                    .FirstOrDefault(x => x.m_shared.m_name == productName && x.m_stack >= 2);
                if (stack == null || chest.GetEmptySlots() > 0)
                {
                    problem =
                        $"the test chest could not be filled to room for exactly one {DisplayName(productName)} (stack size {maxStack}, {chest.GetEmptySlots()} empty slot(s) after adding {added})";
                    return false;
                }

                chest.RemoveItem(stack, 1);

                var room = chest.GetAllItems().Where(x => x.m_shared.m_name == productName)
                    .Sum(x => x.m_shared.m_maxStackSize - x.m_stack);
                if (room != 1)
                    problem =
                        $"the filled test chest has room for {room} {DisplayName(productName)}, not exactly one";
                return problem == null;
            }

            private void CheckNobody(string check, string who, CycleOutcome outcome)
            {
                if (outcome.Problem != null)
                {
                    _checks[check] = CheckResult.NotRun(check, outcome.Problem);
                    return;
                }

                var faults = new List<string>();
                if (outcome.CraftRaises.Count > 0)
                    faults.Add($"Craft raised the {_skill} skill {Describe(outcome.CraftRaises)}");
                if (outcome.StoreRaises.Count > 0)
                    faults.Add($"Store raised the {_skill} skill {Describe(outcome.StoreRaises)}");
                AddStoreFaults(faults, outcome, 1);

                _checks[check] = faults.Count == 0
                    ? CheckResult.Pass(check,
                        $"with {who}, Craft took one {DisplayName(_oven.MaterialName)} and Store put one {DisplayName(_oven.ProductName)} into the chest with the bonus roll made certain, and neither raised the {_skill} skill")
                    : CheckResult.Fail(check, $"with {who}: {string.Join("; ", faults)}");
            }

            private void CheckBuilder(CookingStation station, Piece piece, long playerId)
            {
                var plain = Cycle(station, piece, playerId, 0f);
                _checks[Builder] = JudgeBuilder(Builder, plain, 1,
                    $"with you as the builder and the bonus roll made impossible, Craft raised the {_skill} skill by {CraftRaise} and Store by {StoreRaise} and put exactly one {DisplayName(_oven.ProductName)} into the chest");

                var amount = InventoryGui.instance.m_craftBonusAmount;
                if (!station.m_canGiveBonusYield || amount <= 0)
                {
                    var reason = !station.m_canGiveBonusYield
                        ? "the stone oven gives no bonus food in this game build (m_canGiveBonusYield is off)"
                        : $"the game's bonus amount is {amount}";
                    _checks[BuilderBonus] = CheckResult.Skip(BuilderBonus, reason);
                    _checks[BonusNoRoom] = CheckResult.Skip(BonusNoRoom, reason);
                    return;
                }

                var bonus = Cycle(station, piece, playerId, CertainBonusChance);
                _checks[BuilderBonus] = JudgeBuilder(BuilderBonus, bonus, 1 + amount,
                    $"with you as the builder and the bonus roll made certain, Store put {1 + amount} {DisplayName(_oven.ProductName)} (one and the bonus of {amount}) into the chest, and the {_skill} skill rose by {CraftRaise} and {StoreRaise}; the bonus was taken out again");

                var full = Cycle(station, piece, playerId, CertainBonusChance, true);
                var drops = _benches.NewDropsNear(new StationBench[] { _oven });
                var result = JudgeBuilder(BonusNoRoom, full, 1,
                    $"with room for one item in the chest and the bonus roll made certain, Store put the cooked {DisplayName(_oven.ProductName)} in without its bonus, emptied the slot and raised the {_skill} skill by {StoreRaise}; the filler was taken out again");
                _checks[BonusNoRoom] = result.Status == CheckStatus.Pass && drops.Length > 0
                    ? CheckResult.Fail(BonusNoRoom, "the cooked item went in, but" + drops)
                    : result;
            }

            private CheckResult JudgeBuilder(string check, CycleOutcome outcome, int expected, string pass)
            {
                if (outcome.Problem != null) return CheckResult.NotRun(check, outcome.Problem);

                var faults = new List<string>();
                if (outcome.CraftRaises.Count != 1 || !Mathf.Approximately(outcome.CraftRaises[0], CraftRaise))
                    faults.Add($"Craft raised the {_skill} skill {Describe(outcome.CraftRaises)}, not once by {CraftRaise}");
                if (outcome.StoreRaises.Count != 1 || !Mathf.Approximately(outcome.StoreRaises[0], StoreRaise))
                    faults.Add($"Store raised the {_skill} skill {Describe(outcome.StoreRaises)}, not once by {StoreRaise}");
                AddStoreFaults(faults, outcome, expected);

                return faults.Count == 0
                    ? CheckResult.Pass(check, pass)
                    : CheckResult.Fail(check, string.Join("; ", faults));
            }

            private void AddStoreFaults(List<string> faults, CycleOutcome outcome, int expected)
            {
                if (!outcome.Stored) faults.Add("Store returned false");
                if (!outcome.SlotCleared) faults.Add("the oven's slot still holds the cooked item");
                if (outcome.Delivered != expected)
                    faults.Add(
                        $"the chest got {outcome.Delivered} {DisplayName(_oven.ProductName)}, not {expected}");
            }

            private static string Describe(List<float> raises)
            {
                return raises.Count == 0
                    ? "never"
                    : $"{raises.Count} time(s) ({string.Join(", ", raises.Select(x => x.ToString("0.##")))})";
            }

            private void CheckRestored(Player player)
            {
                var putBack = PutBack(_runner);
                if (putBack && _stateHandBack != null)
                {
                    SmokeSession.ReleaseHandBack(_stateHandBack);
                    _stateHandBack = null;
                }

                var faults = new List<string>();
                if (!putBack) faults.Add("putting the state back did not complete");
                var skill = FindSkill(player, _skill);
                if (skill != null &&
                    (!Mathf.Approximately(skill.m_level, _hadSkill ? _level : 0f) ||
                     !Mathf.Approximately(skill.m_accumulator, _hadSkill ? _accumulator : 0f)))
                    faults.Add(
                        $"the {_skill} skill is at level {skill.m_level:0.##} with progress {skill.m_accumulator:0.###}, not {_level:0.##} and {_accumulator:0.###}");
                if (InventoryGui.instance &&
                    !Mathf.Approximately(InventoryGui.instance.m_craftBonusChance, _bonusChance))
                    faults.Add(
                        $"the bonus chance is {InventoryGui.instance.m_craftBonusChance}, not {_bonusChance}");
                var patches = Harmony.GetPatchInfo(RaiseMethod);
                if (patches != null && patches.Prefixes.Any(x => x.owner == HarmonyId))
                    faults.Add("the Player.RaiseSkill prefix is still in place");

                _checks[Restored] = faults.Count == 0
                    ? CheckResult.Pass(Restored,
                        $"the {_skill} skill is back at level {_level:0.##} with progress {_accumulator:0.###}, the bonus chance at {_bonusChance}, and the Player.RaiseSkill prefix is removed")
                    : CheckResult.Fail(Restored, string.Join("; ", faults));
            }

            // Puts back what Watch recorded. Returns false while something could not be put back
            // yet, so the session tries again at its next trigger.
            private bool PutBack(string reason)
            {
                var done = true;
                try
                {
                    if (_harmony != null)
                    {
                        _harmony.UnpatchSelf();
                        _harmony = null;
                    }
                }
                catch (Exception e)
                {
                    done = false;
                    _warn(
                        $"[Automatics Smoke] {reason}: the Player.RaiseSkill prefix could not be removed yet ({e.GetType().Name}: {e.Message}); it is tried again at the next opportunity");
                }

                _watched = null;
                Raises.Clear();

                // An inventory screen that is gone takes the changed value with it; the next one
                // starts from the game's own.
                if (_bonusTouched && InventoryGui.instance)
                    InventoryGui.instance.m_craftBonusChance = _bonusChance;
                _bonusTouched = false;

                if (_skillTouched)
                {
                    var player = Player.m_localPlayer;
                    if (player)
                    {
                        var skill = FindSkill(player, _skill);
                        if (skill != null)
                        {
                            skill.m_level = _hadSkill ? _level : 0f;
                            skill.m_accumulator = _hadSkill ? _accumulator : 0f;
                        }

                        _skillTouched = false;
                    }
                    else
                    {
                        _warn(
                            $"[Automatics Smoke] {reason}: your character is gone, so the {_skill} skill could not be put back to level {_level:0.##} with progress {_accumulator:0.###}; check it in the skills panel");
                        _skillTouched = false;
                    }
                }

                return done;
            }

            private static Skills.Skill FindSkill(Player player, Skills.SkillType type)
            {
                return player.GetSkills()?.GetSkillList()
                    .FirstOrDefault(x => x.m_info != null && x.m_info.m_skill == type);
            }
        }
    }
}
