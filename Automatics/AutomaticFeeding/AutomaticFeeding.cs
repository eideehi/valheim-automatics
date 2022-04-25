using System.Collections.Generic;
using System.Linq;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticFeeding
{
    internal class AutomaticFeeding : MonoBehaviour
    {
        private static readonly List<AutomaticFeeding> AutomaticFeedings;

        static AutomaticFeeding()
        {
            AutomaticFeedings = new List<AutomaticFeeding>();
        }

        private BaseAI _baseAI;
        private MonsterAI _monsterAI;
        private Tameable _tamable;
        private Character _character;

        private Humanoid _feeder;
        private Container _feedBox;
        private ItemDrop.ItemData _feedItem;

        public static bool IsFeedBox(BaseAI baseAI, StaticTarget target)
        {
            return AutomaticFeedings.Any(x => (x._baseAI == baseAI) && x.IsFeedBox(target));
        }

        public static bool Feeding(MonsterAI monsterAI, Humanoid humanoid, float delta)
        {
            return AutomaticFeedings.Any(x => (x._monsterAI == monsterAI) && x.Feeding(humanoid, delta));
        }

        private void Awake()
        {
            _baseAI = GetComponent<BaseAI>();
            _monsterAI = GetComponent<MonsterAI>();
            _tamable = GetComponent<Tameable>();
            _character = GetComponent<Character>();

            AutomaticFeedings.Add(this);
        }

        private void OnDestroy()
        {
            AutomaticFeedings.Remove(this);

            _baseAI = null;
            _monsterAI = null;
            _tamable = null;
            _character = null;
            _feeder = null;
            _feedBox = null;
            _feedItem = null;
        }

        private bool IsFeedBox(StaticTarget target)
        {
            var container = target.GetComponentInChildren<Container>();
            if (container == null) return false;

            return container == _feedBox || container.GetInventory().GetAllItems().Any(CanConsume);
        }

        private bool Feeding(Humanoid humanoid, float delta)
        {
            if (_monsterAI.m_consumeItems == null || _monsterAI.m_consumeItems.Count == 0) return false;

            if (Reflection.GetField<float>(_monsterAI, "m_consumeSearchTimer") == 0f)
                UpdateFeedInfo();

            if (!_tamable.IsHungry()) return false;
            if (_feedBox == null && _feeder == null) return false;
            if (_feedItem == null) return false;

            var foundFeedBox = _feedBox != null;
            var inventory = foundFeedBox ? _feedBox.GetInventory() : _feeder.GetInventory();
            if (!inventory.HaveItem(_feedItem.m_shared.m_name)) return false;

            var canEating = true;
            if (Config.NeedGetCloseToEatTheFeed)
            {
                canEating = false;
                var position = foundFeedBox ? _feedBox.transform.position : _feeder.transform.position;
                var consumeRange = _monsterAI.m_consumeRange + 1f;
                if (MoveTo(delta, position, consumeRange, false))
                {
                    LookAt(position);
                    canEating = IsLookingAt(position, 20f);
                }
            }

            if (canEating && inventory.RemoveOneItem(_feedItem))
            {
                _monsterAI.m_onConsumedItem?.Invoke(null);
                humanoid.m_consumeItemEffects.Create(_baseAI.transform.position, Quaternion.identity);
                Reflection.GetField<ZSyncAnimation>(_baseAI, "m_animator").SetTrigger("consume");
            }

            return true;
        }

        private void UpdateFeedInfo()
        {
            var range = Config.FeedSearchRange;
            if (range <= 0f)
                range = _monsterAI.m_consumeSearchRange;

            _feeder = null;
            _feedBox = null;
            _feedItem = null;

            var type = _character.IsTamed() ? Animal.Tamed : Animal.Wild;
            if ((Config.AllowToFeedFromContainer & type) != 0)
                FindFeedBox(range);

            if (_feedBox == null)
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
                _feedBox = container;
                _feedItem = item;
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
                _feeder = player;
                _feedItem = item;
            }
        }

        private bool CanConsume(ItemDrop.ItemData item)
        {
            return Reflection.InvokeMethod<bool>(_monsterAI, "CanConsume", item);
        }

        private bool HavePath(Vector3 target)
        {
            return Reflection.InvokeMethod<bool>(_baseAI, "HavePath", target);
        }

        private bool MoveTo(float dt, Vector3 point, float dist, bool run)
        {
            return Reflection.InvokeMethod<bool>(_baseAI, "MoveTo", dt, point, dist, run);
        }

        private void LookAt(Vector3 point)
        {
            Reflection.InvokeMethod(_baseAI, "LookAt", point);
        }

        private bool IsLookingAt(Vector3 point, float minAngle)
        {
            return Reflection.InvokeMethod<bool>(_baseAI, "IsLookingAt", point, minAngle);
        }
    }
}