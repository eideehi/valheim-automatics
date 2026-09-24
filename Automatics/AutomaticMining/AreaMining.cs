using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticMining
{
    /// <summary>
    ///     Area mining: a local-player pickaxe swing that hits an allowed mineral also hits other
    ///     allowed minerals and rock sections near the first such hit. The swing context lives
    ///     from the Attack.DoMeleeAttack prefix that began it to that call's finalizer; the damage
    ///     prefixes record what the swing hit, and only the postfix of a swing that completed
    ///     expands, once.
    /// </summary>
    internal static class AreaMining
    {
        private static readonly HashSet<Collider> HitSections = new HashSet<Collider>();
        private static readonly HashSet<Component> HitObjects = new HashSet<Component>();
        private static readonly List<Target> Candidates = new List<Target>();

        private static bool _swing;
        private static bool _expanding;
        private static Player _player;
        private static ItemDrop.ItemData _weapon;
        private static HitData _trigger;

        // MineRock5 keeps each section's health on a private nested class. When the lookup is
        // unavailable, the active collider still tells whether a section is standing.
        private static readonly MethodInfo MineRock5GetHitArea =
            AccessTools.Method(typeof(MineRock5), "GetHitArea", new[] { typeof(int) });
        private static readonly MethodInfo MineRock5GetAreaIndex =
            AccessTools.Method(typeof(MineRock5), "GetAreaIndex", new[] { typeof(Collider) });
        private static readonly FieldInfo HitAreaHealth =
            AccessTools.Field(AccessTools.Inner(typeof(MineRock5), "HitArea"), "m_health");

        // Smoke probes: the extra hits applied since start, whether one is being applied, and
        // where the last expansion was centred.
        internal static int ExpandedHitCount { get; private set; }
        internal static bool Expanding => _expanding;
        internal static bool SwingActive => _swing;
        internal static Vector3 LastTriggerPoint { get; private set; }

        /// <summary>
        ///     Opens the swing context and returns true, unless one is already open: a melee attack
        ///     started inside another leaves the context to the call that opened it.
        /// </summary>
        internal static bool BeginSwing(Player player, ItemDrop.ItemData weapon)
        {
            if (_swing || _expanding) return false;

            Reset();
            _swing = true;
            _player = player;
            _weapon = weapon;
            return true;
        }

        /// <summary>
        ///     Records one hit of the swing. Rock sections are recorded by collider, so another
        ///     section of the same rock can still be an extra target; any other object as a whole.
        ///     The first hit on an allowed mineral becomes the trigger, as a copy taken before the
        ///     hit is sent.
        /// </summary>
        internal static void RecordHit(Component target, HitData hit)
        {
            if (!_swing || _expanding || target == null || hit == null) return;

            if (target is MineRock || target is MineRock5)
            {
                if (hit.m_hitCollider != null) HitSections.Add(hit.m_hitCollider);
            }
            else
            {
                HitObjects.Add(target);
            }

            if (_trigger == null && AutomaticMining.IsAllowedMineral(target))
                _trigger = hit.Clone();
        }

        /// <summary>
        ///     Applies the trigger hit to the nearest allowed targets the swing did not hit. The
        ///     swing has already paid its own durability, stamina and skill gain; each extra target
        ///     wears the pickaxe once more, and nothing else is charged or raised.
        /// </summary>
        internal static void Expand()
        {
            if (!_swing || _expanding || _trigger == null || !_player || _weapon == null) return;

            var trigger = _trigger;
            _trigger = null;
            LastTriggerPoint = trigger.m_point;

            Candidates.Clear();
            AutomaticMining.CollectAreaTargets(_player, trigger.m_point, Config.AreaMiningRadius,
                Candidates);
            Candidates.Sort((x, y) => x.Distance.CompareTo(y.Distance));

            var shared = _weapon.m_shared;
            var remaining = Config.AreaMiningMaxTargets;
            _expanding = true;
            try
            {
                foreach (var target in Candidates)
                {
                    if (remaining <= 0) break;
                    if (IsHit(target)) continue;
                    if (!trigger.CheckToolTier(target.MinToolTier)) continue;
                    // An earlier extra hit can break this section, or the rest of the rock with
                    // it; a target that no longer stands costs neither durability nor the cap.
                    if (!IsStanding(target)) continue;
                    // Checked before every extra hit, the first included: the swing itself has
                    // already worn the pickaxe.
                    if (shared.m_useDurability && _weapon.m_durability <= 0f) break;

                    var hit = trigger.Clone();
                    hit.m_hitCollider = target.Collider;
                    hit.m_point = target.Point;
                    // A rock resolves a hit with a radius by overlap at the point instead of by
                    // its collider, which could hit a section other than this target.
                    hit.m_radius = 0f;
                    target.Destructible.Damage(hit);
                    ExpandedHitCount++;
                    remaining--;

                    if (target.Owner is MineRock || target.Owner is MineRock5)
                        HitSections.Add(target.Collider);
                    else
                        HitObjects.Add(target.Owner);

                    if (shared.m_useDurability)
                        _weapon.m_durability -= shared.m_useDurabilityDrain * Game.m_durabilityRate;
                }
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Area mining stopped\n{e}");
            }
            finally
            {
                _expanding = false;
                Candidates.Clear();
            }
        }

        internal static void EndSwing()
        {
            Reset();
        }

        internal static void Reset()
        {
            _swing = false;
            _expanding = false;
            _player = null;
            _weapon = null;
            _trigger = null;
            HitSections.Clear();
            HitObjects.Clear();
            Candidates.Clear();
        }

        private static bool IsStanding(Target target)
        {
            if (!target.Owner || !target.NetView || !target.NetView.IsValid()) return false;

            var collider = target.Collider;
            if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy) return false;

            if (target.Owner is MineRock5 rock5 && MineRock5GetHitArea != null &&
                MineRock5GetAreaIndex != null && HitAreaHealth != null)
            {
                var index = (int)MineRock5GetAreaIndex.Invoke(rock5, new object[] { collider });
                var area = index < 0 ? null : MineRock5GetHitArea.Invoke(rock5, new object[] { index });
                if (area == null || (float)HitAreaHealth.GetValue(area) <= 0f) return false;
            }

            return true;
        }

        private static bool IsHit(Target target)
        {
            return target.Owner is MineRock || target.Owner is MineRock5
                ? HitSections.Contains(target.Collider)
                : HitObjects.Contains(target.Owner);
        }

        internal readonly struct Target
        {
            public Target(IDestructible destructible, Component owner, ZNetView netView,
                Collider collider, int minToolTier, Vector3 point, float distance)
            {
                Destructible = destructible;
                Owner = owner;
                NetView = netView;
                Collider = collider;
                MinToolTier = minToolTier;
                Point = point;
                Distance = distance;
            }

            public IDestructible Destructible { get; }
            public Component Owner { get; }
            public ZNetView NetView { get; }
            public Collider Collider { get; }
            public int MinToolTier { get; }
            public Vector3 Point { get; }
            public float Distance { get; }
        }
    }
}
