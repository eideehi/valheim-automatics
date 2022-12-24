using System;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    [DisallowMultipleComponent]
    internal class ItemPicker : MonoBehaviour
    {
        private Component _component;
        private ZNetView _zNetView;

        private void Awake()
        {
            _component = GetItemComponent();

            if (!_component)
                throw new Exception("Failed to acquire Component.");

            if (!Objects.GetZNetView(_component, out _zNetView))
                throw new Exception("Failed to acquire ZNetView.");

            if (_zNetView.GetZDO() != null)
                _zNetView.Register<long>("AutoPickup", RPC_AutoPickup);
        }

        private void OnDestroy()
        {
            _component = null;
            _zNetView = null;
        }

        private Component GetItemComponent()
        {
            var pickable = GetComponent<Pickable>();
            if (pickable) return pickable;
            var pickableItem = GetComponent<PickableItem>();
            if (pickableItem) return pickableItem;
            var itemDrop = GetComponent<ItemDrop>();
            if (itemDrop) return itemDrop;
            return null;
        }

        private void RPC_AutoPickup(long sender, long player)
        {
            switch (_component)
            {
                case Pickable pickable:
                    AutoPickup(player, pickable);
                    break;
                case PickableItem pickableItem:
                    AutoPickup(player, pickableItem);
                    break;
                case ItemDrop itemDrop:
                    AutoPickup(player, itemDrop);
                    break;
            }
        }

        private void AutoPickup(long playerId, Pickable pickable)
        {
            var player = Player.GetPlayer(playerId);
            if (!player) return;

            if (!_zNetView.IsOwner() || Reflections.GetField<bool>(pickable, "m_picked"))
                return;

            var inventory = player.GetInventory();
            if (!inventory.CanAddItem(pickable.m_itemPrefab, pickable.m_amount))
                return;

            pickable.m_pickEffector.Create(
                pickable.m_pickEffectAtSpawnPoint
                    ? pickable.transform.position + Vector3.up * pickable.m_spawnOffset
                    : pickable.transform.position, Quaternion.identity);

            inventory.AddItem(pickable.m_itemPrefab, pickable.m_amount);

            if (!pickable.m_extraDrops.IsEmpty())
            {
                var offset = 0;
                foreach (var item in pickable.m_extraDrops.GetDropListItems())
                {
                    if (!inventory.CanAddItem(item.m_dropPrefab, item.m_stack))
                    {
                        Reflections.InvokeMethod(pickable, "Drop", item.m_dropPrefab, offset++,
                            item.m_stack);
                        continue;
                    }

                    inventory.AddItem(item.m_dropPrefab, item.m_stack);
                }
            }

            if (pickable.m_aggravateRange > 0f)
                BaseAI.AggravateAllInArea(pickable.transform.position, pickable.m_aggravateRange,
                    BaseAI.AggravatedReason.Theif);

            _zNetView.InvokeRPC(ZNetView.Everybody, "SetPicked", true);
        }

        private void AutoPickup(long playerId, PickableItem pickableItem)
        {
            var player = Player.GetPlayer(playerId);
            if (!player) return;

            if (!_zNetView.IsOwner() || Reflections.GetField<bool>(pickableItem, "m_picked"))
                return;

            var stackSize = Reflections.InvokeMethod<int>(pickableItem, "GetStackSize");
            var inventory = player.GetInventory();
            if (!inventory.CanAddItem(pickableItem.m_itemPrefab.m_itemData, stackSize))
                return;

            Reflections.SetField(pickableItem, "m_picked", true);
            pickableItem.m_pickEffector.Create(pickableItem.transform.position,
                Quaternion.identity);
            inventory.AddItem(pickableItem.m_itemPrefab.gameObject, stackSize);
            _zNetView.Destroy();
        }

        private void AutoPickup(long playerId, ItemDrop itemDrop)
        {
            var player = Player.GetPlayer(playerId);
            if (!player) return;
            if (!player.GetInventory().CanAddItem(itemDrop.m_itemData)) return;

            itemDrop.Pickup(player);
        }
    }
}