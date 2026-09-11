using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticDoor
{
    /// <summary>
    ///     Automatic Door on the module lifecycle. A scope owns its patch, four hook
    ///     subscriptions and every <see cref="AutomaticDoor" /> component it attaches, so turning
    ///     the module off removes them without changing any door's open state.
    /// </summary>
    internal sealed class Module : IAutomaticsModule
    {
        public static readonly Module Instance = new Module();

        internal static bool Active;
        internal static bool StopRequested;
        internal static bool Running => Active && !StopRequested;
        internal static int Generation;
        internal static ModuleScope CurrentScope;

        /// <summary>
        ///     Discovery follows the applied activation rather than the stop request. If a disable
        ///     request is withdrawn before the pump runs, no later reconciliation occurs, so an
        ///     object seen in that window still has to join the current scope.
        /// </summary>
        internal static bool Discovering => Active && CurrentScope != null;

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

        private Module()
        {
        }

        public string Name => "automatic-door";
        public string ConfigSection => "automatic_door";
        public bool SupportsRuntimeToggle => true;
        public ConfigEntry<AutomaticsModule> ModuleToggle => Config.ModuleEntry;

        public void SetStopRequested(bool value)
        {
            StopRequested = value;
        }

        public void BindConfig()
        {
            Config.Initialize();
        }

        /// <summary>
        ///     Acquires the patch first, then subscriptions, then components. The scope tears these
        ///     down in reverse, leaving the patch installed until every handler and component is
        ///     inactive.
        /// </summary>
        public void Enable(ModuleScope scope)
        {
            Generation = scope.Generation;
            CurrentScope = scope;

            // The Awake postfix must be installed before the scan, or a door that appears between
            // them is reached by neither path.
            scope.PatchAll(typeof(Patches));

            // Named handlers let the scope remove the same delegates that it added.
            scope.Subscribe<Action<Player, ZNetView>>(
                h => Hooks.OnPlayerAwake += h,
                h => Hooks.OnPlayerAwake -= h,
                OnPlayerAwake);
            scope.Subscribe<Action<Player, bool>>(
                h => Hooks.OnPlayerUpdate += h,
                h => Hooks.OnPlayerUpdate -= h,
                OnPlayerUpdate);
            scope.Subscribe<Action<Player, float>>(
                h => Hooks.OnPlayerFixedUpdate += h,
                h => Hooks.OnPlayerFixedUpdate -= h,
                OnPlayerFixedUpdate);
            scope.Subscribe<Action<float>>(
                h => Hooks.OnDedicatedServerFixedUpdate += h,
                h => Hooks.OnDedicatedServerFixedUpdate -= h,
                OnDedicatedServerFixedUpdate);

            ReconcileDoors(scope);
            Active = true;
        }

        /// <summary>
        ///     Stops new automatic open and close work before clearing session bookkeeping. The
        ///     scope removes its resources; this method deliberately never closes a door.
        /// </summary>
        public void Disable(ModuleScope scope)
        {
            Active = false;
            Generation = 0;
            CurrentScope = null;
            ResetState();
        }

        [AutomaticsInitializer(1)]
        private static void Initialize()
        {
            ModuleRegistry.Register(Instance);
        }

        /// <summary>
        ///     A new player marks a new world session. This only clears bookkeeping, so it remains
        ///     safe and useful when a previously fetched invocation list reaches it after a stop.
        /// </summary>
        private static void OnPlayerAwake(Player player, ZNetView nview)
        {
            ResetState();
        }

        internal static void AttachAutomaticDoor(GameObject target)
        {
            if (!Discovering) return;

            CurrentScope.TryAttach<AutomaticDoor>(target, out _);
        }

        private static void ReconcileDoors(ModuleScope scope)
        {
            foreach (var door in UnityEngine.Object.FindObjectsByType<Door>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!Objects.GetZNetView(door, out var zNetView)) continue;
                if (zNetView.GetZDO() == null) continue;

                scope.Attach<AutomaticDoor>(door.gameObject);
            }
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
            if (!Running) return;
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
                    var type = messagePosition.ToMessageType();
                    player.Message(type, message);
                }
            }
        }

        private static void OnDedicatedServerFixedUpdate(float delta)
        {
            if (!Running) return;

            UpdateAutomaticDoor(Player.GetAllPlayers(), delta);
        }

        private static void OnPlayerFixedUpdate(Player player, float delta)
        {
            if (!Running) return;
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
