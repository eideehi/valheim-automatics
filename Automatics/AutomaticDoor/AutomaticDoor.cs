using System.Collections.Generic;
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

        private bool IsDoorOpen => _zNetView && _zNetView.GetZDO().GetInt("state") != 0;

        private bool CanDoorInteract => _door &&
                                        PrivateArea.CheckAccess(_door.transform.position) &&
                                        Reflection.InvokeMethod<bool>(_door, "CanInteract");

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

            _door = GetComponent<Door>();
            _zNetView = GetComponent<ZNetView>();

            InvokeRepeating(nameof(UpdateDoor), Random.Range(1f, 2f), 0.1f);
        }

        private void OnDestroy()
        {
            CancelInvoke();

            _door = null;
            _zNetView = null;

            AutomaticDoors.Remove(this);
        }

        private void UpdateDoor()
        {
            const string openDoor = nameof(OpenDoor);
            const string closeDoor = nameof(CloseDoor);

            if (!Config.AutomaticDoorEnabled || !CanDoorInteract)
            {
                CancelInvoke(openDoor);
                CancelInvoke(closeDoor);
                return;
            }

            var player = Player.GetClosestPlayer(_door.transform.position, Config.PlayerSearchRadiusToOpen);
            if (IsDoorOpen)
            {
                var isInvoking = IsInvoking(closeDoor);
                if (!player && !isInvoking)
                {
                    Invoke(closeDoor, Config.IntervalToClose - 0.1f);
                }
                else if (player && isInvoking)
                {
                    CancelInvoke(closeDoor);
                }
            }
            else
            {
                var isInvoking = IsInvoking(openDoor);
                if (player && !isInvoking)
                {
                    Invoke(openDoor, Config.IntervalToOpen - 0.1f);
                }
                else if (!player && isInvoking)
                {
                    CancelInvoke(openDoor);
                }
            }
        }

        private void CloseDoor()
        {
            if (!Config.AutomaticDoorEnabled) return;
            if (!IsDoorOpen || !CanDoorInteract) return;
            if (Player.IsPlayerInRange(_door.transform.position, Config.PlayerSearchRadiusToClose)) return;

            _zNetView.InvokeRPC("UseDoor", false);
        }

        private void OpenDoor()
        {
            if (!Config.AutomaticDoorEnabled) return;
            if (IsDoorOpen || !CanDoorInteract) return;

            var player = Player.GetClosestPlayer(_door.transform.position, Config.PlayerSearchRadiusToOpen);
            if (!player) return;

            _door.Interact(player, false, false);
        }
    }
}