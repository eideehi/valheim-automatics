using System.Collections.Generic;
using Automatics.ModUtils;
using UnityEngine;

namespace Automatics.AutomaticDoor
{
    [DisallowMultipleComponent]
    internal class AutomaticDoor : MonoBehaviour
    {
        private static readonly List<AutomaticDoor> AutomaticDoors;

        static AutomaticDoor()
        {
            AutomaticDoors = new List<AutomaticDoor>();
        }

        private Door _door;
        private ZNetView _zNetView;
        private bool _active;
        private Status _status;
        private Player _closestPlayer;

        public static void ChangeInterval(bool isChangedOpenInterval)
        {
            AutomaticDoors.ForEach(x => { x.CancelInvoke(isChangedOpenInterval ? nameof(Open) : nameof(Close)); });
        }

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

        private void Run()
        {
            const string open = nameof(Open);
            const string close = nameof(Close);

            _active = Config.AutomaticDoorEnabled && IsValid() && CheckAccess() && CanInteract();
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
            var radius = isOpen ? Config.PlayerSearchRadiusToClose : Config.PlayerSearchRadiusToOpen;

            _closestPlayer = Player.GetClosestPlayer(_door.transform.position, radius);

            var foundClosestPlayer = _closestPlayer != null;
            var invoke = isOpen ? close : open;
            var isInvoking = IsInvoking(invoke);

            if (!isInvoking && ((isOpen && !foundClosestPlayer) || (!isOpen && foundClosestPlayer)))
            {
                var interval = isOpen ? Config.IntervalToClose : Config.IntervalToOpen;
                Invoke(invoke, interval - 0.1f);
            }
            else if (isInvoking && ((isOpen && foundClosestPlayer) || (!isOpen && !foundClosestPlayer)))
            {
                CancelInvoke(invoke);
            }
        }

        private void Close()
        {
            if (IsValid() && _active && _status == Status.Open && _closestPlayer == null)
                _zNetView.InvokeRPC("UseDoor", false);
        }

        private void Open()
        {
            if (IsValid() && _active && _status == Status.Close && _closestPlayer != null)
                _door.Interact(_closestPlayer, false, false);
        }

        private bool IsValid()
        {
            return _door != null && _zNetView != null && _zNetView.IsValid();
        }

        private bool CheckAccess()
        {
            return PrivateArea.CheckAccess(_door.transform.position);
        }

        private bool CanInteract()
        {
            return Reflection.InvokeMethod<bool>(_door, "CanInteract");
        }

        private enum Status
        {
            Open,
            Close,
            Null
        }
    }
}