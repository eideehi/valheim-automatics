using System;
using System.Collections.Generic;
using Automatics.Valheim;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFeeding
{
    [DisallowMultipleComponent]
    internal class AutomaticFeeding : MonoBehaviour, IScopedComponent
    {
        // MonsterAI.CanConsume resolved once into a cached delegate so the
        // per-AI-tick food check never allocates a Traverse + params array per
        // item. The guarded bind falls back to reflection if Valheim renames it.
        private static readonly Func<MonsterAI, ItemDrop.ItemData, bool> ConsumeCheck;

        private readonly List<Container> _feedBoxBuffer = new List<Container>();
        private Tameable _tamable;
        private Character _character;
        private MonsterAI _monsterAI;
        private BaseAI _baseAI;
        private float _consumeSearchTimer;
        private Container _closestFeedBox;
        private Humanoid _closestFeeder;
        private ItemDrop.ItemData _consumeTargetItem;

        public int ScopeGeneration { get; set; }

        static AutomaticFeeding()
        {
            ConsumeCheck = BindConsumeCheck();
        }

        private static Func<MonsterAI, ItemDrop.ItemData, bool> BindConsumeCheck()
        {
            try
            {
                var method = AccessTools.Method(typeof(MonsterAI), "CanConsume",
                    new[] { typeof(ItemDrop.ItemData) });
                if (method != null)
                    return AccessTools
                        .MethodDelegate<Func<MonsterAI, ItemDrop.ItemData, bool>>(method);
                Automatics.Logger.Warning(() =>
                    "MonsterAI.CanConsume not found; falling back to reflection.");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind MonsterAI.CanConsume delegate; falling back to reflection: {e.Message}");
            }

            return null;
        }

        private void Awake()
        {
            _tamable = GetComponent<Tameable>();
            _character = GetComponent<Character>();
            _monsterAI = GetComponent<MonsterAI>();
            _baseAI = GetComponent<BaseAI>();
        }

        private void OnDestroy()
        {
            _baseAI = null;
            _monsterAI = null;
            _tamable = null;
            _character = null;
            _closestFeeder = null;
            _closestFeedBox = null;
            _consumeTargetItem = null;
        }

        /// <summary>
        ///     Drops the feed targets and the search state before the scope destroys the component.
        ///     No item is held here: a feeding call takes an item out and feeds it within the same
        ///     call, so there is nothing to put back.
        /// </summary>
        public void OnScopeDetaching()
        {
            _closestFeedBox = null;
            _closestFeeder = null;
            _consumeTargetItem = null;
            _consumeSearchTimer = 0f;
            _feedBoxBuffer.Clear();
        }

        /// <summary>
        ///     Only reads the animal's surroundings, but it still opens with the stop request, so a
        ///     wild animal is back on the game's own targeting as soon as the module is asked to
        ///     stop. false leaves the game's result as it is.
        /// </summary>
        public static bool CancelAttackOnFeedBox(BaseAI baseAI, StaticTarget target)
        {
            if (!Module.Running) return false;

            // AutomaticFeeding lives on the same GameObject as the patched AI, so resolve
            // it directly instead of scanning every animal in the world. The old
            // AllInstance.Any scan made each per-frame postfix O(animals), i.e.
            // O(animals^2) across all animals each frame.
            var feeding = baseAI.GetComponent<AutomaticFeeding>();
            return feeding != null && feeding.CancelAttackOnFeedBox(target);
        }

        /// <summary>
        ///     The entry point that moves an item (R8.11). It reads the stop request once, as its
        ///     first statement, and nothing it reaches reads it again. A call claims a feed box,
        ///     takes one item out of it or out of the local player's inventory, and hands the item
        ///     to the animal's consume callback within the same call. The claim, the removal and
        ///     the callback all run code that is not ours, and a callback there can change the
        ///     setting, and so raise the stop request, before the call returns. A second check after
        ///     the item left its inventory would lose it without feeding the animal, so there is
        ///     none, and nothing in the call reaches an entry point again. A call that passed the
        ///     gate finishes; the next one sees the stop, and false leaves the game's result as it
        ///     is.
        /// </summary>
        public static bool Feeding(MonsterAI monsterAI, Humanoid humanoid, float delta)
        {
            if (!Module.Running) return false;

            var feeding = monsterAI.GetComponent<AutomaticFeeding>();
            return feeding != null && feeding.Feeding(humanoid, delta);
        }

        private bool HasNetworkOwnership()
        {
            return Objects.HasValidOwnership(_character);
        }

        private bool CancelAttackOnFeedBox(StaticTarget target)
        {
            if (!HasNetworkOwnership()) return false;
            var animalType = _character.IsTamed() ? AnimalType.Tamed : AnimalType.Wild;
            if (!Logics.IsAllowToFeedFromContainer(animalType)) return false;

            var container = target.GetComponentInChildren<Container>();
            if (container == null || ContainerAccess.IsGrave(container)) return false;
            // Owner-side feeding also runs on dedicated servers where there is no
            // local player, so only enforce the player access model when a local
            // player exists; otherwise fall through to the owner-only mutation
            // gate at consume time.
            var localPlayer = Player.m_localPlayer;
            if (localPlayer != null && !ContainerAccess.CanUseContainer(localPlayer, container))
                return false;

            var inventory = container.GetInventory();
            if (inventory == null) return false;

            return FindConsumable(inventory) != null;
        }

        private bool Feeding(Humanoid humanoid, float delta)
        {
            if (!HasNetworkOwnership()) return false;
            if (_monsterAI.m_consumeItems == null || _monsterAI.m_consumeItems.Count == 0)
                return false;

            _consumeSearchTimer += delta;
            if (_consumeSearchTimer >= _monsterAI.m_consumeSearchInterval)
            {
                _consumeSearchTimer = 0f;
                if (!_tamable.IsHungry())
                {
                    // Drop any targets found on an earlier pass so a later non-search
                    // frame cannot consume food while the animal is no longer hungry.
                    _closestFeedBox = null;
                    _closestFeeder = null;
                    _consumeTargetItem = null;
                    return false;
                }

                UpdateFeedInfo();
            }

            if (!_tamable.IsHungry())
            {
                _closestFeedBox = null;
                _closestFeeder = null;
                _consumeTargetItem = null;
                return false;
            }

            if (!_closestFeedBox && !_closestFeeder) return false;
            if (_consumeTargetItem == null) return false;

            var feedBoxFound = (bool)_closestFeedBox;
            var inventory = feedBoxFound
                ? _closestFeedBox.GetInventory()
                : _closestFeeder.GetInventory();
            if (inventory == null) return false;
            if (!inventory.HaveItem(_consumeTargetItem.m_shared.m_name)) return false;

            var canEating = true;
            if (Config.NeedGetCloseToEatTheFeed)
            {
                canEating = false;
                var position = feedBoxFound
                    ? _closestFeedBox.transform.position
                    : _closestFeeder.transform.position;
                // 1f is added to account for the width of the container
                var consumeRange = _monsterAI.m_consumeRange + 1f;
                if (MoveTo(delta, position, consumeRange, false))
                {
                    LookAt(position);
                    canEating = IsLookingAt(position, 20f);
                }
            }

            if (canEating)
            {
                // Owner-only removal: claim ownership, then bail unless this peer
                // can mutate the feed box right now, so an item is never removed
                // from a chest a player has open or another peer/server owns
                // (which would desync the container). Mirrors
                // SeedPool.RemoveFromContainer. Player feeders are pre-filtered to
                // the local player in FindFeeder, so their inventory is always
                // authoritative and needs no extra guard.
                if (feedBoxFound)
                {
                    if (!ContainerAccess.TryPrepareContainerMutationServerAware(
                            Player.m_localPlayer, _closestFeedBox))
                        return true;
                    // Re-fetch the inventory after taking ownership so the
                    // post-ownership (possibly ZDO-reloaded) instance is mutated.
                    inventory = _closestFeedBox.GetInventory();
                    if (inventory == null) return true;
                }

                // Re-resolve the live item by name: the target captured before the
                // ownership claim can be a stale ItemData that RemoveOneItem would
                // silently no-op on.
                var item = inventory.GetItem(_consumeTargetItem.m_shared.m_name);
                if (item != null && inventory.RemoveOneItem(item))
                {
                    var dropPrefab = _consumeTargetItem.m_dropPrefab;
                    if (dropPrefab != null)
                        _monsterAI.m_onConsumedItem?.Invoke(dropPrefab.GetComponent<ItemDrop>());
                    humanoid.m_consumeItemEffects.Create(_baseAI.transform.position,
                        Quaternion.identity);
                    var animator = Reflections.GetField<ZSyncAnimation>(_baseAI, "m_animator");
                    if (animator != null) animator.SetTrigger("consume");

                    _closestFeeder = null;
                    _closestFeedBox = null;
                    _consumeTargetItem = null;
                }
            }

            return true;
        }

        private void UpdateFeedInfo()
        {
            var range = Config.FeedSearchRange;
            if (range <= 0f)
                range = _monsterAI.m_consumeSearchRange;

            _closestFeeder = null;
            _closestFeedBox = null;
            _consumeTargetItem = null;

            var animalType = _character.IsTamed() ? AnimalType.Tamed : AnimalType.Wild;
            if (Logics.IsAllowToFeedFromContainer(animalType))
                FindFeedBox(range);

            if (_closestFeedBox == null && Logics.IsAllowToFeedFromPlayer(animalType))
                FindFeeder(range);
        }

        private void FindFeedBox(float range)
        {
            var needGetClose = Config.NeedGetCloseToEatTheFeed;
            var origin = _baseAI.transform.position;
            var closest = float.MaxValue;

            ContainerCache.Fill(_feedBoxBuffer);
            for (var i = 0; i < _feedBoxBuffer.Count; i++)
            {
                var container = _feedBoxBuffer[i];
                if (container == null) continue;

                var position = container.transform.position;
                var distance = Vector3.Distance(position, origin);

                if (distance > range || distance >= closest) continue;
                if (ContainerAccess.IsGrave(container)) continue;
                if (needGetClose && !HavePath(position)) continue;
                var localPlayer = Player.m_localPlayer;
                if (localPlayer != null && !ContainerAccess.CanUseContainer(localPlayer, container))
                    continue;

                var inventory = container.GetInventory();
                if (inventory == null) continue;

                var item = FindConsumable(inventory);
                if (item == null) continue;

                closest = distance;
                _closestFeedBox = container;
                _consumeTargetItem = item;
            }
        }

        private void FindFeeder(float searchRange)
        {
            // Only the local player's inventory can be mutated authoritatively
            // on this client; mutating a remote player's inventory would not
            // replicate to its owning peer.
            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return;

            var needGetClose = Config.NeedGetCloseToEatTheFeed;
            var origin = _baseAI.transform.position;

            var position = localPlayer.transform.position;
            var distance = Vector3.Distance(position, origin);
            if (distance > searchRange) return;
            if (needGetClose && !HavePath(position)) return;

            var item = FindConsumable(localPlayer.GetInventory());
            if (item == null) return;

            _closestFeeder = localPlayer;
            _consumeTargetItem = item;
        }

        private bool CanConsume(ItemDrop.ItemData item)
        {
            return ConsumeCheck != null
                ? ConsumeCheck(_monsterAI, item)
                : Reflections.InvokeMethod<bool>(_monsterAI, "CanConsume", item);
        }

        private ItemDrop.ItemData FindConsumable(Inventory inventory)
        {
            var items = inventory.GetAllItems();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (CanConsume(item)) return item;
            }

            return null;
        }

        private bool HavePath(Vector3 target)
        {
            return Reflections.InvokeMethod<bool>(_baseAI, "HavePath", target);
        }

        private bool MoveTo(float dt, Vector3 point, float dist, bool run)
        {
            return Reflections.InvokeMethod<bool>(_baseAI, "MoveTo", dt, point, dist, run);
        }

        private void LookAt(Vector3 point)
        {
            Reflections.InvokeMethod(_baseAI, "LookAt", point);
        }

        private bool IsLookingAt(Vector3 point, float minAngle)
        {
            // BaseAI.IsLookingAt(Vector3 point, float minAngle, bool inverted = false): Traverse
            // resolves the method from the runtime types of the arguments passed, and a defaulted
            // parameter is never filled in for us, so the arity must match or resolution fails and
            // the call throws. false is the vanilla default.
            return Reflections.InvokeMethod<bool>(_baseAI, "IsLookingAt", point, minAngle,
                false);
        }
    }
}
