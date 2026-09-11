using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMining
{
    [DisallowMultipleComponent]
    public class AutomaticMining : MonoBehaviour, IScopedComponent
    {
        private static readonly IList<AutomaticMining> AllInstance;
        private static readonly RaycastHit[] RaycastHitBuffer;
        private static readonly Lazy<int> MineralMaskLazy;

        private Component _component;
        private ZNetView _zNetView;

        internal static int AllInstanceCount => AllInstance.Count;
        public int ScopeGeneration { get; set; }

        static AutomaticMining()
        {
            AllInstance = new List<AutomaticMining>();
            RaycastHitBuffer = new RaycastHit[128];
            MineralMaskLazy = new Lazy<int>(() =>
                LayerMask.GetMask("piece", "Default", "static_solid", "Default_small"));
        }

        private static int MineralMask => MineralMaskLazy.Value;

        private void Awake()
        {
            _component = GetDestructibleComponent();
            if (!_component)
                throw new Exception("Component must inherit IDestructible.");

            if (!Objects.GetZNetView(_component, out _zNetView))
                throw new Exception("Component does not have ZNetView");

            AllInstance.Add(this);
        }

        private void OnDestroy()
        {
            Unregister();
        }

        public void OnScopeDetaching()
        {
            Unregister();
        }

        private void Unregister()
        {
            AllInstance.Remove(this);

            _component = null;
            _zNetView = null;
        }

        public static void TryMining(Player player)
        {
            //TODO: Improve position taking method. Petrified bone and soft tissue position is not being obtained correctly.
            var origin = player.transform.position;
            // Filter to owned, mineable minerals before measuring distance: AutomaticMining
            // is attached to every Destructible in the world (barrels, crates, ...), so the
            // old `let distance` ran Vector3.Distance for all of them each tick. Keep ToList
            // since Mining() can destroy a node and mutate AllInstance mid-iteration.
            //
            // A cheap squared-distance pre-reject runs before the per-instance name match
            // and the sort. The margin is deliberately generous: large MineRock/MineRock5
            // nodes (silver vein, obsidian, petrified bone/soft tissue) have a root pivot
            // far from their hit colliders, and the precise per-collider eye->hit check
            // inside Mining() still enforces the exact range, so an over-generous margin
            // only costs a few extra name matches, never a missed mine.
            var maxRange = Config.MiningRange > 0 ? Config.MiningRange : 32;
            var cullSqr = (maxRange + 24f) * (maxRange + 24f);
            var automaticMinings = (from x in AllInstance
                    where x._zNetView.IsValid() &&
                          x._zNetView.HasOwner() &&
                          (x.transform.position - origin).sqrMagnitude <= cullSqr &&
                          IsAllowMiningMinerals(x._component)
                    orderby Vector3.Distance(origin, x.transform.position)
                    select x).ToList();

            foreach (var automaticMining in automaticMinings)
                if (automaticMining.Mining(player))
                    return;

            bool IsAllowMiningMinerals(Component component)
            {
                var name = Objects.GetName(component);
                return ValheimObject.Mineral.GetIdentify(name, out var identifier) &&
                       Config.AllowMiningMinerals.Contains(identifier);
            }
        }

        private Component GetDestructibleComponent()
        {
            switch (GetComponent<IDestructible>())
            {
                case MineRock rock: return rock;
                case MineRock5 rock5: return rock5;
                case Destructible destructible: return destructible;
                case Component component: return component;
                default: return null;
            }
        }

        private bool Mining(Player player)
        {
            if (!GetMiningTarget(out var parent, out var minToolTier))
                return false;

            var pickaxe = GetPickaxe(player, minToolTier);
            if (pickaxe == null) return false;

            var colliders = GetColliders(parent);
            var range = GetMiningRange(pickaxe);
            return Mining(player, pickaxe, range, parent, colliders);
        }

        private bool GetMiningTarget(out IDestructible parent, out int minToolTier)
        {
            switch (_component)
            {
                case MineRock rock:
                    parent = rock;
                    minToolTier = rock.m_minToolTier;
                    return true;
                case MineRock5 rock5:
                    parent = rock5;
                    minToolTier = rock5.m_minToolTier;
                    return true;
                case Destructible destructible:
                    parent = destructible;
                    minToolTier = destructible.m_minToolTier;
                    return true;
                default:
                    parent = null;
                    minToolTier = 0;
                    return false;
            }
        }

        private static Collider[] GetColliders(IDestructible parent)
        {
            switch (parent)
            {
                case MineRock rock:
                    return Reflections.GetField<Collider[]>(rock, "m_hitAreas");
                case MineRock5 rock5:
                    return rock5.gameObject.GetComponentsInChildren<Collider>();
                case Destructible destructible:
                    var collider = destructible.GetComponentInChildren<Collider>();
                    return collider != null
                        ? new[] { collider }
                        : System.Array.Empty<Collider>();
                default:
                    return null;
            }
        }

        private static bool Mining(Player player, ItemDrop.ItemData pickaxe, float range,
            IDestructible parent, params Collider[] colliders)
        {
            if (colliders == null || colliders.Length == 0) return false;

            var equipments = player.GetInventory().GetEquippedItems();
            var parts = (from collider in colliders
                where collider != null
                let result = GetHitPosition(player.m_eye.position, collider, range)
                where result.Position != Vector3.zero &&
                      IsMineableMineral(collider, equipments)
                orderby result.Distance
                select (collider, result.Position)).Take(3).ToList();
            var mined = false;
            foreach (var (collider, hit) in parts)
            {
                if (!IsValidPickaxe(player, pickaxe)) continue;
                if (Vector3.Distance(player.m_eye.position, hit) > range) continue;

                CreateHitEffect(pickaxe, hit);
                parent.Damage(CreateHitData(player, pickaxe, hit, collider, parts.Count));
                UsePickaxe(player, pickaxe);
                mined = true;

                Automatics.Logger.Debug(() =>
                {
                    var name = "Unknown";
                    if (parent is Component component)
                        name = Automatics.L10N.Translate(Objects.GetName(component));
                    return $"Mining: [name: {name}, pos: {collider.bounds.center}]";
                });
            }

            return mined;
        }

        private static bool IsValidPickaxe(Player player, ItemDrop.ItemData pickaxe)
        {
            if (pickaxe.m_shared.m_skillType != Skills.SkillType.Pickaxes) return false;
            if (Config.NeedToEquipPickaxe && !player.IsItemEquiped(pickaxe)) return false;
            return pickaxe.m_durability > 0f;
        }

        private static ItemDrop.ItemData GetPickaxe(Player player, int minToolTier)
        {
            var weapon = player.GetCurrentWeapon();
            if (weapon != null)
                if (weapon.m_durability > 0f &&
                    weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes &&
                    weapon.m_shared.m_toolTier >= minToolTier)
                    return weapon;

            if (Config.NeedToEquipPickaxe) return null;

            return (from x in player.GetInventory().GetAllItems()
                where x.m_durability > 0f &&
                      x.m_shared.m_skillType == Skills.SkillType.Pickaxes &&
                      x.m_shared.m_toolTier >= minToolTier
                orderby x.m_shared.m_toolTier
                select x).FirstOrDefault();
        }

        private static float GetMiningRange(ItemDrop.ItemData pickaxe)
        {
            var attack = pickaxe.m_shared.m_attack;
            return Config.MiningRange > 0 ? Config.MiningRange : attack.m_attackRange;
        }

        private static (Vector3 Position, float Distance) GetHitPosition(Vector3 origin,
            Collider target, float range)
        {
            var direction = target.bounds.center - origin;
            var hitCount = Physics.RaycastNonAlloc(origin, direction, RaycastHitBuffer, range,
                MineralMask);
            for (var i = 0; i < hitCount; i++)
            {
                var hit = RaycastHitBuffer[i];
                if (hit.collider == target && hit.distance <= range)
                    return (hit.point, hit.distance);
            }

            return (Vector3.zero, -1f);
        }

        private static bool IsMineableMineral(Collider mineral,
            IEnumerable<ItemDrop.ItemData> equipments)
        {
            if (mineral.bounds.max.y >= ZoneSystem.instance.GetGroundHeight(mineral.bounds.center))
                return true;
            if (!Config.AllowMiningUndergroundMinerals) return false;
            return !Config.NeedToEquipWishboneForUndergroundMinerals ||
                   equipments.Any(x => x.m_shared.m_name == "$item_wishbone");
        }

        private static void CreateHitEffect(ItemDrop.ItemData pickaxe, Vector3 pos)
        {
            pickaxe.m_shared.m_hitEffect.Create(pos, Quaternion.identity);
            pickaxe.m_shared.m_attack.m_hitEffect.Create(pos, Quaternion.identity);
        }

        private static HitData CreateHitData(Player player, ItemDrop.ItemData pickaxe,
            Vector3 hit, Collider collider, int hitCount)
        {
            var shared = pickaxe.m_shared;

            var hitData = new HitData
            {
                m_toolTier = (short)shared.m_toolTier,
                m_statusEffectHash = shared.m_attackStatusEffect != null
                    ? shared.m_attackStatusEffect.NameHash()
                    : 0,
                m_skill = shared.m_skillType,
                m_damage = pickaxe.GetDamage(),
                m_point = hit,
                m_hitCollider = collider
            };

            var attack = shared.m_attack;
            var randomSkillFactor = player.GetRandomSkillFactor(shared.m_skillType);
            if (attack.m_multiHit && attack.m_lowerDamagePerHit && hitCount > 1)
                randomSkillFactor /= hitCount * 0.75f;

            hitData.SetAttacker(player);
            hitData.m_damage.Modify(attack.m_damageMultiplier);
            hitData.m_damage.Modify(randomSkillFactor);
            hitData.m_damage.Modify((float)(1.0 + Mathf.Max(0, player.GetLevel() - 1) * 0.5));
            player.GetSEMan().ModifyAttack(shared.m_skillType, ref hitData);

            return hitData;
        }

        private static void UsePickaxe(Player player, ItemDrop.ItemData pickaxe)
        {
            var shared = pickaxe.m_shared;
            var attack = shared.m_attack;

            if (shared.m_useDurability)
                pickaxe.m_durability -= shared.m_useDurabilityDrain * Game.m_durabilityRate;

            player.AddNoise(attack.m_attackHitNoise);
            player.RaiseSkill(shared.m_skillType);
        }
    }
}
