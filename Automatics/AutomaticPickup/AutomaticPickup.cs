using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    [DisallowMultipleComponent]
    internal sealed class AutomaticPickup : MonoBehaviour
    {
        private Player _player;
        private float _pickupTimer;
        private bool _active;

        private void Awake()
        {
            _player = GetComponent<Player>();
            if (_player.IsOwner())
                StartCoroutine(nameof(Pickup));
        }

        private void OnDestroy()
        {
            if (_player.IsOwner())
                StopCoroutine(nameof(Pickup));
            _player = null;
        }

        [SuppressMessage("ReSharper", "IteratorNeverReturns")]
        private IEnumerator Pickup()
        {
            while (true)
            {
                while (!_active) yield return new WaitForSeconds(0.1f);
                _active = false;

                if (Config.PickupAllNearbyKey.MainKey == KeyCode.None)
                {
                    PickupAllNearby(_player, (Pickable x) => true);
                    yield return null;
                    PickupAllNearby(_player, (PickableItem x) => true);
                }
                else
                {
                    var hovering = Reflections.GetField<GameObject>(_player, "m_hovering");
                    if (!hovering) continue;

                    var lastHoverInteractTime =
                        Reflections.GetField<float>(_player, "m_lastHoverInteractTime");
                    if (Time.time - lastHoverInteractTime < 0.20000000298023224) continue;
                    Reflections.SetField(_player, "m_lastHoverInteractTime", Time.time);

                    var pickable = hovering.GetComponentInParent<Pickable>();
                    if (pickable)
                    {
                        var pickableName = GetPickableName(pickable);
                        PickupAllNearby(_player, x => GetPickableName(x) == pickableName);
                        Reflections.InvokeMethod(_player, "DoInteractAnimation",
                            hovering.transform.position);
                        continue;
                    }

                    yield return null;

                    var pickableItem = hovering.GetComponentInParent<PickableItem>();
                    if (pickableItem)
                    {
                        var itemName = GetPickableItemName(pickableItem);
                        PickupAllNearby(_player, x => GetPickableItemName(x) == itemName);
                        Reflections.InvokeMethod(_player, "DoInteractAnimation",
                            hovering.transform.position);
                        continue;
                    }

                    yield return null;

                    var itemDrop = hovering.GetComponentInParent<ItemDrop>();
                    if (itemDrop)
                    {
                        var itemName = GetItemDropName(itemDrop);
                        PickupAllNearby(_player, x => GetItemDropName(x) == itemName);
                        Reflections.InvokeMethod(_player, "DoInteractAnimation",
                            hovering.transform.position);
                        continue;
                    }
                }
            }
        }

        private void Update()
        {
            if (Game.IsPaused()) return;
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;
            if (_player.InAttack() || _player.InDodge()) return;
            if (!Reflections.InvokeMethod<bool>(_player, "TakeInput")) return;
            if (!Config.PickupAllNearbyKey.IsDown()) return;

            _active = true;
        }

        private void FixedUpdate()
        {
            if (Game.IsPaused()) return;
            if (Config.PickupAllNearbyKey.MainKey != KeyCode.None) return;
            if (Config.AutomaticPickupInterval <= 0) return;

            _pickupTimer += Time.deltaTime;
            if (_pickupTimer < Config.AutomaticPickupInterval) return;
            _pickupTimer = 0f;

            _active = true;
        }

        private static string GetPickableName(Pickable pickable)
        {
            return pickable.GetHoverName();
        }

        private static string GetPickableItemName(PickableItem pickableItem)
        {
            return !pickableItem.m_itemPrefab
                ? ""
                : pickableItem.m_itemPrefab.m_itemData.m_shared.m_name;
        }

        private static string GetItemDropName(ItemDrop itemDrop)
        {
            return itemDrop.m_itemData.m_shared.m_name;
        }

        private static void PickupAllNearby(Player player, Predicate<PickableItem> predicate)
        {
            var origin = player.transform.position;
            var playerId = player.GetPlayerID();

            var range = Config.AutomaticPickupRange;
            foreach (var pickableItem in PickableItemCache.GetAllInstance())
            {
                if (Vector3.Distance(origin, pickableItem.transform.position) > range) continue;
                if (!predicate.Invoke(pickableItem)) continue;
                if (Objects.GetZNetView(pickableItem, out var zNetView) && zNetView.IsValid())
                    zNetView.InvokeRPC("AutoPickup", playerId);
            }
        }

        private static void PickupAllNearby(Player player, Predicate<Pickable> predicate)
        {
            var origin = player.transform.position;
            var playerId = player.GetPlayerID();

            var range = Config.AutomaticPickupRange;
            foreach (var pickable in PickableCache.GetAllInstance())
            {
                if (Vector3.Distance(origin, pickable.transform.position) > range) continue;
                if (!predicate.Invoke(pickable)) continue;

                if (pickable.m_tarPreventsPicking)
                {
                    var floating = Reflections.GetField<Floating>(pickable, "m_floating");
                    if (!floating)
                    {
                        floating = pickable.GetComponent<Floating>();
                        if (floating)
                            Reflections.SetField(pickable, "m_floating", floating);
                    }

                    if (floating && floating.IsInTar())
                        continue;
                }

                if (Objects.GetZNetView(pickable, out var zNetView) && zNetView.IsValid())
                    zNetView.InvokeRPC("AutoPickup", playerId);
            }
        }

        private static void PickupAllNearby(Player player, Predicate<ItemDrop> predicate)
        {
            var origin = player.transform.position;
            var playerId = player.GetPlayerID();

            var range = Config.AutomaticPickupRange;
            foreach (var itemDrop in GetAllItemDrop())
            {
                if (Vector3.Distance(origin, itemDrop.transform.position) > range) continue;
                if (!predicate.Invoke(itemDrop)) continue;
                if (itemDrop.InTar()) continue;
                if (Objects.GetZNetView(itemDrop, out var zNetView) && zNetView.IsValid())
                    zNetView.InvokeRPC("AutoPickup", playerId);
            }
        }

        private static IEnumerable<ItemDrop> GetAllItemDrop()
        {
            return Reflections.GetStaticField<ItemDrop, List<ItemDrop>>("m_instances") ??
                   Enumerable.Empty<ItemDrop>();
        }
    }
}