using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     Sits on every standing tree, small tree, fallen log and tree stump with a ZDO while the
    ///     module runs. A pass hits the nearest allowed one that a usable axe can hit, once, the way
    ///     a swing would, and stops there.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class AutomaticWoodcutting : MonoBehaviour, IScopedComponent
    {
        private static readonly IList<AutomaticWoodcutting> AllInstance;
        private static readonly List<Collider> ColliderBuffer;
        private static readonly Lazy<int> SightMaskLazy;

        // A log ignores damage until 0.2 s after it spawns. When the flag cannot be read, a log
        // counts as past that delay.
        private static readonly AccessTools.FieldRef<TreeLog, bool> LogFirstFrame;

        private Component _component;
        private IDestructible _target;
        private ZNetView _zNetView;
        private string _prefabName;

        internal static int AllInstanceCount => AllInstance.Count;
        public int ScopeGeneration { get; set; }

        internal Component Owner => _component;
        internal IDestructible Target => _target;

        // A tree and a log let a tier 0 hit through whatever the world's tool rule says, as their
        // own damage handlers do; a Destructible's, a stump's or a small tree's, does not.
        internal bool AlwaysAllowTierZero => !(_component is Destructible);

        static AutomaticWoodcutting()
        {
            AllInstance = new List<AutomaticWoodcutting>();
            ColliderBuffer = new List<Collider>();
            // The layers Automatic Mining checks its line from the eyes against.
            SightMaskLazy = new Lazy<int>(() =>
                LayerMask.GetMask("piece", "Default", "static_solid", "Default_small"));

            var firstFrame = AccessTools.Field(typeof(TreeLog), "m_firstFrame");
            if (firstFrame != null && firstFrame.FieldType == typeof(bool))
                LogFirstFrame = AccessTools.FieldRefAccess<TreeLog, bool>(firstFrame);
        }

        private static int SightMask => SightMaskLazy.Value;

        private void Awake()
        {
            var tree = GetComponent<TreeBase>();
            var log = tree ? null : GetComponent<TreeLog>();
            var stump = tree || log ? null : GetComponent<Destructible>();
            if (tree)
            {
                _component = tree;
                _target = tree;
            }
            else if (log)
            {
                _component = log;
                _target = log;
            }
            else if (stump)
            {
                _component = stump;
                _target = stump;
            }
            else
            {
                throw new Exception("Component must be a TreeBase, a TreeLog or a Destructible.");
            }

            if (!Objects.GetZNetView(_component, out _zNetView))
                throw new Exception("Component does not have ZNetView");

            _prefabName = Objects.GetPrefabName(gameObject);
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
            _target = null;
            _zNetView = null;
        }

        public static void TryWoodcutting(Player player)
        {
            // No target can be hit without a usable axe, so none is looked for.
            if (!GetReach(player, out var range)) return;

            // A cheap squared-distance pre-reject runs before the species match and the sort. The
            // margin is deliberately generous: a log or a large trunk has its root pivot far from
            // the collider a hit lands on, and the eye-to-hit check inside Chop still enforces
            // the exact range. ToList, since a hit can fell a tree or break a log, and the log or
            // the pieces that take its place join AllInstance mid-iteration.
            var origin = player.transform.position;
            var cullSqr = (range + 24f) * (range + 24f);
            var candidates = (from x in AllInstance
                    where x.IsStanding() &&
                          (x.transform.position - origin).sqrMagnitude <= cullSqr &&
                          x.IsAllowed()
                    orderby Vector3.Distance(origin, x.transform.position)
                    select x).ToList();

            foreach (var candidate in candidates)
                if (candidate.Chop(player))
                    return;
        }

        /// <summary>
        ///     Whether the target can take a hit now: its view is valid and owned, and a log is past
        ///     the delay after it spawns in which it ignores damage. Passes and area woodcutting
        ///     both use it, so neither hits a log a felling has just spawned.
        /// </summary>
        internal bool IsStanding()
        {
            if (!_component || !_zNetView || !_zNetView.IsValid() || !_zNetView.HasOwner()) return false;
            return !(_component is TreeLog log) || LogFirstFrame == null || !LogFirstFrame(log);
        }

        // Whether Allow Woodcutting Tree allows the species of this tree, log or stump.
        internal bool IsAllowed()
        {
            return TreeSpecies.IsAllowed(_prefabName);
        }

        /// <summary>
        ///     Hits this target once with a usable axe, as a swing would, and returns whether it
        ///     did. Nothing is spent when it does not: the target must be standing; the axe in the
        ///     right hand, or the inventory axe that Need To Equip Axe allows, must have durability
        ///     left and a tool tier that reaches the target; the first thing a ray from the eyes
        ///     meets within range must be the target itself.
        ///     <see cref="TryWoodcutting" /> calls it for each candidate, nearest first.
        /// </summary>
        internal bool Chop(Player player)
        {
            if (!IsStanding()) return false;

            var axe = GetAxe(player, GetMinToolTier());
            if (axe == null) return false;

            var eye = player.m_eye.position;
            if (!GetHitPoint(eye, GetWoodcuttingRange(axe), out var collider, out var point))
                return false;

            var skill = GetHitSkill(axe, _target);
            CreateHitEffect(axe, point);
            _target.Damage(CreateHitData(player, axe, skill, point, collider));
            UseAxe(player, axe, _target, skill);

            Automatics.Logger.Debug(() =>
                $"Woodcutting: [name: {_prefabName}, pos: {point}]");
            return true;
        }

        internal int GetMinToolTier()
        {
            switch (_component)
            {
                case TreeBase tree: return tree.m_minToolTier;
                case TreeLog log: return log.m_minToolTier;
                case Destructible stump: return stump.m_minToolTier;
                default: return 0;
            }
        }

        // The targets area woodcutting may add to a swing: every standing, allowed tree, log and
        // stump with a collider whose surface lies within radius of center, at its nearest such
        // point. The caller applies the tool tier, what the swing already hit and the cap.
        internal static void CollectAreaTargets(Vector3 center, float radius,
            List<AreaWoodcutting.Target> targets)
        {
            // The same root-pivot margin as TryWoodcutting; the per-collider check is exact.
            var cullSqr = (radius + 24f) * (radius + 24f);
            foreach (var instance in AllInstance)
            {
                if (!instance.IsStanding()) continue;
                if ((instance.transform.position - center).sqrMagnitude > cullSqr) continue;
                if (!instance.IsAllowed()) continue;
                if (!instance.GetNearestSurfacePoint(center, radius, out var collider, out var point,
                        out var distance))
                    continue;

                targets.Add(new AreaWoodcutting.Target(instance, collider, point, distance));
            }
        }

        private bool GetNearestSurfacePoint(Vector3 center, float radius, out Collider collider,
            out Vector3 point, out float distance)
        {
            collider = null;
            point = Vector3.zero;
            distance = float.MaxValue;

            ColliderBuffer.Clear();
            GetComponentsInChildren(false, ColliderBuffer);
            foreach (var candidate in ColliderBuffer)
            {
                if (!candidate.enabled || candidate.isTrigger) continue;
                // The bounds only reject: a trunk or a slanted log fills little of them.
                if (candidate.bounds.SqrDistance(center) > radius * radius) continue;
                if (!GetSurfacePoint(candidate, center, out var surface)) continue;

                var length = Vector3.Distance(surface, center);
                if (length > radius || length >= distance) continue;

                collider = candidate;
                point = surface;
                distance = length;
            }

            ColliderBuffer.Clear();
            return collider != null;
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

        // The longest reach of the axes a pass may use: the one in the right hand and, unless one
        // has to be equipped, those in the inventory. False when there is no usable axe.
        private static bool GetReach(Player player, out float range)
        {
            range = 0f;
            var found = false;
            var weapon = player.RightItem;
            if (IsUsableAxe(weapon))
            {
                range = GetWoodcuttingRange(weapon);
                found = true;
            }

            if (Config.NeedToEquipAxe) return found;

            foreach (var item in player.GetInventory().GetAllItems())
            {
                if (!IsUsableAxe(item)) continue;
                range = Mathf.Max(range, GetWoodcuttingRange(item));
                found = true;
            }

            return found;
        }

        // The axe in the right hand when it can cut the target; otherwise, unless one has to be
        // equipped, the lowest-tier axe in the inventory that can.
        private static ItemDrop.ItemData GetAxe(Player player, int minToolTier)
        {
            var weapon = player.RightItem;
            if (CanCut(weapon, minToolTier)) return weapon;
            if (Config.NeedToEquipAxe) return null;

            ItemDrop.ItemData axe = null;
            foreach (var item in player.GetInventory().GetAllItems())
                if (CanCut(item, minToolTier) &&
                    (axe == null || item.m_shared.m_toolTier < axe.m_shared.m_toolTier))
                    axe = item;
            return axe;
        }

        private static bool IsUsableAxe(ItemDrop.ItemData item)
        {
            if (item == null) return false;

            var shared = item.m_shared;
            return shared.m_skillType == Skills.SkillType.Axes &&
                   (!shared.m_useDurability || item.m_durability > 0f);
        }

        private static bool CanCut(ItemDrop.ItemData item, int minToolTier)
        {
            return IsUsableAxe(item) && item.m_shared.m_toolTier >= minToolTier;
        }

        private static float GetWoodcuttingRange(ItemDrop.ItemData axe)
        {
            var attack = axe.m_shared.m_attack;
            return Config.WoodcuttingRange > 0 ? Config.WoodcuttingRange : attack.m_attackRange;
        }

        // Aims from the eyes at a point of each of the target's colliders and keeps the nearest
        // landing. A ray lands only when the first thing it meets within range belongs to the
        // target, as the first thing a swing meets is what it hits: a wall, a rock or another
        // tree in between stops it.
        private bool GetHitPoint(Vector3 eye, float range, out Collider collider, out Vector3 point)
        {
            collider = null;
            point = Vector3.zero;
            var nearest = float.MaxValue;

            ColliderBuffer.Clear();
            GetComponentsInChildren(false, ColliderBuffer);
            foreach (var candidate in ColliderBuffer)
            {
                if (!candidate.enabled || candidate.isTrigger) continue;

                var toAim = GetAimPoint(candidate, eye) - eye;
                var length = toAim.magnitude;
                if (length < 0.001f) continue;

                if (!Physics.Raycast(eye, toAim / length, out var hit, range, SightMask,
                        QueryTriggerInteraction.Ignore))
                    continue;
                if (Projectile.FindHitObject(hit.collider) != gameObject) continue;
                if (hit.distance >= nearest) continue;

                nearest = hit.distance;
                collider = hit.collider;
                point = hit.point;
            }

            ColliderBuffer.Clear();
            return collider != null;
        }

        // The point of the collider nearest the eyes where Unity can find one, which on a
        // standing trunk is at eye height. On any other collider it is the collider's vertical
        // centre line at eye height, kept within its bounds.
        private static Vector3 GetAimPoint(Collider collider, Vector3 eye)
        {
            if (collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider ||
                collider is MeshCollider mesh && mesh.convex)
                return collider.ClosestPoint(eye);

            var bounds = collider.bounds;
            var center = bounds.center;
            return new Vector3(center.x, Mathf.Clamp(eye.y, bounds.min.y, bounds.max.y), center.z);
        }

        // The skill a real swing uses on this target: the attack's special-hit skill when the
        // target is of its special-hit type, else the weapon's own.
        private static Skills.SkillType GetHitSkill(ItemDrop.ItemData axe, IDestructible target)
        {
            var shared = axe.m_shared;
            var attack = shared.m_attack;
            return attack.m_specialHitSkill != Skills.SkillType.None &&
                   (target.GetDestructibleType() & attack.m_specialHitType) != DestructibleType.None
                ? attack.m_specialHitSkill
                : shared.m_skillType;
        }

        // Level with the ground and away from the player, so a felled tree falls away from the
        // player.
        private static Vector3 GetHitDirection(Player player, Vector3 point)
        {
            var direction = point - player.transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = player.transform.forward;
                direction.y = 0f;
            }

            return direction.normalized;
        }

        private static void CreateHitEffect(ItemDrop.ItemData axe, Vector3 pos)
        {
            axe.m_shared.m_hitEffect.Create(pos, Quaternion.identity);
            axe.m_shared.m_attack.m_hitEffect.Create(pos, Quaternion.identity);
        }

        private static HitData CreateHitData(Player player, ItemDrop.ItemData axe,
            Skills.SkillType skill, Vector3 hit, Collider collider)
        {
            var shared = axe.m_shared;

            var hitData = new HitData
            {
                m_toolTier = (short)shared.m_toolTier,
                m_itemWorldLevel = (byte)axe.m_worldLevel,
                m_statusEffectHash = shared.m_attackStatusEffect != null
                    ? shared.m_attackStatusEffect.NameHash()
                    : 0,
                m_skill = skill,
                m_damage = axe.GetDamage(),
                m_point = hit,
                m_dir = GetHitDirection(player, hit),
                // A swing pushes a log by the attack's force; a pass leaves it where it lies.
                m_pushForce = 0f,
                m_hitCollider = collider,
                m_hitType = HitData.HitType.PlayerHit
            };

            var attack = shared.m_attack;
            hitData.SetAttacker(player);
            hitData.m_damage.Modify(attack.m_damageMultiplier);
            hitData.m_damage.Modify(player.GetRandomSkillFactor(skill));
            hitData.m_damage.Modify((float)(1.0 + Mathf.Max(0, player.GetLevel() - 1) * 0.5));
            player.GetSEMan().ModifyAttack(skill, ref hitData);

            return hitData;
        }

        // A swing raises the hit's skill only on a target of the attack's special-hit type or of
        // its skill-hit type, so a target of neither raises nothing.
        private static void UseAxe(Player player, ItemDrop.ItemData axe, IDestructible target,
            Skills.SkillType skill)
        {
            var shared = axe.m_shared;
            var attack = shared.m_attack;

            if (shared.m_useDurability)
                axe.m_durability -= shared.m_useDurabilityDrain * Game.m_durabilityRate;

            player.AddNoise(attack.m_attackHitNoise);

            var type = target.GetDestructibleType();
            var special = attack.m_specialHitSkill != Skills.SkillType.None &&
                          (type & attack.m_specialHitType) != DestructibleType.None;
            if (special || (type & attack.m_skillHitType) != DestructibleType.None)
                player.RaiseSkill(skill, attack.m_raiseSkillAmount);
        }
    }
}
