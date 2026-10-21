using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Automatics.Valheim;
using JetBrains.Annotations;
using ModUtils;
using Splatform;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class AutomaticMapping
    {
        // The dynamic scan loop (Character / Fish / Bird / Vehicle walk) and
        // the downstream RefreshPins call are throttled to 10 Hz; AnimatePins
        // still rides the vanilla UpdatePins prefix so visual smoothing stays
        // responsive, and the scan-loop latency shows up as at most ~100 ms of
        // pin-target lag which SmoothDamp absorbs.
        //
        // The epsilon tolerates the float round-off from summing a 50 Hz
        // fixed step (0.02f stores as ~0.01999...): five ticks accumulate to
        // ~0.09999994f, so a strict `< 0.1f` comparison would drop the fifth
        // tick and the cadence would fall to 8.3 Hz. Subtracting the interval
        // on pass (instead of zeroing) preserves the carry-over past the gate.
        private const float DynamicScanInterval = 0.1f;
        private const float DynamicScanEpsilon = 1e-4f;

        private static float _dynamicScanAccumulator;
        private static float _lastAnimateTime;
        private static ZDOMan _zdoMan;

        private static void RemoveCachedPins()
        {
            // Both calls must run, so the results are combined without short-circuiting.
            var removed = DynamicObjectMapping.RemoveCachedPins();
            removed |= StaticObjectMapping.RemoveCachedPins();
            if (removed) Map.RefreshPins();
        }

        // Whether the area around the player has loaded is not asked here: the scans ask it only
        // when one is due, after their own cadence gates.
        private static bool CanRun(Player player)
        {
            return !Game.IsPaused() &&
                   player == Player.m_localPlayer &&
                   player.IsOwner();
        }

        public static void Cleanup()
        {
            if (_zdoMan != null) _zdoMan.m_onZDODestroyed -= OnZDODestroyed;
            _zdoMan = null;
            Navigation.Cleanup();
            DynamicObjectMapping.Cleanup();
            StaticObjectMapping.Cleanup();
            MappingProfiler.Reset();
            _dynamicScanAccumulator = 0f;
            _lastAnimateTime = 0f;
        }

        // The scan entry points below, and SetSaveFlag, open with the module gate: the player's
        // fixed update, the minimap update and the transpiled click handlers reach them, and a
        // stop request has to end their effect on the map in the frame it is made.
        public static void DynamicMapping(Player player, float delta)
        {
            if (!Module.Running) return;

            EnsureDestructionSubscription();
            MappingProfiler.FlushIfDue();

            if (!CanRun(player)) return;
            if (!Config.EnableAutomaticMapping)
            {
                RemoveCachedPins();
                return;
            }

            _dynamicScanAccumulator += delta;
            if (_dynamicScanAccumulator + DynamicScanEpsilon < DynamicScanInterval) return;

            // Subtract so residual time past the gate carries forward and the
            // cadence self-corrects; clamp in case of a very long frame (load
            // screen, tab away) so the accumulator cannot bank enough credit
            // to fire multiple scans back-to-back once ticks resume.
            _dynamicScanAccumulator -= DynamicScanInterval;
            if (_dynamicScanAccumulator < 0f)
                _dynamicScanAccumulator = 0f;
            else if (_dynamicScanAccumulator > DynamicScanInterval)
                _dynamicScanAccumulator = DynamicScanInterval;

            // No scan runs while the area around the player is still loading. The due scan is
            // skipped, not postponed, so while the area loads readiness is asked once per interval.
            if (!ZNetScene.instance.IsAreaReady(player.transform.position)) return;

            DynamicObjectMapping.Mapping(delta);
            Map.RefreshPins();
        }

        public static void Mapping(Player player, float delta, bool takeInput)
        {
            if (!Module.Running) return;
            EnsureDestructionSubscription();
            if (!CanRun(player)) return;
            if (!Config.EnableAutomaticMapping)
            {
                RemoveCachedPins();
                return;
            }

            // The static pass asks about area readiness itself, once its interval or key lets it run.
            StaticObjectMapping.Mapping(delta, takeInput);
        }

        // A destroyed object's pin is removed where every game sees the destruction: each game
        // that holds the object's ZDO raises ZDOMan.m_onZDODestroyed, whichever game destroyed it,
        // also for an object with no destroy callback, such as a used-up MineRock deposit. Where the
        // destroying game has a callback or transpiled call for the object (Module.HookDestroyOnce,
        // Patches), that has already removed the pin, and nothing is left for this. A flora
        // patch's pin is left to that callback alone (StaticObjectMapping.OnZdoDestroyed). The
        // ZDOMan is created with each world, so the scan entry points follow the current one, and
        // the session's cleanup releases it.
        private static void EnsureDestructionSubscription()
        {
            if (_zdoMan == ZDOMan.instance) return;
            if (_zdoMan != null) _zdoMan.m_onZDODestroyed -= OnZDODestroyed;
            _zdoMan = ZDOMan.instance;
            if (_zdoMan != null) _zdoMan.m_onZDODestroyed += OnZDODestroyed;
        }

        // Only a persistent ZDO counts: unloading never destroys one, while a non-persistent
        // object's ZDO is destroyed when the object unloads as well.
        private static void OnZDODestroyed(ZDO zdo)
        {
            if (!Module.Running) return;
            if (!Config.EnableAutomaticMapping) return;
            if (!zdo.Persistent) return;

            DynamicObjectMapping.OnZdoDestroyed(zdo.m_uid);
            StaticObjectMapping.OnZdoDestroyed(zdo.m_uid);
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            Map.UntrackAutomaticPin(pinData);
            PinIndex.Untrack(pinData);
            Navigation.OnRemovePin(pinData);
            MapFilter.OnRemovePin(pinData);
            if (ReferenceEquals(pinData, PinTooltip.HoveredPin)) PinTooltip.Hide();
            DynamicObjectMapping.OnRemovePin(pinData);
            StaticObjectMapping.OnRemovePin(pinData);
        }

        // Derives a wall-clock delta so SmoothDamp progresses at a steady rate
        // regardless of whether UpdatePins is firing on vanilla's dirty-flag
        // cadence or our throttled scan cadence. The first call after Cleanup
        // (when _lastAnimateTime == 0) falls back to Time.deltaTime so
        // SmoothDamp does not see a multi-minute jump at login.
        public static void AnimatePins()
        {
            if (!Module.Running) return;

            var now = Time.time;
            float delta;
            if (_lastAnimateTime <= 0f)
                delta = Time.deltaTime;
            else
                delta = now - _lastAnimateTime;
            _lastAnimateTime = now;

            DynamicObjectMapping.AnimatePins(delta);
        }

        [UsedImplicitly]
        public static bool SetSaveFlag(Vector3 pos, float radius)
        {
            // False hands the click back to the game's own handling.
            if (!Module.Running) return false;

            var pinData = Map.GetClosestPin(pos, radius, x => x.m_ownerID == 0L && !x.m_save);
            if (pinData is null) return false;

            return StaticObjectMapping.SetSaveFlag(pinData) ||
                   DynamicObjectMapping.SetSaveFlag(pinData);
        }
    }
}
