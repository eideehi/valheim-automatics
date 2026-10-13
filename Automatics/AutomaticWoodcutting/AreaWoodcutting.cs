using System;
using System.Collections.Generic;
using UnityEngine;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     Area woodcutting: a local-player axe swing that hits an allowed tree, log or stump also
    ///     hits other allowed trees, logs and stumps near the first such hit. The swing context
    ///     lives from the Attack.DoMeleeAttack prefix that began it to that call's finalizer; the
    ///     damage prefixes record what the swing hit, and only the postfix of a swing that
    ///     completed expands, once.
    /// </summary>
    internal static class AreaWoodcutting
    {
        private static readonly HashSet<Component> HitObjects = new HashSet<Component>();
        private static readonly List<Target> Candidates = new List<Target>();

        private static bool _swing;
        private static bool _expanding;
        private static Player _player;
        private static ItemDrop.ItemData _weapon;
        private static HitData _trigger;

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
        ///     Records one hit of the swing. The first hit on an allowed tree, log or stump becomes
        ///     the trigger, as a copy taken before the hit is sent.
        /// </summary>
        internal static void RecordHit(Component target, HitData hit)
        {
            if (!_swing || _expanding || target == null || hit == null) return;

            HitObjects.Add(target);

            if (_trigger != null) return;
            var component = target.GetComponent<AutomaticWoodcutting>();
            if (component && component.IsAllowed())
                _trigger = hit.Clone();
        }

        /// <summary>
        ///     Applies the trigger hit to the nearest allowed targets the swing did not hit. The
        ///     swing has already paid its own durability, stamina and skill gain; each extra target
        ///     wears the axe once more, and nothing else is charged or raised.
        /// </summary>
        internal static void Expand()
        {
            if (!_swing || _expanding || _trigger == null || !_player || _weapon == null) return;

            var trigger = _trigger;
            _trigger = null;
            LastTriggerPoint = trigger.m_point;

            Candidates.Clear();
            AutomaticWoodcutting.CollectAreaTargets(trigger.m_point, Config.AreaWoodcuttingRadius,
                Candidates);
            Candidates.Sort((x, y) => x.Distance.CompareTo(y.Distance));

            var shared = _weapon.m_shared;
            var remaining = Config.AreaWoodcuttingMaxTargets;
            _expanding = true;
            try
            {
                foreach (var target in Candidates)
                {
                    if (remaining <= 0) break;

                    var component = target.Component;
                    if (HitObjects.Contains(component.Owner)) continue;
                    if (!trigger.CheckToolTier(component.GetMinToolTier(), component.AlwaysAllowTierZero))
                        continue;
                    // An earlier extra hit can fell this tree or break this log; a target that no
                    // longer stands costs neither durability nor the cap.
                    if (!component.IsStanding()) continue;
                    // Checked before every extra hit, the first included: the swing itself has
                    // already worn the axe.
                    if (shared.m_useDurability && _weapon.m_durability <= 0f) break;

                    var hit = trigger.Clone();
                    hit.m_hitCollider = target.Collider;
                    hit.m_point = target.Point;
                    component.Target.Damage(hit);
                    ExpandedHitCount++;
                    remaining--;

                    HitObjects.Add(component.Owner);

                    if (shared.m_useDurability)
                        _weapon.m_durability -= shared.m_useDurabilityDrain * Game.m_durabilityRate;
                }
            }
            catch (Exception e)
            {
                Automatics.Logger.Error($"Area woodcutting stopped\n{e}");
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
            HitObjects.Clear();
            Candidates.Clear();
        }

        internal readonly struct Target
        {
            public Target(AutomaticWoodcutting component, Collider collider, Vector3 point,
                float distance)
            {
                Component = component;
                Collider = collider;
                Point = point;
                Distance = distance;
            }

            public AutomaticWoodcutting Component { get; }
            public Collider Collider { get; }
            public Vector3 Point { get; }
            public float Distance { get; }
        }
    }
}
