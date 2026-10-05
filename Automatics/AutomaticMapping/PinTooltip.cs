using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    internal static class PinTooltip
    {
        internal const string ObjectName = "AutomaticsPinTooltip";
        private const float RetryDelay = 5f;
        private static readonly Func<Minimap, Vector3, Vector3> ScreenToWorld =
            AccessTools.MethodDelegate<Func<Minimap, Vector3, Vector3>>(AccessTools.Method(typeof(Minimap), "ScreenToWorldPoint"));
        private static readonly AccessTools.FieldRef<Minimap, float> RemoveRadius =
            AccessTools.FieldRefAccess<Minimap, float>("m_removeRadius");
        private static readonly AccessTools.FieldRef<Minimap, float> LargeZoom =
            AccessTools.FieldRefAccess<Minimap, float>("m_largeZoom");
        private static readonly Type TextType = AccessTools.Property(typeof(Minimap.PinNameData), "PinNameText").PropertyType;
        private static readonly PropertyInfo TextProperty = AccessTools.Property(TextType, "text");
        private static Minimap _map;
        private static Component _topic;
        private static Component _text;
        private static RectTransform _body;
        private static Vector3 _pointer;
        private static Vector3 _world;
        private static float _refreshAt;
        private static float _radius;
        private static float _nextAttemptTime;
        private static bool _errorLogged;
        private static readonly Vector3[] Corners = new Vector3[4];

        internal static GameObject Root { get; private set; }
        internal static Minimap.PinData HoveredPin { get; private set; }
        internal static PinTooltipContent.Content Content { get; private set; }
        internal static PinTooltipContent.SavedObjects ResolvedObjects { get; private set; }
        internal static bool HasTopicAndText => _topic && _text && _topic != _text;

        internal static void Update(Minimap map)
        {
            UpdateSafely(map, null);
        }

        // The same cursor path is used by the smoke checks without moving the user's mouse.
        internal static void UpdateAt(Minimap map, Vector3 pointer)
        {
            UpdateSafely(map, pointer);
        }

        private static void UpdateSafely(Minimap map, Vector3? pointer)
        {
            if (Time.unscaledTime < _nextAttemptTime) return;
            try
            {
                UpdateTooltip(map, pointer ?? ZInput.pointerPosition);
            }
            catch (Exception e)
            {
                _nextAttemptTime = Time.unscaledTime + RetryDelay;
                try
                {
                    DestroyTooltip();
                }
                catch (Exception)
                {
                    // Owned references are cleared before the engine destroys the object.
                }
                if (_errorLogged) return;
                _errorLogged = true;
                Automatics.Logger.Error($"Pin tooltip failed and is retried every {RetryDelay}s: {e}");
            }
        }

        private static void UpdateTooltip(Minimap map, Vector3 pointer)
        {
            if (!Module.Running || !Config.EnableMapEnhancements || !map ||
                map.m_mode != Minimap.MapMode.Large || MapFilter.SearchFocused || !Player.m_localPlayer)
            {
                Hide();
                return;
            }
            if (_map && _map != map) Cleanup();
            var world = ScreenToWorld(map, pointer);
            var due = Time.unscaledTime >= _refreshAt;
            var pin = HoveredPin;
            var radius = RemoveRadius(map) * (LargeZoom(map) * 2f);
            var searched = pointer != _pointer || world != _world || radius != _radius || due ||
                pin != null && (!pin.m_uiElement || !pin.m_uiElement.gameObject.activeInHierarchy);
            if (searched) pin = Map.GetClosestPin(world, radius);
            _radius = radius;
            _pointer = pointer;
            _world = world;
            if (pin == null)
            {
                var nextRefresh = searched ? Time.unscaledTime + 1f : _refreshAt;
                Hide();
                _refreshAt = nextRefresh;
                return;
            }
            var changed = !ReferenceEquals(pin, HoveredPin);
            if (changed) ResolvedObjects = PinTooltipContent.ResolveSavedObjects(pin);
            HoveredPin = pin;
            if (!Root) Create(map);
            if (changed || due || Content == null)
            {
                Content = PinTooltipContent.Build(pin, ResolvedObjects);
                if (_topic) TextProperty.SetValue(_topic, Content.Name);
                TextProperty.SetValue(_text, _topic ? Content.Text : Content.Name + "\n" + Content.Text);
                Root.SetActive(true);
                LayoutRebuilder.ForceRebuildLayoutImmediate(_body);
                _refreshAt = Time.unscaledTime + 1f;
            }
            Root.transform.position = pointer;
            _body.GetWorldCorners(Corners);
            var min = Corners[0];
            var max = Corners[2];
            var offset = new Vector3(min.x < 0f ? -min.x : max.x > Screen.width ? Screen.width - max.x : 0f,
                min.y < 0f ? -min.y : max.y > Screen.height ? Screen.height - max.y : 0f);
            Root.transform.position += offset;
        }

        private static void Create(Minimap map)
        {
            var element = InventoryGui.instance.m_playerGrid.m_elementPrefab.GetComponent<InventoryElement>();
            var prefab = element.m_tooltip.m_tooltipPrefab;
            var root = Object.Instantiate(prefab, map.m_largeRoot.GetComponentInParent<Canvas>().transform);
            try
            {
                root.name = ObjectName;
                root.SetActive(false);
                var body = (RectTransform)root.transform.GetChild(0);
                var topicChild = Utils.FindChild(root.transform, "Topic");
                var textChild = Utils.FindChild(root.transform, "Text");
                var topic = topicChild ? topicChild.GetComponent(TextType) : null;
                var text = textChild ? textChild.GetComponent(TextType) : null;
                if (!text) text = root.GetComponentInChildren(TextType, true);
                if (!text) throw new InvalidOperationException("Pin tooltip prefab has no text component");
                if (topic == text) topic = null;
                foreach (var graphic in root.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
                foreach (var localize in root.GetComponentsInChildren<Localize>(true)) localize.enabled = false;
                _map = map;
                _body = body;
                _topic = topic;
                _text = text;
                Root = root;
            }
            finally
            {
                if (Root != root) Object.Destroy(root);
            }
        }

        internal static void Hide()
        {
            if (Root && Root.activeSelf) Root.SetActive(false);
            _refreshAt = 0f;
            HoveredPin = null;
            Content = null;
            ResolvedObjects = null;
        }

        internal static void Invalidate()
        {
            _refreshAt = 0f;
        }

        private static void DestroyTooltip()
        {
            var root = Root;
            Root = null;
            HoveredPin = null;
            Content = null;
            ResolvedObjects = null;
            _map = null;
            _topic = _text = null;
            _body = null;
            _refreshAt = _radius = 0f;
            _pointer = _world = Vector3.zero;
            if (!root) return;
            root.SetActive(false);
            Object.Destroy(root);
        }

        internal static void Cleanup()
        {
            DestroyTooltip();
            _nextAttemptTime = 0f;
            _errorLogged = false;
        }
    }
}
