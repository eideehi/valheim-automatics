using System;
using System.Collections.Generic;
using UnityEngine;

namespace Automatics.AutomaticWoodcutting
{
    /// <summary>
    ///     Area woodcutting: a local-player axe swing that hits an allowed tree, log or stump also
    ///     hits other allowed trees, logs and stumps near the first such hit, those of the same
    ///     kind as what that hit struck before the others. The swing context lives from the
    ///     Attack.DoMeleeAttack prefix that began it to that call's finalizer; the damage prefixes
    ///     record what the swing hit, and only the postfix of a swing that completed expands, once.
    /// </summary>
    internal static class AreaWoodcutting
    {
        private static readonly HashSet<Component> HitObjects = new HashSet<Component>();
        private static readonly List<Target> Candidates = new List<Target>();
        private static readonly HashSet<string> TriggerSpecies = new HashSet<string>(StringComparer.Ordinal);

        // The targets of the same kind as the trigger's first, then the others; nearest first
        // within each.
        private static readonly Comparison<Target> ByKindThenDistance = (x, y) =>
            x.SameKind != y.SameKind ? (x.SameKind ? -1 : 1) : x.Distance.CompareTo(y.Distance);

        private static bool _swing;
        private static bool _expanding;
        private static Player _player;
        private static ItemDrop.ItemData _weapon;
        private static HitData _trigger;

        // The form and the prefab name of the target the trigger hit, kept apart from the target
        // itself: the swing may fell it before the expansion.
        private static TargetForm _triggerForm;
        private static string _triggerPrefab;

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
        ///     the trigger, as a copy taken before the hit is sent, together with the form and the
        ///     prefab name of what it hit.
        /// </summary>
        internal static void RecordHit(Component target, HitData hit)
        {
            if (!_swing || _expanding || target == null || hit == null) return;

            HitObjects.Add(target);

            if (_trigger != null) return;
            var component = target.GetComponent<AutomaticWoodcutting>();
            if (!component || !component.IsAllowed()) return;

            _trigger = hit.Clone();
            _triggerForm = component.GetForm();
            _triggerPrefab = component.PrefabName;
        }

        /// <summary>
        ///     Applies the trigger hit to the allowed targets the swing did not hit: first those of
        ///     the same kind as the target the trigger hit, of its form and sharing a species with
        ///     it, then the others, each nearest first. The swing has already paid its own
        ///     durability, stamina and skill gain; each extra target wears the axe once more, and
        ///     nothing else is charged or raised.
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
            TriggerSpecies.Clear();
            TreeSpecies.GetSpecies(_triggerPrefab, TriggerSpecies);
            for (var i = 0; i < Candidates.Count; i++)
            {
                var candidate = Candidates[i].Component;
                if (candidate.GetForm() == _triggerForm &&
                    TreeSpecies.HasAnySpecies(candidate.PrefabName, TriggerSpecies))
                    Candidates[i] = Candidates[i].AsSameKind();
            }
            Candidates.Sort(ByKindThenDistance);

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
                TriggerSpecies.Clear();
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
            _triggerForm = default;
            _triggerPrefab = null;
            HitObjects.Clear();
            Candidates.Clear();
            TriggerSpecies.Clear();
        }

        internal readonly struct Target
        {
            public Target(AutomaticWoodcutting component, Collider collider, Vector3 point,
                float distance, bool sameKind = false)
            {
                Component = component;
                Collider = collider;
                Point = point;
                Distance = distance;
                SameKind = sameKind;
            }

            public AutomaticWoodcutting Component { get; }
            public Collider Collider { get; }
            public Vector3 Point { get; }
            public float Distance { get; }

            // Of the same kind as the target the swing's trigger hit.
            public bool SameKind { get; }

            public Target AsSameKind()
            {
                return new Target(Component, Collider, Point, Distance, true);
            }
        }
    }
}
