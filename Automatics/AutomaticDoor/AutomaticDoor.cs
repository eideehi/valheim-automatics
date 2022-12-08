using System;
using System.Collections.Generic;
using ModUtils;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Automatics.AutomaticDoor
{
    [DisallowMultipleComponent]
    internal class AutomaticDoor : MonoBehaviour
    {
        private static readonly Lazy<int> LazyPieceMask;
        private static readonly List<AutomaticDoor> AutomaticDoors;
        private bool _active;
        private Player _closestPlayer;

        private Door _door;
        private bool _noObstaclesBetweenPlayer;
        private Status _status;
        private ZNetView _zNetView;

        static AutomaticDoor()
        {
            LazyPieceMask = new Lazy<int>(() => LayerMask.GetMask("Default", "static_solid",
                "Default_small", "piece",
                "piece_nonsolid", "terrain", "vehicle"));
            AutomaticDoors = new List<AutomaticDoor>();
        }

        private static int PieceMask => LazyPieceMask.Value;

        private void Awake()
        {
            AutomaticDoors.Add(this);
            InvokeRepeating(nameof(Run), Random.Range(1f, 2f), 0.1f);

            _door = GetComponent<Door>();
            _zNetView = GetComponent<ZNetView>();
        }

        private void OnDestroy()
        {
            CancelInvoke();
            AutomaticDoors.Remove(this);

            _door = null;
            _zNetView = null;
            _closestPlayer = null;
        }

        public static void ChangeInterval(bool isChangedOpenInterval)
        {
            AutomaticDoors.ForEach(x =>
            {
                x.CancelInvoke(isChangedOpenInterval ? nameof(Open) : nameof(Close));
            });
        }

        private void Run()
        {
            const string open = nameof(Open);
            const string close = nameof(Close);

            _active = Config.EnableAutomaticDoor && IsValid() && CheckAccess() && CanInteract();
            _status = Status.Null;
            _closestPlayer = null;

            if (!_active)
            {
                CancelInvoke(open);
                CancelInvoke(close);
                return;
            }

            _status = _zNetView.GetZDO().GetInt("state") == 0 ? Status.Close : Status.Open;

            var isOpen = _status == Status.Open;
            var invoke = isOpen ? close : open;
            var interval = isOpen ? Config.IntervalToClose : Config.IntervalToOpen;

            if (interval < 0.1f)
            {
                CancelInvoke(invoke);
                return;
            }

            _closestPlayer = Player.GetClosestPlayer(_door.transform.position,
                isOpen ? Config.DistanceForAutomaticClosing : Config.DistanceForAutomaticOpening);
            _noObstaclesBetweenPlayer =
                _closestPlayer != null && !FindObstaclesBetween(_closestPlayer);

            var foundClosestPlayer = _closestPlayer != null;
            var isInvoking = IsInvoking(invoke);

            if (!isInvoking && ((isOpen && (!foundClosestPlayer || !_noObstaclesBetweenPlayer)) ||
                                (!isOpen && foundClosestPlayer && _noObstaclesBetweenPlayer)))
                Invoke(invoke, interval - 0.1f);
            else if (isInvoking && ((isOpen && foundClosestPlayer && _noObstaclesBetweenPlayer) ||
                                    (!isOpen && (!foundClosestPlayer ||
                                                 !_noObstaclesBetweenPlayer))))
                CancelInvoke(invoke);
        }

        private void Close()
        {
            if (IsValid() && _active && _status == Status.Open &&
                (_closestPlayer == null || !_noObstaclesBetweenPlayer))
                _zNetView.InvokeRPC("UseDoor", false);
        }

        private void Open()
        {
            if (IsValid() && _active && _status == Status.Close && _closestPlayer != null &&
                _noObstaclesBetweenPlayer)
                _door.Interact(_closestPlayer, false, false);
        }

        private bool IsValid()
        {
            return _door != null && _zNetView != null && _zNetView.IsValid() &&
                   Core.IsAllowAutomaticDoor(_door);
        }

        private bool FindObstaclesBetween(Player player)
        {
            var from = Reflections.GetField<Collider>(player, "m_collider")?.bounds.center ??
                       player.m_eye.position;
            var to = _door.transform.position;

            if (!Physics.Linecast(from, to, out var hitInfo, PieceMask)) return false;

            var door = hitInfo.collider.GetComponentInParent<Door>();
            if (door != null) return door != _door;

            return hitInfo.collider.GetComponentInParent<Piece>() != null;
        }

        private bool CheckAccess()
        {
            return PrivateArea.CheckAccess(_door.transform.position);
        }

        private bool CanInteract()
        {
            return Reflections.InvokeMethod<bool>(_door, "CanInteract");
        }

        private enum Status
        {
            Open,
            Close,
            Null
        }
    }
}