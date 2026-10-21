using System;
using System.Collections.Generic;
using HarmonyLib;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticDoor
{
    [DisallowMultipleComponent]
    internal sealed class AutomaticDoor : MonoBehaviour, IScopedComponent
    {
        private const float PredictionLookAheadSeconds = 0.35f;
        private const float MaxPredictionDistance = 1.5f;
        private const float MinPredictionSpeed = 1f;
        private const float MinApproachDot = 0.35f;
        private const float CloseGracePeriod = 0.35f;
        private const string PrivateAreaAllAreasField = "m_allAreas";
        private const string PrivateAreaPieceField = "m_piece";
        private const string PrivateAreaZNetViewField = "m_nview";

        private static readonly Lazy<int> LazyPieceMask;
        private static readonly IList<AutomaticDoor> AllInstance;

        // Hot-path (10 Hz, per door, per player) Door members resolved once into
        // cached delegates/field-refs instead of allocating a Traverse wrapper +
        // params object[] on every call. Each bind is guarded so a future Valheim
        // rename only disables that one accessor and falls back to Reflections.
        private static readonly Action<Door, Vector3> OpenInvoker;
        private static readonly Func<Door, bool> CanInteractInvoker;
        private static readonly AccessTools.FieldRef<Character, Collider> ColliderRef;

        private static int PieceMask => LazyPieceMask.Value;

        private Door _door;
        private Transform _transform;
        private ZNetView _zNetView;
        private bool _allowAutomaticDoor;
        private bool _allowAutomaticDoorDirty;
        private float _lastAutomaticOpenTime;

        public static bool HasRegisteredDoor => AllInstance.Count > 0;
        internal static int RegisteredDoorCount => AllInstance.Count;
        public int ScopeGeneration { get; set; }

        static AutomaticDoor()
        {
            LazyPieceMask = new Lazy<int>(() =>
                LayerMask.GetMask("Default", "static_solid", "Default_small", "piece",
                    "piece_nonsolid", "terrain", "vehicle"));
            AllInstance = new List<AutomaticDoor>();

            OpenInvoker = BindMethod<Action<Door, Vector3>>(
                typeof(Door), "Open", new[] { typeof(Vector3) });
            CanInteractInvoker = BindMethod<Func<Door, bool>>(
                typeof(Door), "CanInteract", Type.EmptyTypes);
            ColliderRef = BindColliderRef();
        }

        private static TDelegate BindMethod<TDelegate>(Type type, string name,
            Type[] parameters) where TDelegate : Delegate
        {
            try
            {
                var method = AccessTools.Method(type, name, parameters);
                if (method != null) return AccessTools.MethodDelegate<TDelegate>(method);

                Automatics.Logger.Warning(() =>
                    $"{type.Name}.{name} not found; falling back to reflection.");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind {type.Name}.{name} delegate; falling back to reflection: {e.Message}");
            }

            return null;
        }

        private static AccessTools.FieldRef<Character, Collider> BindColliderRef()
        {
            try
            {
                // Character.m_collider is declared as CapsuleCollider, but AccessTools binds a
                // reference-typed field whenever the requested type is assignable from it
                // (AccessTools.ValidateFieldType). Requesting the base Collider therefore binds
                // today and keeps binding if the field's concrete type changes again.
                return AccessTools.FieldRefAccess<Character, Collider>("m_collider");
            }
            catch (Exception e)
            {
                Automatics.Logger.Warning(() =>
                    $"Failed to bind Character.m_collider field ref; falling back to reflection: {e.Message}");
                return null;
            }
        }

        private void Awake()
        {
            _door = GetComponent<Door>();
            _transform = transform;
            Objects.GetZNetView(_door, out _zNetView);

            _allowAutomaticDoorDirty = true;
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

            _zNetView = null;
            _transform = null;
            _door = null;
        }

        public static void MarkAllowAutomaticDoorDirty()
        {
            if (AllInstance.Count == 0) return;

            foreach (var automaticDoor in AllInstance)
                if (automaticDoor)
                    automaticDoor._allowAutomaticDoorDirty = true;
        }

        public static void TryOpenNearby(Player player, Vector3 velocity)
        {
            if (!Module.Running) return;
            if (!player) return;

            var openRange = Config.DistanceForAutomaticOpening;
            var searchRange = openRange + GetPredictionDistance(velocity);
            var searchRangeSquared = searchRange * searchRange;

            for (var index = AllInstance.Count - 1; index >= 0; index--)
            {
                var automaticDoor = AllInstance[index];
                if (!IsRegistered(automaticDoor))
                {
                    AllInstance.RemoveAt(index);
                    continue;
                }

                automaticDoor.TryOpen(player, velocity, searchRangeSquared, openRange);
            }
        }

        public static void TryCloseDoors()
        {
            if (!Module.Running) return;
            var closeRange = Config.DistanceForAutomaticClosing;
            var holdOpenRange = Config.IntervalToOpen >= 0.1f
                ? Mathf.Max(closeRange, Config.DistanceForAutomaticOpening)
                : closeRange;
            var players = Player.GetAllPlayers();
            for (var index = AllInstance.Count - 1; index >= 0; index--)
            {
                var automaticDoor = AllInstance[index];
                if (!IsRegistered(automaticDoor))
                {
                    AllInstance.RemoveAt(index);
                    continue;
                }

                automaticDoor.TryClose(players, holdOpenRange);
            }
        }

        private static bool IsRegistered(AutomaticDoor automaticDoor)
        {
            return automaticDoor && automaticDoor._door && automaticDoor._transform &&
                   automaticDoor._zNetView != null;
        }

        private static float GetPredictionDistance(Vector3 velocity)
        {
            var speed = velocity.magnitude;
            if (speed < MinPredictionSpeed) return 0f;

            return Mathf.Min(speed * PredictionLookAheadSeconds, MaxPredictionDistance);
        }

        private void TryOpen(Player player, Vector3 velocity, float searchRangeSquared,
            float openRange)
        {
            if (!IsValid()) return;
            if (!IsAllowAutomaticDoor()) return;
            if (IsDoorOpen()) return;

            // Reject on the cheap squared-distance test before the reflective
            // CanInteract/CanOpen gates, so reflection never runs for doors the
            // player is too far from.
            var playerPosition = player.transform.position;
            var doorPosition = _transform.position;
            if ((doorPosition - playerPosition).sqrMagnitude > searchRangeSquared) return;

            if (!CanInteract(player)) return;
            if (!CanOpen(player)) return;
            if (!ShouldOpen(playerPosition, velocity, openRange)) return;
            if (IsExistsObstaclesBetweenTo(player)) return;

            if (_door.m_keyItem && Player.m_localPlayer == player)
                player.Message(MessageHud.MessageType.Center,
                    Localization.instance.Localize("$msg_door_usingkey",
                        _door.m_keyItem.m_itemData.m_shared.m_name));

            OpenDoor((playerPosition - doorPosition).normalized);
            _lastAutomaticOpenTime = Time.time;
        }

        // A player who could open the door holds it open while within reach, whether or not a
        // piece stands between them and the door. Otherwise the door is closed: the game closes
        // an open door whatever direction it is used from, and refuses on its own while the door
        // is moving or cannot be closed.
        private void TryClose(IEnumerable<Player> players, float holdOpenRange)
        {
            if (!IsValid()) return;
            if (!IsAllowAutomaticDoor()) return;
            if (!IsDoorOpen()) return;
            if (Time.time - _lastAutomaticOpenTime < CloseGracePeriod) return;

            var holdOpenRangeSquared = holdOpenRange * holdOpenRange;
            var doorPosition = _transform.position;

            foreach (var player in players)
            {
                if (!player) continue;
                if ((player.transform.position - doorPosition).sqrMagnitude > holdOpenRangeSquared)
                    continue;
                if (CanInteract(player) && CanOpen(player)) return;
            }

            OpenDoor(_transform.forward);
        }

        private void OpenDoor(Vector3 direction)
        {
            if (OpenInvoker != null)
                OpenInvoker(_door, direction);
            else
                Reflections.InvokeMethod(_door, "Open", direction);
        }

        private bool IsAllowAutomaticDoor()
        {
            if (!_allowAutomaticDoorDirty) return _allowAutomaticDoor;

            // Doors inside a dungeon are never opened or closed, whatever the list allows. The
            // game places dungeon interiors 5000 m above their entrance and treats everything
            // above 3000 m as indoors; a door never moves between the two, so the result is
            // cached with the list check.
            _allowAutomaticDoor = !Character.InInterior(_transform.position) &&
                                  Logics.IsAllowAutomaticDoor(_door);
            _allowAutomaticDoorDirty = false;
            return _allowAutomaticDoor;
        }

        private bool IsValid()
        {
            return Objects.HasValidOwnership(_zNetView);
        }

        private bool IsDoorOpen()
        {
            return _zNetView.GetZDO().GetInt("state") != 0;
        }

        private bool CanInteract(Player player)
        {
            if (_door.m_checkGuardStone && !CheckWardAccess(player, _transform.position))
                return false;
            return CanInteractInvoker != null
                ? CanInteractInvoker(_door)
                : Reflections.InvokeMethod<bool>(_door, "CanInteract");
        }

        private static bool CheckWardAccess(Player player, Vector3 point)
        {
            if (!player) return false;

            var areas = Reflections.GetStaticField<PrivateArea, List<PrivateArea>>(PrivateAreaAllAreasField);
            if (areas == null) return false;

            // Same rule as vanilla PrivateArea.CheckAccess without its ward check: the player may
            // use the door when any enabled ward covering it grants access, or when none covers
            // it. A permitted player is therefore not blocked by an overlapping ward they have no
            // access to. IsInside (a cheap XZ distance test) runs before IsEnabled (a reflective
            // field read and ZDO lookup) so the reflection is skipped for out-of-range wards.
            var covered = false;
            foreach (var area in areas)
            {
                if (!area || !IsInside(area, point, 0f) || !IsEnabled(area)) continue;
                if (HasPlayerAccess(area, player)) return true;
                covered = true;
            }

            return !covered;
        }

        private static bool IsEnabled(PrivateArea area)
        {
            if (!area) return false;

            var zNetView = Reflections.GetField<ZNetView>(area, PrivateAreaZNetViewField);
            return zNetView != null &&
                   zNetView.IsValid() &&
                   zNetView.GetZDO().GetBool(ZDOVars.s_enabled);
        }

        private static bool IsInside(PrivateArea area, Vector3 point, float radius)
        {
            return Utils.DistanceXZ(area.transform.position, point) < area.m_radius + radius;
        }

        private static bool HasPlayerAccess(PrivateArea area, Player player)
        {
            var playerId = player.GetPlayerID();
            if (playerId == 0L) return false;

            var piece = Reflections.GetField<Piece>(area, PrivateAreaPieceField);
            if (piece && piece.GetCreator() == playerId)
                return true;

            var zNetView = Reflections.GetField<ZNetView>(area, PrivateAreaZNetViewField);
            if (zNetView == null || !zNetView.IsValid()) return false;

            var zdo = zNetView.GetZDO();
            var permittedCount = zdo.GetInt(ZDOVars.s_permitted);
            for (var index = 0; index < permittedCount; index++)
                if (zdo.GetLong("pu_id" + index, 0L) == playerId)
                    return true;

            return false;
        }

        private bool CanOpen(Player player)
        {
            // Door.HaveKey(Humanoid player, bool matchWorldLevel = true): Traverse resolves the
            // method from the runtime types of the arguments passed. Reference types bind by
            // assignability (Player satisfies Humanoid), but a defaulted parameter is never
            // filled in for us, so the arity must match or resolution fails and the call throws.
            // true mirrors the vanilla Door.Interact eligibility check.
            return !_door.m_keyItem ||
                   Reflections.InvokeMethod<bool>(_door, "HaveKey", player, true);
        }

        private bool ShouldOpen(Vector3 playerPosition, Vector3 velocity, float openRange)
        {
            var toDoor = _transform.position - playerPosition;
            var openRangeSquared = openRange * openRange;
            if (toDoor.sqrMagnitude <= openRangeSquared)
                return true;

            var predictionDistance = GetPredictionDistance(velocity);
            if (predictionDistance <= 0f)
                return false;

            var speed = velocity.magnitude;
            var moveDirection = velocity / speed;
            if (Vector3.Dot(moveDirection, toDoor.normalized) < MinApproachDot)
                return false;

            var predictedPosition = playerPosition + moveDirection * predictionDistance;
            return (_transform.position - predictedPosition).sqrMagnitude <= openRangeSquared;
        }

        private bool IsExistsObstaclesBetweenTo(Player player)
        {
            // Unity's lifetime check (implicit bool), not `?.`: a destroyed Collider
            // is non-null to C# but throws on .bounds.
            var collider = ColliderRef != null
                ? ColliderRef(player)
                : Reflections.GetField<Collider>(player, "m_collider");
            var from = collider ? collider.bounds.center : player.m_eye.position;
            var to = _transform.position;

            if (!Physics.Linecast(from, to, out var hitInfo, PieceMask)) return false;

            var hitDoor = hitInfo.collider.GetComponentInParent<Door>();
            if (hitDoor) return hitDoor != _door;

            return hitInfo.collider.GetComponentInParent<Piece>();
        }
    }
}
