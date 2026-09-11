using System;
using HarmonyLib;
using ModUtils;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Automatics.AutomaticMapping
{
    internal static class Navigation
    {
        private const float TargetPinValidationInterval = 0.5f;
        private const float OverlayMinWidth = 120f;
        private const float OverlayHeight = 44f;
        private const float EdgeMargin = 40f;
        private const float TextLeft = 8f;
        private const float TextRight = 8f;
        private const float TextHeight = 18f;
        private const float ManualPinMapPlaneHeightEpsilon = 0.01f;

        // Resolve GameCamera.m_camera through a cached field-ref delegate; the navigation
        // overlay reads it every frame and a fresh Traverse lookup each frame is wasteful.
        private static readonly AccessTools.FieldRef<GameCamera, Camera> CameraRef =
            AccessTools.FieldRefAccess<GameCamera, Camera>("m_camera");

        private static Minimap.PinData _targetPin;
        private static GameObject _overlayObject;
        private static RectTransform _overlayRect;
        private static Canvas _overlayCanvas;
        private static Text _nameText;
        private static Text _distanceText;
        private static string _displayName = string.Empty;
        // Raw (unlocalized) pin name reference last seen, so UpdateName can skip
        // re-localizing every frame when the source m_name reference is unchanged.
        private static string _rawPinName;
        // Cached as the rounded integer the overlay actually displays, so UpdateDistance
        // can skip rebuilding the "{n}m" string every frame when the value is unchanged.
        private static int _displayDistanceMeters = int.MinValue;
        private static bool _overlayWidthDirty;
        private static float _nextTargetPinValidationTime;

        public static void Cleanup()
        {
            _targetPin = null;
            _displayName = string.Empty;
            _rawPinName = null;
            _displayDistanceMeters = int.MinValue;
            _overlayWidthDirty = false;
            _nextTargetPinValidationTime = 0f;
            DestroyOverlay();
        }

        public static void OnRemovePin(Minimap.PinData pinData)
        {
            if (pinData != null && ReferenceEquals(pinData, _targetPin))
                ClearTarget(false);
        }

        public static void ClearTarget()
        {
            ClearTarget(false);
        }

        public static bool TryHandleMapClick(Minimap map)
        {
            // False hands the click back to the game's own handling.
            if (!Module.Running) return false;
            if (!map) return false;
            if (Config.NavigationStartKey.MainKey == KeyCode.None) return false;
            if (map.m_mode != Minimap.MapMode.Large) return false;
            if (!Config.NavigationStartKey.IsPressed()) return false;

            var pos = Reflections.InvokeMethod<Vector3>(map, "ScreenToWorldPoint",
                ZInput.pointerPosition);
            var removeRadius = Reflections.GetField<float>(map, "m_removeRadius");
            var largeZoom = Reflections.GetField<float>(map, "m_largeZoom");
            var radius = removeRadius * (largeZoom * 2f);
            var pinData = Map.GetClosestPin(pos, radius);
            if (pinData == null) return false;

            if (ReferenceEquals(pinData, _targetPin))
                ClearTarget(true);
            else
                SetTarget(pinData);

            return true;
        }

        public static void Update(Player player)
        {
            // Stopped: the overlay is hidden in the frame the stop is requested, and destroyed
            // with the rest of the session when the module is turned off.
            if (!Module.Running)
            {
                SetVisible(false);
                return;
            }

            ValidateTargetPin();

            if (!ShouldShow(player))
            {
                SetVisible(false);
                return;
            }

            if (!EnsureOverlay())
                return;

            var targetPos = GetNavigationTargetPosition(_targetPin);
            if (!TryGetScreenPoint(targetPos, out var screenPoint))
            {
                SetVisible(false);
                return;
            }

            UpdateName();
            UpdateDistance(player, targetPos);
            if (_overlayWidthDirty)
                ResizeOverlay();
            UpdatePosition(screenPoint);
            SetVisible(true);
        }

        private static bool ShouldShow(Player player)
        {
            return _targetPin != null &&
                   player &&
                   player == Player.m_localPlayer &&
                   player.IsOwner() &&
                   !Game.IsPaused() &&
                   Hud.instance &&
                   Minimap.instance &&
                   Minimap.instance.m_mode != Minimap.MapMode.Large;
        }

        private static void SetTarget(Minimap.PinData pinData)
        {
            _targetPin = pinData;
            _displayName = string.Empty;
            _rawPinName = null;
            _displayDistanceMeters = int.MinValue;
            _overlayWidthDirty = true;
            _nextTargetPinValidationTime = 0f;
            ShowStartMessage(pinData);
        }

        private static void ClearTarget(bool showMessage)
        {
            _targetPin = null;
            _displayName = string.Empty;
            _rawPinName = null;
            _displayDistanceMeters = int.MinValue;
            _overlayWidthDirty = false;
            _nextTargetPinValidationTime = 0f;
            SetVisible(false);
            if (showMessage)
                ShowMessage(Automatics.L10N.Localize(
                    "@message_automatic_mapping_navigation_cleared"));
        }

        private static void SetVisible(bool visible)
        {
            if (_overlayObject)
                _overlayObject.SetActive(visible);
        }

        private static bool EnsureOverlay()
        {
            if (_overlayObject && _overlayRect && _nameText && _distanceText)
                return true;

            DestroyOverlay();

            var parent = GetOverlayParent();
            if (!parent) return false;

            _overlayObject = new GameObject("AutomaticsNavigationOverlay", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Image));
            _overlayRect = _overlayObject.GetComponent<RectTransform>();
            _overlayRect.SetParent(parent, false);
            _overlayCanvas = _overlayRect.GetComponentInParent<Canvas>();
            _overlayRect.anchorMin = new Vector2(0.5f, 0.5f);
            _overlayRect.anchorMax = new Vector2(0.5f, 0.5f);
            _overlayRect.pivot = new Vector2(0.5f, 0.5f);
            _overlayRect.sizeDelta = new Vector2(OverlayMinWidth, OverlayHeight);
            _overlayRect.SetAsLastSibling();

            var background = _overlayObject.GetComponent<Image>();
            background.color = new Color(0f, 0f, 0f, 0.45f);
            background.raycastTarget = false;

            _nameText = CreateNameText(_overlayRect);
            _distanceText = CreateDistanceText(_overlayRect);
            SetVisible(false);
            return true;
        }

        private static RectTransform GetOverlayParent()
        {
            if (!Hud.instance) return null;

            var parent = Hud.instance.transform as RectTransform;
            if (parent) return parent;

            var canvas = Hud.instance.GetComponentInChildren<Canvas>();
            return canvas ? canvas.transform as RectTransform : null;
        }

        private static Text CreateDistanceText(Transform parent)
        {
            var textObject = new GameObject("Distance", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Text), typeof(Outline));
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(parent, false);
            textRect.anchorMin = new Vector2(0f, 0.5f);
            textRect.anchorMax = new Vector2(1f, 0.5f);
            textRect.pivot = new Vector2(0f, 0.5f);
            textRect.anchoredPosition = new Vector2(TextLeft, -10f);
            textRect.sizeDelta = new Vector2(-(TextLeft + TextRight), TextHeight);

            var text = textObject.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static Text CreateNameText(Transform parent)
        {
            var textObject = new GameObject("Name", typeof(RectTransform),
                typeof(CanvasRenderer), typeof(Text), typeof(Outline));
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.SetParent(parent, false);
            textRect.anchorMin = new Vector2(0f, 0.5f);
            textRect.anchorMax = new Vector2(1f, 0.5f);
            textRect.pivot = new Vector2(0f, 0.5f);
            textRect.anchoredPosition = new Vector2(TextLeft, 10f);
            textRect.sizeDelta = new Vector2(-(TextLeft + TextRight), TextHeight);

            var text = textObject.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            text.fontSize = 14;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            var outline = textObject.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1f, -1f);
            return text;
        }

        private static void UpdateName()
        {
            // Skip the Replace/Trim/Localize work when the source m_name reference
            // is unchanged; static pins assign m_name once and dynamic pins only on
            // the scan interval, so the reference is stable between frames.
            var raw = _targetPin?.m_name;
            if (ReferenceEquals(raw, _rawPinName)) return;
            _rawPinName = raw;

            var name = GetDisplayPinName(_targetPin);
            if (_displayName == name) return;

            _displayName = name;
            _nameText.text = name;
            _overlayWidthDirty = true;
        }

        private static void UpdateDistance(Player player, Vector3 targetPos)
        {
            var meters = Mathf.RoundToInt(Utils.DistanceXZ(player.transform.position, targetPos));
            if (meters == _displayDistanceMeters) return;

            _displayDistanceMeters = meters;
            _distanceText.text = meters + "m";
            _overlayWidthDirty = true;
        }

        private static void UpdatePosition(Vector2 screenPoint)
        {
            var parent = _overlayRect.parent as RectTransform;
            if (!parent) return;

            var canvas = _overlayCanvas;
            var uiCamera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera
                : null;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPoint,
                    uiCamera, out var anchoredPosition))
            {
                var parentHalfWidth = parent.rect.width * 0.5f;
                var parentHalfHeight = parent.rect.height * 0.5f;
                var overlayHalfWidth = _overlayRect.rect.width * 0.5f;
                var overlayHalfHeight = _overlayRect.rect.height * 0.5f;
                var xLimit = Mathf.Max(0f, parentHalfWidth - overlayHalfWidth - EdgeMargin);
                var yLimit = Mathf.Max(0f, parentHalfHeight - overlayHalfHeight - EdgeMargin);
                anchoredPosition.x = Mathf.Clamp(anchoredPosition.x, -xLimit, xLimit);
                anchoredPosition.y = Mathf.Clamp(anchoredPosition.y, -yLimit, yLimit);
                _overlayRect.anchoredPosition = anchoredPosition;
            }
        }

        private static bool TryGetScreenPoint(Vector3 targetPos, out Vector2 screenPoint)
        {
            screenPoint = default;
            var camera = GetWorldCamera();
            if (!camera) return false;

            var viewport = camera.WorldToViewportPoint(targetPos);
            if (viewport.z <= 0f) return false;
            if (viewport.x < 0f || viewport.x > 1f) return false;
            if (viewport.y < 0f || viewport.y > 1f) return false;

            var marginX = Screen.width <= 0f ? 0f : EdgeMargin / Screen.width;
            var marginY = Screen.height <= 0f ? 0f : EdgeMargin / Screen.height;
            viewport.x = Mathf.Clamp(viewport.x, marginX, 1f - marginX);
            viewport.y = Mathf.Clamp(viewport.y, marginY, 1f - marginY);
            screenPoint = new Vector2(viewport.x * Screen.width, viewport.y * Screen.height);
            return true;
        }

        private static Camera GetWorldCamera()
        {
            if (GameCamera.instance)
            {
                var gameCamera = CameraRef(GameCamera.instance);
                if (gameCamera) return gameCamera;
            }

            return Camera.main;
        }

        private static void DestroyOverlay()
        {
            if (_overlayObject)
                Object.Destroy(_overlayObject);

            _overlayObject = null;
            _overlayRect = null;
            _overlayCanvas = null;
            _nameText = null;
            _distanceText = null;
        }

        private static void ShowStartMessage(Minimap.PinData pinData)
        {
            var pinName = GetDisplayPinName(pinData);

            ShowMessage(string.IsNullOrEmpty(pinName)
                ? Automatics.L10N.Localize("@message_automatic_mapping_navigation_started")
                : Automatics.L10N.Localize("@message_automatic_mapping_navigation_started_to",
                    pinName));
        }

        private static void ShowMessage(string message)
        {
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message);
        }

        private static void ResizeOverlay()
        {
            var preferredTextWidth = Mathf.Max(_nameText.preferredWidth, _distanceText.preferredWidth);
            var width = Mathf.Max(OverlayMinWidth, TextLeft + TextRight + preferredTextWidth);
            if (Screen.width > 0)
                width = Mathf.Min(width, Mathf.Max(OverlayMinWidth, Screen.width - EdgeMargin * 2f));

            _overlayRect.sizeDelta = new Vector2(width, OverlayHeight);
            _overlayWidthDirty = false;
        }

        private static void ValidateTargetPin()
        {
            if (_targetPin == null) return;
            if (Time.time < _nextTargetPinValidationTime) return;

            _nextTargetPinValidationTime = Time.time + TargetPinValidationInterval;
            if (!Map.ContainsPin(_targetPin) || Map.ShouldHideAutomaticPin(_targetPin))
                ClearTarget(false);
        }

        private static string GetDisplayPinName(Minimap.PinData pinData)
        {
            var pinName = pinData?.m_name;
            if (string.IsNullOrEmpty(pinName))
                return string.Empty;

            pinName = pinName.Replace("\n", " ").Trim();
            return Localization.instance != null
                ? Localization.instance.Localize(pinName)
                : pinName;
        }

        private static Vector3 GetNavigationTargetPosition(Minimap.PinData pinData)
        {
            if (pinData == null) return Vector3.zero;

            var pos = pinData.m_pos;
            if (!ShouldResolveTerrainHeight(pinData)) return pos;
            return TryResolveTerrainHeight(pos, out var height)
                ? new Vector3(pos.x, height, pos.z)
                : pos;
        }

        private static bool ShouldResolveTerrainHeight(Minimap.PinData pinData)
        {
            return pinData.m_save &&
                   Mathf.Abs(pinData.m_pos.y) <= ManualPinMapPlaneHeightEpsilon;
        }

        private static bool TryResolveTerrainHeight(Vector3 pos, out float height)
        {
            try
            {
                if (Heightmap.GetHeight(pos, out height))
                    return true;

                if (WorldGenerator.instance != null)
                {
                    height = WorldGenerator.instance.GetHeight(pos.x, pos.z);
                    return true;
                }
            }
            catch (Exception)
            {
                // Fall through to the original pin position if Valheim cannot
                // resolve terrain height in the current world state.
            }

            height = pos.y;
            return false;
        }
    }
}
