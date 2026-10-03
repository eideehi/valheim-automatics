using System;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticMapping
{
    internal static class AimedObjectBar
    {
        private const float RetryDelay = 5f;
        private static readonly MappingOverlay Overlay = new MappingOverlay("AutomaticsAimedObjectOverlay");
        private static ObjectPinning.AimedObject _aimed;
        private static ObjectPinning.AimedObject _cached;
        private static Character _character;
        private static float _nextAttemptTime;
        private static bool _errorLogged;

        internal static GameObject Label => Overlay.Root;

        internal static void Update(Player player)
        {
            if (!Module.Running || !Config.EnableAimedObjectBar)
            {
                ClearBar();
                return;
            }
            if (Time.unscaledTime < _nextAttemptTime) return;

            try
            {
                UpdateBar(player);
            }
            catch (Exception e)
            {
                _nextAttemptTime = Time.unscaledTime + RetryDelay;
                try
                {
                    ClearBar();
                }
                catch (Exception)
                {
                    // The next attempt recreates the label either way.
                }
                if (_errorLogged) return;
                _errorLogged = true;
                Automatics.Logger.Error($"Aimed object bar failed and is retried every {RetryDelay}s: {e}");
            }
        }

        private static void UpdateBar(Player player)
        {
            _aimed = null;
            if (!Navigation.CanShowHud(player) || !GameCamera.instance)
            {
                Overlay.SetVisible(false);
                return;
            }

            var camera = GameCamera.instance.transform;
            if (!ObjectPinning.TryResolveFromRay(player, new Ray(camera.position, camera.forward), out var aimed, _cached) ||
                !aimed.Component || string.IsNullOrEmpty(aimed.Name) || aimed.Location != null)
            {
                Overlay.SetVisible(false);
                return;
            }
            if (_cached == null || _cached.Instance != aimed.Instance)
                _character = aimed.Instance.GetComponent<Character>();
            _cached = aimed;
            var distance = Utils.DistanceXZ(player.transform.position, aimed.Position);
            if (distance > Config.AimedObjectBarRange)
            {
                Overlay.SetVisible(false);
                return;
            }
            _aimed = aimed;
            if (Navigation.ShowsInstance(aimed.Instance) || Navigation.CanUseEnemyHud(_character))
            {
                Overlay.SetVisible(false);
                return;
            }

            var position = Overlay.GetPosition(aimed.Instance);
            if (!MappingOverlay.TryGetScreenPoint(position, out var screenPoint) || !Overlay.Ensure())
            {
                Overlay.SetVisible(false);
                return;
            }
            Overlay.Show(aimed.Name, distance, screenPoint, MappingOverlay.GetHealth(aimed.Instance));
        }

        internal static Character GetHudTarget()
        {
            if (!Module.Running || !Config.EnableAimedObjectBar ||
                !Navigation.CanShowHud(Player.m_localPlayer) || _aimed == null || !_aimed.Instance) return null;
            return Navigation.CanUseEnemyHud(_character) ? _character : null;
        }

        private static void ClearBar()
        {
            _aimed = null;
            _cached = null;
            _character = null;
            Overlay.Destroy();
        }

        internal static void Cleanup()
        {
            ClearBar();
            _nextAttemptTime = 0f;
            _errorLogged = false;
        }
    }
}
