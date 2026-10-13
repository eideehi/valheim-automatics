using System;
using System.Collections.Generic;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     Sits on every standing tree and fallen log with a ZDO while the module runs. A pass hits
    ///     the nearest of them that the axe in the right hand can hit, once, the way a swing would,
    ///     and stops there.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class AutomaticWoodcutting : MonoBehaviour, IScopedComponent
    {
        private static readonly IList<AutomaticWoodcutting> AllInstance;
        private static readonly List<Collider> ColliderBuffer;
        private static readonly Lazy<int> SightMaskLazy;

        private Component _component;
        private IDestructible _target;
        private ZNetView _zNetView;

        internal static int AllInstanceCount => AllInstance.Count;
        public int ScopeGeneration { get; set; }

        static AutomaticWoodcutting()
        {
            AllInstance = new List<AutomaticWoodcutting>();
            ColliderBuffer = new List<Collider>();
            // The layers Automatic Mining checks its line from the eyes against.
            SightMaskLazy = new Lazy<int>(() =>
                LayerMask.GetMask("piece", "Default", "static_solid", "Default_small"));
        }

        private static int SightMask => SightMaskLazy.Value;

        private void Awake()
        {
            var tree = GetComponent<TreeBase>();
            if (tree)
            {
                _component = tree;
                _target = tree;
            }
            else
            {
                var log = GetComponent<TreeLog>();
                if (!log)
                    throw new Exception("Component must be a TreeBase or a TreeLog.");

                _component = log;
                _target = log;
            }

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
            _target = null;
            _zNetView = null;
        }

        public static void TryWoodcutting(Player player)
        {
            // No target can be hit without an axe in the right hand, so none is looked for.
            var axe = player.RightItem;
            if (axe == null || axe.m_shared.m_skillType != Skills.SkillType.Axes) return;

            // A cheap squared-distance pre-reject runs before the sort. The margin is deliberately
            // generous: a log or a large trunk has its root pivot far from the collider a hit
            // lands on, and the eye-to-hit check inside Chop still enforces the exact range.
            // ToList, since a hit can fell a tree or break a log, and the log or the pieces that
            // take its place join AllInstance mid-iteration.
            var range = GetWoodcuttingRange(axe);
            var origin = player.transform.position;
            var cullSqr = (range + 24f) * (range + 24f);
            var candidates = (from x in AllInstance
                    where x._zNetView.IsValid() &&
                          x._zNetView.HasOwner() &&
                          (x.transform.position - origin).sqrMagnitude <= cullSqr
                    orderby Vector3.Distance(origin, x.transform.position)
                    select x).ToList();

            foreach (var candidate in candidates)
                if (candidate.Chop(player))
                    return;
        }

        /// <summary>
        ///     Hits this tree or log once with the axe in the player's right hand, as a swing
        ///     would, and returns whether it did. Nothing is spent when it does not: the right hand
        ///     must hold an axe with durability left whose tool tier reaches the target, and the
        ///     first thing a ray from the eyes meets within range must be the target itself.
        ///     <see cref="TryWoodcutting" /> calls it for each candidate, nearest first.
        /// </summary>
        internal bool Chop(Player player)
        {
            if (!_component || !_zNetView || !_zNetView.IsValid()) return false;

            var axe = GetAxe(player, GetMinToolTier());
            if (axe == null) return false;

            var eye = player.m_eye.position;
            if (!GetHitPoint(eye, GetWoodcuttingRange(axe), out var collider, out var point))
                return false;

            var skill = GetHitSkill(axe, _target);
            CreateHitEffect(axe, point);
            _target.Damage(CreateHitData(player, axe, skill, point, collider));
            UseAxe(player, axe, skill);

            Automatics.Logger.Debug(() =>
                $"Woodcutting: [name: {Objects.GetPrefabName(gameObject)}, pos: {point}]");
            return true;
        }

        private int GetMinToolTier()
        {
            switch (_component)
            {
                case TreeBase tree: return tree.m_minToolTier;
                case TreeLog log: return log.m_minToolTier;
                default: return 0;
            }
        }

        private static ItemDrop.ItemData GetAxe(Player player, int minToolTier)
        {
            var weapon = player.RightItem;
            if (weapon == null) return null;

            var shared = weapon.m_shared;
            if (shared.m_skillType != Skills.SkillType.Axes) return null;
            if (shared.m_useDurability && weapon.m_durability <= 0f) return null;
            return shared.m_toolTier >= minToolTier ? weapon : null;
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

        private static void UseAxe(Player player, ItemDrop.ItemData axe, Skills.SkillType skill)
        {
            var shared = axe.m_shared;
            var attack = shared.m_attack;

            if (shared.m_useDurability)
                axe.m_durability -= shared.m_useDurabilityDrain * Game.m_durabilityRate;

            player.AddNoise(attack.m_attackHitNoise);
            player.RaiseSkill(skill, attack.m_raiseSkillAmount);
        }
    }
}
