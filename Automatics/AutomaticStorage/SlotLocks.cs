using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace Automatics.AutomaticStorage
{
    internal static class SlotLocks
    {
        private const string CustomDataKey = "automatics.automatic_storage.locked_slots";
        private const string IconName = "AutomaticsStorageSlotLock";

        private static Sprite _lockSprite;
        private static bool _spriteWarningLogged;
        private static InventoryGrid _playerGrid;

        internal static void CaptureSprite()
        {
            if (_lockSprite) return;

            foreach (var serverList in Resources.FindObjectsOfTypeAll<ServerListGui>())
            {
                if (!serverList.m_serverListElement) continue;
                var privateIcon = serverList.m_serverListElement.transform.Find("Private");
                if (!privateIcon) continue;
                var image = privateIcon.GetComponentInChildren<Image>(true);
                if (!image || !image.sprite) continue;
                _lockSprite = image.sprite;
                return;
            }

            if (_spriteWarningLogged) return;
            _spriteWarningLogged = true;
            Automatics.Logger.Warning("Automatic Storage could not find the server browser lock sprite; slot lock icons will not be shown.");
        }

        internal static HashSet<Vector2i> GetLockedSlots(Player player)
        {
            var slots = new HashSet<Vector2i>();
            if (player == null || !player.m_customData.TryGetValue(CustomDataKey, out var saved))
                return slots;

            foreach (var entry in saved.Split(';'))
            {
                var coordinates = entry.Split(',');
                if (coordinates.Length != 2 ||
                    !int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                    !int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                    continue;
                slots.Add(new Vector2i(x, y));
            }

            return slots;
        }

        internal static bool ToggleFromClick(InventoryGrid grid, UIInputHandler clickHandler)
        {
            if (!IsLockGesture(grid)) return false;

            var player = Player.m_localPlayer;
            var element = clickHandler.GetComponentInParent<InventoryElement>();
            if (!element || element.GetComponentInParent<InventoryGrid>() != grid) return false;

            var slots = GetLockedSlots(player);
            if (!slots.Add(element.Position)) slots.Remove(element.Position);
            if (slots.Count == 0)
                player.m_customData.Remove(CustomDataKey);
            else
                player.m_customData[CustomDataKey] = string.Join(";", slots.OrderBy(x => x.y)
                    .ThenBy(x => x.x)
                    .Select(x => x.x.ToString(CultureInfo.InvariantCulture) + "," +
                                 x.y.ToString(CultureInfo.InvariantCulture)));
            RefreshIcons(grid, player);
            return true;
        }

        internal static bool IsLockGesture(InventoryGrid grid)
        {
            var gui = InventoryGui.instance;
            var player = Player.m_localPlayer;
            if (!Module.Running || !gui || !InventoryGui.IsVisible() ||
                grid != gui.m_playerGrid || !player || !player.IsOwner()) return false;

            KeyboardShortcut key = Config.SlotLockKey;
            return key.MainKey != KeyCode.None && Input.GetKey(key.MainKey) &&
                   key.Modifiers.All(Input.GetKey);
        }

        internal static void RefreshIcons(InventoryGrid grid, Player player)
        {
            if (!Module.Running || !grid || player != Player.m_localPlayer) return;
            _playerGrid = grid;
            var slots = GetLockedSlots(player);
            foreach (var element in grid.GetComponentsInChildren<InventoryElement>(true))
            {
                var icon = element.transform.Find(IconName);
                var show = _lockSprite && slots.Contains(element.Position);
                if (!show)
                {
                    if (icon) icon.gameObject.SetActive(false);
                    continue;
                }

                if (!icon)
                {
                    var iconObject = new GameObject(IconName, typeof(RectTransform), typeof(Image));
                    var rect = (RectTransform)iconObject.transform;
                    rect.SetParent(element.transform, false);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.zero;
                    rect.pivot = Vector2.zero;
                    rect.sizeDelta = new Vector2(14f, 14f);
                    rect.anchoredPosition = new Vector2(1f, 10f);
                    var image = iconObject.GetComponent<Image>();
                    image.sprite = _lockSprite;
                    image.raycastTarget = false;
                    icon = rect;
                }

                icon.gameObject.SetActive(true);
            }
        }

        internal static void HideIcons()
        {
            if (!_playerGrid) return;
            foreach (var element in _playerGrid.GetComponentsInChildren<InventoryElement>(true))
            {
                var icon = element.transform.Find(IconName);
                if (icon) icon.gameObject.SetActive(false);
            }
            _playerGrid = null;
        }
    }
}
