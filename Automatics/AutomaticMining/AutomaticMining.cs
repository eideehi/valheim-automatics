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
        private static readonly List<Collider> SectionBuffer;
        private static readonly Lazy<int> MineralMaskLazy;

        private Component _component;
        private ZNetView _zNetView;

        // Where this component sits in AllInstance, or -1 when it is not registered. Order in
        // AllInstance means nothing: passes sort by distance and area mining visits every entry.
        private int _index = -1;

        internal static int AllInstanceCount => AllInstance.Count;
        public int ScopeGeneration { get; set; }

        static AutomaticMining()
        {
            AllInstance = new List<AutomaticMining>();
            RaycastHitBuffer = new RaycastHit[128];
            SectionBuffer = new List<Collider>();
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

            _index = AllInstance.Count;
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
            // The last entry takes this one's place. A second call, detach then destroy, finds -1.
            if (_index >= 0)
            {
                var last = AllInstance[AllInstance.Count - 1];
                AllInstance[_index] = last;
                last._index = _index;
                AllInstance.RemoveAt(AllInstance.Count - 1);
                _index = -1;
            }

            _component = null;
            _zNetView = null;
        }

        public static void TryMining(Player player)
        {
            //TODO: Improve position taking method. Petrified bone and soft tissue position is not being obtained correctly.
            // No target can be mined without a usable pickaxe, so none is looked for.
            if (!HasUsablePickaxe(player)) return;

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
                          IsAllowedMineral(x._component)
                    orderby Vector3.Distance(origin, x.transform.position)
                    select x).ToList();

            foreach (var automaticMining in automaticMinings)
                if (automaticMining.Mining(player))
                    return;
        }

        internal static bool IsAllowedMineral(Component component)
        {
            var name = Objects.GetName(component);
            return ValheimObject.Mineral.GetIdentify(name, out var identifier) &&
                   Config.AllowMiningMinerals.Contains(identifier);
        }

        // The targets area mining may add to a swing: every hit collider of an allowed mineral
        // within radius of center that the underground rule lets Automatic Mining mine, the same
        // registry and rules TryMining uses. The caller applies the tool tier, what the swing
        // already hit and the cap.
        internal static void CollectAreaTargets(Player player, Vector3 center, float radius,
            List<AreaMining.Target> targets)
        {
            // The same root-pivot margin as TryMining; the per-collider bounds check is exact.
            var cullSqr = (radius + 24f) * (radius + 24f);
            var equipments = player.GetInventory().GetEquippedItems();
            foreach (var automaticMining in AllInstance)
            {
                if (!automaticMining._zNetView.IsValid() || !automaticMining._zNetView.HasOwner())
                    continue;
                if ((automaticMining.transform.position - center).sqrMagnitude > cullSqr) continue;
                if (!IsAllowedMineral(automaticMining._component)) continue;
                if (!automaticMining.GetMiningTarget(out var parent, out var minToolTier)) continue;

                var colliders = GetColliders(parent);
                if (colliders == null) continue;
                foreach (var collider in colliders)
                {
                    // A destroyed rock section keeps its collider on an inactive object.
                    if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                        continue;

                    // The bounds only reject: a rotated or irregular rock fills little of them.
                    if (collider.bounds.SqrDistance(center) > radius * radius) continue;
                    if (!GetSurfacePoint(collider, center, out var point)) continue;
                    var distance = Vector3.Distance(point, center);
                    if (distance > radius) continue;
                    if (!IsMineableMineral(collider, equipments)) continue;

                    targets.Add(new AreaMining.Target(parent, automaticMining._component,
                        automaticMining._zNetView, collider, minToolTier, point, distance));
                }

                SectionBuffer.Clear();
            }
        }

        // A point on the collider's surface near origin. Unity finds the closest point only on
        // primitive and convex mesh colliders; on any other collider the point is where a ray from
        // origin toward the bounds centre enters it, which is never nearer than the closest point.
        // An origin inside such a collider finds no entry, and the collider is not reached.
        private static bool GetSurfacePoint(Collider collider, Vector3 origin, out Vector3 point)
        {
            if (collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider ||
                collider is MeshCollider mesh && mesh.convex)
            {
                point = collider.ClosestPoint(origin);
                return true;
            }

            var toCenter = collider.bounds.center - origin;
            var length = toCenter.magnitude;
            if (length > 0.001f &&
                collider.Raycast(new Ray(origin, toCenter / length), out var hit, length))
            {
                point = hit.point;
                return true;
            }

            point = default;
            return false;
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

        // The hit colliders of the target. Those of a MineRock5 are read into SectionBuffer, which
        // the caller empties once it has no more use for them.
        private static IReadOnlyList<Collider> GetColliders(IDestructible parent)
        {
            switch (parent)
            {
                case MineRock rock:
                    return Reflections.GetField<Collider[]>(rock, "m_hitAreas");
                case MineRock5 rock5:
                    rock5.gameObject.GetComponentsInChildren(false, SectionBuffer);
                    return SectionBuffer;
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
            IDestructible parent, IReadOnlyList<Collider> colliders)
        {
            if (colliders == null || colliders.Count == 0) return false;

            var equipments = player.GetInventory().GetEquippedItems();
            var parts = (from collider in colliders
                where collider != null
                let result = GetHitPosition(player.m_eye.position, collider, range)
                where result.Position != Vector3.zero &&
                      IsMineableMineral(collider, equipments)
                orderby result.Distance
                select (collider, result.Position)).Take(3).ToList();
            // The damage below can start area mining, which reads the buffer again.
            SectionBuffer.Clear();
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

        // Whether GetPickaxe finds a pickaxe for some tool tier: the current weapon, or, unless one
        // has to be equipped, an item of the inventory, that is a pickaxe with durability left.
        private static bool HasUsablePickaxe(Player player)
        {
            var weapon = player.GetCurrentWeapon();
            if (weapon != null && IsPickaxeWithDurability(weapon)) return true;

            if (Config.NeedToEquipPickaxe) return false;

            foreach (var item in player.GetInventory().GetAllItems())
                if (IsPickaxeWithDurability(item))
                    return true;

            return false;
        }

        private static bool IsPickaxeWithDurability(ItemDrop.ItemData item)
        {
            return item.m_durability > 0f && item.m_shared.m_skillType == Skills.SkillType.Pickaxes;
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
            // The ray lands on the target only if the target is on a layer of MineralMask and its
            // bounds reach within range of the origin: no point of a collider lies nearer than its
            // bounds.
            if ((MineralMask & (1 << target.gameObject.layer)) == 0 ||
                target.bounds.SqrDistance(origin) > range * range)
                return (Vector3.zero, -1f);

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
                m_itemWorldLevel = (byte)pickaxe.m_worldLevel,
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
