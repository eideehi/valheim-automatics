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
    internal sealed class AutomaticPickup : MonoBehaviour, IScopedComponent
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

        /// <summary>
        ///     Set by the scope immediately before it destroys this component, and by OnDestroy.
        ///     Once true it never goes back: this component belongs to an activation that is
        ///     over, and a later activation gets a new component of its own.
        /// </summary>
        private bool _cancelled;

        /// <summary>
        ///     Whether an iterator is currently started. Owned by OnEnable and OnDisable.
        /// </summary>
        private bool _running;

        public int ScopeGeneration { get; set; }

        /// <summary>
        ///     Whether this component is still the one its module owns. Irreversible: everything
        ///     it looks at describes an activation that has already ended, so an iterator that
        ///     sees it false folds itself rather than idling. The stop request is deliberately
        ///     not here - see <see cref="Live" />.
        ///     Generation zero is the reserved "not attached" stamp, so it is never a match; a
        ///     component reads zero between AddComponent and the stamp the scope writes a
        ///     statement later.
        /// </summary>
        private bool Alive =>
            !_cancelled
            && Module.Active
            && ScopeGeneration != 0
            && ScopeGeneration == Module.Generation
            && _player;

        /// <summary>
        ///     Whether this component may act this frame. Everything reversible lives here: the
        ///     stop request can be lowered again before the pump ever runs, and ownership of a
        ///     Player can come back. An iterator that sees this false goes back to idling, never
        ///     to yield break, or a disable-then-immediate-re-enable inside one frame would kill
        ///     pickup on a component that survives it.
        ///     Ownership is checked here, every frame, rather than once in Awake: a Player can
        ///     gain or lose ownership while this component lives, and an activation that
        ///     reconciles the scene would otherwise freeze whatever was true at attach time
        ///     (MAJOR-6). This is a deliberate behaviour change against v1.7.0.
        ///     A paused game is reversible in exactly the same way, which is why the check sits
        ///     here rather than only in Update and FixedUpdate: those two refuse to raise
        ///     _active while paused, but a pass already suspended at a yield would otherwise
        ///     resume and pick during the pause.
        /// </summary>
        private bool Live =>
            Alive
            && !Module.StopRequested
            && !Game.IsPaused()
            && _player.IsOwner();

        private void Awake()
        {
            _player = GetComponent<Player>();
        }

        /// <summary>
        ///     The coroutine starts here rather than in Awake. Unity stops a component's
        ///     coroutines when its GameObject is deactivated and does not call Awake again when
        ///     it comes back, so an iterator started in Awake never returns: Update would go on
        ///     raising _active with nothing left to consume it, and pickup would be dead for
        ///     that player until the module was toggled. The activation scan deliberately
        ///     includes inactive objects, so a component can also be attached to an object that
        ///     is not active yet - in which case this runs when the object is activated, which
        ///     is exactly when the coroutine should begin.
        ///     Started whatever the ownership is: ownership is a per-frame question now, so
        ///     deciding it once here would leave a Player that gains ownership later with no
        ///     coroutine at all (MAJOR-6). A non-owner costs one idling WaitForSeconds.
        /// </summary>
        private void OnEnable()
        {
            // Cancelled belongs to an activation that is over; a re-activation gets its own
            // component. Running guards the case OnDisable cannot: disabling the component
            // rather than the GameObject raises OnDisable without Unity stopping anything, so
            // without this a second iterator could be started beside the first.
            if (_cancelled) return;
            if (_running) return;

            _running = true;
            StartCoroutine(nameof(Pickup));
        }

        /// <summary>
        ///     Stops explicitly rather than relying on the engine, because the two ways this is
        ///     reached differ: deactivating the GameObject stops coroutines, disabling the
        ///     component alone does not. Stopping here makes both leave the same state behind,
        ///     which is what lets OnEnable restart exactly one iterator.
        /// </summary>
        private void OnDisable()
        {
            _running = false;
            _active = false;
            StopCoroutine(nameof(Pickup));
        }

        /// <summary>
        ///     Called synchronously by the owning scope just before it destroys this component.
        ///     Engine destruction is deferred to the end of the frame and OnDestroy with it, so
        ///     a coroutine left to be stopped there gets to run at least one more pickup after
        ///     the module was turned off. No ownership guard: the coroutine runs whether or not
        ///     this Player is owned, so stopping it must not depend on ownership either.
        /// </summary>
        public void OnScopeDetaching()
        {
            _cancelled = true;
            _active = false;
            _running = false;
            StopCoroutine(nameof(Pickup));
        }

        private void OnDestroy()
        {
            // The same shutdown for the destructions the scope does not drive - a world unload,
            // a Player object the game removes. The ownership guard that used to stand here
            // meant a Player whose ownership had moved kept its coroutine running.
            _cancelled = true;
            _active = false;
            _running = false;
            StopCoroutine(nameof(Pickup));
            _player = null;
        }

        internal static void ClearSharedBuffers()
        {
            // The buffers are static, so they outlive every component that filled them and would
            // otherwise keep destroyed Pickables, PickableItems and ItemDrops alive across a
            // disable. EmptyItemDrops is not cleared: it is the empty sentinel GetAllItemDrop
            // falls back to, and being empty is its whole contract.
            PickableBuffer.Clear();
            PickableItemBuffer.Clear();
            ItemDropBuffer.Clear();
        }

        // A coroutine resumes at the yield it suspended on, not at the head of its loop. That is
        // why each yield below carries its own guard rather than the loop relying on one guard
        // at the top: an iterator suspended at any of them resumes straight into the next pickup
        // or interact animation, and the guard at the loop head is never reached on that path.
        // Removing them as redundant reopens exactly that hole.
        [SuppressMessage("ReSharper", "IteratorNeverReturns")]
        private IEnumerator Pickup()
        {
            while (true)
            {
                // Live, not just _active: Update and FixedUpdate raise _active up to a tenth of
                // a second before this loop looks at it, so a request to stop that arrives in
                // between would otherwise drive one more pass off a stale flag. Idling here
                // rather than breaking keeps both of Live's conditions reversible.
                while (!_active || !Live)
                {
                    yield return _idleWait;
                    if (!Alive) yield break;
                }

                _active = false;

                if (Config.PickupAllNearbyKey.MainKey == KeyCode.None)
                {
                    PickupAllNearby(_player,
                        (Pickable x) => !AutomaticFarming.AutomaticFarming.ClaimsCrop(_player, x));
                    yield return null;
                    if (!Alive) yield break;
                    if (!Live) continue;
                    PickupAllNearby(_player, (PickableItem x) => true);
                    yield return null;
                    if (!Alive) yield break;
                    if (!Live) continue;
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
                    if (!Alive) yield break;
                    if (!Live) continue;
                    // The hover target was validated before the suspension, not after it. It
                    // can be picked, destroyed or swapped while the iterator is parked, and
                    // dereferencing it then raises MissingReferenceException outside the
                    // per-object handlers below - which kills the iterator and silently stops
                    // pickup for this player.
                    if (!hovering) continue;

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
                    if (!Alive) yield break;
                    if (!Live) continue;
                    if (!hovering) continue;

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
            if (!Live) return;
            if (Config.PickupAllNearbyKey.MainKey == KeyCode.None) return;
            if (_player.InAttack() || _player.InDodge()) return;
            if (!Reflections.InvokeMethod<bool>(_player, "TakeInput")) return;
            if (!Config.PickupAllNearbyKey.IsDown()) return;

            _active = true;
        }

        private void FixedUpdate()
        {
            if (!Live) return;
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
