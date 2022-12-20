using System;
using System.Collections.Generic;
using System.Linq;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMining
{
    [DisallowMultipleComponent]
    public class AutomaticMining : MonoBehaviour
    {
        private static readonly IList<AutomaticMining> AllInstance;
        private static readonly RaycastHit[] RaycastHitBuffer;
        private static readonly Lazy<int> MineralMaskLazy;

        private Component _component;
        private ZNetView _zNetView;

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
            AllInstance.Remove(this);

            _component = null;
            _zNetView = null;
        }

        public static void TryMining(Player player)
        {
            //TODO: Improve position taking method. Petrified bone and soft tissue position is not being obtained correctly.
            var origin = player.transform.position;
            var automaticMining = (from x in AllInstance
                    let distance = Vector3.Distance(origin, x.transform.position)
                    where x._zNetView.IsValid() && x._zNetView.IsOwner() &&
                          IsAllowMiningMinerals(x._component)
                    orderby distance
                    select x)
                .FirstOrDefault();

            if (automaticMining)
                automaticMining.Mining(player);

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

        private void Mining(Player player)
        {
            var pickaxe = GetPickaxe(player);
            if (pickaxe == null) return;

            var attack = pickaxe.m_shared.m_attack;
            var range = Config.MiningRange > 0 ? Config.MiningRange : attack.m_attackRange;

            switch (_component)
            {
                case MineRock rock:
                    if (rock.m_minToolTier <= pickaxe.m_shared.m_toolTier)
                        Mining(player, pickaxe, range, rock,
                            Reflections.GetField<Collider[]>(rock, "m_hitAreas"));
                    break;
                case MineRock5 rock5:
                    if (rock5.m_minToolTier <= pickaxe.m_shared.m_toolTier)
                        Mining(player, pickaxe, range, rock5,
                            rock5.gameObject.GetComponentsInChildren<Collider>());
                    break;
                case Destructible destructible:
                    if (destructible.m_minToolTier <= pickaxe.m_shared.m_toolTier)
                        Mining(player, pickaxe, range, destructible,
                            destructible.GetComponentInChildren<Collider>());
                    break;
            }
        }

        private static void Mining(Player player, ItemDrop.ItemData pickaxe, float range,
            IDestructible parent, params Collider[] colliders)
        {
            if (colliders == null || colliders.Length == 0) return;

            var equipments = player.GetInventory().GetEquipedtems();
            var parts = (from collider in colliders
                let result = GetHitPosition(player.m_eye.position, collider, range)
                where result.Position != Vector3.zero &&
                      IsMineableMineral(collider, equipments)
                orderby result.Distance
                select (collider, result.Position)).Take(3).ToList();
            foreach (var (collider, hit) in parts)
            {
                if (!IsValidPickaxe(player, pickaxe)) continue;
                if (Vector3.Distance(player.m_eye.position, hit) > Config.MiningRange) continue;

                CreateHitEffect(pickaxe, hit);
                parent.Damage(CreateHitData(player, pickaxe, hit, collider, parts.Count));
                UsePickaxe(player, pickaxe);

                Automatics.Logger.Debug(() =>
                {
                    var name = "Unknown";
                    if (parent is Component component)
                        name = Automatics.L10N.Translate(Objects.GetName(component));
                    return $"Mining: [name: {name}, pos: {collider.bounds.center}]";
                });
            }
        }

        private static bool IsValidPickaxe(Player player, ItemDrop.ItemData pickaxe)
        {
            if (pickaxe.m_shared.m_skillType != Skills.SkillType.Pickaxes) return false;
            if (Config.NeedToEquipPickaxe && !player.IsItemEquiped(pickaxe)) return false;
            return pickaxe.m_durability > 0f;
        }

        private static ItemDrop.ItemData GetPickaxe(Player player)
        {
            var weapon = player.GetCurrentWeapon();
            if (weapon != null)
                if (weapon.m_durability > 0f &&
                    weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes)
                    return weapon;

            if (Config.NeedToEquipPickaxe) return null;

            return (from x in player.GetInventory().GetAllItems()
                where x.m_durability > 0f &&
                      x.m_shared.m_skillType == Skills.SkillType.Pickaxes
                orderby x.m_shared.m_toolTier
                select x).FirstOrDefault();
        }

        private static (Vector3 Position, float Distance) GetHitPosition(Vector3 origin,
            Collider target, float range)
        {
            var direction = target.bounds.center - origin;
            Physics.RaycastNonAlloc(origin, direction, RaycastHitBuffer, range, MineralMask);
            foreach (var hit in RaycastHitBuffer.Where(hit =>
                         hit.collider == target && hit.distance <= range))
                return (hit.point, hit.distance);
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
                m_toolTier = shared.m_toolTier,
                m_statusEffect = shared.m_attackStatusEffect != null
                    ? shared.m_attackStatusEffect.name
                    : "",
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
                pickaxe.m_durability -= shared.m_useDurabilityDrain;

            player.AddNoise(attack.m_attackHitNoise);
            player.RaiseSkill(shared.m_skillType);
        }
    }
}