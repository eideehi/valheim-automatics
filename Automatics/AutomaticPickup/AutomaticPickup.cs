using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Automatics.Valheim;
using ModUtils;
using UnityEngine;

namespace Automatics.AutomaticPickup
{
    [DisallowMultipleComponent]
    internal sealed class AutomaticPickup : MonoBehaviour
    {
        // Reused snapshot buffers so each pickup pass does not allocate a fresh
        // List by enumerating the instance caches (Fill copies into these).
        private static readonly List<Pickable> PickableBuffer = new List<Pickable>();
        private static readonly List<PickableItem> PickableItemBuffer =
            new List<PickableItem>();
        private static readonly List<ItemDrop> ItemDropBuffer = new List<ItemDrop>();
        private static readonly List<ItemDrop> EmptyItemDrops = new List<ItemDrop>();

        private readonly WaitForSeconds _idleWait = new WaitForSeconds(0.1f);
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
                while (!_active) yield return _idleWait;
                _active = false;

                if (Config.PickupAllNearbyKey.MainKey == KeyCode.None)
                {
                    PickupAllNearby(_player,
                        (Pickable x) => !AutomaticFarming.AutomaticFarming.ClaimsCrop(_player, x));
                    yield return null;
                    PickupAllNearby(_player, (PickableItem x) => true);
                    yield return null;
                    PickupAllNearby(_player, (ItemDrop x) => x.m_autoPickup);
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
                            pickable.gameObject);
                        continue;
                    }

                    yield return null;

                    var pickableItem = hovering.GetComponentInParent<PickableItem>();
                    if (pickableItem)
                    {
                        var itemName = GetPickableItemName(pickableItem);
                        PickupAllNearby(_player, x => GetPickableItemName(x) == itemName);
                        Reflections.InvokeMethod(_player, "DoInteractAnimation",
                            pickableItem.gameObject);
                        continue;
                    }

                    yield return null;

                    var itemDrop = hovering.GetComponentInParent<ItemDrop>();
                    if (itemDrop)
                    {
                        var itemName = GetItemDropName(itemDrop);
                        PickupAllNearby(_player, x => GetItemDropName(x) == itemName);
                        Reflections.InvokeMethod(_player, "DoInteractAnimation",
                            itemDrop.gameObject);
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

            var range = Config.AutomaticPickupRange;
            var rangeSq = range * range;
            PickableItemCache.Fill(PickableItemBuffer);
            for (var i = 0; i < PickableItemBuffer.Count; i++)
            {
                var pickableItem = PickableItemBuffer[i];
                if (!pickableItem) continue;
                if ((origin - pickableItem.transform.position).sqrMagnitude > rangeSq) continue;
                if (!predicate.Invoke(pickableItem)) continue;
                if (Objects.GetZNetView(pickableItem, out var zNetView) && zNetView.IsValid())
                    // Isolate per-object failures so one bad pickable never aborts
                    // the pass or tears down the picking coroutine.
                    try
                    {
                        PickPickableItem(player, pickableItem, zNetView);
                    }
                    catch (Exception e)
                    {
                        Automatics.Logger.Debug(() => $"Pickup skipped object: {e}");
                    }
            }
        }

        private static void PickupAllNearby(Player player, Predicate<Pickable> predicate)
        {
            var origin = player.transform.position;

            var range = Config.AutomaticPickupRange;
            var rangeSq = range * range;
            PickableCache.Fill(PickableBuffer);
            for (var i = 0; i < PickableBuffer.Count; i++)
            {
                var pickable = PickableBuffer[i];
                if (!pickable) continue;
                if ((origin - pickable.transform.position).sqrMagnitude > rangeSq) continue;
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
                    try
                    {
                        PickableHarvester.Harvest(player, pickable, zNetView);
                    }
                    catch (Exception e)
                    {
                        Automatics.Logger.Debug(() => $"Pickup skipped object: {e}");
                    }
            }
        }

        private static void PickupAllNearby(Player player, Predicate<ItemDrop> predicate)
        {
            var origin = player.transform.position;

            var range = Config.AutomaticPickupRange;
            var rangeSq = range * range;
            // Snapshot the game's live ItemDrop list: ItemDrop.Pickup can remove
            // entries mid-pass, and forward-indexing the live list would skip the
            // item that shifts into the removed slot.
            ItemDropBuffer.Clear();
            ItemDropBuffer.AddRange(GetAllItemDrop());
            for (var i = 0; i < ItemDropBuffer.Count; i++)
            {
                var itemDrop = ItemDropBuffer[i];
                if (!itemDrop) continue;
                if (itemDrop.IsPiece()) continue;
                if ((origin - itemDrop.transform.position).sqrMagnitude > rangeSq) continue;
                if (!predicate.Invoke(itemDrop)) continue;
                if (itemDrop.InTar()) continue;
                if (Objects.GetZNetView(itemDrop, out var zNetView) && zNetView.GetZDO() != null)
                    try
                    {
                        PickItemDrop(player, itemDrop);
                    }
                    catch (Exception e)
                    {
                        Automatics.Logger.Debug(() => $"Pickup skipped object: {e}");
                    }
            }
        }

        private static void PickPickableItem(Player player, PickableItem pickableItem,
            ZNetView zNetView)
        {
            if (Reflections.GetField<bool>(pickableItem, "m_picked")) return;
            // Guard the prefab the way GetPickableItemName already does: vanilla
            // GetStackSize falls back to m_itemPrefab.m_itemData when the ZDO has
            // no stored stack, so this must precede the GetStackSize call below.
            if (!pickableItem.m_itemPrefab) return;

            var stackSize = Reflections.InvokeMethod<int>(pickableItem, "GetStackSize");
            if (stackSize <= 0) return;
            if (!PickableHarvester.CanAddItem(player, pickableItem.m_itemPrefab.gameObject, stackSize))
                return;

            if (!zNetView.IsOwner())
            {
                pickableItem.Interact(player, false, false);
                return;
            }

            pickableItem.m_pickEffector.Create(pickableItem.transform.position, Quaternion.identity);
            var addedStack = PickableHarvester.AddItem(player.GetInventory(),
                pickableItem.m_itemPrefab.gameObject, stackSize);
            if (addedStack <= 0) return;

            Reflections.SetField(pickableItem, "m_picked", true);
            if (addedStack < stackSize)
                DropPickableItemRemainder(pickableItem, stackSize - addedStack);
            zNetView.Destroy();
        }

        private static void PickItemDrop(Player player, ItemDrop itemDrop)
        {
            if (!PickableHarvester.CanAddItem(player, itemDrop.m_itemData)) return;
            itemDrop.Pickup(player);
        }

        private static void DropPickableItemRemainder(PickableItem pickableItem, int stack)
        {
            if (stack <= 0) return;

            var position = pickableItem.transform.position + Vector3.up * 0.2f;
            var obj = UnityEngine.Object.Instantiate(pickableItem.m_itemPrefab.gameObject, position,
                pickableItem.transform.rotation);
            var itemDrop = obj.GetComponent<ItemDrop>();
            if (itemDrop != null)
            {
                itemDrop.m_itemData.m_stack = stack;
                ItemDrop.OnCreateNew(itemDrop);
            }

            var rigidbody = obj.GetComponent<Rigidbody>();
            if (rigidbody)
                rigidbody.linearVelocity = Vector3.up * 4f;
        }

        private static List<ItemDrop> GetAllItemDrop()
        {
            return Reflections.GetStaticField<ItemDrop, List<ItemDrop>>("s_instances") ??
                   EmptyItemDrops;
        }
    }
}
