using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Automatics.AutomaticMapping;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using Held = System.Collections.Generic.Dictionary<BepInEx.Configuration.ConfigEntryBase,
    Automatics.SmokeTest.SmokeSession.SettingRestoration>;

namespace Automatics.SmokeTest
{
    // The setup a smoke command does for itself before a check, so that a person handles only
    // what code cannot provide: a setting written for the run, a module turned on for the run, the
    // player moved to clear ground, items taken out of the inventory. Each helper changes the game
    // only through SmokeSession - a setting through BeginSetting/WriteSetting, everything else
    // through a hand-back it registers itself - so a run cut short still has it put back, under
    // the rules at the top of SmokeSession.cs. Nothing here changes product behaviour.
    //
    // The coroutines are stepped the way the sections step their own:
    //   var steps = SmokeSetup.Relocate(run.RunId, SmokeSetup.Clearance(8f, SmokeSetup.SiteKinds.All), result);
    //   while (steps.MoveNext()) yield return steps.Current;
    internal static class SmokeSetup
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("Automatics Smoke Setup");

        // -- Settings --------------------------------------------------------------------------

        // Takes the setting on with the session - once per run; held remembers it - and writes it.
        // The session records the change before it is made and refuses it when the record cannot
        // be written; the refusal says why. The normal settings restore puts it back.
        public static bool Write(int runId, Held held, ConfigEntryBase entry, object value, out string refusal)
        {
            if (!held.TryGetValue(entry, out var setting))
            {
                if (!SmokeSession.BeginSetting(runId, entry, out setting, out refusal)) return false;
                held[entry] = setting;
            }

            return SmokeSession.WriteSetting(runId, setting, value, out refusal);
        }

        // The same for a feature section: a refusal is logged, and false returned.
        public static bool Write(FeatureRun run, Held held, ConfigEntryBase entry, object value)
        {
            if (Write(run.RunId, held, entry, value, out var refusal)) return true;

            FeaturesSmokeCommand.Warn($"[Automatics Smoke] {Name(entry)} was not changed for the run: {refusal}");
            return false;
        }

        // A config class's private static ConfigEntry<T> field, for a setting without an accessor,
        // the way the farming, planting, supply and repair sections reach theirs. Null when the
        // field is not there or holds another type.
        public static ConfigEntry<T> Field<T>(Type config, string field)
        {
            return AccessTools.Field(config, field)?.GetValue(null) as ConfigEntry<T>;
        }

        // Puts every setting the run holds back and reports it as the one check the sections end
        // with: PASS when each is back, SKIP when one was left to the user or the run was
        // abandoned, FAIL when one could not be put back. Nothing when nothing was written.
        public static void ReportSettings(FeatureRun run, string check)
        {
            var results = SmokeSession.RestoreSettings(run.RunId);
            if (results.Count == 0) return;

            var detail = string.Join("; ", results.Select(x => x.Value));
            if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.Failed))
                run.Fail(check, detail);
            else if (results.Any(x => x.Key == SmokeSession.ToggleOutcome.LeftForUser ||
                                      x.Key == SmokeSession.ToggleOutcome.NotThisRun))
                run.Skip(check, "restoration skipped: " + detail);
            else
                run.Pass(check, detail);
        }

        // -- Modules ---------------------------------------------------------------------------

        // How many frames a module gets to start after its toggle is written: the supply
        // section's figure. The registry applies a toggle from its pump, a frame or so later.
        private const int ModuleStartFrames = 600;

        // Turns a module on for the run when it is not running: writes its toggle through the
        // session and waits until the module reports running. result.Changed says it was turned
        // on; the normal settings restore puts the toggle back. running is the module's own
        // Running property, for instance () => SupplyModule.Running.
        public static IEnumerator EnableModule(int runId, Held held, ConfigEntry<AutomaticsModule> toggle,
            Func<bool> running, SetupResult result)
        {
            if (running())
            {
                result.Ok = true;
                result.Reason = "already running";
                yield break;
            }

            if (!Write(runId, held, toggle, AutomaticsModule.Enabled, out var refusal))
            {
                result.Reason = $"{Name(toggle)} could not be turned on for the run: {refusal}";
                yield break;
            }

            for (var frame = 0; frame < ModuleStartFrames && SmokeSession.IsCurrent(runId) && !running(); frame++)
                yield return null;

            if (!running())
            {
                result.Reason = $"{Name(toggle)} was turned on, but the module did not start within {ModuleStartFrames} frames";
                yield break;
            }

            result.Ok = true;
            result.Changed = true;
            result.Reason = "turned on for the run and put back afterwards";
        }

        // -- Teleports -------------------------------------------------------------------------

        // From Player.TeleportTo and UpdateTeleport (decompiled):
        //   - TeleportTo returns false while a teleport runs and while m_teleportCooldown is under
        //     2 s. The cooldown counts up only while no teleport runs and is zeroed when one
        //     starts, so 2 s have to pass after one ends before the next is accepted.
        //   - UpdateTeleport runs from FixedUpdate. After 2 s it places the player at the target
        //     every physics step; a distant teleport then also waits until 8 s have passed. It
        //     ends once ZNetScene.IsAreaReady(target) and ZoneSystem.FindFloor(target) both hold.
        //     With the area ready but no floor found, a short hop ends at once by putting the
        //     player back where they were ("$msg_portal_blocked"); a distant teleport waits until
        //     15 s and then ends at GetSolidHeight(target) + 0.5.
        //   - So a short hop takes at least 2 s and a distant teleport at least 8 s, and neither
        //     ends while the area stays unready. The timeouts below are not from the decompile;
        //     the first in-game run confirms them (unverified).
        private const float TeleportAcceptTimeout = 3f;
        private const float ShortHopTimeout = 10f;
        private const float DistantTimeout = 30f;
        private const float ArrivalTolerance = 2f;

        // Moves the local player with Player.TeleportTo and waits until the teleport has ended,
        // retrying while the cooldown refuses it. result.Position is where the player ended up;
        // Ok is false when the teleport was refused for TeleportAcceptTimeout, did not end within
        // its timeout, or ended somewhere else than the target (no floor there). The first move of
        // a run records where the command started, and the run's hand-back returns the player
        // there (see Excursion).
        //
        // protect turns god mode and ghost mode on for the move and off again when it has ended,
        // and the run's hand-back turns them off when the move is cut short. What the modes do,
        // from the decompile: BaseAI.CanHearTarget and CanSeeTarget return false for a player in
        // ghost mode, so CanSenseTarget is false, BaseAI.FindEnemy never picks the player (its
        // HuntPlayer fallback returns null for one too), and MonsterAI.UpdateSleep does not wake
        // for one. A creature that already has the player as its target keeps it - MonsterAI's
        // UpdateTarget drops a target only when dead, no longer an enemy or in lava - until
        // m_timeSinceSensedTargetCreature passes 30 s, which a ghost player never resets. So ghost
        // mode stops new targeting at once and ends an existing pursuit within 30 s. God mode
        // does not stop damage: Character.ApplyDamage only floors the health at 1 in god or ghost
        // mode, so the player cannot die. A hit the player lands in either mode marks the
        // target's ZDO as cheated (Character.ApplyDamage), which is why both are off again before
        // the caller goes on.
        public static IEnumerator Teleport(int runId, Vector3 position, Quaternion rotation, bool distant,
            bool protect, SetupResult result)
        {
            var player = Player.m_localPlayer;
            if (!player)
            {
                result.Reason = "no local player";
                yield break;
            }

            var excursion = Excursion.Of(runId, player);
            if (protect) excursion.Protect();

            var deadline = Time.time + TeleportAcceptTimeout;
            var accepted = false;
            while (player && SmokeSession.IsCurrent(runId) && Time.time <= deadline)
            {
                accepted = player.TeleportTo(position, rotation, distant);
                if (accepted) break;
                yield return null;
            }

            if (!accepted)
            {
                if (protect) excursion.Unprotect();
                if (!player)
                    result.Reason = "the player left the world";
                else if (!SmokeSession.IsCurrent(runId))
                    result.Reason = "the run was abandoned";
                else
                    result.Reason =
                        $"TeleportTo refused the move to {Format(position)} for {TeleportAcceptTimeout:0} s (a teleport is running, or one ended less than 2 s ago)";
                if (player) result.Position = player.transform.position;
                yield break;
            }

            var timeout = distant ? DistantTimeout : ShortHopTimeout;
            deadline = Time.time + timeout;
            while (player && player.IsTeleporting() && Time.time <= deadline && SmokeSession.IsCurrent(runId))
                yield return null;

            if (protect) excursion.Unprotect();
            if (!player)
            {
                result.Reason = "the player left the world during the teleport";
                yield break;
            }

            result.Position = player.transform.position;
            if (player.IsTeleporting())
                result.Reason = $"the teleport to {Format(position)} did not end within {timeout:0} s (the area around it may not be ready)";
            else if (global::Utils.DistanceXZ(result.Position, position) > ArrivalTolerance)
                result.Reason = $"the game found no floor at {Format(position)} and left the player at {Format(result.Position)}";
            else
            {
                result.Ok = true;
                result.Changed = true;
                result.Reason = $"at {Format(result.Position)}";
            }
        }

        // Moves the player back to where the command started, when a move was made this run, so a
        // command can end with the player home before its Summary line; the run's hand-back then
        // finds nothing to do. Ok with nothing done when no move was made.
        public static IEnumerator ReturnToStart(int runId, SetupResult result)
        {
            var excursion = Excursion.Current(runId);
            if (excursion == null || !excursion.AwayFromStart())
            {
                result.Ok = true;
                result.Reason = "the player is where the command started";
                yield break;
            }

            var steps = Teleport(runId, excursion.Start, excursion.StartRotation, excursion.StartIsFar(), false, result);
            while (steps.MoveNext()) yield return steps.Current;
        }

        // -- Relocation ------------------------------------------------------------------------

        [Flags]
        public enum SiteKinds
        {
            None = 0,
            Pieces = 1,
            Containers = 2,
            Characters = 4,
            Drops = 8,
            Pickables = 16,
            // Anything with an IDestructible: trees, logs, stumps, rocks and deposits, creatures,
            // and building pieces with wear.
            Destructibles = 32,
            All = Pieces | Containers | Characters | Drops | Pickables | Destructibles
        }

        // The candidate sites, after the spot the player stands on: rings of 15, 30 and 45 m, each
        // walked in 12 bearings from the player's facing outward - 0°, then 30° right, 30° left,
        // 60° right, and so on to 180° - so the order is fixed by where the player faces.
        private static readonly float[] Rings = { 15f, 30f, 45f };
        private const int Bearings = 12;

        // Every site is tested at its centre and at four points SiteRim out: on ground at least
        // WaterMargin above the water level and no steeper than SlopeLimit, the ground normal's y
        // (0.9 is about 26°), and farther than SiteRim from every location's radius. Whether 26°
        // is the right cut for the test world is for the first in-game run (unverified).
        private const float SiteRim = 5f;
        private const float WaterMargin = 0.5f;
        private const float SlopeLimit = 0.9f;

        // The player is placed this far above the ground, so the teleport's floor search starts
        // above it; they settle onto it at once (unverified).
        private const float PlacementLift = 0.1f;

        // How many of the rejected sites a failed search names.
        private const int ReportedSites = 12;

        // The default site predicate: nothing of the kinds within radius of the site, and no map
        // pin within it either. Returns why not, or null. The player's own colliders do not count.
        // Pins are known through the mapping module's index, so with that module off none is
        // found; a caller whose check needs pin-free ground requires the module anyway.
        public static Func<Vector3, string> Clearance(float radius, SiteKinds kinds)
        {
            return site =>
            {
                var pin = Map.GetClosestPin(site, radius, includeInactive: true);
                if (pin != null) return $"pin '{pin.m_name}' within {radius:0} m";

                var player = Player.m_localPlayer;
                foreach (var collider in Physics.OverlapSphere(site, radius, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (!collider || (player && collider.transform.IsChildOf(player.transform))) continue;
                    var found = Describe(collider, kinds);
                    if (found != null) return $"{found} within {radius:0} m";
                }

                return null;
            };
        }

        // Finds the first site near the player that passes the ground filters and the caller's
        // predicate - problem returns why a site will not do, or null; compose Clearance with
        // anything else - and moves the player there with a short hop, keeping their facing. The
        // spot they stand on is tried first, so nothing moves when it is already clear. Ok is
        // false when no site was found or the move failed; Reason is written to be a SKIP reason.
        public static IEnumerator Relocate(int runId, Func<Vector3, string> problem, SetupResult result)
        {
            var player = Player.m_localPlayer;
            if (!player)
            {
                result.Reason = "no local player";
                yield break;
            }

            // The start is recorded before the search, so the return is to where the command
            // started and not to where a check left the player.
            Excursion.Of(runId, player);

            var notes = new List<string>();
            var tried = 0;
            Vector3 site;
            string where;
            var found = FindSite(player, problem, notes, ref tried, out site, out where);
            if (!found)
            {
                var more = tried > notes.Count ? $"; and {tried - notes.Count} more" : "";
                result.Reason =
                    $"no clear site within {Rings[Rings.Length - 1]:0} m of you ({tried} spots tried: {string.Join("; ", notes)}{more})";
                yield break;
            }

            if (where == null)
            {
                result.Ok = true;
                result.Position = player.transform.position;
                result.Reason = "the spot you stand on is clear";
                yield break;
            }

            var move = new SetupResult();
            var steps = Teleport(runId, site + Vector3.up * PlacementLift, player.transform.rotation, false, false, move);
            while (steps.MoveNext()) yield return steps.Current;

            result.Ok = move.Ok;
            result.Changed = move.Ok;
            result.Position = move.Position;
            result.Reason = move.Ok
                ? $"moved {where} to the clear site {move.Reason}"
                : $"the move {where} to a clear site failed: {move.Reason}";
        }

        // where is null for the spot the player stands on.
        private static bool FindSite(Player player, Func<Vector3, string> problem, List<string> notes, ref int tried,
            out Vector3 site, out string where)
        {
            foreach (var candidate in Candidates(player))
            {
                tried++;
                var point = candidate.Key;
                var reason = SiteProblem(ref point) ?? problem(point);
                if (reason == null)
                {
                    site = point;
                    where = candidate.Value;
                    return true;
                }

                if (notes.Count < ReportedSites) notes.Add($"{candidate.Value ?? "here"}: {reason}");
            }

            site = Vector3.zero;
            where = null;
            return false;
        }

        private static IEnumerable<KeyValuePair<Vector3, string>> Candidates(Player player)
        {
            var origin = player.transform.position;
            yield return new KeyValuePair<Vector3, string>(origin, null);

            var facing = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up).normalized;
            foreach (var ring in Rings)
                for (var i = 0; i < Bearings; i++)
                {
                    var angle = (i + 1) / 2 * (360f / Bearings) * (i % 2 == 0 ? -1f : 1f);
                    var direction = Quaternion.Euler(0f, angle, 0f) * facing;
                    yield return new KeyValuePair<Vector3, string>(origin + direction * ring,
                        $"{ring:0} m at {angle:+0;-0;0}°");
                }
        }

        // The ground filters every site passes before the caller's predicate. Sets the point's
        // height to the ground's.
        private static string SiteProblem(ref Vector3 point)
        {
            if (!ZNetScene.instance.IsAreaReady(point)) return "the area is not loaded yet";

            var centre = point;
            var reason = GroundProblem(ref centre);
            if (reason != null) return reason;
            for (var i = 0; i < 4; i++)
            {
                var rim = centre + Quaternion.Euler(0f, i * 90f, 0f) * Vector3.forward * SiteRim;
                reason = GroundProblem(ref rim);
                if (reason != null) return $"{reason} {SiteRim:0} m out";
            }

            if (Location.IsInsideLocation(centre, SiteRim)) return "inside a location";

            point = centre;
            return null;
        }

        private static string GroundProblem(ref Vector3 point)
        {
            var p = point;
            ZoneSystem.instance.GetGroundData(ref p, out var normal, out _, out _, out var heightmap);
            if (!heightmap) return "no ground";
            var aboveWater = p.y - ZoneSystem.instance.m_waterLevel;
            if (aboveWater < WaterMargin) return $"under water or on the shore ({aboveWater:+0.0;-0.0} m from the water level)";
            if (normal.y < SlopeLimit) return $"sloped ({Mathf.Acos(normal.y) * Mathf.Rad2Deg:0}°)";
            point = p;
            return null;
        }

        // The first of the asked-for kinds the collider belongs to, the narrow kinds before the
        // broad one, so a chest reads as a container and a boar as a creature. Null for none.
        private static string Describe(Collider collider, SiteKinds kinds)
        {
            if ((kinds & SiteKinds.Pieces) != 0)
            {
                var piece = collider.GetComponentInParent<Piece>();
                if (piece) return $"piece '{piece.m_name}' ({Objects.GetPrefabName(piece.gameObject)})";
            }

            if ((kinds & SiteKinds.Containers) != 0)
            {
                var container = collider.GetComponentInParent<Container>();
                if (container) return $"container '{container.m_name}'";
            }

            if ((kinds & SiteKinds.Characters) != 0)
            {
                var character = collider.GetComponentInParent<Character>();
                if (character) return $"creature '{character.m_name}'";
            }

            if ((kinds & SiteKinds.Drops) != 0)
            {
                var drop = collider.GetComponentInParent<ItemDrop>();
                if (drop) return $"item drop '{drop.m_itemData.m_shared.m_name}'";
            }

            if ((kinds & SiteKinds.Pickables) != 0)
            {
                var pickable = collider.GetComponentInParent<Pickable>();
                if (pickable) return $"pickable '{Objects.GetName(pickable)}' ({Objects.GetPrefabName(pickable.gameObject)})";
            }

            if ((kinds & SiteKinds.Destructibles) != 0)
            {
                var destructible = collider.GetComponentInParent<IDestructible>() as Component;
                if (destructible)
                    return $"'{Objects.GetName(destructible)}' ({Objects.GetPrefabName(destructible.gameObject)})";
            }

            return null;
        }

        // -- The inventory ---------------------------------------------------------------------

        // Takes items out of the player's inventory for the run: enough unequipped ones for
        // freeSlots empty slots and freeWeight of carry weight, and every unequipped one match
        // accepts. Restore puts the same ItemData instances back, each into the slot it came from
        // when that is free, else wherever the inventory has room, else dropped at the player's
        // feet; nothing is lost short of the process ending, and a logout saves the character
        // without them (the test character accepts that). Restore is registered as a hand-back;
        // the caller also calls it in its finally, and may call it again.
        //
        // Equipped items are never taken: taking a tool or armour off has side effects of its
        // own, and no check needs it. An equipped item match accepts is named in Left, so the
        // caller can SKIP with it.
        public static InventoryStash Stash(Player player, int freeSlots = 0, float freeWeight = 0f,
            Predicate<ItemDrop.ItemData> match = null)
        {
            var stash = new InventoryStash(player);
            stash.Take(freeSlots, freeWeight, match);
            SmokeSession.TrackHandBack(_ => stash.Restore());
            return stash;
        }

        internal sealed class InventoryStash
        {
            private readonly Player _player;
            private readonly List<ItemDrop.ItemData> _taken = new List<ItemDrop.ItemData>();
            private readonly List<string> _left = new List<string>();

            public InventoryStash(Player player)
            {
                _player = player;
            }

            public int Taken => _taken.Count;

            // The equipped items match accepted, by name.
            public IReadOnlyList<string> Left => _left;

            // What was taken, for a PASS note.
            public string Report => _taken.Count == 0
                ? "nothing had to be put aside"
                : $"put aside {_taken.Count} stack(s): {string.Join(", ", _taken.Select(x => $"{x.m_shared.m_name} x{x.m_stack}"))}";

            internal void Take(int freeSlots, float freeWeight, Predicate<ItemDrop.ItemData> match)
            {
                var inventory = _player.GetInventory();
                var items = inventory.GetAllItems();
                _left.AddRange(items.Where(x => match != null && match(x) && _player.IsItemEquiped(x))
                    .Select(x => x.m_shared.m_name));

                // Matches first, all of them; then, for room, from the bottom of the grid up and
                // the heaviest first, so the hotbar is touched last and the fewest stacks move.
                var candidates = items.Where(x => !_player.IsItemEquiped(x))
                    .OrderByDescending(x => match != null && match(x))
                    .ThenByDescending(x => x.m_gridPos.y)
                    .ThenByDescending(x => x.GetWeight())
                    .ToList();
                foreach (var item in candidates)
                {
                    var wanted = match != null && match(item);
                    var room = inventory.GetEmptySlots() < freeSlots ||
                               _player.GetMaxCarryWeight() - inventory.GetTotalWeight() < freeWeight;
                    if (!wanted && !room) break;
                    if (inventory.RemoveItem(item)) _taken.Add(item);
                }
            }

            // Safe to call again: each item goes back once. Inventory.AddItem first merges a
            // stackable item's units into a stack of the same kind with room, so such an item can
            // come back as units in another stack rather than as its own instance; the count is
            // the same either way.
            public bool Restore()
            {
                if (_taken.Count == 0) return true;
                if (!_player)
                {
                    // The player object is gone with the world; there is no inventory to put
                    // them back into.
                    _taken.Clear();
                    return true;
                }

                var inventory = _player.GetInventory();
                foreach (var item in _taken.ToArray())
                {
                    if (!inventory.ContainsItem(item) && !inventory.AddItem(item, item.m_gridPos) &&
                        !inventory.AddItem(item) && !Drop(item))
                        continue;
                    _taken.Remove(item);
                }

                return _taken.Count == 0;
            }

            private bool Drop(ItemDrop.ItemData item)
            {
                var drop = ItemDrop.DropItem(item, 0, _player.transform.position + Vector3.up, Quaternion.identity);
                if (!drop) return false;

                // As the game marks an item the player drops: not picked up again on its own.
                drop.OnPlayerDrop();
                var p = drop.transform.position;
                Log.LogWarning(
                    $"[Automatics Smoke] {item.m_shared.m_name} x{item.m_stack} did not fit back into your inventory and was dropped at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})");
                return true;
            }
        }

        // -- What a run's moves owe the player --------------------------------------------------

        // Where the command started, which the player is returned to at the end of the command
        // (plan decision D2), and the god and ghost mode a protected teleport turned on. One per
        // run, made at the first move and registered with the session then, so a run cut short in
        // or after a move still has them handed back. The hand-back turns the modes off at once;
        // the return it starts ends on its own 2 s or more later, so it reports done only once
        // the player is no longer teleporting, and the next trigger finds it done.
        private sealed class Excursion
        {
            // A player this close to the start is home: the command returned them itself, or
            // never moved them.
            private const float ReturnTolerance = 1f;

            private static Excursion _current;

            private readonly int _runId;
            private readonly Player _player;
            private bool _godSet;
            private bool _ghostSet;
            private bool _returnStarted;
            private bool _done;

            private Excursion(int runId, Player player)
            {
                _runId = runId;
                _player = player;
                Start = player.transform.position;
                StartRotation = player.transform.rotation;
            }

            public Vector3 Start { get; }
            public Quaternion StartRotation { get; }

            public static Excursion Of(int runId, Player player)
            {
                if (_current == null || _current._runId != runId)
                {
                    _current = new Excursion(runId, player);
                    SmokeSession.TrackHandBack(_current.HandBack);
                }

                return _current;
            }

            public static Excursion Current(int runId)
            {
                return _current != null && _current._runId == runId ? _current : null;
            }

            public bool AwayFromStart()
            {
                return _player && Vector3.Distance(_player.transform.position, Start) > ReturnTolerance;
            }

            // A return from another zone goes as a distant teleport, which never ends by putting
            // the player back where they were.
            public bool StartIsFar()
            {
                return _player && Vector3.Distance(_player.transform.position, Start) > ZoneSystem.c_ZoneSize;
            }

            public void Protect()
            {
                if (!_player.InGodMode())
                {
                    _player.SetGodMode(true);
                    _godSet = true;
                }

                if (!_player.InGhostMode())
                {
                    _player.SetGhostMode(true);
                    _ghostSet = true;
                }
            }

            public void Unprotect()
            {
                if (_player)
                {
                    if (_godSet) _player.SetGodMode(false);
                    if (_ghostSet) _player.SetGhostMode(false);
                }

                _godSet = false;
                _ghostSet = false;
            }

            private bool HandBack(string reason)
            {
                if (_done) return true;
                if (!_player)
                {
                    // Logged out: the modes and the position went with the player object.
                    _done = true;
                    return true;
                }

                Unprotect();
                if (!_returnStarted)
                {
                    if (!AwayFromStart())
                    {
                        _done = true;
                        return true;
                    }

                    if (!_player.TeleportTo(Start, StartRotation, StartIsFar()))
                    {
                        Log.LogWarning(
                            $"[Automatics Smoke] {reason}: the player could not be moved back to the command's starting spot {Format(Start)} yet (a teleport is running, or one ended less than 2 s ago); it is tried again at the next opportunity");
                        return false;
                    }

                    _returnStarted = true;
                }

                if (_player.IsTeleporting())
                {
                    Log.LogWarning(
                        $"[Automatics Smoke] {reason}: the player is on the way back to the command's starting spot {Format(Start)}; the next opportunity confirms the arrival");
                    return false;
                }

                _done = true;
                return true;
            }
        }

        // -- Results ---------------------------------------------------------------------------

        // What a setup step came to. Ok says the caller can go on; Reason says why not, written
        // as a SKIP reason, or carries a note for a PASS line; Changed says the step changed
        // something the run puts back (a module turned on, the player moved); Position is where a
        // move left the player.
        internal sealed class SetupResult
        {
            public bool Ok;
            public bool Changed;
            public string Reason = "";
            public Vector3 Position;
        }

        private static string Name(ConfigEntryBase entry)
        {
            return $"{entry.Definition.Section}/{entry.Definition.Key}";
        }

        private static string Format(Vector3 p)
        {
            return $"({p.x:0.0}, {p.y:0.0}, {p.z:0.0})";
        }
    }
}
