using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFeeding
{
    [DisallowMultipleComponent]
    internal class AutomaticFeeding : MonoBehaviour
    {
        private static readonly IList<AutomaticFeeding> AllInstance;

        private Tameable _tamable;
        private Character _character;
        private MonsterAI _monsterAI;
        private BaseAI _baseAI;
        private float _consumeSearchTimer;
        private Container _closestFeedBox;
        private Humanoid _closestFeeder;
        private ItemDrop.ItemData _consumeTargetItem;

        static AutomaticFeeding()
        {
            AllInstance = new List<AutomaticFeeding>();
        }

        private void Awake()
        {
            _tamable = GetComponent<Tameable>();
            _character = GetComponent<Character>();
            _monsterAI = GetComponent<MonsterAI>();
            _baseAI = GetComponent<BaseAI>();

            AllInstance.Add(this);
        }

        private void OnDestroy()
        {
            AllInstance.Remove(this);

            _baseAI = null;
            _monsterAI = null;
            _tamable = null;
            _character = null;
            _closestFeeder = null;
            _closestFeedBox = null;
            _consumeTargetItem = null;
        }

        public static bool CancelAttackOnFeedBox(BaseAI baseAI, StaticTarget target)
        {
            return AllInstance.Any(x => x._baseAI == baseAI && x.CancelAttackOnFeedBox(target));
        }

        public static bool Feeding(MonsterAI monsterAI, Humanoid humanoid, float delta)
        {
            return AllInstance.Any(x =>
                x._monsterAI == monsterAI && x.Feeding(humanoid, delta));
        }

        private bool CancelAttackOnFeedBox(StaticTarget target)
        {
            if (!_character.IsTamed() && !Logics.IsAllowToFeedFromContainer(AnimalType.Wild))
                return false;

            var container = target.GetComponentInChildren<Container>();
            if (container == null) return false;

            return ReferenceEquals(container, _closestFeedBox) ||
                   container.GetInventory().GetAllItems().Any(CanConsume);
        }

        private bool Feeding(Humanoid humanoid, float delta)
        {
            if (_monsterAI.m_consumeItems == null || !_monsterAI.m_consumeItems.Any())
                return false;

            _consumeSearchTimer += delta;
            if (_consumeSearchTimer >= _monsterAI.m_consumeSearchInterval)
            {
                _consumeSearchTimer = 0f;
                if (!_tamable.IsHungry()) return false;
                UpdateFeedInfo();
            }

            if (!_closestFeedBox && !_closestFeeder) return false;
            if (_consumeTargetItem == null) return false;

            var feedBoxFound = (bool)_closestFeedBox;
            var inventory = feedBoxFound
                ? _closestFeedBox.GetInventory()
                : _closestFeeder.GetInventory();
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

            if (canEating && inventory.RemoveOneItem(_consumeTargetItem))
            {
                _monsterAI.m_onConsumedItem?.Invoke(
                    _consumeTargetItem.m_dropPrefab.GetComponent<ItemDrop>());
                humanoid.m_consumeItemEffects.Create(_baseAI.transform.position,
                    Quaternion.identity);
                Reflections.GetField<ZSyncAnimation>(_baseAI, "m_animator").SetTrigger("consume");

                _closestFeeder = null;
                _closestFeedBox = null;
                _consumeTargetItem = null;
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

            foreach (var container in ContainerCache.GetAllInstance())
            {
                var position = container.transform.position;
                var distance = Vector3.Distance(position, origin);

                if (distance > range || distance >= closest) continue;
                if (needGetClose && !HavePath(position)) continue;

                var item = container.GetInventory().GetAllItems().FirstOrDefault(CanConsume);
                if (item == null) continue;

                closest = distance;
                _closestFeedBox = container;
                _consumeTargetItem = item;
            }
        }

        private void FindFeeder(float searchRange)
        {
            var needGetClose = Config.NeedGetCloseToEatTheFeed;
            var origin = _baseAI.transform.position;
            var closest = float.MaxValue;

            foreach (var player in Player.GetAllPlayers())
            {
                var position = player.transform.position;
                var distance = Vector3.Distance(position, origin);

                if (distance > searchRange || distance >= closest) continue;
                if (needGetClose && !HavePath(position)) continue;

                var item = player.GetInventory().GetAllItems().FirstOrDefault(CanConsume);
                if (item == null) continue;

                closest = distance;
                _closestFeeder = player;
                _consumeTargetItem = item;
            }
        }

        private bool CanConsume(ItemDrop.ItemData item)
        {
            return Reflections.InvokeMethod<bool>(_monsterAI, "CanConsume", item);
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
            return Reflections.InvokeMethod<bool>(_baseAI, "IsLookingAt", point, minAngle);
        }
    }
}