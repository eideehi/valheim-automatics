using System.Collections.Generic;
using HarmonyLib;
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

        public static void ChangeInterval(float interval)
        {
            AutomaticDoors.ForEach(x =>
            {
                x.CancelInvoke(nameof(UpdateDoor));
                x.InvokeRepeating(nameof(UpdateDoor), Random.Range(1f, 2f), interval);
            });
        }

        private void Awake()
        {
            AutomaticDoors.Add(this);

            _door = GetComponent<Door>();
            _zNetView = GetComponent<ZNetView>();

            InvokeRepeating(nameof(UpdateDoor), Random.Range(1f, 2f), Config.UpdateInterval);
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(UpdateDoor));

            _door = null;
            _zNetView = null;

            AutomaticDoors.Remove(this);
        }

        private void UpdateDoor()
        {
            if (!Config.AutomaticDoorEnabled) return;
            if (!Reflection.InvokeMethod<bool>(_door, "CanInteract")) return;
            if (!PrivateArea.CheckAccess(_door.transform.position)) return;

            var player = Player.GetClosestPlayer(_door.transform.position, Config.PlayerSearchRadius);
            var isClosed = _zNetView.GetZDO().GetInt("state") == 0;
            if (player && isClosed)
            {
                _door.Interact(player, false, false);
            }
            else if (!player && !isClosed)
            {
                _zNetView.InvokeRPC("UseDoor", false);
            }
        }
    }
}