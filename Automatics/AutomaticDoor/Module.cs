using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Automatics.AutomaticDoor
{
    internal static class Module
    {
        private struct PlayerMotionState
        {
            public Vector3 LastPosition;
            public float LastTime;
            public bool HasLastPosition;
        }

        private static readonly Dictionary<int, PlayerMotionState> PlayerMotionStates;
        private static readonly HashSet<int> ActivePlayerStateKeys;
        private static readonly List<int> PlayerStateKeysBuffer;
        private static float _openTimer;
        private static float _closeTimer;

        static Module()
        {
            PlayerMotionStates = new Dictionary<int, PlayerMotionState>();
            ActivePlayerStateKeys = new HashSet<int>();
            PlayerStateKeysBuffer = new List<int>();
        }

        [AutomaticsInitializer(1)]
        private static void Initialize()
        {
            Config.Initialize();
            if (Config.ModuleDisabled) return;

            Hooks.OnPlayerAwake += (_, __) => ResetState();
            Hooks.OnPlayerUpdate += OnPlayerUpdate;
            Hooks.OnPlayerFixedUpdate += OnPlayerFixedUpdate;
            Hooks.OnDedicatedServerFixedUpdate += OnDedicatedServerFixedUpdate;
            Harmony.CreateAndPatchAll(typeof(Patches), Automatics.GetHarmonyId("automatic-door"));
        }

        private static void ResetState()
        {
            _openTimer = 0f;
            _closeTimer = 0f;
            PlayerMotionStates.Clear();
            ActivePlayerStateKeys.Clear();
            PlayerStateKeysBuffer.Clear();
        }

        private static void ResetTimers()
        {
            _openTimer = 0f;
            _closeTimer = 0f;
        }

        private static void OnPlayerUpdate(Player player, bool takeInput)
        {
            if (Player.m_localPlayer != player || !player.IsOwner()) return;
            if (!takeInput) return;

            if (Config.AutomaticDoorEnableDisableToggle.IsDown())
            {
                Config.EnableAutomaticDoor = !Config.EnableAutomaticDoor;
                var messagePosition = Config.AutomaticDoorEnableDisableToggleMessage;
                if (messagePosition != Message.None)
                {
                    var message = Automatics.L10N.Localize(
                        "@message_automatic_door_enable_disable_toggle",
                        Config.EnableAutomaticDoor ? "@enabled" : "@disabled");
                    var type = messagePosition == Message.Center
                        ? MessageHud.MessageType.Center
                        : MessageHud.MessageType.TopLeft;
                    player.Message(type, message);
                }
            }
        }

        private static void OnDedicatedServerFixedUpdate(float delta)
        {
            UpdateAutomaticDoor(Player.GetAllPlayers(), delta);
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            if (Player.m_localPlayer != player || !player.IsOwner()) return;

            UpdateAutomaticDoor(Player.GetAllPlayers(), delta);
        }

        private static void UpdateAutomaticDoor(IList<Player> players, float delta)
        {
            CleanupPlayerStates(players);

            if (IsGamePaused())
            {
                InvalidatePlayerMotion();
                return;
            }

            if (!Config.EnableAutomaticDoor || !AutomaticDoor.HasRegisteredDoor)
            {
                ResetTimers();
                return;
            }

            if (Config.IntervalToOpen >= 0.1f)
            {
                _openTimer += delta;
                if (_openTimer >= Config.IntervalToOpen)
                {
                    _openTimer = 0f;
                    foreach (var currentPlayer in players)
                    {
                        if (!currentPlayer) continue;
                        AutomaticDoor.TryOpenNearby(currentPlayer, GetVelocity(currentPlayer, delta));
                    }
                }
            }
            else
            {
                _openTimer = 0f;
            }

            if (Config.IntervalToClose >= 0.1f)
            {
                _closeTimer += delta;
                if (_closeTimer >= Config.IntervalToClose)
                {
                    _closeTimer = 0f;
                    AutomaticDoor.TryCloseDoors();
                }
            }
            else
            {
                _closeTimer = 0f;
            }
        }

        private static bool IsGamePaused()
        {
            return Game.instance != null && Game.IsPaused();
        }

        private static int GetPlayerStateKey(Player player)
        {
            return player.GetInstanceID();
        }

        private static void CleanupPlayerStates(IList<Player> players)
        {
            if (PlayerMotionStates.Count == 0) return;

            ActivePlayerStateKeys.Clear();
            foreach (var player in players)
            {
                if (!player) continue;
                ActivePlayerStateKeys.Add(GetPlayerStateKey(player));
            }

            PlayerStateKeysBuffer.Clear();
            foreach (var key in PlayerMotionStates.Keys)
                if (!ActivePlayerStateKeys.Contains(key))
                    PlayerStateKeysBuffer.Add(key);

            foreach (var key in PlayerStateKeysBuffer)
                    PlayerMotionStates.Remove(key);
        }

        private static void InvalidatePlayerMotion()
        {
            if (PlayerMotionStates.Count == 0) return;

            PlayerStateKeysBuffer.Clear();
            foreach (var key in PlayerMotionStates.Keys)
                PlayerStateKeysBuffer.Add(key);

            foreach (var key in PlayerStateKeysBuffer)
            {
                var state = PlayerMotionStates[key];
                state.HasLastPosition = false;
                PlayerMotionStates[key] = state;
            }
        }

        private static Vector3 GetVelocity(Player player, float delta)
        {
            var key = GetPlayerStateKey(player);
            if (!PlayerMotionStates.TryGetValue(key, out var state))
                state = default;

            var currentPosition = player.transform.position;
            var currentTime = Time.time;
            var velocity = Vector3.zero;

            if (state.HasLastPosition)
            {
                var elapsed = currentTime - state.LastTime;
                if (elapsed > Mathf.Epsilon && elapsed < 1f)
                    velocity = (currentPosition - state.LastPosition) / elapsed;
            }

            state.LastPosition = currentPosition;
            state.LastTime = currentTime;
            state.HasLastPosition = true;
            PlayerMotionStates[key] = state;
            return velocity;
        }
    }
}
