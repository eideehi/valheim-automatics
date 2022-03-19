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
        private DoorStatus _status;
        private Player _closestPlayer;

        public static void ChangeInterval(bool isChangedOpenInterval)
        {
            AutomaticDoors.ForEach(x =>
            {
                x.CancelInvoke(isChangedOpenInterval ? nameof(OpenDoor) : nameof(CloseDoor));
            });
        }

        private void Awake()
        {
            AutomaticDoors.Add(this);
            InvokeRepeating(nameof(UpdateDoor), Random.Range(1f, 2f), 0.1f);

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

        private void UpdateDoor()
        {
            _active = false;
            _status = DoorStatus.Null;
            _closestPlayer = null;

            if (!Config.AutomaticDoorEnabled || _door == null || _zNetView == null || !_zNetView.IsValid() ||
                !PrivateArea.CheckAccess(_door.transform.position) ||
                !Reflection.InvokeMethod<bool>(_door, "CanInteract"))
            {
                CancelInvoke(nameof(OpenDoor));
                CancelInvoke(nameof(CloseDoor));
                return;
            }

            _active = true;
            _status = _zNetView.GetZDO().GetInt("state") == 0 ? DoorStatus.Close : DoorStatus.Open;

            if (_status == DoorStatus.Open)
            {
                _closestPlayer = Player.GetClosestPlayer(_door.transform.position, Config.PlayerSearchRadiusToClose);
                var isInvoking = IsInvoking(nameof(CloseDoor));
                if (!_closestPlayer && !isInvoking)
                {
                    Invoke(nameof(CloseDoor), Config.IntervalToClose - 0.1f);
                }
                else if (_closestPlayer && isInvoking)
                {
                    CancelInvoke(nameof(CloseDoor));
                }
            }
            else
            {
                _closestPlayer = Player.GetClosestPlayer(_door.transform.position, Config.PlayerSearchRadiusToOpen);
                var isInvoking = IsInvoking(nameof(OpenDoor));
                if (_closestPlayer && !isInvoking)
                {
                    Invoke(nameof(OpenDoor), Config.IntervalToOpen - 0.1f);
                }
                else if (!_closestPlayer && isInvoking)
                {
                    CancelInvoke(nameof(OpenDoor));
                }
            }
        }

        private void CloseDoor()
        {
            if (!_active) return;
            if (_status != DoorStatus.Open || _closestPlayer) return;

            _zNetView.InvokeRPC("UseDoor", false);
        }

        private void OpenDoor()
        {
            if (!_active) return;
            if (_status != DoorStatus.Close || !_closestPlayer) return;

            _door.Interact(_closestPlayer, false, false);
        }

        private enum DoorStatus
        {
            Open,
            Close,
            Null
        }
    }
}