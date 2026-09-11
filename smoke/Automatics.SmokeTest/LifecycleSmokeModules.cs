using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Object = UnityEngine.Object;
using DoorComponent = Automatics.AutomaticDoor.AutomaticDoor;
using DoorModule = Automatics.AutomaticDoor.Module;
using DoorPatches = Automatics.AutomaticDoor.Patches;
using FarmingClaims = Automatics.AutomaticFarming.AutomaticFarming;
using FarmingConfig = Automatics.AutomaticFarming.Config;
using FarmingModule = Automatics.AutomaticFarming.Module;
using FarmingPatches = Automatics.AutomaticFarming.Patches;
using FarmingPlanting = Automatics.AutomaticFarming.CropPlanting;
using FarmingRole = Automatics.AutomaticFarming.ContainerRole;
using FarmingRoles = Automatics.AutomaticFarming.ContainerRoles;
using FeedingComponent = Automatics.AutomaticFeeding.AutomaticFeeding;
using FeedingModule = Automatics.AutomaticFeeding.Module;
using FeedingPatches = Automatics.AutomaticFeeding.Patches;
using MiningComponent = Automatics.AutomaticMining.AutomaticMining;
using MiningConfig = Automatics.AutomaticMining.Config;
using MiningModule = Automatics.AutomaticMining.Module;
using MiningPatches = Automatics.AutomaticMining.Patches;
using PickupConfig = Automatics.AutomaticPickup.Config;
using PickupModule = Automatics.AutomaticPickup.Module;
using RepairModule = Automatics.AutomaticRepair.Module;
using RepairPatches = Automatics.AutomaticRepair.Patches;
using RepairItems = Automatics.AutomaticRepair.ItemRepair;
using RepairPieces = Automatics.AutomaticRepair.PieceRepair;
using StorageModule = Automatics.AutomaticStorage.Module;

namespace Automatics.SmokeTest
{
    // automatics_smoke_lifecycle <module>: the runtime lifecycle check for a migrated module, driven
    // by a descriptor instead of code of its own. It takes the module through three off -> on cycles
    // by writing its `module` setting, the way a user does, and checks after each transition:
    //   - at the disable request, in the same frame, the module is still applied but no longer
    //     running, which is the predicate its entry points open with;
    //   - while off, no patch is left under its Harmony id and every probe - a handler count, a
    //     component count or a counter - reads what the descriptor says it should while off;
    //   - while on, the module is applied and running, the patches are exactly the declared set,
    //     and every probe is back at the value it had before the run (or, for a component probe
    //     with a target, at the number of objects it should be on right now).
    // A module adds what only it can show as extra checks, at the request, while off or after the
    // enable. Each extra reports PASS, FAIL or SKIP on its own.
    //
    // It spawns nothing and writes only the setting, through SmokeSession, under the lifecycle
    // command's rules. It only ever turns a module off and back on: when the module is off in the
    // user's settings, every check SKIPs and nothing is written.
    internal static partial class LifecycleSmokeCommand
    {
        private const int ModuleCycles = 3;

        // The migrated modules this runner takes, by the argument that names them. A descriptor is
        // built only when its argument is run, so a module whose types cannot be read stops its own
        // run and nothing else.
        private static readonly Dictionary<string, Func<ModuleDescriptor>> Descriptors =
            new Dictionary<string, Func<ModuleDescriptor>>(StringComparer.Ordinal)
            {
                ["door"] = DescribeDoor,
                ["farming"] = DescribeFarming,
                ["feeding"] = DescribeFeeding,
                ["mining"] = DescribeMining,
                ["repair"] = DescribeRepair,
                ["storage"] = DescribeStorage
            };

        // Automatic Door owns one patch, four hook subscriptions and one component on every Door
        // with a ZDO. The state fingerprint is read only: pausing the module must not close a door
        // or otherwise rewrite the world's record of it.
        private static ModuleDescriptor DescribeDoor()
        {
            Dictionary<ZDOID, int> beforeDisable = null;
            return new ModuleDescriptor
            {
                Argument = "door",
                Module = DoorModule.Instance,
                Active = () => DoorModule.Active,
                Running = () => DoorModule.Running,
                PatchTypes = new[] { typeof(DoorPatches) },
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 1),
                    Probe.Handlers("Hooks.OnPlayerUpdate", () => Hooks.OnPlayerUpdate, 1),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 1),
                    Probe.Handlers("Hooks.OnDedicatedServerFixedUpdate",
                        () => Hooks.OnDedicatedServerFixedUpdate, 1),
                    // The four probes above only show the hook's total handler count, which
                    // another subscriber's own add or remove can move by exactly as much as
                    // Door's, and comparing only against the value before the run would let a
                    // Door handler stuck at 0 or duplicated at 2 pass as long as it read the same
                    // both times (0 -> 0 -> 0, 2 -> 0 -> 2). These name Door's own handler,
                    // count only its occurrences, and pin the re-enabled reading to exactly 1
                    // instead, so either failure shows.
                    Probe.Counter("door.OnPlayerAwake.handler",
                        () => CountHandlersOf(Hooks.OnPlayerAwake, typeof(DoorModule), "OnPlayerAwake"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("door.OnPlayerUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerUpdate, typeof(DoorModule), "OnPlayerUpdate"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("door.OnPlayerFixedUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerFixedUpdate, typeof(DoorModule),
                            "OnPlayerFixedUpdate"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("door.OnDedicatedServerFixedUpdate.handler",
                        () => CountHandlersOf(Hooks.OnDedicatedServerFixedUpdate, typeof(DoorModule),
                            "OnDedicatedServerFixedUpdate"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Components<DoorComponent>(CountEligibleDoors),
                    Probe.Counter("RegisteredDoorCount", () => DoorComponent.RegisteredDoorCount,
                        OffExpectation.Zero, 0, CountActiveDoorComponents)
                },
                Precondition = null,
                Extras = new[]
                {
                    // Captured just before the disable is written (Phase.BeforeRequest), not at
                    // the request: the request itself can move a door (a config SettingChanged or
                    // SetStopRequested reacting to it), and a snapshot taken after that write
                    // would already miss it.
                    new ExtraCheck("state_snapshot_captured", Phase.BeforeRequest,
                        (record, cycle) =>
                        {
                            beforeDisable = DoorStates();
                            var name = record.Check("state_snapshot_captured");
                            record.Cycle(name, cycle, beforeDisable.Count > 0,
                                $"recorded {beforeDisable.Count} door state(s) without changing them",
                                "no loaded Door with a readable ZDO was available as a positive control");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("door_id_set_unchanged", Phase.WhileOff,
                        (record, cycle) =>
                        {
                            var after = DoorStates();
                            var same = beforeDisable != null &&
                                       new HashSet<ZDOID>(beforeDisable.Keys).SetEquals(after.Keys);
                            var name = record.Check("door_id_set_unchanged");
                            record.Cycle(name, cycle, same,
                                $"the same {after.Count} readable door ZDO id(s) remained while off",
                                $"the readable door ZDO ids changed across the disable ({beforeDisable?.Count ?? 0} before, {after.Count} after)");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("door_state_unchanged", Phase.WhileOff,
                        (record, cycle) =>
                        {
                            var after = DoorStates();
                            var same = beforeDisable != null && beforeDisable.Count == after.Count &&
                                       beforeDisable.All(x => after.TryGetValue(x.Key, out var state) &&
                                                              state == x.Value);
                            var name = record.Check("door_state_unchanged");
                            record.Cycle(name, cycle, same,
                                $"all {after.Count} readable door state value(s) were unchanged",
                                "one or more readable Door ZDO state values changed across the disable");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("components_current_generation", Phase.AfterOn,
                        (record, cycle) =>
                        {
                            var doors = EligibleDoors();
                            var generation = DoorModule.Generation;
                            var attached = doors.Count(x =>
                            {
                                var component = x.GetComponent<DoorComponent>();
                                return component && component.ScopeGeneration == generation;
                            });
                            var name = record.Check("components_current_generation");
                            record.Cycle(name, cycle, doors.Length > 0 && attached == doors.Length,
                                $"all {attached} eligible door(s) carry the current generation {generation}",
                                $"{attached} of {doors.Length} eligible door(s) carry the current generation {generation}");
                            return Enumerable.Empty<object>().GetEnumerator();
                        })
                }
            };
        }

        private static Door[] EligibleDoors()
        {
            return Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => Objects.GetZNetView(x, out var zNetView) && zNetView.GetZDO() != null)
                .ToArray();
        }

        private static int CountEligibleDoors()
        {
            return EligibleDoors().Length;
        }

        private static int CountActiveDoorComponents()
        {
            return Object.FindObjectsByType<DoorComponent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(x => x && x.gameObject.activeInHierarchy);
        }

        private static Dictionary<ZDOID, int> DoorStates()
        {
            var states = new Dictionary<ZDOID, int>();
            foreach (var door in EligibleDoors())
            {
                Objects.GetZNetView(door, out var zNetView);
                var zdo = zNetView.GetZDO();
                if (zdo.m_uid != ZDOID.None) states[zdo.m_uid] = zdo.GetInt("state");
            }

            return states;
        }

        // How many frames the check waits, after an enable, for the farming interval hook to take up
        // its timer again.
        private const int FarmingTimerFrames = 30;

        // Automatic Farming owns one patch and three hook subscriptions and attaches no component.
        // Automatic Pickup asks its crop claim before it collects a crop, so the claim has to let go
        // at the disable request and come back after the enable. The check harvests, plants and
        // designates nothing and changes no Farming or Pickup setting; its precondition keeps it
        // from running where Automatic Pickup could collect, while farming is off, a crop farming
        // would have replanted.
        private static ModuleDescriptor DescribeFarming()
        {
            var claimedBeforeDisable = -1;
            var timersBeforeDisable = -1;
            var saplingMapBeforeDisable = false;
            return new ModuleDescriptor
            {
                Argument = "farming",
                Module = FarmingModule.Instance,
                Active = () => FarmingModule.Active,
                Running = () => FarmingModule.Running,
                PatchTypes = new[] { typeof(FarmingPatches) },
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 1),
                    Probe.Handlers("Hooks.OnPlayerUpdate", () => Hooks.OnPlayerUpdate, 1),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 1),
                    Probe.Counter("farming.OnPlayerAwake.handler",
                        () => CountHandlersOf(Hooks.OnPlayerAwake, typeof(FarmingModule), "OnPlayerAwake"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("farming.OnPlayerUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerUpdate, typeof(FarmingModule), "OnPlayerUpdate"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("farming.OnPlayerFixedUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerFixedUpdate, typeof(FarmingModule),
                            "OnPlayerFixedUpdate"),
                        OffExpectation.Zero, 0, () => 1),
                    // Container roles are the user's and live on the containers: a disable neither
                    // clears nor rewrites them, so the count stays where it was throughout.
                    Probe.Counter("farming.designated_containers", CountDesignatedContainers,
                        OffExpectation.BelowBaseline, 0)
                },
                Precondition = FarmingPrecondition,
                Extras = new[]
                {
                    // Just before the disable is written. The sapling lookup is built here when no
                    // farming pass has built it yet - a read of the loaded prefabs that changes
                    // nothing in the world - so that its release can be seen in every cycle.
                    new ExtraCheck("session_state_captured", Phase.BeforeRequest,
                        (record, cycle) =>
                        {
                            FarmingPlanting.TryResolveSapling(string.Empty, out _);
                            claimedBeforeDisable = CountClaimedCrops();
                            timersBeforeDisable = FarmingModule.FarmingTimerCount;
                            saplingMapBeforeDisable = FarmingPlanting.SaplingMapBuilt;
                            var name = record.Check("session_state_captured");
                            record.Cycle(name, cycle, saplingMapBeforeDisable,
                                $"recorded {claimedBeforeDisable} claimed crop(s), {timersBeforeDisable} farming timer(s) and a built sapling lookup before disabling",
                                "the sapling lookup could not be built before disabling");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    // In the frame of the request, not at the teardown frames later: from then on
                    // farming harvests nothing, and a crop it still claimed would be left to nobody.
                    new ExtraCheck("claims_released_at_request", Phase.AtRequest,
                        (record, cycle) =>
                        {
                            RecordClaimsReleased(record, cycle, "claims_released_at_request",
                                claimedBeforeDisable, "right after the disable request");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("claims_released_while_off", Phase.WhileOff,
                        (record, cycle) =>
                        {
                            RecordClaimsReleased(record, cycle, "claims_released_while_off",
                                claimedBeforeDisable, "while the module is off");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("timers_cleared", Phase.WhileOff,
                        (record, cycle) =>
                        {
                            var count = FarmingModule.FarmingTimerCount;
                            var name = record.Check("timers_cleared");
                            if (timersBeforeDisable <= 0)
                                record.CycleSkip(name, cycle,
                                    "the farming timer table was empty before disabling, so its cleanup could not be observed");
                            else
                                record.Cycle(name, cycle, count == 0,
                                    $"the {timersBeforeDisable} farming timer(s) present before disabling were cleared",
                                    $"the farming timer table had {timersBeforeDisable} entry or entries before disabling and holds {count} while off");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("sapling_map_cleared", Phase.WhileOff,
                        (record, cycle) =>
                        {
                            var name = record.Check("sapling_map_cleared");
                            if (!saplingMapBeforeDisable)
                                record.CycleSkip(name, cycle,
                                    "the sapling lookup was not built before disabling, so its release could not be observed");
                            else
                                record.Cycle(name, cycle, !FarmingPlanting.SaplingMapBuilt,
                                    "the sapling lookup built before disabling was dropped",
                                    "the sapling lookup is still built while the module is off (R6.1)");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    // The positive control for the two release checks: the claim comes back once
                    // the module is on again. It needs a claimed crop loaded out of Pickup's reach.
                    new ExtraCheck("claims_restored", Phase.AfterOn,
                        (record, cycle) =>
                        {
                            var name = record.Check("claims_restored");
                            if (claimedBeforeDisable <= 0)
                            {
                                record.CycleSkip(name, cycle,
                                    "no crop Automatic Farming claims was loaded before disabling, so the positive control could not be observed");
                            }
                            else
                            {
                                var now = CountClaimedCrops();
                                record.Cycle(name, cycle, now > 0,
                                    $"{now} crop(s) are claimed again once the module is back on ({claimedBeforeDisable} before disabling)",
                                    $"no crop is claimed once the module is back on, where {claimedBeforeDisable} were before disabling (R7.5)");
                            }

                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("interval_timer_resumed", Phase.AfterOn, IntervalTimerResumed)
                }
            };
        }

        private static void RecordClaimsReleased(CycleRecorder record, int cycle, string suffix, int before,
            string when)
        {
            var name = record.Check(suffix);
            if (before <= 0)
            {
                record.CycleSkip(name, cycle,
                    "no crop Automatic Farming claims was loaded before disabling, so the release could not be observed");
                return;
            }

            var now = CountClaimedCrops();
            record.Cycle(name, cycle, now == 0,
                $"{when} none of the {before} crop(s) claimed before disabling is claimed",
                $"{when} {now} crop(s) are still claimed, so Automatic Pickup leaves them to a module that no longer harvests (R1.5a / R7.5)");
        }

        // The interval hook takes up its timer again on its first physics step after the enable.
        // Waiting for that here also leaves the next cycle's disable a timer to clear: the enable
        // is seen in the frame it lands in, before any physics step has run the hook.
        private static IEnumerator IntervalTimerResumed(CycleRecorder record, int cycle)
        {
            var name = record.Check("interval_timer_resumed");
            if (!FarmsOnAnInterval())
            {
                record.CycleSkip(name, cycle,
                    "farming runs on a key or is turned off in the settings, so it keeps no interval timer");
                yield break;
            }

            for (var frame = 0; frame < FarmingTimerFrames && FarmingModule.FarmingTimerCount == 0; frame++)
                yield return null;

            var count = FarmingModule.FarmingTimerCount;
            record.Cycle(name, cycle, count > 0,
                $"the interval hook took up its timer again ({count} held)",
                $"the interval hook took up no timer within {FarmingTimerFrames} frames of the enable");
        }

        private static string FarmingPrecondition()
        {
            var player = Player.m_localPlayer;
            if (!player) return "there is no local player";

            if (!PickupModule.Running || !HasCropOrPlantInPickupReach(player)) return null;

            return "Automatic Pickup is running and a crop Automatic Farming harvests, or a growing plant, is within its range + 1 m; stand farther from your fields and run the check again";
        }

        // Every crop farming would claim, whether or not farming runs right now - the check asks
        // again before each enable, while it is off - and every growing plant, which can ripen into
        // such a crop while the check runs.
        private static bool HasCropOrPlantInPickupReach(Player player)
        {
            var reach = PickupConfig.AutomaticPickupRange + 1f;
            var origin = player.transform.position;
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                if (!pickable || !IsFarmingCrop(pickable)) continue;
                if (Vector3.Distance(origin, pickable.transform.position) <= reach) return true;
            }

            foreach (var plant in Object.FindObjectsByType<Plant>(FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                if (!plant) continue;
                if (Vector3.Distance(origin, plant.transform.position) <= reach) return true;
            }

            return false;
        }

        // The crop rule of AutomaticFarming.TryGetCropIdentifier, which the claim starts from.
        private static bool IsFarmingCrop(Pickable pickable)
        {
            return ValheimObject.Flora.GetIdentify(Objects.GetName(pickable), out var identifier) &&
                   FarmingConfig.AllowFarmingCrops.Contains(identifier);
        }

        // The guards of Module.OnPlayerFixedUpdate that decide whether it keeps a timer at all.
        private static bool FarmsOnAnInterval()
        {
            return FarmingConfig.EnableAutomaticFarming && FarmingConfig.FarmingInterval >= 0.1f &&
                   FarmingConfig.FarmingKey.MainKey == KeyCode.None;
        }

        private static int CountClaimedCrops()
        {
            var player = Player.m_localPlayer;
            var claimed = 0;
            foreach (var pickable in PickableCache.GetAllInstance())
                if (pickable && FarmingClaims.ClaimsCrop(player, pickable))
                    claimed++;

            return claimed;
        }

        private static int CountDesignatedContainers()
        {
            var designated = 0;
            foreach (var container in ContainerCache.GetAllInstance())
                if (container && FarmingRoles.GetRole(container) != FarmingRole.None)
                    designated++;

            return designated;
        }

        // Automatic Feeding owns one patch and one component on every Tameable with a ZDO, and no
        // hook subscription, so the four hook counts stay where they were throughout. The check
        // spawns no animal and no food and changes no Feeding setting: a component only acts when a
        // hungry animal already finds food, as it would with the module left on.
        private static ModuleDescriptor DescribeFeeding()
        {
            return new ModuleDescriptor
            {
                Argument = "feeding",
                Module = FeedingModule.Instance,
                Active = () => FeedingModule.Active,
                Running = () => FeedingModule.Running,
                PatchTypes = new[] { typeof(FeedingPatches) },
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 0),
                    Probe.Handlers("Hooks.OnPlayerUpdate", () => Hooks.OnPlayerUpdate, 0),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 0),
                    Probe.Handlers("Hooks.OnDedicatedServerFixedUpdate",
                        () => Hooks.OnDedicatedServerFixedUpdate, 0),
                    // Animals move and zones load while the check runs, so the component count is
                    // compared with the tameables loaded at that moment rather than with the count
                    // before the run, and a scene with none is inconclusive.
                    Probe.Components<FeedingComponent>(CountEligibleTameables)
                },
                Precondition = null,
                Extras = new[]
                {
                    new ExtraCheck("components_current_generation", Phase.AfterOn,
                        (record, cycle) =>
                        {
                            var tameables = EligibleTameables();
                            var generation = FeedingModule.Generation;
                            var attached = tameables.Count(x =>
                            {
                                var component = x.GetComponent<FeedingComponent>();
                                return component && component.ScopeGeneration == generation;
                            });
                            var name = record.Check("components_current_generation");
                            if (tameables.Length == 0)
                                record.CycleSkip(name, cycle,
                                    "no loaded Tameable with a ZDO was available, so the generation could not be observed");
                            else
                                record.Cycle(name, cycle, attached == tameables.Length,
                                    $"all {attached} eligible tameable(s) carry the current generation {generation}",
                                    $"{attached} of {tameables.Length} eligible tameable(s) carry the current generation {generation}");
                            return Enumerable.Empty<object>().GetEnumerator();
                        })
                }
            };
        }

        private static Tameable[] EligibleTameables()
        {
            return Object.FindObjectsByType<Tameable>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(x => Objects.GetZNetView(x, out var zNetView) && zNetView.GetZDO() != null)
                .ToArray();
        }

        private static int CountEligibleTameables()
        {
            return EligibleTameables().Length;
        }

        // How many frames the check waits, after an enable, for the mining interval hook to take up
        // its timer again.
        private const int MiningTimerFrames = 30;

        // Automatic Mining owns one patch, three hook subscriptions and one component on every
        // supported destructible with a ZDO. The precondition keeps normal mining from firing in
        // an on interval without changing any of the user's inner settings.
        private static ModuleDescriptor DescribeMining()
        {
            var timerCountBeforeDisable = -1;
            return new ModuleDescriptor
            {
                Argument = "mining",
                Module = MiningModule.Instance,
                Active = () => MiningModule.Active,
                Running = () => MiningModule.Running,
                PatchTypes = new[] { typeof(MiningPatches) },
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 1),
                    Probe.Handlers("Hooks.OnPlayerUpdate", () => Hooks.OnPlayerUpdate, 1),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 1),
                    Probe.Counter("mining.OnPlayerAwake.handler",
                        () => CountHandlersOf(Hooks.OnPlayerAwake, typeof(MiningModule),
                            "OnPlayerAwake"), OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("mining.OnPlayerUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerUpdate, typeof(MiningModule),
                            "OnPlayerUpdate"), OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("mining.OnPlayerFixedUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerFixedUpdate, typeof(MiningModule),
                            "OnPlayerFixedUpdate"), OffExpectation.Zero, 0, () => 1),
                    Probe.Components<MiningComponent>(CountEligibleMinerals),
                    Probe.Counter("AllInstance.Count", () => MiningComponent.AllInstanceCount,
                        OffExpectation.Zero, 0, CountActiveMiningComponents)
                },
                Precondition = MiningPrecondition,
                Extras = new[]
                {
                    new ExtraCheck("timer_state_captured", Phase.BeforeRequest,
                        (record, cycle) =>
                        {
                            timerCountBeforeDisable = MiningModule.MiningTimerCount;
                            var name = record.Check("timer_state_captured");
                            record.Cycle(name, cycle, timerCountBeforeDisable >= 0,
                                $"recorded {timerCountBeforeDisable} mining timer(s) before disabling",
                                "the mining timer table could not be read before disabling");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("timers_cleared", Phase.WhileOff,
                        (record, cycle) =>
                        {
                            var count = MiningModule.MiningTimerCount;
                            var name = record.Check("timers_cleared");
                            if (timerCountBeforeDisable == 0)
                                record.CycleSkip(name, cycle,
                                    "the mining timer table was already empty before disabling, so cleanup could not be observed");
                            else
                                record.Cycle(name, cycle, timerCountBeforeDisable > 0 && count == 0,
                                    $"the {timerCountBeforeDisable} mining timer(s) present before disabling were cleared",
                                    $"the mining timer table had {timerCountBeforeDisable} entry or entries before disabling and holds {count} while off");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("components_current_generation", Phase.AfterOn,
                        (record, cycle) =>
                        {
                            var minerals = EligibleMinerals();
                            var generation = MiningModule.Generation;
                            var attached = minerals.Count(x =>
                            {
                                var component = x.GetComponent<MiningComponent>();
                                return component && component.ScopeGeneration == generation;
                            });
                            var name = record.Check("components_current_generation");
                            record.Cycle(name, cycle, minerals.Length > 0 && attached == minerals.Length,
                                $"all {attached} eligible mineral object(s) carry the current generation {generation}",
                                $"{attached} of {minerals.Length} eligible mineral object(s) carry the current generation {generation}");
                            return Enumerable.Empty<object>().GetEnumerator();
                        }),
                    new ExtraCheck("interval_timer_resumed", Phase.AfterOn, MiningIntervalTimerResumed)
                }
            };
        }

        // The interval hook takes up its timer again on its first physics step after the enable.
        // Waiting for that here also leaves the next cycle's disable a timer to clear: the enable
        // is seen in the frame it lands in, before any physics step has run the hook.
        private static IEnumerator MiningIntervalTimerResumed(CycleRecorder record, int cycle)
        {
            var name = record.Check("interval_timer_resumed");
            var player = Player.m_localPlayer;
            if (!player)
            {
                record.CycleSkip(name, cycle, "there is no local player");
                yield break;
            }

            if (!MinesOnAnInterval())
            {
                record.CycleSkip(name, cycle,
                    "mining runs on a key or is turned off in the settings, so it keeps no interval timer");
                yield break;
            }

            for (var frame = 0; frame < MiningTimerFrames && MiningModule.MiningTimerCount == 0; frame++)
                yield return null;

            var count = MiningModule.MiningTimerCount;
            record.Cycle(name, cycle, count > 0,
                $"the interval hook took up its timer again ({count} held)",
                $"the interval hook took up no timer within {MiningTimerFrames} frames of the enable");
        }

        private static GameObject[] EligibleMinerals()
        {
            var eligible = new HashSet<GameObject>();
            AddNetworked(eligible, Object.FindObjectsByType<Destructible>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
            AddNetworked(eligible, Object.FindObjectsByType<MineRock>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
            AddNetworked(eligible, Object.FindObjectsByType<MineRock5>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
            return eligible.ToArray();
        }

        private static void AddNetworked<T>(ISet<GameObject> eligible, IEnumerable<T> targets)
            where T : Component
        {
            foreach (var target in targets)
                if (Objects.GetZNetView(target, out var zNetView) && zNetView.GetZDO() != null)
                    eligible.Add(target.gameObject);
        }

        private static int CountEligibleMinerals()
        {
            return EligibleMinerals().Length;
        }

        private static int CountActiveMiningComponents()
        {
            return Object.FindObjectsByType<MiningComponent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(x => x && x.gameObject.activeInHierarchy);
        }

        private static string MiningPrecondition()
        {
            var player = Player.m_localPlayer;
            if (!player) return "there is no local player";

            if (!HasUsablePickaxe(player) || !HasNearbyAllowedMineral(player)) return null;

            return "unequip or remove the pickaxe, or move away from rocks and run the check again";
        }

        private static bool HasUsablePickaxe(Player player)
        {
            var items = MiningConfig.NeedToEquipPickaxe
                ? player.GetInventory().GetEquippedItems()
                : player.GetInventory().GetAllItems();
            return items.Any(x => x != null && x.m_durability > 0f &&
                                  x.m_shared.m_skillType == Skills.SkillType.Pickaxes);
        }

        private static bool HasNearbyAllowedMineral(Player player)
        {
            var range = (MiningConfig.MiningRange > 0 ? MiningConfig.MiningRange : 32) + 24f + 2f;
            var origin = player.transform.position;
            foreach (var mineral in EligibleMinerals())
            {
                if (!mineral || !mineral.activeInHierarchy) continue;
                var destructible = mineral.GetComponent<IDestructible>() as Component;
                if (!destructible) continue;
                if (!ValheimObject.Mineral.GetIdentify(Objects.GetName(destructible), out var identifier))
                    continue;
                if (!MiningConfig.AllowMiningMinerals.Contains(identifier)) continue;
                if (Vector3.Distance(origin, mineral.transform.position) <= range) return true;
            }

            return false;
        }

        // The guards of Module.OnPlayerFixedUpdate that decide whether it keeps a timer at all.
        private static bool MinesOnAnInterval()
        {
            return MiningConfig.EnableAutomaticMining && MiningConfig.MiningInterval >= 0.1f &&
                   MiningConfig.MiningKey.MainKey == KeyCode.None;
        }

        // Automatic Storage moves items only when its key is pressed, and the check never presses
        // it: the module's one subscription is what comes and goes. The other two player hooks are
        // counted to show that it touches nothing else.
        private static ModuleDescriptor DescribeStorage()
        {
            return new ModuleDescriptor
            {
                Argument = "storage",
                Module = StorageModule.Instance,
                Active = () => StorageModule.Active,
                Running = () => StorageModule.Running,
                PatchTypes = Type.EmptyTypes,
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerUpdate", () => Hooks.OnPlayerUpdate, 1),
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 0),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 0)
                },
                Precondition = null,
                Extras = new ExtraCheck[0]
            };
        }

        // Repair's normal enabled behavior can continue during this run. The check itself only
        // calls its repair entries while off, with null arguments, and never damages anything.
        private static ModuleDescriptor DescribeRepair()
        {
            var extras = new List<ExtraCheck>
            {
                new ExtraCheck("stopped_entries", Phase.WhileOff, (record, cycle) =>
                {
                    var name = record.Check("stopped_entries");
                    if (RepairModule.Running)
                    {
                        record.Cycle(name, cycle, false, "", "Repair is still running");
                        return Enumerable.Empty<object>().GetEnumerator();
                    }
                    RepairItems.Repair(null);
                    RepairPieces.Repair(null);
                    RepairItems.CraftingStationInteractHook(null, null);
                    record.Cycle(name, cycle, true, "all three stopped entries returned with null arguments", "");
                    return Enumerable.Empty<object>().GetEnumerator();
                })
            };
            AddRepairCleanupCheck(extras, typeof(RepairModule), "ItemRepairTimers");
            AddRepairCleanupCheck(extras, typeof(RepairModule), "PieceRepairTimers");
            AddRepairCleanupCheck(extras, typeof(RepairItems), "WornItemsBuffer");
            AddRepairCleanupCheck(extras, typeof(RepairPieces), "PieceBuffer");
            AddRepairCleanupCheck(extras, typeof(RepairPieces), "PieceSet");
            AddRepairCleanupCheck(extras, typeof(RepairPieces), "ColliderBuffer");
            AddRepairCleanupCheck(extras, typeof(RepairPieces), "_skipRadiusMethodLookup");

            return new ModuleDescriptor
            {
                Argument = "repair",
                Module = RepairModule.Instance,
                Active = () => RepairModule.Active,
                Running = () => RepairModule.Running,
                PatchTypes = new[] { typeof(RepairPatches) },
                Probes = new[]
                {
                    Probe.Handlers("Hooks.OnPlayerAwake", () => Hooks.OnPlayerAwake, 1),
                    Probe.Handlers("Hooks.OnPlayerFixedUpdate", () => Hooks.OnPlayerFixedUpdate, 1),
                    Probe.Counter("repair.OnPlayerAwake.handler",
                        () => CountHandlersOf(Hooks.OnPlayerAwake, typeof(RepairModule), "OnPlayerAwake"),
                        OffExpectation.Zero, 0, () => 1),
                    Probe.Counter("repair.OnPlayerFixedUpdate.handler",
                        () => CountHandlersOf(Hooks.OnPlayerFixedUpdate, typeof(RepairModule), "OnPlayerFixedUpdate"),
                        OffExpectation.Zero, 0, () => 1)
                },
                Precondition = null,
                Extras = extras.ToArray()
            };
        }

        private static void AddRepairCleanupCheck(List<ExtraCheck> extras, Type owner, string field)
        {
            var before = -1;
            extras.Add(new ExtraCheck(field + "_observed", Phase.BeforeRequest, (record, cycle) =>
            {
                before = ReadRepairState(owner, field);
                record.Cycle(record.Check(field + "_observed"), cycle, before >= 0,
                    $"{field} held {before} before disabling", $"could not read {field}");
                return Enumerable.Empty<object>().GetEnumerator();
            }));
            extras.Add(new ExtraCheck(field + "_cleared", Phase.WhileOff, (record, cycle) =>
            {
                var after = ReadRepairState(owner, field);
                var name = record.Check(field + "_cleared");
                if (before < 0 || after != 0)
                    record.Cycle(name, cycle, false, "", $"{field}: before={before}, while off={after}; expected readable and empty");
                else if (before == 0)
                    record.CycleSkip(name, cycle, $"{field} was already empty before disabling; cleanup was not observed");
                else
                    record.Cycle(name, cycle, true, $"{field} cleared {before} retained value(s)", "");
                return Enumerable.Empty<object>().GetEnumerator();
            }));
        }

        private static int ReadRepairState(Type owner, string fieldName)
        {
            var field = AccessTools.Field(owner, fieldName);
            if (field == null) return -1;
            var value = field.GetValue(null);
            if (value is bool flag) return flag ? 1 : 0;
            if (value is Array array) return array.Cast<object>().Count(x => x != null);
            if (value == null) return -1;
            var count = AccessTools.Property(value.GetType(), "Count");
            return count?.GetValue(value, null) is int number ? number : -1;
        }

        // Every argument the command takes besides none at all, in the order the usage lists them.
        private static IEnumerable<string> ModuleArguments()
        {
            return new[] { MappingArgument }.Concat(Descriptors.Keys.OrderBy(x => x, StringComparer.Ordinal));
        }

        // Builds the descriptor before the run takes the session, so a descriptor that cannot be
        // built never holds the lock.
        private static bool TryDescribe(string argument, out ModuleDescriptor descriptor, out string problem)
        {
            descriptor = null;
            problem = "";
            if (!Descriptors.TryGetValue(argument, out var describe))
            {
                problem = $"'{argument}' names no module";
                return false;
            }

            try
            {
                descriptor = describe();
            }
            catch (Exception e)
            {
                problem = $"the {argument} module could not be described: {e.GetType().Name}: {e.Message}";
                return false;
            }

            if (descriptor == null || descriptor.Module == null || descriptor.Active == null ||
                descriptor.Running == null || descriptor.Probes == null)
            {
                problem = $"the {argument} descriptor leaves out the module, its flags or its probes";
                descriptor = null;
                return false;
            }

            return true;
        }

        private static IEnumerator RunModule(Terminal context, int runId, string runner,
            ModuleDescriptor descriptor)
        {
            var run = new ModuleRun(runId, runner, descriptor);

            Emit(context, LogLevel.Info,
                $"[Automatics Smoke] {runner} started: {descriptor.Module.ConfigSection}/module is turned off and on {ModuleCycles} times through its setting, nothing is spawned, and the setting is restored");

            var steps = Contain(run.Execute(), (_, message) => run.Stopped(message), "");
            while (steps.MoveNext()) yield return steps.Current;

            var finish = Contain(run.Finish(), (_, message) => run.Stopped(message), "");
            while (finish.MoveNext()) yield return finish.Current;

            Report(context, run.Results);
        }

        private static int CountHandlers(Delegate handlers)
        {
            return handlers == null ? 0 : handlers.GetInvocationList().Length;
        }

        // The occurrence count of one named handler within a delegate's invocation list, so a
        // module's own subscription can be told apart from another subscriber's on the same
        // hook: the multicast list's total can land back where it started for the wrong reason.
        private static int CountHandlersOf(Delegate handlers, Type declaringType, string methodName)
        {
            return handlers == null
                ? 0
                : handlers.GetInvocationList()
                    .Count(x => x.Method.DeclaringType == declaringType && x.Method.Name == methodName);
        }

        // An event is read through the field behind it; -1 when there is no such field.
        private static int CountEventHandlers(Type owner, string eventName)
        {
            var field = AccessTools.Field(owner, eventName);
            return field == null ? -1 : CountHandlers(field.GetValue(null) as Delegate);
        }

        private static int CountObjects<T>() where T : Object
        {
            return Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        }

        // -- The descriptor ------------------------------------------------------------------

        private sealed class ModuleDescriptor
        {
            // The module's name in lower case, as the command takes it; the checks are named after it.
            public string Argument;

            // The registered module. Its name is the registry's, and its toggle is read when the
            // run starts.
            public IAutomaticsModule Module;

            // The module's static applied state and its Running predicate.
            public Func<bool> Active;
            public Func<bool> Running;

            // The patch classes the module applies; empty when it applies none.
            public Type[] PatchTypes;

            public Probe[] Probes;

            // Null when the check can run, or why it cannot. Asked before anything is written.
            public Func<string> Precondition;

            public ExtraCheck[] Extras;
        }

        // What a probe should read while the module is off: nothing at all, its value before the
        // run less its Delta, or anything.
        private enum OffExpectation
        {
            Zero,
            BelowBaseline,
            Unchecked
        }

        private sealed class Probe
        {
            public string Name;

            // -1 when the value cannot be read, which fails the check that reads it.
            public Func<int> Read;

            public OffExpectation Off;

            // How far below its value before the run a BelowBaseline probe reads while off; 0 for
            // one the module must not touch.
            public int Delta;

            // A probe that reads 0 before the run shows nothing either way, so its checks are
            // inconclusive rather than passed.
            public bool NeedsNonZeroBaseline;

            // When set, what the probe has to read once the module is back on, in place of its
            // value before the run: the number of objects it should be on right now. Objects come
            // and go while the check runs, so a count taken before the run can be out of date. A
            // target of 0 shows nothing, and makes the check inconclusive.
            public Func<int> Target;

            // The handler count of a delegate property, such as Hooks.OnPlayerUpdate.
            public static Probe Handlers(string name, Func<Delegate> read, int delta)
            {
                return new Probe
                {
                    Name = name, Read = () => CountHandlers(read()), Off = OffExpectation.BelowBaseline,
                    Delta = delta
                };
            }

            // The handler count of an event, read through the field behind it.
            public static Probe Event(Type owner, string eventName, int delta)
            {
                return new Probe
                {
                    Name = $"{owner.Name}.{eventName}", Read = () => CountEventHandlers(owner, eventName),
                    Off = OffExpectation.BelowBaseline, Delta = delta
                };
            }

            // The number of components of one type in the scene, which is 0 while the module is off.
            public static Probe Components<T>() where T : Object
            {
                return new Probe
                {
                    Name = typeof(T).Name, Read = CountObjects<T>, Off = OffExpectation.Zero,
                    NeedsNonZeroBaseline = true
                };
            }

            // The same, compared once the module is back on with the objects it should be on then.
            public static Probe Components<T>(Func<int> eligible) where T : Object
            {
                return new Probe
                {
                    Name = typeof(T).Name, Read = CountObjects<T>, Off = OffExpectation.Zero,
                    Target = eligible
                };
            }

            // Any other number, such as a private static read by reflection that returns -1 when the
            // field is not there.
            public static Probe Counter(string name, Func<int> read, OffExpectation off, int delta,
                Func<int> target = null)
            {
                return new Probe
                {
                    Name = name, Read = read, Off = off, Delta = delta, Target = target
                };
            }
        }

        private enum Phase
        {
            // Just before the disable is written, in the same synchronous step, whether or not
            // the write itself goes on to happen. A check here may not wait for a frame either:
            // it exists to see the state the disable request is about to act on, and a write
            // itself can already move that state.
            BeforeRequest,

            // Straight after the disable is written, before any frame passes. A check here may not
            // wait for a frame.
            AtRequest,

            // Once the registry reports Disabled and the probes were compared.
            WhileOff,

            // Once the module is back on and the probes were compared.
            AfterOn
        }

        // A check only one module can make. It records its own result for the cycle it is given -
        // PASS, FAIL or SKIP - under recorder.Check(Name).
        private sealed class ExtraCheck
        {
            public ExtraCheck(string name, Phase when, Func<CycleRecorder, int, IEnumerator> run)
            {
                Name = name;
                When = when;
                Run = run;
            }

            public string Name { get; }
            public Phase When { get; }
            public Func<CycleRecorder, int, IEnumerator> Run { get; }
        }

        // -- Recording -----------------------------------------------------------------------

        // The results of a cycled run. A check made once is settled once; a check made in every
        // cycle passes only when every cycle passed, and the first cycle that does not pass decides
        // it. A check the run never reached SKIPs.
        private sealed class CycleRecorder
        {
            private readonly string _prefix;
            private readonly int _cycles;
            private readonly List<string> _order;
            private readonly HashSet<string> _perCycle;

            private readonly Dictionary<string, CheckResult> _settled =
                new Dictionary<string, CheckResult>(StringComparer.Ordinal);

            private readonly Dictionary<string, int> _passes = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _passNotes = new Dictionary<string, string>(StringComparer.Ordinal);
            private string _notReached = "not reached: an earlier step stopped the run";

            public CycleRecorder(string prefix, int cycles, IEnumerable<string> order, IEnumerable<string> perCycle)
            {
                _prefix = prefix;
                _cycles = cycles;
                _order = order.ToList();
                _perCycle = new HashSet<string>(perCycle, StringComparer.Ordinal);
            }

            public IReadOnlyList<CheckResult> Results
            {
                get
                {
                    var results = new List<CheckResult>();
                    foreach (var name in _order)
                        if (_settled.TryGetValue(name, out var result))
                        {
                            results.Add(result);
                        }
                        else if (_perCycle.Contains(name) && Passes(name) == _cycles)
                        {
                            _passNotes.TryGetValue(name, out var note);
                            results.Add(new CheckResult(CheckStatus.Pass, name,
                                string.IsNullOrEmpty(note)
                                    ? $"all {_cycles} cycles"
                                    : $"all {_cycles} cycles; {note}"));
                        }
                        else
                        {
                            results.Add(new CheckResult(CheckStatus.Skip, name, _notReached));
                        }

                    return results;
                }
            }

            // The full name of a check: the module's argument, an underscore and the suffix.
            public string Check(string suffix)
            {
                return _prefix + "_" + suffix;
            }

            public void Settle(CheckStatus status, string name, string message)
            {
                if (!_settled.ContainsKey(name)) _settled[name] = new CheckResult(status, name, message);
            }

            public void Cycle(string name, int cycle, bool passed, string pass, string failure)
            {
                if (_settled.ContainsKey(name)) return;
                if (!passed)
                {
                    _settled[name] = new CheckResult(CheckStatus.Fail, name, $"cycle {cycle}: {failure}");
                    return;
                }

                _passes[name] = Passes(name) + 1;
                _passNotes[name] = pass;
            }

            public void CycleSkip(string name, int cycle, string reason)
            {
                Settle(CheckStatus.Skip, name, $"cycle {cycle}: {reason}");
            }

            public int Passes(string name)
            {
                return _passes.TryGetValue(name, out var passes) ? passes : 0;
            }

            public bool IsSettled(string name)
            {
                return _settled.ContainsKey(name);
            }

            public void SkipAll(string message)
            {
                foreach (var name in _order) Settle(CheckStatus.Skip, name, message);
            }

            // The step that was running fails; the ones after it were not reached.
            public void Stopped(string message, int cycle)
            {
                _notReached = "not reached: the run stopped on " + message;
                foreach (var name in _order)
                {
                    if (_settled.ContainsKey(name)) continue;
                    if (_perCycle.Contains(name) && Passes(name) >= cycle) continue;

                    var prefix = _perCycle.Contains(name) ? $"cycle {cycle}: " : "";
                    Settle(CheckStatus.Fail, name, prefix + "the run stopped on " + message);
                    return;
                }
            }
        }

        // What one comparison of the probes found.
        private sealed class ProbeReading
        {
            public readonly List<string> Failures = new List<string>();
            public readonly List<string> Inconclusive = new List<string>();
            public readonly List<string> Seen = new List<string>();
        }

        // -- The run -------------------------------------------------------------------------

        private sealed class ModuleRun
        {
            private readonly ModuleDescriptor _descriptor;
            private readonly CycleRecorder _record;
            private readonly StateWait _wait = new StateWait();
            private readonly ErrorObserver _errors;
            private readonly Func<string, bool> _observerHandBack;
            private readonly string _runner;

            private readonly string _settled;
            private readonly string _offStopped;
            private readonly string _offState;
            private readonly string _offPatches;
            private readonly string _offProbes;
            private readonly string _onState;
            private readonly string _onRunning;
            private readonly string _onPatches;
            private readonly string _onProbes;
            private readonly string _restored;
            private readonly string _noErrors;

            private ConfigEntry<AutomaticsModule> _toggle;
            private string _moduleName;
            private string _section;
            private string _harmonyId;
            private bool _toggleOwned;
            private bool _proceed;
            private int _cycle;
            private int[] _baseline;

            public ModuleRun(int runId, string runner, ModuleDescriptor descriptor)
            {
                RunId = runId;
                _runner = runner;
                _descriptor = descriptor;

                var prefix = descriptor.Argument;
                _settled = prefix + "_initial_state_settled";
                _offStopped = prefix + "_off_stopped_at_request";
                _offState = prefix + "_off_state_disabled";
                _offPatches = prefix + "_off_patches_removed";
                _offProbes = prefix + "_off_probes_as_expected";
                _onState = prefix + "_on_state_enabled";
                _onRunning = prefix + "_on_running";
                _onPatches = prefix + "_on_patches_applied";
                _onProbes = prefix + "_on_probes_at_baseline";
                _restored = prefix + "_restored_initial_state";
                _noErrors = prefix + "_no_errors_logged";

                // The order the checks are recorded in, which is also the order they are reported
                // in: the module's extra checks sit after the checks of the phase they run in.
                var order = new List<string> { _settled };
                order.AddRange(ExtraNames(Phase.BeforeRequest));
                order.Add(_offStopped);
                order.AddRange(ExtraNames(Phase.AtRequest));
                order.AddRange(new[] { _offState, _offPatches, _offProbes });
                order.AddRange(ExtraNames(Phase.WhileOff));
                order.AddRange(new[] { _onState, _onRunning, _onPatches, _onProbes });
                order.AddRange(ExtraNames(Phase.AfterOn));
                order.AddRange(new[] { _restored, _noErrors });

                var once = new HashSet<string>(StringComparer.Ordinal) { _settled, _restored, _noErrors };
                _record = new CycleRecorder(prefix, ModuleCycles, order, order.Where(x => !once.Contains(x)));

                _errors = new ErrorObserver("AutomaticsObserverProbe-" + prefix, runner);

                // The observer goes back with the session too, however the run ends.
                _observerHandBack = _ =>
                {
                    _errors.Stop();
                    return true;
                };
            }

            private int RunId { get; }

            public IReadOnlyList<CheckResult> Results => _record.Results;

            public IEnumerator Execute()
            {
                if (ZNetScene.instance == null || !Player.m_localPlayer)
                {
                    _record.SkipAll("no world is loaded (ZNetScene or the local player is missing)");
                    yield break;
                }

                var module = _descriptor.Module;
                _moduleName = module.Name;
                _section = module.ConfigSection;
                if (!module.SupportsRuntimeToggle)
                {
                    _record.Settle(CheckStatus.Fail, _settled,
                        $"{_moduleName} does not support runtime toggling, so there is no lifecycle to check");
                    yield break;
                }

                _toggle = module.ModuleToggle;
                if (_toggle == null)
                {
                    _record.Settle(CheckStatus.Fail, _settled, $"the {_section} module toggle is not bound");
                    yield break;
                }

                _harmonyId = global::Automatics.Automatics.GetHarmonyId(_moduleName);

                // Asked before anything is written, so a module the check cannot use is left as it is.
                var precondition = _descriptor.Precondition?.Invoke();
                if (precondition != null)
                {
                    _record.SkipAll(precondition);
                    yield break;
                }

                // The check only ever turns the module off and back on. Turning it on for the user
                // would let it act in their world, so when it is off it stays off.
                if (_toggle.Value != AutomaticsModule.Enabled)
                {
                    _record.SkipAll(
                        $"{_section}/module is off in your settings, so the check changed nothing. It only ever turns the module off and back on; turn it on yourself if you want this check, then run it again.");
                    yield break;
                }

                // From here on any write to the setting that is not the check's own is the user
                // taking it back.
                if (!SmokeSession.BeginToggle(RunId, _toggle, out var refusal))
                {
                    _record.SkipAll("inconclusive: " + refusal);
                    yield break;
                }

                _toggleOwned = true;
                SmokeSession.TrackHandBack(_observerHandBack);
                _errors.Start();

                // The registry has to agree with the setting before anything is asked of it, or a
                // transition still pending from before the command would be read as this one's.
                var settle = Await(ModuleState.Enabled, true, true);
                while (settle.MoveNext()) yield return settle.Current;
                if (!RecordWait(_settled, 0, "nothing was changed")) yield break;

                if (_descriptor.Probes.Length == 0)
                {
                    _record.Settle(CheckStatus.Fail, _offProbes,
                        "the descriptor declares no probe, so nothing would show what the module took away and brought back");
                    _record.Settle(CheckStatus.Fail, _onProbes,
                        "the descriptor declares no probe, so nothing would show what the module took away and brought back");
                }

                // Taken once two frames in a row agree, for the reason given at ComponentSettleFrames.
                var baseline = ReadProbes();
                for (var frame = 1; frame < ComponentSettleFrames; frame++)
                {
                    yield return null;
                    var again = ReadProbes();
                    var agreed = again.SequenceEqual(baseline);
                    baseline = again;
                    if (agreed) break;
                }

                _baseline = baseline;

                for (_cycle = 1; _cycle <= ModuleCycles; _cycle++)
                {
                    var off = TurnOff(_cycle);
                    while (off.MoveNext()) yield return off.Current;
                    if (!_proceed) yield break;

                    var on = TurnOn(_cycle);
                    while (on.MoveNext()) yield return on.Current;
                    if (!_proceed) yield break;
                }
            }

            // Puts the setting back and waits for the registry to follow, then judges the log.
            public IEnumerator Finish()
            {
                if (_toggleOwned)
                {
                    var outcome = SmokeSession.RestoreToggle(RunId, out var detail);
                    switch (outcome)
                    {
                        case SmokeSession.ToggleOutcome.NothingWritten:
                            _record.Settle(CheckStatus.Skip, _restored,
                                "nothing was written, so there was nothing to put back");
                            break;
                        case SmokeSession.ToggleOutcome.LeftForUser:
                            _record.Settle(CheckStatus.Skip, _restored, "restoration skipped: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.NotThisRun:
                            _record.Settle(CheckStatus.Skip, _restored, "inconclusive: " + detail);
                            break;
                        case SmokeSession.ToggleOutcome.Failed:
                            _record.Settle(CheckStatus.Fail, _restored, detail);
                            break;
                        default:
                            // A fault is not final here: putting the value back is also what asks
                            // the registry to recover, so the wait gives it the full bound.
                            var wait = Await(ModuleState.Enabled, false, false, AutomaticsModule.Enabled);
                            while (wait.MoveNext()) yield return wait.Current;
                            RecordWait(_restored, 0, detail);
                            break;
                    }
                }

                if (_errors.Started) JudgeLog();
                SmokeSession.ReleaseHandBack(_observerHandBack);
            }

            // The step that was running fails; the ones after it were not reached.
            public void Stopped(string message)
            {
                _record.Stopped(message, Math.Min(Math.Max(_cycle, 1), ModuleCycles));
            }

            private IEnumerator TurnOff(int cycle)
            {
                _proceed = false;

                // Run before the write itself, synchronously, whether or not the write goes on to
                // succeed: this is the last point a check can see the state the disable is about
                // to act on, since the write can already move it (a config SettingChanged or
                // SetStopRequested reacting to it).
                RunExtrasSync(Phase.BeforeRequest, cycle);

                if (!Write(AutomaticsModule.Disabled, _offStopped, cycle)) yield break;

                // The write itself raises the stop request, frames before the pump takes anything
                // apart: the module is still applied but no longer running, and its entry points
                // open with that predicate. A coroutine runs after Update, where the pump runs, so
                // the wait below cannot see the frame before the pump; this is what shows the stop
                // takes hold at the request.
                var applied = _descriptor.Active();
                var running = _descriptor.Running();
                _record.Cycle(_offStopped, cycle, applied && !running,
                    "right after the disable request the module was still applied but no longer running",
                    $"right after the disable request the module was {(applied ? "applied" : "not applied")} and {(running ? "still running" : "not running")} (R1.5a)");
                RunExtrasSync(Phase.AtRequest, cycle);

                var wait = Await(ModuleState.Disabled, true, true);
                while (wait.MoveNext()) yield return wait.Current;
                if (!RecordWait(_offState, cycle)) yield break;

                // Object.Destroy takes effect at the end of the frame the pump ran in.
                for (var i = 0; i < DestroySettleFrames; i++) yield return null;

                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    _record.CycleSkip(_offPatches, cycle,
                        "this Harmony build cannot remove patches at runtime; they stay applied by design (R3.2)");
                }
                else
                {
                    var left = PatchesUnder(_harmonyId, out var total);
                    _record.Cycle(_offPatches, cycle, total == 0, $"no patch is left under \"{_harmonyId}\"",
                        $"{total} patch(es) are still under \"{_harmonyId}\" after the disable: [{string.Join(", ", left.OrderBy(x => x).Take(5))}]");
                }

                var reading = CompareProbes(true);
                for (var frame = 1; frame < ComponentSettleFrames && reading.Failures.Count > 0; frame++)
                {
                    yield return null;
                    reading = CompareProbes(true);
                }

                RecordProbes(_offProbes, cycle, reading, "while the module is off");

                var extras = RunExtras(Phase.WhileOff, cycle);
                while (extras.MoveNext()) yield return extras.Current;

                _proceed = true;
            }

            private IEnumerator TurnOn(int cycle)
            {
                _proceed = false;

                var precondition = _descriptor.Precondition?.Invoke();
                if (precondition != null)
                {
                    SkipOnChecks(cycle,
                        $"the safety precondition changed before re-enabling: {precondition}; Enabled was not written and this and the remaining on-side checks were skipped");
                    yield break;
                }

                if (!Write(AutomaticsModule.Enabled, _onState, cycle)) yield break;
                var wait = Await(ModuleState.Enabled, true, true);
                while (wait.MoveNext()) yield return wait.Current;
                if (!RecordWait(_onState, cycle)) yield break;

                var applied = _descriptor.Active();
                var running = _descriptor.Running();
                _record.Cycle(_onRunning, cycle, applied && running,
                    "once the registry reported Enabled the module was applied and running",
                    $"the registry reports Enabled, but the module is {(applied ? "applied" : "not applied")} and {(running ? "running" : "not running")}");

                CheckPatchSet(cycle);

                var reading = CompareProbes(false);
                for (var frame = 1; frame < ComponentSettleFrames && reading.Failures.Count > 0; frame++)
                {
                    yield return null;
                    reading = CompareProbes(false);
                }

                RecordProbes(_onProbes, cycle, reading, "once the module is back on");

                var extras = RunExtras(Phase.AfterOn, cycle);
                while (extras.MoveNext()) yield return extras.Current;

                _proceed = true;
            }

            private void SkipOnChecks(int cycle, string reason)
            {
                foreach (var name in new[] { _onState, _onRunning, _onPatches, _onProbes }
                             .Concat(ExtraNames(Phase.AfterOn)))
                    _record.CycleSkip(name, cycle, reason);
            }

            // The patches under the module's Harmony id have to be exactly the ones its patch
            // classes declare, compared as (target, patch method) pairs and counted as patches, so
            // a pair applied twice does not hide. A module that declares none must have none.
            private void CheckPatchSet(int cycle)
            {
                if (!ModuleRegistry.RuntimeToggleSupported)
                {
                    _record.CycleSkip(_onPatches, cycle,
                        "this Harmony build cannot remove patches at runtime, so the patches were never taken off (R3.2)");
                    return;
                }

                var types = _descriptor.PatchTypes ?? Type.EmptyTypes;
                var expected = ExpectedPatches(types);
                var declared = string.Join(", ", types.Select(x => x.FullName));
                if (types.Length > 0 && expected.Count == 0)
                {
                    _record.Cycle(_onPatches, cycle, false, "",
                        $"no [HarmonyPatch] target could be read from {declared}");
                    return;
                }

                var actual = PatchesUnder(_harmonyId, out var total);
                var missing = expected.Where(x => !actual.Contains(x)).OrderBy(x => x).ToArray();
                var unexpected = actual.Where(x => !expected.Contains(x)).OrderBy(x => x).ToArray();
                _record.Cycle(_onPatches, cycle,
                    missing.Length == 0 && unexpected.Length == 0 && total == expected.Count,
                    types.Length == 0
                        ? $"no patch is under \"{_harmonyId}\", and the module declares none"
                        : $"{total} patches under \"{_harmonyId}\", exactly the pairs {declared} declare",
                    $"the patches under \"{_harmonyId}\" differ from what the module declares: {total} patch(es) where it declares {expected.Count}; missing [{string.Join(", ", missing)}], unexpected [{string.Join(", ", unexpected)}]");
            }

            private int[] ReadProbes()
            {
                return _descriptor.Probes.Select(ReadProbe).ToArray();
            }

            // A probe that throws is one that cannot be read.
            private static int ReadProbe(Probe probe)
            {
                try
                {
                    return probe.Read();
                }
                catch (Exception)
                {
                    return -1;
                }
            }

            // Every probe against what it should read now: while off, what its OffExpectation says;
            // once on, its target when it has one and its value before the run otherwise.
            private ProbeReading CompareProbes(bool whileOff)
            {
                var reading = new ProbeReading();
                for (var i = 0; i < _descriptor.Probes.Length; i++)
                {
                    var probe = _descriptor.Probes[i];
                    var before = _baseline[i];
                    var now = ReadProbe(probe);
                    if (now < 0 || before < 0)
                    {
                        reading.Failures.Add($"{probe.Name} could not be read");
                        continue;
                    }

                    int? target = null;
                    if (probe.Target != null)
                    {
                        var eligible = -1;
                        try
                        {
                            eligible = probe.Target();
                        }
                        catch (Exception)
                        {
                            // Left at -1, which is reported below.
                        }

                        if (eligible < 0)
                        {
                            reading.Failures.Add($"the objects {probe.Name} should be on could not be counted");
                            continue;
                        }

                        target = eligible;
                    }

                    var showsNothing = whileOff
                        ? (target.HasValue || probe.NeedsNonZeroBaseline) && before == 0
                        : target.HasValue
                            ? target.Value == 0
                            : probe.NeedsNonZeroBaseline && before == 0;

                    int expected;
                    string expectation;
                    if (!whileOff)
                    {
                        expected = target ?? before;
                        expectation = target.HasValue
                            ? $"the {expected} object(s) it should be on"
                            : $"{before}, its value before the run";
                    }
                    else if (probe.Off == OffExpectation.Unchecked)
                    {
                        reading.Seen.Add($"{probe.Name} {now} (not checked while off)");
                        continue;
                    }
                    else if (probe.Off == OffExpectation.Zero)
                    {
                        expected = 0;
                        expectation = "0";
                    }
                    else
                    {
                        expected = before - probe.Delta;
                        expectation = probe.Delta == 0
                            ? $"{before}, its value before the run"
                            : $"{expected}, {probe.Delta} below its value before the run";
                    }

                    if (now != expected)
                        reading.Failures.Add($"{probe.Name} reads {now} where it should read {expectation}");
                    else if (showsNothing)
                        reading.Inconclusive.Add(target.HasValue
                            ? $"{probe.Name} has no object to be on, so 0 could not be told from a miss"
                            : $"{probe.Name} read 0 before the run, so 0 could not be told from a miss");
                    else
                        reading.Seen.Add($"{probe.Name} {now}");
                }

                return reading;
            }

            // Any probe that does not read what it should fails the check; with none failing, any
            // probe that could show nothing makes it inconclusive.
            private void RecordProbes(string name, int cycle, ProbeReading reading, string when)
            {
                if (_descriptor.Probes.Length == 0) return;

                if (reading.Failures.Count > 0)
                    _record.Cycle(name, cycle, false, "", $"{when}: {string.Join("; ", reading.Failures)}");
                else if (reading.Inconclusive.Count > 0)
                    _record.CycleSkip(name, cycle, $"inconclusive {when}: {string.Join("; ", reading.Inconclusive)}");
                else
                    _record.Cycle(name, cycle, true, $"{when}: {string.Join(", ", reading.Seen)}", "");
            }

            private IEnumerable<ExtraCheck> ExtrasIn(Phase phase)
            {
                return (_descriptor.Extras ?? new ExtraCheck[0]).Where(x => x != null && x.When == phase);
            }

            private IEnumerable<string> ExtraNames(Phase phase)
            {
                return ExtrasIn(phase).Select(x => _descriptor.Argument + "_" + x.Name);
            }

            // Neither BeforeRequest nor AtRequest may wait for a frame: each is there to see the
            // frame it runs in (respectively, just before the disable is written, and right
            // after), and yielding would mean it no longer saw that frame.
            private void RunExtrasSync(Phase phase, int cycle)
            {
                foreach (var extra in ExtrasIn(phase))
                {
                    var name = _record.Check(extra.Name);
                    var passes = _record.Passes(name);
                    try
                    {
                        var steps = extra.Run(_record, cycle);
                        if (steps != null && steps.MoveNext())
                            _record.Cycle(name, cycle, false, "",
                                "a synchronous check waited for a frame, so it no longer saw the frame it was recorded for");
                    }
                    catch (Exception e)
                    {
                        _record.Cycle(name, cycle, false, "", e.GetType().Name + ": " + e.Message);
                    }

                    RequireAResult(name, cycle, passes);
                }
            }

            private IEnumerator RunExtras(Phase phase, int cycle)
            {
                foreach (var extra in ExtrasIn(phase).ToList())
                {
                    var name = _record.Check(extra.Name);
                    var passes = _record.Passes(name);
                    var steps = Contain(extra.Run(_record, cycle) ?? Enumerable.Empty<object>().GetEnumerator(),
                        (_, message) => _record.Cycle(name, cycle, false, "", message), name);
                    while (steps.MoveNext()) yield return steps.Current;

                    RequireAResult(name, cycle, passes);
                }
            }

            // An extra check that records nothing for a cycle would read as not reached; it fails
            // instead, so a check that forgot to report cannot hide.
            private void RequireAResult(string name, int cycle, int passesBefore)
            {
                if (_record.IsSettled(name) || _record.Passes(name) != passesBefore) return;
                _record.Cycle(name, cycle, false, "", "the check recorded no result");
            }

            private void JudgeLog()
            {
                _errors.Stop();
                var unexpected = _errors.Unexpected;
                if (unexpected.Count > 0)
                {
                    foreach (var entry in unexpected.Take(10))
                        Log.LogWarning($"[Automatics Smoke] {_runner}: logged while the check ran: {entry}");
                    var first = unexpected[0];
                    var end = first.IndexOf('\n');
                    _record.Settle(CheckStatus.Fail, _noErrors,
                        $"{unexpected.Count} error(s) or exception(s) were logged while the check ran (the full text is in the warnings above); the first: {(end < 0 ? first : first.Substring(0, end))}");
                }
                else if (_errors.Probes != 1)
                {
                    _record.Settle(CheckStatus.Skip, _noErrors,
                        $"the log observer saw its own probe {_errors.Probes} time(s) instead of once, so it cannot vouch for an empty log");
                }
                else
                {
                    _record.Settle(CheckStatus.Pass, _noErrors,
                        "no error or exception was logged while the check ran (the observer saw its own probe once)");
                }
            }

            private IEnumerator Await(ModuleState expected, bool faultIsFinal, bool worldRequired,
                AutomaticsModule? heldValue = null)
            {
                return AwaitState(RunId, _toggle, _moduleName, expected, faultIsFinal, worldRequired,
                    _wait, heldValue);
            }

            // The user's path: a config write raises SettingChanged, which the registry turns into
            // a request. SmokeSession refuses once the user has changed the setting.
            private bool Write(AutomaticsModule value, string name, int cycle)
            {
                if (SmokeSession.WriteToggle(RunId, value, out var refusal)) return true;

                _record.CycleSkip(name, cycle, "inconclusive: " + refusal + "; the run stopped");
                return false;
            }

            // Reaching the state passes; a fault or a timeout is a failure of the module; anything
            // that took the run out of the check's hands makes it inconclusive. Cycle 0 is a check
            // that is recorded once.
            private bool RecordWait(string name, int cycle, string note = "")
            {
                var suffix = string.IsNullOrEmpty(note) ? "" : "; " + note;
                switch (_wait.Result)
                {
                    case WaitResult.Reached:
                        if (cycle == 0) _record.Settle(CheckStatus.Pass, name, note);
                        else _record.Cycle(name, cycle, true, note, "");
                        return true;
                    case WaitResult.Faulted:
                    case WaitResult.TimedOut:
                        if (cycle == 0) _record.Settle(CheckStatus.Fail, name, _wait.Report + suffix);
                        else _record.Cycle(name, cycle, false, "", _wait.Report + suffix);
                        return false;
                    default:
                        if (cycle == 0) _record.Settle(CheckStatus.Skip, name, "inconclusive: " + _wait.Report + suffix);
                        else _record.CycleSkip(name, cycle, "inconclusive: " + _wait.Report + suffix);
                        return false;
                }
            }
        }
    }
}
