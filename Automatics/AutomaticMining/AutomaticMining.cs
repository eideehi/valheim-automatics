using ModUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Automatics.AutomaticMining
{
    using Object = Valheim.Object;

    internal static class AutomaticMining
    {
        private static readonly Collider[] ColliderBuffer;
        private static readonly Lazy<int> MineralMaskLazy;

        private static int MineralMask => MineralMaskLazy.Value;

        static AutomaticMining()
        {
            ColliderBuffer = new Collider[512];
            MineralMaskLazy = new Lazy<int>(() => LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
                "piece_nonsolid", "hitbox", "vehicle"));
        }

        private static float _lastRunningTime;

        public static void Run(Player player, bool takeInput)
        {
            if (!Config.EnableAutomaticMining) return;
            if (player == null || Player.m_localPlayer != player) return;

            if (Config.MiningKey.MainKey != KeyCode.None)
            {
                if (!Config.MiningKey.IsDown()) return;
            }
            else if (Time.time - _lastRunningTime < Config.MiningInterval)
            {
                return;
            }

            var pickaxe = GetPickaxe(player);
            if (pickaxe == null) return;

            var attack = pickaxe.m_shared.m_attack;
            var range = Config.MiningRange > 0 ? Config.MiningRange : attack.m_attackRange;

            var done = new HashSet<Vector3>();
            foreach (var mineral in GetNearbyMinerals(player.transform.position, range))
            {
                if (!done.Add(mineral.transform.position)) continue;

                if (mineral is MineRock rock)
                    Mining(player, pickaxe, range, rock);
                else if (mineral is MineRock5 rock5)
                    Mining(player, pickaxe, range, rock5);
                else if (mineral is Destructible destructible)
                    Mining(player, pickaxe, range, destructible);
            }

            _lastRunningTime = Time.time;
        }

        private static ItemDrop.ItemData GetPickaxe(Player player)
        {
            if (!Config.NeedToEquipPickaxe)
                return (from x in player.GetInventory().GetAllItems()
                    where x.m_shared.m_skillType == Skills.SkillType.Pickaxes
                    orderby x.m_shared.m_toolTier
                    select x).FirstOrDefault();

            var weapon = player.GetCurrentWeapon();
            return weapon != null && weapon.m_shared.m_skillType == Skills.SkillType.Pickaxes ? weapon : null;
        }

        private static IEnumerable<MonoBehaviour> GetNearbyMinerals(Vector3 origin, float range)
        {
            return from x in Objects.GetInsideSphere(origin, range, IsMineral, ColliderBuffer, MineralMask)
                orderby x.Item3
                select x.Item2;
        }

        private static MonoBehaviour IsMineral(Collider collider)
        {
            var obj = collider.GetComponentInParent<IDestructible>() as MonoBehaviour;
            return Object.GetMineralDeposit(Objects.GetName(obj), out _) ? obj : null;
        }

        private static void Mining(Player player, ItemDrop.ItemData pickaxe, float range, MineRock rock)
        {
            if (rock.m_minToolTier > pickaxe.m_shared.m_toolTier) return;

            var areas = Reflections.GetField<Collider[]>(rock, "m_hitAreas");
            if (areas == null) return;

            var origin = player.transform.position;
            var minerals = areas.Where(x => IsInRange(origin, x, range)).ToList();

            foreach (var mineral in minerals)
            {
                Automatics.Logger.Debug(() =>
                {
                    var name = Objects.GetName(rock);
                    return
                        $"Mining: [type: MineRock, name: {name}({Automatics.L10N.Translate(name)}), pos: {mineral.bounds.center}]";
                });

                CreateHitEffect(pickaxe, mineral.bounds.center);
                rock.Damage(CreateHitData(player, pickaxe, mineral, minerals.Count));
                UsePickaxe(player, pickaxe);
            }
        }

        private static void Mining(Player player, ItemDrop.ItemData pickaxe, float range, MineRock5 rock)
        {
            if (rock.m_minToolTier > pickaxe.m_shared.m_toolTier) return;

            var areas = rock.gameObject.GetComponentsInChildren<Collider>();
            if (areas == null) return;

            var origin = player.transform.position;
            var equippedItems = player.GetInventory().GetEquipedtems();
            var minerals = areas.Where(x =>
            {
                if (!IsInRange(origin, x, range)) return false;
                if (x.bounds.max.y >= ZoneSystem.instance.GetGroundHeight(x.bounds.center)) return true;
                if (!Config.AllowMiningUndergroundMinerals) return false;
                return !Config.NeedToEquipWishboneForMiningUndergroundMinerals ||
                       equippedItems.Select(y => y.m_shared.m_name).Any(y => y == "$item_wishbone");
            }).ToList();

            foreach (var mineral in minerals)
            {
                Automatics.Logger.Debug(() =>
                {
                    var name = Objects.GetName(rock);
                    return
                        $"Mining: [type: MineRock5, name: {name}({Automatics.L10N.Translate(name)}), pos: {mineral.bounds.center}]";
                });

                CreateHitEffect(pickaxe, mineral.bounds.center);
                rock.Damage(CreateHitData(player, pickaxe, mineral, minerals.Count));
                UsePickaxe(player, pickaxe);
            }
        }

        private static void Mining(Player player, ItemDrop.ItemData pickaxe, float range, Destructible destructible)
        {
            if (destructible.m_minToolTier > pickaxe.m_shared.m_toolTier) return;

            var collider = destructible.GetComponentInChildren<Collider>();
            if (collider == null || !IsInRange(player.transform.position, collider, range)) return;

            Automatics.Logger.Debug(() =>
            {
                var name = Objects.GetName(destructible);
                return
                    $"Mining: [type: Destructible, name: {name}({Automatics.L10N.Translate(name)}), pos: {collider.bounds.center}]";
            });

            CreateHitEffect(pickaxe, collider.bounds.center);
            destructible.Damage(CreateHitData(player, pickaxe, collider, 1));
            UsePickaxe(player, pickaxe);
        }

        private static bool IsInRange(Vector3 origin, Collider collider, float range)
        {
            if (!Physics.Linecast(origin, collider.bounds.center, out var hitInfo, MineralMask)) return false;
            return hitInfo.collider == collider && hitInfo.distance <= range;
        }

        private static void CreateHitEffect(ItemDrop.ItemData pickaxe, Vector3 pos)
        {
            pickaxe.m_shared.m_hitEffect.Create(pos, Quaternion.identity);
            pickaxe.m_shared.m_attack.m_hitEffect.Create(pos, Quaternion.identity);
        }

        private static HitData CreateHitData(Player player, ItemDrop.ItemData pickaxe, Collider collider, int hitCount)
        {
            var shared = pickaxe.m_shared;

            var hitData = new HitData
            {
                m_toolTier = shared.m_toolTier,
                m_statusEffect = shared.m_attackStatusEffect != null ? shared.m_attackStatusEffect.name : "",
                m_skill = shared.m_skillType,
                m_damage = pickaxe.GetDamage(),
                m_point = collider.bounds.center,
                m_hitCollider = collider,
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